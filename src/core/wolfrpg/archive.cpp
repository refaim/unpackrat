#include "archive.h"

#include "archive_source.h"

#include "chacha_crypto.h"
#include "game_context.h"
#include "legacy_archive.h"
#include "modern_crypto.h"
#include "pro_keys.h"
#include "text_encoding.h"
#include "wolfx_archive.h"
#include "wolfx_candidates.h"
#include "wolfx_game_metadata.h"

#include <uberwolf/DXLib/DXArchive.h>
#include <uberwolf/WolfDec.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <ios>
#include <limits>
#include <memory>
#include <new>
#include <numeric>
#include <span>
#include <stdexcept>
#include <streambuf>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

// The public WolfRPG reader. This translation unit is the JOIN: one open() classifies an untrusted
// source and hands it to exactly one already-tested, project-owned backend, all of which publish the
// same value contract (entry / read_error / key_required).
//
//   * the classic DXLib "DX8" container (and its modern / Pro / CC2 crypt revisions) is driven
//     through the pinned DXArchive byte-source API: the reader owns container validation, the
//     depth-first walk, the body geometry and the stored / LZ / Huffman codecs, while this unit
//     supplies a fresh Source per operation and, where the container needs it, an operation-local
//     encoded transform borrowed from an immutable project-owned context;
//   * the legacy DXLib "DX1..DX6" family is delegated to src/core/wolfrpg/legacy_archive.*;
//   * the single-file WolfX stream is delegated to src/core/wolfrpg/wolfx_archive.*.
//
// The crypt-revision decision (default key trial, exact lookup, Pro/CC2 companion recovery) reuses
// the pinned WolfDec policy rather than a second copy of the mode table, and the modern / ChaCha
// transforms reuse the project-owned modern_crypto / chacha_crypto adapters. No cipher, key search
// or metadata parser is re-implemented here.
namespace unpackrat::wolfrpg
{
    namespace
    {
        // Largest progress delta reported for a single write. The vendor already emits decoded
        // data in at most this chunk, so every delta is one genuine destination write.
        constexpr std::size_t progress_chunk_bytes = std::size_t{32} * 1024U;

        constexpr std::size_t modern_header_bytes = 64;

        // The single-file WolfX container always begins with these five bytes.
        constexpr std::array<unsigned char, 5> wolfx_magic = {'W', 'O', 'L', 'F', 'X'};

        // The value the shared trial driver reads as "no mode detected yet": the original
        // WolfDec::m_mode starts at -1 and every mode decision runs while it still holds that value.
        constexpr std::uint32_t undetected_mode = static_cast<std::uint32_t>(-1);

        // ------------------------------------------------------------------ stream byte access

        // Source access goes straight at the stream buffer instead of through the formatted istream
        // layer. istream operations (clear/seekg/read/tellg) fold every buffer exception into
        // latched state, so a caller resource failure such as std::bad_alloc would be swallowed and
        // reported only as a short count. One level down keeps a caller-owned exception exactly as
        // thrown while a source fault is still normalized. The source operations below share one
        // policy: the caller resource exceptions std::bad_alloc and std::length_error keep their
        // concrete type and payload, and every other thrown type is a source fault (read_error).
        [[nodiscard]] std::streambuf &source_buffer(std::istream &stream)
        {
            std::streambuf *buffer = stream.rdbuf();
            if (buffer == nullptr) {
                throw read_error();
            }
            return *buffer;
        }

        [[nodiscard]] std::streamoff source_size(std::istream &stream)
        {
            std::streambuf &buffer = source_buffer(stream);
            try {
                return static_cast<std::streamoff>(buffer.pubseekoff(0, std::ios_base::end, std::ios_base::in));
            } catch (const std::bad_alloc &) {
                throw;
            } catch (const std::length_error &) {
                throw;
            } catch (...) {
                throw read_error();
            }
        }

        // The physical length of a source whose size must be known exactly. A refused size query (a
        // negative result) is a source fault here; a caller that must surface the refusal to the
        // vendor instead keeps the raw value.
        [[nodiscard]] std::uint64_t checked_source_size(std::istream &stream)
        {
            const std::streamoff size = source_size(stream);
            if (size < 0) {
                throw read_error();
            }
            return static_cast<std::uint64_t>(size);
        }

