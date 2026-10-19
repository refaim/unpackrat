#include "legacy_archive.h"

#include "legacy_crypto.h"
#include "legacy_stream_source.h"
#include "lz_reader.h"
#include "text_encoding.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <ios>
#include <memory>
#include <new>
#include <stdexcept>
#include <streambuf>
#include <string>
#include <utility>
#include <vector>

// The pinned vendor headers are C-style and warn freely under /W4; keep them quiet without
// relaxing warnings on our own translation unit. The shared DX1..DX6 kernel lives behind them, so
// this adapter parses no container layout of its own: it drives dxarc_legacy and only owns the
// stream bridge and the public value mapping. Vendor types never cross this unit's boundary.
#pragma warning(push, 0)
#include <uberwolf/DXLib/DXArchiveVer5.h>
#pragma warning(pop)

namespace unpackrat::wolfrpg
{
    namespace detail
    {
        int read_legacy_source(legacy_stream_source &source, const std::int64_t offset, void *dest, const int size)
        {
            if (size < 0 || offset < 0) {
                return -1;
            }
            std::streambuf &buffer = source.buffer;
            const auto position = static_cast<std::streamoff>(offset);
            const std::streampos failure(std::streamoff(-1));
            try {
                const std::streampos via_off = buffer.pubseekoff(position, std::ios::beg, std::ios::in);
                bool positioned = via_off != failure && static_cast<std::streamoff>(via_off) == position;
                if (via_off != failure && !positioned) {
                    source.source_failed = true;
                    return -1;
                }
                if (!positioned) {
                    const std::streampos via_pos = buffer.pubseekpos(std::streampos(position), std::ios::in);
                    positioned = via_pos != failure && static_cast<std::streamoff>(via_pos) == position;
                }
                if (!positioned) {
                    source.source_failed = true;
                    return -1;
                }
                const auto want = static_cast<std::streamsize>(size);
                if (buffer.sgetn(static_cast<char *>(dest), want) != want) {
                    source.source_failed = true;
                    return -1;
                }
                return 0;
            } catch (const std::bad_alloc &) {
                throw;
            } catch (const std::length_error &) {
                throw;
            } catch (...) {
                // Key mismatches may be retried; a source failure may not. Keep that distinction
                // across the vendor callback result without masking caller resource exceptions.
                source.source_failed = true;
                return -1;
            }
        }

    } // namespace detail

    namespace
    {
        constexpr std::size_t stream_chunk = 32 * 1024;
        constexpr std::uint64_t press32_sentinel = 0xffffffffULL;
        constexpr std::uint64_t press64_sentinel = 0xffffffffffffffffULL;
        constexpr std::uint32_t default_code_page = 932;

        // The source side is touched only through the caller's streambuf, never through the
        // formatted istream operations: the caller owns the stream's exception mask, so an ordinary
        // source fault must come back as read_error while an allocation or length failure is
        // re-raised with its own identity. pubseekoff has a pubseekpos fallback for sources that
        // expose only absolute positioning, every negative position is rejected outright, and a
        // seek that lands anywhere other than the requested offset is treated as a failure.
        using stream_bridge = detail::legacy_stream_source;

        // KernelSource calls synchronously with the address of our live, required-reference bridge.
        int stream_read(void *user, const std::int64_t offset, void *dest, const int size)
        {
            return detail::read_legacy_source(*static_cast<stream_bridge *>(user), offset, dest, size);
        }

        std::int64_t stream_size(std::streambuf &buffer)
        {
            try {
                const std::streampos end = buffer.pubseekoff(0, std::ios::end, std::ios::in);
                if (end == std::streampos(std::streamoff(-1))) {
                    return -1;
                }
                return static_cast<std::int64_t>(static_cast<std::streamoff>(end));
            } catch (const std::bad_alloc &) {
                throw;
            } catch (const std::length_error &) {
                throw;
            } catch (...) {
                return -1;
            }
        }

        struct body_info final
        {
            std::uint64_t offset = 0;     // absolute physical offset of the stored bytes
            std::uint64_t stored = 0;     // stored byte count
            std::uint64_t unpacked = 0;   // declared unpacked size
            std::uint64_t phase_base = 0; // key-XOR base phase for the stored bytes
            bool compressed = false;
        };

