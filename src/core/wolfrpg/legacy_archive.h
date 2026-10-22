#ifndef UNPACKRAT_CORE_WOLFRPG_LEGACY_ARCHIVE_H
#define UNPACKRAT_CORE_WOLFRPG_LEGACY_ARCHIVE_H

#include "archive.h"

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <istream>
#include <memory>
#include <ostream>
#include <span>
#include <string_view>

// Independent reader for the legacy DXLib / WolfRPG "DX1..DX6" container family, the format the
// classic DXLib archiver produced before the current DX8 container. It shares only the public
// value types (entry / read_error / key_required) with the DX8 reader and carries no Observer or
// Win32 dependency; the vendor cipher is reached through the private legacy_crypto adapter.
//
// The observable contract mirrors the DX8 reader:
//   * open() parses and validates the metadata from a random-access stream and never retains it;
//   * extract() re-reads one entry body from a caller-supplied stream;
//   * source-stream failures raise read_error while output-stream failures and any exception
//     thrown by the progress callback propagate unchanged.
namespace unpackrat::wolfrpg
{
    class legacy_archive final
    {
      public:
        // Construction is reserved for open(): an archive instance is only meaningful once its
        // metadata has been parsed and validated. The passkey keeps the constructor off the public
        // surface while still allowing std::make_unique from the static factory.
        struct passkey
        {
          private:
            passkey() = default;
            friend class legacy_archive;
        };

        explicit legacy_archive(passkey);
        ~legacy_archive();
        legacy_archive(legacy_archive &&) noexcept;
        legacy_archive &operator=(legacy_archive &&) noexcept;
        legacy_archive(const legacy_archive &) = delete;
        legacy_archive &operator=(const legacy_archive &) = delete;

        // Supports DX container versions 1 through 6. A non-empty ``password`` selects the caller
        // key; otherwise the pinned DX1..DX6 default keys are tried in order. An archive that no
        // candidate key decrypts into a structurally valid container raises read_error without
        // treating the bytes as a password prompt.
        static std::unique_ptr<legacy_archive> open(std::istream &stream, const std::filesystem::path &path,
                                                    std::string_view password = {});

        [[nodiscard]] std::span<const entry> entries() const noexcept;
        [[nodiscard]] std::uint32_t version() const noexcept;

        // Streams one entry body to ``output``. The progress callback receives byte deltas.
        void extract(std::size_t index, std::istream &stream, std::ostream &output,
                     const std::function<void(std::int64_t)> &progress) const;

      private:
        struct impl;
        std::unique_ptr<impl> impl_;
    };
} // namespace unpackrat::wolfrpg

#endif