        // Absolute seek: seekoff first, so a buffer that implements only seekoff is sufficient on
        // its own. Only a rejected (negative) seekoff falls back to seekpos; the landing must be
        // exactly the requested target and anything else is a source failure.
        [[nodiscard]] bool seek_source(std::streambuf &buffer, const std::uint64_t position)
        {
            const auto target = static_cast<std::streamoff>(position);
            std::streamoff landed = -1;
            try {
                landed = static_cast<std::streamoff>(buffer.pubseekoff(target, std::ios_base::beg, std::ios_base::in));
                if (landed < 0) {
                    landed = static_cast<std::streamoff>(buffer.pubseekpos(target, std::ios_base::in));
                }
            } catch (const std::bad_alloc &) {
                throw;
            } catch (const std::length_error &) {
                throw;
            } catch (...) {
                throw read_error();
            }
            return landed == target;
        }

        // Reads exactly ``size`` bytes at ``offset`` (a short read is never handed back as data).
        // The caller-owned resource exceptions propagate; every other failure is a source fault.
        void read_exact_at(std::istream &stream, const std::uint64_t offset, void *destination, const std::size_t size)
        {
            std::streambuf &buffer = source_buffer(stream);
            if (!seek_source(buffer, offset)) {
                throw read_error();
            }
            std::streamsize got = 0;
            try {
                got = buffer.sgetn(static_cast<char *>(destination), static_cast<std::streamsize>(size));
            } catch (const std::bad_alloc &) {
                throw;
            } catch (const std::length_error &) {
                throw;
            } catch (...) {
                throw read_error();
            }
            if (got != static_cast<std::streamsize>(size)) {
                throw read_error();
            }
        }

        // Reads exactly ``count`` bytes from the start of the stream. The caller only asks for a
        // probe the source is known to be large enough for, so a read that comes back short is a
        // genuine byte-source fault (read_error), never a "this is not the format" signal: treating
        // a short probe as a format mismatch would silently retry another key or backend and hide
        // the fault. Caller-owned resource exceptions keep their identity.
        [[nodiscard]] std::vector<unsigned char> read_prefix(std::istream &stream, const std::size_t count)
        {
            std::vector<unsigned char> bytes(count, 0);
            std::streambuf &buffer = source_buffer(stream);
            if (!seek_source(buffer, 0)) {
                throw read_error();
            }
            std::streamsize got = 0;
            try {
                got = buffer.sgetn(reinterpret_cast<char *>(bytes.data()), static_cast<std::streamsize>(count));
            } catch (const std::bad_alloc &) {
                throw;
            } catch (const std::length_error &) {
                throw;
            } catch (...) {
                throw read_error();
            }
            if (got != static_cast<std::streamsize>(count)) {
                throw read_error();
            }
            return bytes;
        }

        // ------------------------------------------------------------------------ name / path policy

        // A decoded component is usable only when it is neither traversal name and carries no
        // control byte, colon or path separator. The policy is applied to the *decoded* UTF-8
        // component only: an encoded Shift-JIS trail byte may legitimately be 0x5C, so treating a
        // raw 0x5C byte as a separator before decoding would corrupt the name.
        [[nodiscard]] bool is_safe_component(const std::string &component) noexcept
        {
            if (component == "." || component == "..") {
                return false;
            }
            return std::all_of(component.begin(), component.end(), [](const char value) {
                const auto byte = static_cast<unsigned char>(value);
                return byte >= 0x20U && value != ':' && value != '/' && value != '\\';
            });
        }

        [[nodiscard]] std::string join_path(const std::vector<std::string> &prefix, const std::string_view name)
        {
            const std::size_t total = std::accumulate(
                prefix.begin(), prefix.end(), name.size(),
                [](const std::size_t sum, const std::string &component) { return sum + component.size() + 1U; });
            std::string path;
            path.reserve(total);
            for (const std::string &component : prefix) {
                path += component;
                path.push_back('/');
            }
            path += name;
            return path;
        }

        // Widens a vendor geometry value to the published signed size only when it fits.
        [[nodiscard]] std::int64_t checked_size(const std::uint64_t value)
        {
            if (value > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max())) {
                throw read_error();
            }
            return static_cast<std::int64_t>(value);
        }

