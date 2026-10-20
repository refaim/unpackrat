#ifndef UNPACKRAT_TESTS_FRAMEWORK_OBSERVER_H
#define UNPACKRAT_TESTS_FRAMEWORK_OBSERVER_H

#include <cstdint>
#include <filesystem>
#include <functional>
#include <memory>
#include <string>
#include <system_error>
#include <utility>
#include <vector>

namespace test
{
    struct file
    {
        std::wstring path;
        const int64_t uncompressed_size = 0;
        const int64_t compressed_size = 0;
        const std::function<void(const std::filesystem::path &)> extract;
    };

    class module
    {
      public:
        module() {};

        virtual ~module() = default;
    };

    // Owns a destination path for the duration of a test and removes it on every exit path,
    // including a failing assertion. Construct it before extracting so a stale file from an
    // earlier run cannot influence the next assertion.
    class temporary_output_file final
    {
      public:
        explicit temporary_output_file(std::filesystem::path path) : path_(std::move(path))
        {
            std::error_code error;
            std::filesystem::remove(path_, error);
        }

        ~temporary_output_file()
        {
            std::error_code error;
            std::filesystem::remove(path_, error);
        }

        temporary_output_file(const temporary_output_file &) = delete;
        temporary_output_file &operator=(const temporary_output_file &) = delete;

        [[nodiscard]] const std::filesystem::path &path() const noexcept
        {
            return path_;
        }

      private:
        std::filesystem::path path_;
    };

    class observer final
    {
      public:
        observer();

        std::vector<file> list_files(const std::filesystem::path &path) const;

      private:
        std::vector<std::unique_ptr<module>> modules_;
    };
} // namespace test
#endif