        // Decodes a validated raw path into UTF-8. The stored components are separated by '/', and
        // each component is decoded on its own before the parts are rejoined, so a multi-byte trail
        // byte such as 0x5C inside an encoded character is never mistaken for a separator.
        std::string decode_path(const unsigned char *raw, const int length, const std::uint32_t code_page)
        {
            const std::uint32_t page = code_page == 0 ? default_code_page : code_page;
            std::string path;
            std::size_t start = 0;
            const auto total = static_cast<std::size_t>(length);
            for (std::size_t index = 0; index <= total; ++index) {
                if (index != total && raw[index] != static_cast<unsigned char>('/')) {
                    continue;
                }
                const std::span<const std::byte> component(reinterpret_cast<const std::byte *>(raw + start),
                                                           index - start);
                if (!path.empty()) {
                    path.push_back('/');
                }
                path += text::convert(component, page);
                start = index + 1;
            }
            return path;
        }

        struct collect_state final
        {
            std::uint32_t version = 0;
            std::uint32_t code_page = 0;
            std::uint64_t data_start = 0;
            std::vector<entry> entries;
            std::vector<body_info> bodies;
        };

        // KernelList emits only files with validated, nonempty path components; directories are
        // traversed internally and its callback's directory argument is always zero.
        int collect_record(void *user, const unsigned char *raw_path, const int raw_length, const int /*depth*/,
                           const int /*is_directory*/, const unsigned int /*attributes*/,
                           const std::int64_t data_address, const std::int64_t data_size, const std::int64_t press_size)
        {
            auto *const state = static_cast<collect_state *>(user);
            const bool compressed =
                state->version == 6
                    ? press_size != static_cast<std::int64_t>(press64_sentinel)
                    : (state->version >= 2 ? press_size != static_cast<std::int64_t>(press32_sentinel) : false);
            const std::uint64_t stored =
                compressed ? static_cast<std::uint64_t>(press_size) : static_cast<std::uint64_t>(data_size);
            const std::uint64_t offset = state->data_start + static_cast<std::uint64_t>(data_address);
            const std::uint64_t phase = state->version >= 5 ? static_cast<std::uint64_t>(data_size) : offset;

            state->entries.push_back(entry{decode_path(raw_path, raw_length, state->code_page),
                                           static_cast<std::int64_t>(stored), data_size});
            state->bodies.push_back(
                body_info{offset, stored, static_cast<std::uint64_t>(data_size), phase, compressed});
            return 0;
        }

        std::vector<legacy::key> key_candidates(const std::string_view password)
        {
            std::vector<legacy::key> candidates;
            if (!password.empty()) {
                candidates.push_back(legacy::derive_key(password));
                return candidates;
            }
            candidates.push_back(legacy::derive_key(std::string_view{}));
            for (const std::string &source : legacy::default_mode_keys()) {
                candidates.push_back(legacy::derive_key(source));
            }
            candidates.push_back(legacy::raw_fallback_key());
            return candidates;
        }
    } // namespace

    struct legacy_archive::impl final
    {
        std::uint32_t version = 0;
        std::vector<entry> entries;
        std::vector<body_info> bodies;
        std::array<unsigned char, 12> secret{};
    };

    legacy_archive::legacy_archive(passkey) : impl_(std::make_unique<impl>())
    {
    }

    legacy_archive::~legacy_archive() = default;
    legacy_archive::legacy_archive(legacy_archive &&) noexcept = default;
    legacy_archive &legacy_archive::operator=(legacy_archive &&) noexcept = default;

    std::unique_ptr<legacy_archive> legacy_archive::open(std::istream &stream, const std::filesystem::path & /*path*/,
                                                         const std::string_view password)
    {
        std::streambuf *const buffer = stream.rdbuf();
        if (buffer == nullptr) {
            throw read_error();
        }
        const std::int64_t size = stream_size(*buffer);
        if (size < 0) {
            throw read_error();
        }

        stream_bridge bridge{*buffer};
        dxarc_legacy::KernelSource source{&bridge, size, &stream_read};
        dxarc_legacy::KernelKey operations{&DXArchive_VER5::KeyCreate, &DXArchive_VER5::KeyConv};

        for (legacy::key &candidate : key_candidates(password)) {
            dxarc_legacy::KernelHead head{};
            const int header_result = dxarc_legacy::KernelHeaderKey(&source, &operations, candidate.data(), &head);
            if (bridge.source_failed) {
                throw read_error();
            }
            if (header_result != 0) {
                continue;
            }

            std::vector<std::byte> metadata(head.HeadSize);
            const int metadata_result = dxarc_legacy::KernelMetadata(
                &source, &operations, &head, reinterpret_cast<unsigned char *>(metadata.data()));
            // Header validation established the metadata region and key. KernelMetadata now
            // reports only a source-read failure, so another password cannot repair this result.
            if (metadata_result != 0) {
                throw read_error();
            }

            collect_state state;
            state.version = head.Version;
            state.code_page = head.CodePage;
            state.data_start = static_cast<std::uint64_t>(head.DataStart);
            dxarc_legacy::KernelSink lister{&state, &collect_record};
            if (dxarc_legacy::KernelList(&head, reinterpret_cast<const unsigned char *>(metadata.data()), &lister) !=
                0) {
                continue;
            }

            auto reader = std::make_unique<legacy_archive>(passkey{});
            reader->impl_->version = state.version;
            reader->impl_->entries = std::move(state.entries);
            reader->impl_->bodies = std::move(state.bodies);
            for (std::size_t index = 0; index < reader->impl_->secret.size(); ++index) {
                reader->impl_->secret[index] = head.Key[index];
            }
            return reader;
        }

        throw read_error();
    }

