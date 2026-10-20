#include "../../core/wolfrpg/legacy_archive.h"
#include "../../core/wolfrpg/legacy_crypto.h"
#include "../support/wolfrpg_default_mode_fixtures.h"
#include <algorithm>
#include <array>
#include <catch2/catch_test_macros.hpp>
#include <cstring>
#include <sstream>
#include <string>
#include <uberwolf/DXLib/DXArchiveVer5.h>
#include <vector>
namespace
{
    namespace wolf = unpackrat::wolfrpg;
    std::vector<std::byte> renamed_fixture(const std::string &name, unsigned page)
    {
        auto bytes = test::support::wolfrpg_default_mode::load_bytes("default-mode-0.wolf");
        std::array<unsigned char, 12> key{};
        const auto password = wolf::legacy::default_mode_keys().front();
        DXArchive_VER5::KeyCreate(password.c_str(), key.data());
        DARC_HEAD_VER5 header{};
        std::memcpy(&header, bytes.data(), sizeof(header));
        DXArchive_VER5::KeyConv(&header, sizeof(header), 0, key.data());
        REQUIRE(header.Version == 5);
        REQUIRE(name.size() < 12u);
        const auto at = header.FileNameTableStartAddress;
        REQUIRE(at + header.HeadSize <= bytes.size());
        std::vector<std::byte> metadata(bytes.begin() + static_cast<std::ptrdiff_t>(at),
                                        bytes.begin() + static_cast<std::ptrdiff_t>(at) +
                                            static_cast<std::ptrdiff_t>(header.HeadSize));
        DXArchive_VER5::KeyConv(metadata.data(), static_cast<int>(metadata.size()), 0, key.data());
        const std::string original = "hello.txt";
        auto found =
            std::search(reinterpret_cast<char *>(metadata.data()),
                        reinterpret_cast<char *>(metadata.data() + metadata.size()), original.begin(), original.end());
        REQUIRE(found != reinterpret_cast<char *>(metadata.data() + metadata.size()));
        const auto index = found - reinterpret_cast<char *>(metadata.data());
        REQUIRE(index >= 16);
        REQUIRE(static_cast<std::size_t>(index) + 12 <= metadata.size());
        for (const auto offset : {index, index - 12}) {
            std::fill_n(reinterpret_cast<char *>(metadata.data()) + offset, 12, '\0');
            std::copy(name.begin(), name.end(), reinterpret_cast<char *>(metadata.data()) + offset);
        }
        std::uint16_t parity = 0;
        for (const unsigned char c : name)
            parity = static_cast<std::uint16_t>(parity + c);
        std::memcpy(metadata.data() + index - 14, &parity, sizeof(parity));
        header.CodePage = page;
        DXArchive_VER5::KeyConv(&header, sizeof(header), 0, key.data());
        std::memcpy(bytes.data(), &header, sizeof(header));
        DXArchive_VER5::KeyConv(metadata.data(), static_cast<int>(metadata.size()), 0, key.data());
        std::copy(metadata.begin(), metadata.end(), bytes.begin() + static_cast<std::ptrdiff_t>(at));
        return bytes;
    }
    std::istringstream input(const std::vector<std::byte> &bytes)
    {
        return std::istringstream(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()),
                                  std::ios::binary);
    }
} // namespace
TEST_CASE("legacy CP932 trail backslash lists and extracts the UTF8 Japanese name", "[vendor-contract]")
{
    for (const unsigned page : {932U, 0U}) {
        auto source = input(renamed_fixture(std::string("\x95\x5c.dat", 6), page));
        auto reader = wolf::legacy_archive::open(source, "owned.wolf");
        const auto entries = reader->entries();
        const auto found =
            std::find_if(entries.begin(), entries.end(), [](const auto &e) { return e.path == "\xe8\xa1\xa8.dat"; });
        REQUIRE(found != entries.end());
        std::ostringstream output;
        reader->extract(static_cast<std::size_t>(found - entries.begin()), source, output, [](std::int64_t) {});
        REQUIRE(output.str() == "Owned WolfRPG fixture payload\n");
    }
}

TEST_CASE("legacy name policy distinguishes decoded DBCS characters from forbidden names", "[vendor-contract]")
{
    struct row
    {
        std::string name;
        unsigned page;
        bool allowed;
    };
    const std::vector<row> cases{{"", 932, false},
                                 {".", 932, false},
                                 {"..", 932, false},
                                 {".x", 932, true},
                                 {"x.", 932, true},
                                 {"a", 932, true},
                                 {"a\\b", 932, false},
                                 {"a/b", 932, false},
                                 {"a:b", 932, false},
                                 {std::string(1, '\x01'), 932, false},
                                 {std::string(1, '\x7f'), 932, false},
                                 {std::string(1, '\x95'), 932, false},
                                 {std::string("\x95\x20", 2), 932, false},
                                 {std::string("\x95\x5c", 2), 932, true},
                                 {std::string("\x95\x5c", 2), 0, true},
                                 {std::string("\x95\x5c", 2), 1251, false},
                                 {std::string("a\x95\x5c", 3), 932, true},
                                 {std::string("\x95\x5c", 2) + char(0x5c) + "x", 932, false}};
    for (const auto &item : cases) {
        REQUIRE((dxarc_legacy::KernelNameValid(reinterpret_cast<const unsigned char *>(item.name.data()),
                                               static_cast<int>(item.name.size()), item.page) != 0) == item.allowed);
    }
}
TEST_CASE("legacy archive rejection retains actual separators traversal and malformed pairs", "[vendor-contract]")
{
    for (const std::string &name : {std::string{}, std::string("."), std::string(".."), std::string("a\\b"),
                                    std::string("a/b"), std::string("a:b"), std::string(1, '\x01'),
                                    std::string(1, '\x7f'), std::string(1, '\x95'), std::string("\x95\x20", 2)}) {
        auto source = input(renamed_fixture(name, 932));
        bool rejected = false;
        try {
            auto reader = wolf::legacy_archive::open(source, "owned.wolf");
        } catch (const wolf::read_error &) {
            rejected = true;
        }
        REQUIRE(rejected);
    }
}
TEST_CASE("the encoding aware legacy kernel retains singlebyte and UTF8 names", "[vendor-contract]")
{
    struct row
    {
        std::string raw;
        unsigned page;
        std::string expected;
    };
    for (const auto &item : std::vector<row>{{"ascii.dat", 1252, "ascii.dat"},
                                             {"\xd2.dat", 1251, "\xd0\xa2.dat"},
                                             {"\xe8\xa1\xa8.dat", 65001, "\xe8\xa1\xa8.dat"}}) {
        auto source = input(renamed_fixture(item.raw, item.page));
        auto reader = wolf::legacy_archive::open(source, "owned.wolf");
        const auto entries = reader->entries();
        auto found = std::find_if(entries.begin(), entries.end(),
                                  [&](const auto &entry) { return entry.path == item.expected; });
        REQUIRE(found != entries.end());
        std::ostringstream output;
        reader->extract(static_cast<std::size_t>(found - entries.begin()), source, output, [](std::int64_t) {});
        REQUIRE(output.str() == "Owned WolfRPG fixture payload\n");
    }
}
