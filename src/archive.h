#ifndef UNPACKRAT_ARCHIVE_H
#define UNPACKRAT_ARCHIVE_H

#include "modules/extractor.h"

#include <filesystem>
#include <fstream>
#include <functional>
#include <span>

namespace archive
{
    class read_error final : public std::runtime_error
    {
      public:
        read_error() : runtime_error("")
        {
        }
    };

    class write_error final : public std::runtime_error
    {
      public:
        write_error() : runtime_error("")
        {
        }
    };

    class user_interrupt final : public std::runtime_error
    {
      public:
        user_interrupt() : runtime_error("")
        {
        }
    };

    class archive final
    {
      public:
        explicit archive(std::unique_ptr<extractor::extractor> extractor);

        extractor::archive_info open(const std::filesystem::path &path, const std::span<const std::byte> &data,
                                     std::string_view password = {});

        void prepare_files();

        [[nodiscard]] const extractor::file &get_file(size_t index) const;
        [[nodiscard]] extractor::file_metadata get_metadata(size_t index) const;
        // Materialises entry metadata into a caller-owned buffer so a lazy catalogue never
        // exposes its own storage. The returned path borrows `buffer` for the call only.
        void get_metadata_into(size_t index, std::span<char> buffer, extractor::file_metadata &out) const;
        // Optional caller-owned WCHAR route. Returns nullopt when the provider has no wide
        // override, so callers can retain the existing UTF-8 route unchanged.
        [[nodiscard]] std::optional<extractor::indexed_catalogue::column> get_wide_metadata_into(
            size_t index, std::span<wchar_t> buffer) const;

        void extract_file(size_t index, const std::filesystem::path &path,
                          const std::function<void(int64_t)> &report_progress) const;

      private:
        std::unique_ptr<extractor::extractor> extractor_;
        std::unique_ptr<std::ifstream> stream_;
        std::vector<std::unique_ptr<extractor::file>> files_;
        bool opened_ = false;
    };
} // namespace archive
#endif
