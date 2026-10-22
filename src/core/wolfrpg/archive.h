#ifndef UNPACKRAT_CORE_WOLFRPG_ARCHIVE_H
#define UNPACKRAT_CORE_WOLFRPG_ARCHIVE_H

#include "game_context.h"

#include <cstdint>
#include <filesystem>
#include <functional>
#include <istream>
#include <memory>
#include <ostream>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

// Classic Wolf RPG / DXLib "DX8" archive reader (the format produced by Wolf RPG v2.225 and
// later versions that keep the DX8 container: DARC_HEAD with a version-8 header).
//
// This reader owns no Observer or Win32 dependency and no container knowledge of its own: it is a
// thin adapter over the pinned DXLib/UberWolf ``DXArchive`` byte-source API (OpenArchiveSource /
// WalkEntries / ExtractEntry / GetEntryPlan), which is the single implementation of the DARC_HEAD
// validation, the depth-first table walk, the body geometry and the stored / LZ / Huffman codecs.
// The DXLib/UberWolf primitives and the vendor object stay confined to the implementation
// translation unit. Archive bytes are still untrusted: every encoded name the adapter converts is
// re-validated as a safe relative path component and every published size is a checked narrowing
// from a vendor geometry value.
//
// When the DX8 header does not validate, the same public surface falls back to the legacy DXLib
// "DX1..DX6" container family (src/core/wolfrpg/legacy_archive.*), so open() / entries() /
// version() / extract() describe and stream either family without a second public entry point. The
// published geometry is still the pinned vendor library's own values and the bytes remain untrusted
// in both families.
namespace unpackrat::wolfrpg
{
    class read_error final : public std::runtime_error
    {
      public:
        read_error() : std::runtime_error("WolfRPG archive read failed")
        {
        }
    };

    // Raised when the archive requires a non-default key that the caller did not supply.
    class key_required final : public std::runtime_error
    {
      public:
        key_required() : std::runtime_error("WolfRPG archive requires a key")
        {
        }
    };

    struct entry final
    {
        std::string path;
        std::int64_t stored_size = 0;
        std::int64_t unpacked_size = 0;
    };

    // Which container family actually owns the opened archive. The DX8 / modern / Pro backends all
    // publish the DX8 container revision 8, while a WolfX stream publishes revision 1; the two
    // collided under a bare version() read, so the family travels with the archive explicitly and
    // version() stays the container revision from the format itself.
    enum class container_backend : std::uint8_t
    {
        dx,    // the DXLib DX1..DX8 container family (classic, modern and Pro/CC2 forms)
        wolfx, // the single-file WolfX stream (revision 1)
    };

    using game_context_resolver = std::function<game_context_result(const std::filesystem::path &)>;

    class archive final
    {
      public:
        // Construction tag. std::make_unique cannot name a private constructor, so the reader
        // exposes a tag whose constructor only archive itself may call: the public surface still
        // has no way to build an archive without parsing one.
        struct passkey
        {
          private:
            friend class archive;
            passkey() = default;
        };

        explicit archive(passkey);
        ~archive();
        archive(archive &&) noexcept;
        archive &operator=(archive &&) noexcept;
        archive(const archive &) = delete;
        archive &operator=(const archive &) = delete;

        // Parses and validates the archive metadata from a random-access input stream. The stream is
        // left untouched between calls; extract() re-reads entry bodies from the supplied stream.
        // An explicit Pro key is tried before companion recovery. CC2's explicit key is the full
        // 64-byte binary key/nonce image; other CC2 password lengths require companion recovery.
        static std::unique_ptr<archive> open(std::istream &stream, const std::filesystem::path &path,
                                             std::string_view password = {},
                                             const game_context_resolver &resolve_context = resolve_from_archive);

        [[nodiscard]] std::span<const entry> entries() const noexcept;
        // The container revision the format itself records: DARC_HEAD Version for the DX1..DX8
        // family, the fixed WolfX revision (1) for a WolfX stream. Never the member crypt version.
        [[nodiscard]] std::uint32_t version() const noexcept;
        // The owning family, so a caller can tell a WolfX revision-1 stream from a DX1 container.
        [[nodiscard]] container_backend backend() const noexcept;

        // Streams one entry body to ``output``. Source-stream failures raise read_error; output
        // stream failures and any exception thrown by ``progress`` propagate unchanged. Progress
        // is reported as per-write signed deltas of at most 32 KiB that sum to the entry size.
        void extract(std::size_t index, std::istream &stream, std::ostream &output,
                     const std::function<void(std::int64_t)> &progress) const;

      private:
        struct impl;
        std::unique_ptr<impl> impl_;
    };
} // namespace unpackrat::wolfrpg
#endif