        // ------------------------------------------------------------------ crypt-kernel selection

        // The three crypt kernels the pinned DXLib KeyConv dispatches on. The mapping is the same
        // one DXA_SnapshotOperationCipher uses, so the adapter picks the kernel the vendor would.
        enum class crypt_kernel : std::uint8_t
        {
            classic, // the 7-byte XOR key path (DX1..DX8 classic / Pro 1000..1009)
            modern,  // the WolfCrypt stream (crypt versions 331..999 and >= 1010)
            chacha,  // the ChaCha20 stream (crypt versions 0x64 and 0xC8)
        };

        [[nodiscard]] constexpr crypt_kernel kernel_for(const std::uint16_t crypt_version) noexcept
        {
            if ((crypt_version >= 331U && crypt_version < 1000U) || crypt_version >= 1010U) {
                return crypt_kernel::modern;
            }
            if (crypt_version == 0x64U || crypt_version == 0xC8U) {
                return crypt_kernel::chacha;
            }
            return crypt_kernel::classic;
        }

        // Builds the 64-byte ChaCha key blob the project-owned context expects. A pinned default
        // ChaCha2 table key is 32 key bytes followed by a 12-byte nonce (optionally terminated); a
        // recovered CC2 key is already the full 64-byte blob, whose nonce the vendor reads at offset
        // 34. Any other shape is not a ChaCha key.
        [[nodiscard]] std::array<std::byte, codec::wolf_chacha_context::key_blob_size> chacha_blob(
            const std::span<const std::byte> key)
        {
            std::array<std::byte, codec::wolf_chacha_context::key_blob_size> blob{};
            if (key.size() == blob.size()) {
                std::copy(key.begin(), key.end(), blob.begin());
                return blob;
            }
            constexpr std::size_t split = codec::wolf_chacha_context::key_size;
            constexpr std::size_t tail = codec::wolf_chacha_context::nonce_offset;
            constexpr std::size_t table_size = split + codec::wolf_chacha_context::nonce_size;
            if (key.size() >= table_size) {
                std::copy(key.begin(), key.begin() + static_cast<std::ptrdiff_t>(split), blob.begin());
                std::copy(key.begin() + static_cast<std::ptrdiff_t>(split),
                          key.begin() + static_cast<std::ptrdiff_t>(table_size),
                          blob.begin() + static_cast<std::ptrdiff_t>(tail));
                return blob;
            }
            throw codec::chacha_error("unsupported WolfRPG ChaCha key length");
        }

        // ------------------------------------------------------------------ stream -> vendor source

        // Builds the vendor byte source over ``stream``. When ``modern`` is set, the first header
        // block is served as the decoded (descrambled) header and every byte at or past it is
        // outer-decoded, exactly the logical view DXLib expects for a modern container; a per-read
        // inner transform then borrows the immutable context. When ``chacha`` is set, a ChaCha20
        // call-range transform borrows that context instead. Both contexts live for the whole
        // operation, so the returned source never dangles. The source must not outlive the call.
        [[nodiscard]] DXArchive::Source make_source(std::istream &stream, const modern_crypto *modern,
                                                    const codec::wolf_chacha_context *chacha)
        {
            DXArchive::Source source;
            source.physical_size = [&stream]() -> s64 {
                const std::streamoff end = source_size(stream);
                if (end < 0) {
                    return -1;
                }
                return static_cast<s64>(end);
            };
            source.read_at = [&stream, modern](const s64 offset, void *destination, const size_t size) -> bool {
                return detail::source_read_at(
                    stream, offset, std::span<std::uint8_t>(static_cast<std::uint8_t *>(destination), size), modern);
            };
            if (modern != nullptr) {
                const modern_crypto *context = modern;
                source.encoded_transform = [context](void *data, const size_t size, const u64 start, const u64 offset) {
                    detail::transform_modern_at(
                        *context, std::span<std::uint8_t>(static_cast<std::uint8_t *>(data), size), start, offset);
                };
            } else if (chacha != nullptr) {
                const codec::wolf_chacha_context *context = chacha;
                source.encoded_transform = [context](void *data, const size_t size, const u64 start, const u64 offset) {
                    context->apply_call_range(std::span<std::byte>(static_cast<std::byte *>(data), size), start,
                                              offset);
                };
            }
            return source;
        }

