#include <algorithm>
#include <cstdint>
#include <limits>
#include <string>

#include <catch2/catch_test_macros.hpp>

#include <uberwolf/WolfX/Types.hpp>

namespace
{
    // One byte past the 32-bit range, so a 32-bit build that stores this size in a
    // std::size_t field cannot represent it and must fail to compile rather than truncate.
    constexpr std::uintmax_t oversized_file_size() noexcept
    {
        return static_cast<std::uintmax_t>(std::numeric_limits<std::uint32_t>::max()) + 1;
    }

    // The same ascending-by-stored-size order collectWolfXFiles applies before cracking.
    void sort_by_file_size(wolfx::WolfXFiles& files)
    {
        std::sort(files.begin(), files.end(), [](const wolfx::WolfXFile& a, const wolfx::WolfXFile& b) {
            return a.fileSize < b.fileSize;
        });
    }
}

TEST_CASE("WolfXFile stores a file size larger than 32 bits", "[uberwolf][wolfx]")
{
    const wolfx::WolfXFile file{std::wstring(L"large.wolfx"), oversized_file_size()};

    CHECK(file.fileSize == oversized_file_size());
}

TEST_CASE("WolfXFile entries order by their full file size", "[uberwolf][wolfx]")
{
    wolfx::WolfXFiles files{
        {std::wstring(L"middle.wolfx"), 4096},
        {std::wstring(L"large.wolfx"), oversized_file_size()},
        {std::wstring(L"small.wolfx"), 1},
    };

    sort_by_file_size(files);

    CHECK(files.at(0).fileSize == 1);
    CHECK(files.at(1).fileSize == 4096);
    CHECK(files.at(2).fileSize == oversized_file_size());
}