    std::span<const entry> legacy_archive::entries() const noexcept
    {
        return impl_->entries;
    }

    std::uint32_t legacy_archive::version() const noexcept
    {
        return impl_->version;
    }

    void legacy_archive::extract(const std::size_t index, std::istream &stream, std::ostream &output,
                                 const std::function<void(std::int64_t)> &progress) const
    {
        if (index >= impl_->bodies.size()) {
            throw read_error();
        }
        std::streambuf *const buffer = stream.rdbuf();
        if (buffer == nullptr) {
            throw read_error();
        }

        stream_bridge bridge{*buffer};
        dxarc_legacy::KernelSource source{&bridge, stream_size(*buffer), &stream_read};
        dxarc_legacy::KernelKey operations{&DXArchive_VER5::KeyCreate, &DXArchive_VER5::KeyConv};

        const body_info &body = impl_->bodies[index];
        if (body.stored == 0 && !body.compressed) {
            return;
        }

        std::array<std::byte, stream_chunk> chunk{};
        if (!body.compressed) {
            std::int64_t cursor = 0;
            while (static_cast<std::uint64_t>(cursor) < body.stored) {
                const int want = static_cast<int>(std::min<std::uint64_t>(body.stored - cursor, stream_chunk));
                const int got = dxarc_legacy::KernelReadStored(
                    &source, &operations, impl_->secret.data(), static_cast<std::int64_t>(body.phase_base),
                    static_cast<std::int64_t>(body.offset), static_cast<std::int64_t>(body.stored), &cursor,
                    reinterpret_cast<unsigned char *>(chunk.data()), want);
                if (got <= 0) {
                    throw read_error();
                }
                output.write(reinterpret_cast<const char *>(chunk.data()), static_cast<std::streamsize>(got));
                progress(static_cast<std::int64_t>(got));
            }
            return;
        }

        std::int64_t cursor = 0;
        const codec::lz_reader::source pull = [&](std::span<std::byte> destination) -> std::size_t {
            // The kernel clamps the physical extent and handles zero-byte requests. Bound only
            // our destination before narrowing its request to the kernel's int-sized count.
            const std::size_t want = std::min(stream_chunk, destination.size());
            const int got = dxarc_legacy::KernelReadStored(
                &source, &operations, impl_->secret.data(), static_cast<std::int64_t>(body.phase_base),
                static_cast<std::int64_t>(body.offset), static_cast<std::int64_t>(body.stored), &cursor,
                reinterpret_cast<unsigned char *>(destination.data()), static_cast<int>(want));
            if (got < 0) {
                throw read_error();
            }
            return static_cast<std::size_t>(got);
        };

        const auto make_reader = [&]() -> codec::lz_reader {
            try {
                return codec::lz_reader(pull, body.stored, body.unpacked);
            } catch (const codec::lz_error &) {
                throw read_error();
            }
        };
        codec::lz_reader reader = make_reader();
        std::uint64_t produced = 0;
        while (produced < body.unpacked) {
            std::size_t got = 0;
            try {
                got = reader.read(chunk);
            } catch (const codec::lz_error &) {
                throw read_error();
            }
            output.write(reinterpret_cast<const char *>(chunk.data()), static_cast<std::streamsize>(got));
            produced += got;
            progress(static_cast<std::int64_t>(got));
        }
    }
} // namespace unpackrat::wolfrpg