        // ------------------------------------------------------------------ one DX8-family candidate

        // The vendor object plus the immutable contexts it borrows, all owned for the archive
        // lifetime. Exactly one of the two contexts is ever set alongside the vendor.
        struct dx_selection
        {
            std::unique_ptr<DXArchive> vendor;
            std::unique_ptr<modern_crypto> modern;
            std::unique_ptr<codec::wolf_chacha_context> chacha;
        };

        // Attempts one already-selected kernel/key candidate against the source. A candidate that
        // simply does not open the container (a wrong key or a different crypt family) returns
        // false; a genuine byte-source failure is a read_error. The vendor's metadata parse already
        // validates the whole table, so a candidate that parses is complete and the caller's single
        // walk stays the only walk.
        [[nodiscard]] bool open_dx_candidate(std::istream &stream, const std::span<const std::uint8_t, 64> header,
                                             const std::uint64_t physical_size, const crypt_kernel kernel,
                                             const std::string_view password, const std::span<const std::byte> key_blob,
                                             dx_selection &out)
        {
            if (kernel == crypt_kernel::modern) {
                try {
                    auto context =
                        std::make_unique<modern_crypto>(modern_crypto::create(header, physical_size, password));
                    auto candidate = std::make_unique<DXArchive>();
                    const DXArchive::Source source = make_source(stream, context.get(), nullptr);
                    const int open_result = candidate->OpenArchiveSource(source, nullptr);
                    if (open_result == DXArchive::SOURCE_RESULT_SOURCE) {
                        throw read_error();
                    }
                    if (open_result != DXArchive::SOURCE_RESULT_OK) {
                        return false;
                    }
                    out.vendor = std::move(candidate);
                    out.modern = std::move(context);
                    return true;
                } catch (const modern_crypto_error &) {
                    return false;
                }
            } else if (kernel == crypt_kernel::chacha) {
                try {
                    auto context = std::make_unique<codec::wolf_chacha_context>(chacha_blob(key_blob));
                    auto candidate = std::make_unique<DXArchive>();
                    const DXArchive::Source source = make_source(stream, nullptr, context.get());
                    const int open_result = candidate->OpenArchiveSource(source, nullptr);
                    if (open_result == DXArchive::SOURCE_RESULT_SOURCE) {
                        throw read_error();
                    }
                    if (open_result != DXArchive::SOURCE_RESULT_OK) {
                        return false;
                    }
                    out.vendor = std::move(candidate);
                    out.chacha = std::move(context);
                    return true;
                } catch (const codec::chacha_error &) {
                    return false;
                }
            }
            // Classic is the remaining validated kernel: the vendor reads the caller's key as a
            // C string and decodes the container with the 7-byte XOR path.
            const std::string key(password);
            auto candidate = std::make_unique<DXArchive>();
            const DXArchive::Source source = make_source(stream, nullptr, nullptr);
            const int open_result = candidate->OpenArchiveSource(source, key.c_str());
            if (open_result == DXArchive::SOURCE_RESULT_SOURCE) {
                throw read_error();
            }
            if (open_result != DXArchive::SOURCE_RESULT_OK) {
                return false;
            }
            out.vendor = std::move(candidate);
            return true;
        }

        // The key bytes of one combined-table entry as a byte view, for the ChaCha blob.
        [[nodiscard]] std::span<const std::byte> entry_key_view(const CryptMode &entry) noexcept
        {
            return std::span<const std::byte>(reinterpret_cast<const std::byte *>(entry.key.data()), entry.key.size());
        }

        // Attempts one mode index of the combined default-then-additional table with the kernel its
        // crypt version selects. The pinned VER5/VER6 default entries carry a different container
        // engine, so they are refused here and left to the legacy fallback.
        [[nodiscard]] bool attempt_default_mode(std::istream &stream, const std::span<const std::uint8_t, 64> header,
                                                const std::uint64_t physical_size, const CryptModes &additional_modes,
                                                const std::uint32_t mode, dx_selection &out)
        {
            const CryptMode &entry = WolfDec::ModeAt(additional_modes, mode);
            if (entry.decFunc != &DXArchive::DecodeArchive) {
                return false;
            }
            const crypt_kernel kernel = kernel_for(entry.cryptVersion);
            if (kernel == crypt_kernel::chacha) {
                // A ChaCha2 default entry carries its key and nonce as the table blob itself, not as
                // a password image.
                return open_dx_candidate(stream, header, physical_size, kernel, {}, entry_key_view(entry), out);
            }
            // Classic and modern entries both use the table key bytes unchanged as the password.
            const std::string_view password(reinterpret_cast<const char *>(entry.key.data()), entry.key.size());
            return open_dx_candidate(stream, header, physical_size, kernel, password, {}, out);
        }

