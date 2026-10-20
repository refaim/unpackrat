// Focused native tests for the patched UberWolfLib default crypt-mode accessor.
//
// These tests compile against the real upstream <uberwolf/WolfDec.h>. They fail
// to compile until WolfDec declares DefaultCryptModes(), which is the RED state
// that motivates build/vcpkg/ports/uberwolf/default-crypt-modes.patch. The tests
// only touch the static accessor, so WolfDec is never instantiated and its
// constructor never loads the on-disk config.

#include <uberwolf/WolfDec.h>

#include <array>
#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

#include <catch2/catch_test_macros.hpp>

namespace
{
    constexpr std::array<std::string_view, 11> kExpectedNames = {
        "Wolf RPG v2.01",      "Wolf RPG v2.10",  "Wolf RPG v2.20",       "Wolf RPG v2.225",
        "Wolf RPG v3.00",      "Wolf RPG v3.14",  "Wolf RPG v3.31",       "Wolf RPG v3.50",
        "Wolf RPG ChaCha2 v1", "One Way Heroics", "One Way Heroics Plus",
    };
}

TEST_CASE("DefaultCryptModes exposes the unchanged pinned default table", "[uberwolf]")
{
    const CryptModes &modes = WolfDec::DefaultCryptModes();

    REQUIRE(modes.size() == kExpectedNames.size());
    for (std::size_t index = 0; index < kExpectedNames.size(); ++index) {
        INFO("mode index " << index);
        CHECK(modes[index].name == kExpectedNames[index]);
    }
}

TEST_CASE("DefaultCryptModes is a stable noexcept reference", "[uberwolf]")
{
    static_assert(noexcept(WolfDec::DefaultCryptModes()), "the accessor must be declared noexcept");

    const CryptModes &first = WolfDec::DefaultCryptModes();
    const CryptModes &second = WolfDec::DefaultCryptModes();
    CHECK(&first == &second);
}

TEST_CASE("DefaultCryptModes keeps classic and newcrypt version numbers", "[uberwolf]")
{
    const CryptModes &modes = WolfDec::DefaultCryptModes();
    REQUIRE(modes.size() > 7);

    CHECK(modes[3].cryptVersion == 0);   // Wolf RPG v2.225 (classic, no header version)
    CHECK(modes[4].cryptVersion == 300); // Wolf RPG v3.00
    CHECK(modes[5].cryptVersion == 314); // Wolf RPG v3.14
    CHECK(modes[6].cryptVersion == 331); // Wolf RPG v3.31
    CHECK(modes[7].cryptVersion == 350); // Wolf RPG v3.50
}

TEST_CASE("DefaultCryptModes entries carry terminated keys and functions", "[uberwolf]")
{
    const CryptModes &modes = WolfDec::DefaultCryptModes();
    REQUIRE(modes.size() >= 3);

    // The first three legacy keys are fixed 12-byte binary blobs rather than
    // C strings, so their last byte is key material instead of a terminator.
    const unsigned char kLegacyFinalByte[] = {0xE1, 0x4C, 0x38};
    for (std::size_t index = 0; index < 3; ++index) {
        INFO("legacy mode index " << index);
        const auto &key = modes[index].key;
        REQUIRE(!key.empty());
        CHECK(key.size() == 12);
        CHECK(static_cast<unsigned char>(key.back()) == kLegacyFinalByte[index]);
        CHECK(modes[index].decFunc != nullptr);
        CHECK(modes[index].encFunc != nullptr);
    }

    for (std::size_t index = 3; index < modes.size(); ++index) {
        INFO("mode " << modes[index].name);
        const auto &key = modes[index].key;
        REQUIRE(!key.empty());
        CHECK(key.back() == 0x00);
        CHECK(modes[index].decFunc != nullptr);
        CHECK(modes[index].encFunc != nullptr);
    }
}
