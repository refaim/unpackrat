#ifndef UNPACKRAT_MODULES_EXTRACTOR_H
#define UNPACKRAT_MODULES_EXTRACTOR_H

#include <filesystem>
#include <functional>
#include <istream>
#include <memory>
#include <optional>
#include <ostream>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace extractor
{
    typedef struct
    {
        uint32_t Data1;
        uint16_t Data2;
        uint16_t Data3;
        uint8_t Data4[8];
    } guid;

    typedef struct
    {
        guid id;
        uint32_t major_version;
        uint32_t minor_version;
    } version_info;

    version_info get_version_info() noexcept;
    void configure(const wchar_t *settings) noexcept;
    void reset_settings() noexcept;

    class read_error final : public std::runtime_error
    {
      public:
        read_error() : runtime_error("")
        {
        }
    };

    class password_required final : public std::runtime_error
    {
      public:
        password_required() : runtime_error("Godot encryption key required")
        {
        }
    };

    // Borrowed metadata remains valid while the owning catalogue is alive and unchanged.
    // Sizes already include any format-specific header; paths are normalized UTF-8.
    struct file_metadata final
    {
        std::string_view path;
        int64_t size = 0;
        int64_t packed_size = 0;
    };

    // Optional owner-dispatched route for compact indexes. Legacy extractors keep their
    // existing file objects and raw-body/header/custom-handler behavior unchanged.
    class indexed_catalogue
    {
      public:
        virtual ~indexed_catalogue() = default;
        virtual void prepare() = 0;
        [[nodiscard]] virtual size_t count() const noexcept = 0;
        // Retained metadata for providers that keep their paths alive for the whole
        // catalogue lifetime. A lazy provider that cannot retain every final path overrides
        // path_into() instead; this default is a hard error so a lazy catalogue can never
        // silently hand back a wrong, empty or dangling path.
        [[nodiscard]] virtual file_metadata metadata(size_t index) const
        {
            static_cast<void>(index);
            throw read_error();
        }
        // Lazy route for a compact index that keeps only a sparse conflict set: materialise
        // entry `index` into the caller-owned `buffer` and return the byte count plus both
        // sizes. The written path borrows `buffer` for the duration of the call only, so no
        // catalogue storage and no shared scratch is exposed. Returning nullopt asks the
        // caller to fall back to the retained metadata() route.
        struct column final
        {
            std::size_t written = 0;
            int64_t size = 0;
            int64_t packed_size = 0;
        };
        [[nodiscard]] virtual std::optional<column> path_into(size_t index, std::span<char> buffer) const
        {
            static_cast<void>(index);
            static_cast<void>(buffer);
            return std::nullopt;
        }
        // Optional wide route for a caller-owned path buffer. The implementation writes at
        // most buffer.size() wchar_t units and returns their count in `written` (wchar_t units,
        // not bytes); it never includes a terminator. The path is written only into the
        // caller-owned buffer and is borrowed for this call only.
        // An empty or undersized buffer throws read_error, and an out-of-range index throws
        // std::out_of_range. Returning nullopt asks the caller to use path_into() instead, so
        // the default leaves legacy and UTF-8-only providers unchanged.
        [[nodiscard]] virtual std::optional<column> wide_path_into(size_t index, std::span<wchar_t> buffer) const
        {
            static_cast<void>(index);
            static_cast<void>(buffer);
            return std::nullopt;
        }
        virtual void extract(size_t index, std::ostream &output,
                             const std::function<void(int64_t)> &report_progress) const = 0;
    };

    // An extractor owns its archive-specific lifetime state. Entry callbacks may borrow this
    // state because archive destroys its file list before releasing the extractor.
    struct storage_state
    {
        virtual ~storage_state() = default;
        [[nodiscard]] virtual indexed_catalogue *indexed_files() noexcept { return nullptr; }
    };

    struct archive_info final
    {
        std::wstring format;
        std::wstring compression;
        std::wstring comment;
    };

    struct file final
    {
        std::string path;
        std::string header;
        int64_t offset = 0;
        int64_t compressed_body_size_in_bytes = 0;
        int64_t uncompressed_body_size_in_bytes = 0;
        uint32_t magic = 0;
        // Optional custom extraction. When empty the archive performs the default
        // header copy followed by the decrypted raw body copy. When set, the handler
        // receives the open archive input stream, the destination output stream and the
        // progress callback, and is responsible for the whole entry body instead.
        //
        // Handler contract:
        //   * The source stream has failbit and badbit exceptions enabled, so a failed
        //     seek or read surfaces as std::ios_base::failure. The handler must translate
        //     those source stream IO failures into extractor::read_error; the archive
        //     maps extractor::read_error to archive::read_error.
        //   * Output stream IO failures surface as std::ios_base::failure. The handler
        //     must let them escape unchanged; the archive maps any escaping
        //     std::ios_base::failure, including a failed output close, to
        //     archive::write_error.
        //   * Any other exception escapes unchanged after the partial destination is
        //     removed.
        std::function<void(std::istream &, std::ostream &, const std::function<void(int64_t)> &)> extract;
    };

    class extractor
    {
      public:
        virtual ~extractor() = default;

        static std::vector<std::byte> get_signature();

        archive_info get_archive_info(std::istream &stream, const std::span<const std::byte> &data,
                                      const std::filesystem::path &path = {}, std::string_view password = {});

        // archive::prepare_files calls this only after get_archive_info has completed
        // successfully for the current source. Per-archive state is therefore admitted.
        std::vector<std::unique_ptr<file>> list_files(std::istream &stream);

        uint32_t decrypt(uint32_t magic, std::vector<char> &data) const;

        // Non-owning capability lookup: state_ owns the provider and archive keeps this
        // extractor alive through every metadata/export call, including a failed prepare.
        [[nodiscard]] indexed_catalogue *indexed_files() const noexcept
        {
            return state_ ? state_->indexed_files() : nullptr;
        }

        void reset_storage() noexcept
        {
            state_.reset();
        }

      protected:
        std::unique_ptr<storage_state> state_;
    };
} // namespace extractor
#endif