        // ------------------------------------------------------------- Pro / CC2 companion recovery

        // Resolves the game context beside ``archive_path`` and recovers the DxArc key from the
        // companion with the project-owned sparse recovery, then opens the container through the
        // kernel its own header names. The full recovered binary key is preserved: the modern
        // interface receives the embedded NUL and the four tail bytes, while only the classic
        // vendor C-string boundary takes the prefix. Returns false when no usable companion key is
        // available (a keyed open the caller must supply, not a malformed file).
        [[nodiscard]] bool open_pro_candidate(std::istream &stream, const std::filesystem::path &path,
                                              const std::span<const std::uint8_t, 64> header,
                                              const std::uint64_t physical_size, const std::uint16_t header_version,
                                              dx_selection &out, const game_context_resolver &resolve_context)
        {
            const game_context_result context = resolve_context(path);
            // A probe that failed on the filesystem is an actual I/O failure, never a missing key:
            // report it as a read error so the Observer boundary surfaces a read error instead of a
            // password prompt. Only a context that was genuinely absent (or holds no companion) is a
            // keyed open the caller must supply.
            if (context.status == game_context_status::io_failure) {
                throw read_error();
            }
            if (context.context.companion.empty()) {
                return false;
            }
            std::ifstream companion(context.context.companion, std::ios::binary);
            if (!companion.is_open()) {
                // The companion was located but cannot be opened: an I/O failure, not a missing key.
                throw read_error();
            }
            const std::uint64_t companion_size = checked_source_size(companion);
            const pro::source read_at = [&companion](const std::uint64_t offset,
                                                     const std::span<std::byte> destination) {
                read_exact_at(companion, offset, destination.data(), destination.size());
            };
            const pro::key_result result = pro::recover_key(companion_size, read_at);
            const pro::recovered_key *recovered = std::get_if<pro::recovered_key>(&result);
            if (recovered == nullptr) {
                return false;
            }
            const std::span<const std::byte> key(recovered->key.data(), recovered->key.size());
            const crypt_kernel kernel = kernel_for(header_version);
            if (kernel == crypt_kernel::chacha) {
                return open_dx_candidate(stream, header, physical_size, kernel, {}, key, out);
            }
            // The recovered binary key is passed through whole: the modern interface keeps the
            // embedded NUL and the four tail bytes, and only the classic vendor C-string boundary
            // cuts the key at its first NUL.
            const std::string_view password(reinterpret_cast<const char *>(key.data()), key.size());
            return open_dx_candidate(stream, header, physical_size, kernel, password, {}, out);
        }

        // -------------------------------------------------------------------------- WolfX open

        // Tries the implicit default and the caller's key first; only an actual key_required retries
        // with the recovered game metadata. A missing companion still carries a usable data folder,
        // so discovery never depends on a Pro companion the WolfX stream does not need.
        [[nodiscard]] std::unique_ptr<wolfx_archive> open_wolfx_source(std::istream &stream,
                                                                       const std::filesystem::path &path,
                                                                       const std::string_view password,
                                                                       const game_context_resolver &resolve_context)
        {
            wolfx_candidates candidates;
            if (!password.empty()) {
                candidates.add_key(std::string(password));
            }
            try {
                return wolfx_archive::open(stream, path, candidates);
            } catch (const key_required &) {
                // Only key rejection selects recovery; every other failure propagates unchanged.
            }
            // Keep recovery out of the handler: Clang 21's comparison instrumentation loses the
            // continuation after the resolver call inside a Windows exception handler.
            const game_context_result context = resolve_context(path);
            if (context.status == game_context_status::io_failure) {
                throw read_error();
            }
            if (context.context.data_folder.empty()) {
                throw key_required();
            }
            collect_game_metadata(context.context.data_folder, {}, candidates);
            return wolfx_archive::open(stream, path, candidates);
        }
    } // namespace

    // The two byte-source operations the vendor callbacks above delegate to. They stay ordinary
    // functions so the request policy a validated vendor geometry never exercises - the empty and
    // negative request, and the overflowing logical position - is testable on its own.
    bool detail::source_read_at(std::istream &stream, const std::int64_t offset,
                                const std::span<std::uint8_t> destination, const modern_crypto *modern)
    {
        if (destination.empty()) {
            return true;
        }
        if (offset < 0) {
            return false;
        }
        std::uint8_t *out = destination.data();
        std::size_t left = destination.size();
        std::uint64_t position = static_cast<std::uint64_t>(offset);
        if (modern != nullptr && position < modern_header_bytes) {
            const std::size_t take =
                std::min<std::size_t>(static_cast<std::size_t>(modern_header_bytes - position), left);
            std::memcpy(out, modern->header().data() + position, take);
            out += take;
            position += take;
            left -= take;
        }
        if (left == 0) {
            return true;
        }
        read_exact_at(stream, position, out, left);
        if (modern != nullptr) {
            modern->decode_outer_at(std::span<std::uint8_t>(out, left), position);
        }
        return true;
    }

    void detail::transform_modern_at(const modern_crypto &modern, const std::span<std::uint8_t> data,
                                     const std::uint64_t start, const std::uint64_t offset)
    {
        if (offset > std::numeric_limits<std::uint64_t>::max() - start) {
            throw read_error();
        }
        modern.apply_inner(data, start + offset);
    }

    struct archive::impl final
    {
        std::unique_ptr<DXArchive> vendor;
        std::vector<entry> entries;
        // Parallel to ``entries``: the validated opaque handle used to stream one body.
        std::vector<DXArchive::EntryRecord> records;
        // The container revision the DX8 header records (8). Kept apart from a member crypt version.
        std::uint32_t container_version = 0;
        // Immutable operation-local contexts borrowed by every per-operation Source callback. Only
        // the one matching the selected kernel is ever set.
        std::unique_ptr<modern_crypto> modern;
        std::unique_ptr<codec::wolf_chacha_context> chacha;
        // Set only when the archive is backed by the legacy DX1..DX6 engine: the two DX8 readers
        // share the public value contract, and exactly one backend is ever live for an archive.
        std::unique_ptr<legacy_archive> legacy;
        // Set only when the archive is a single-file WolfX stream.
        std::unique_ptr<wolfx_archive> wolfx;
    };

    archive::archive(passkey)
    {
    }

    archive::~archive() = default;
    archive::archive(archive &&) noexcept = default;
    archive &archive::operator=(archive &&) noexcept = default;

    std::unique_ptr<archive> archive::open(std::istream &stream, const std::filesystem::path &path,
                                           const std::string_view password,
                                           const game_context_resolver &resolve_context)
    {
        const std::uint64_t physical = checked_source_size(stream);

        // The WolfX magic is checked first: a WolfX stream is a different container, not a DX8 one.
        if (physical >= wolfx_magic.size()) {
            const std::vector<unsigned char> magic = read_prefix(stream, wolfx_magic.size());
            if (magic.size() == wolfx_magic.size() && std::equal(magic.begin(), magic.end(), wolfx_magic.begin())) {
                std::unique_ptr<wolfx_archive> wolfx = open_wolfx_source(stream, path, password, resolve_context);
                auto reader = std::make_unique<archive>(passkey{});
                reader->impl_ = std::make_unique<impl>();
                reader->impl_->wolfx = std::move(wolfx);
                return reader;
            }
        }

        auto built = std::make_unique<impl>();
        dx_selection selection;
        bool dx_opened = false;

        // Only a source large enough to hold a DX8 header can be probed as one; a short but valid
        // legacy container is handed to the fallback untouched.
        const std::vector<unsigned char> header =
            physical >= sizeof(DARC_HEAD) ? read_prefix(stream, sizeof(DARC_HEAD)) : std::vector<unsigned char>{};

        if (header.size() == sizeof(DARC_HEAD)) {
            const std::uint16_t crypt_version = WolfDec::CryptVersionFromHeader(header.data(), header.size());
            const CryptVersionDecision decision = WolfDec::ClassifyCryptVersion(crypt_version);
            const std::span<const std::uint8_t, 64> header_span(header.data(), sizeof(DARC_HEAD));
            const crypt_kernel kernel = kernel_for(crypt_version);

            if (decision.classification == CryptVersionClass::ProRecovery ||
                decision.classification == CryptVersionClass::Cc2Recovery) {
                // A caller-provided Pro password/raw key takes precedence over filesystem recovery.
                // CC2 accepts the explicit 64-byte key/nonce image used by the recovered-key path.
                if (!password.empty()) {
                    if (kernel == crypt_kernel::chacha) {
                        if (password.size() == codec::wolf_chacha_context::key_blob_size) {
                            const auto blob = std::span<const std::byte>(
                                reinterpret_cast<const std::byte *>(password.data()), password.size());
                            dx_opened = open_dx_candidate(stream, header_span, physical, kernel, {}, blob, selection);
                        }
                    } else {
                        dx_opened = open_dx_candidate(stream, header_span, physical, kernel, password, {}, selection);
                    }
                }
                if (!dx_opened) {
                    dx_opened = open_pro_candidate(stream, path, header_span, physical, crypt_version, selection,
                                                   resolve_context);
                }
                if (!dx_opened) {
                    throw key_required();
                }
            } else if (!password.empty() && kernel != crypt_kernel::chacha) {
                // An explicit key drives the kernel the header names. A ChaCha2 default container
                // keeps its pinned table key/nonce and ignores the caller's string, so it takes the
                // default selection below.
                dx_opened = open_dx_candidate(stream, header_span, physical, kernel, password, {}, selection);
            } else {
                // No usable explicit key: reuse the pinned WolfDec policy to pick the builtin default.
                const CryptModes additional_modes{};
                const ModeAttempt attempt = [&additional_modes, &stream, &header_span, physical,
                                             &selection](const std::uint32_t mode) {
                    return attempt_default_mode(stream, header_span, physical, additional_modes, mode, selection);
                };
                std::uint32_t selected_mode = 0;
                // The two recovery classes were handled above, so the classification here is the
                // versionless trial or the exact known version.
                if (decision.classification == CryptVersionClass::Trial) {
                    // A versionless header: the shared driver walks the default order and stops at
                    // the first entry that opens the container.
                    dx_opened = WolfDec::SelectWorkingMode(additional_modes, undetected_mode, selected_mode, attempt);
                } else {
                    // A known crypt version: the shared ordered lookup names the one candidate, and
                    // its failure is final rather than another trial.
                    dx_opened = WolfDec::SelectMode(additional_modes, decision.cryptVersion, selected_mode) &&
                                attempt(selected_mode);
                }
            }
        }

        if (!dx_opened) {
            // No DX8-family entry opened this source, so it is not a DX8 container. The legacy
            // DX1..DX6 engine gets the same key semantics the caller supplied: an explicit password
            // is forwarded unchanged and an empty one selects the pinned default DX1..DX6 keys. A
            // legacy rejection is a plain read_error, so a genuinely malformed source still fails
            // the open exactly as before.
            std::unique_ptr<legacy_archive> legacy = legacy_archive::open(stream, path, password);
            auto reader = std::make_unique<archive>(passkey{});
            reader->impl_ = std::make_unique<impl>();
            reader->impl_->legacy = std::move(legacy);
            return reader;
        }

        built->vendor = std::move(selection.vendor);
        built->modern = std::move(selection.modern);
        built->chacha = std::move(selection.chacha);

        const DARC_HEAD *head = built->vendor->GetHeader();
        built->container_version = head->Version;
        const std::uint32_t codepage = head->CharCodeFormat;

        // A fresh Source for the walk, borrowing the just-committed contexts.
        const DXArchive::Source source = make_source(stream, built->modern.get(), built->chacha.get());

        std::vector<std::string> directory_stack;
        const int walk = built->vendor->WalkEntries([&](const DXArchive::EntryRecord &record) -> int {
            // FileHead / Directory / Name borrow the vendor metadata and stay valid until the vendor
            // is destroyed; the '/' joined Path points into a transient buffer, so it is dropped.
            DXArchive::EntryRecord handle = record;
            handle.Path = nullptr;
            handle.PathBytes = 0;

            const std::span<const std::byte> encoded(reinterpret_cast<const std::byte *>(record.Name),
                                                     record.NameBytes);
            std::string component;
            try {
                component = text::convert(encoded, codepage);
            } catch (const text::encoding_error &) {
                return DXArchive::SOURCE_RESULT_FORMAT;
            }
            if (!is_safe_component(component)) {
                return DXArchive::SOURCE_RESULT_FORMAT;
            }

            // Pre-order depth-first: every ancestor directory already pushed its own component, so
            // the prefix is never shorter than Depth and resize() only trims trailing siblings.
            directory_stack.resize(record.Depth);
            const std::string sub_path = join_path(directory_stack, component);

            if (record.IsDirectory) {
                directory_stack.push_back(std::move(component));
                return 0;
            }

            DXArchive::BodyPlan plan{};
            // GetEntryPlan re-derives the body geometry through the same resolver the walk uses, and
            // that resolver queries the source's physical length again, so this query can fail even
            // though WalkEntries succeeded. It stays an explicit, fail-closed check.
            if (built->vendor->GetEntryPlan(handle, source, plan) != DXArchive::SOURCE_RESULT_OK) {
                throw read_error();
            }
            built->entries.push_back(entry{sub_path, checked_size(plan.Physical), checked_size(plan.OutputSize)});
            built->records.push_back(handle);
            return 0;
        });
        if (walk != DXArchive::SOURCE_RESULT_OK) {
            throw read_error();
        }

        auto reader = std::make_unique<archive>(passkey{});
        reader->impl_ = std::move(built);
        return reader;
    }

    std::span<const entry> archive::entries() const noexcept
    {
        if (impl_ == nullptr) {
            return {};
        }
        if (impl_->legacy != nullptr) {
            return impl_->legacy->entries();
        }
        if (impl_->wolfx != nullptr) {
            return impl_->wolfx->entries();
        }
        return std::span<const entry>(impl_->entries);
    }

    std::uint32_t archive::version() const noexcept
    {
        if (impl_ == nullptr) {
            return 0U;
        }
        if (impl_->legacy != nullptr) {
            return impl_->legacy->version();
        }
        if (impl_->wolfx != nullptr) {
            return impl_->wolfx->version();
        }
        return impl_->container_version;
    }

    container_backend archive::backend() const noexcept
    {
        if (impl_ != nullptr && impl_->wolfx != nullptr) {
            return container_backend::wolfx;
        }
        return container_backend::dx;
    }

    void archive::extract(const std::size_t index, std::istream &stream, std::ostream &output,
                          const std::function<void(std::int64_t)> &progress) const
    {
        if (impl_ == nullptr || index >= entries().size()) {
            throw std::out_of_range("wolfrpg entry index out of range");
        }
        if (impl_->legacy != nullptr) {
            impl_->legacy->extract(index, stream, output, progress);
            return;
        }
        if (impl_->wolfx != nullptr) {
            impl_->wolfx->extract(index, stream, output, progress);
            return;
        }

        const DXArchive::Source source = make_source(stream, impl_->modern.get(), impl_->chacha.get());

        // Every chunk is written once and reported once, split so no delta exceeds 32 KiB. The write
        // and the progress call sit outside any source translation, so a destination-stream failure
        // or a cancellation keeps its own identity.
        const std::function<int(const void *, size_t)> sink = [&output, &progress](const void *data,
                                                                                   const size_t size) -> int {
            const char *bytes = static_cast<const char *>(data);
            std::size_t remaining = size;
            while (remaining > 0) {
                const std::size_t chunk = std::min(remaining, progress_chunk_bytes);
                output.write(bytes, static_cast<std::streamsize>(chunk));
                bytes += chunk;
                remaining -= chunk;
                progress(static_cast<std::int64_t>(chunk));
            }
            return 0;
        };

        // The vendor's own progress callback reports a running total the delta accounting above
        // already covers exactly once, so it stays a no-op and nothing is counted twice.
        const int result = impl_->vendor->ExtractEntry(impl_->records[index], source, sink, [](u64) { return 0; });
        if (result != DXArchive::SOURCE_RESULT_OK) {
            throw read_error();
        }
    }
} // namespace unpackrat::wolfrpg
