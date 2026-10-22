#include "../support/archive_lazy_fixtures.h"
#include "../support/wolfrpg_pro_fixtures.h"

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <initializer_list>
#include <ios>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

// Public-entry tests for the vendor WolfPro companion-key recovery.
//
// The other Pro tests drive the shared DxArc kernels and the unpackrat sparse reader; this one
// drives the real public accessor, WolfPro::GetDxArcKey(), over a genuine companion file written to
// a temporary folder. It is the only test that executes the vendor WolfPro.cpp translation unit,
// so the archive adapter and the pinned whole-buffer entry point cannot silently diverge: both must
// return the same frozen key for the same companion bytes.
#include <uberwolf/WolfPro.h>

namespace
{
    namespace fixtures = test::support::wolfrpg_pro;

    // Owns a temporary folder holding exactly one companion file. The unique per-process directory
    // comes from the shared test support helper, so two companion directories alive at the same
    // time never share a path and the destructor only removes the directory this instance created.
    class companion_directory
    {
      public:
        companion_directory(const std::vector<std::byte> &bytes, const std::wstring &name) : folder_("wolfpro")
        {
            std::ofstream out;
            out.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::failbit) |
                                                               static_cast<unsigned>(std::ios::badbit)));
            out.open(folder_.path() / name,
                     static_cast<std::ios_base::openmode>(static_cast<unsigned>(std::ios::binary) |
                                                          static_cast<unsigned>(std::ios::trunc)));
            out.write(reinterpret_cast<const char *>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
            out.close();
        }

        companion_directory(const companion_directory &) = delete;
        companion_directory &operator=(const companion_directory &) = delete;

        [[nodiscard]] std::wstring path() const
        {
            return folder_.path().wstring();
        }

      private:
        test::support::temporary_directory folder_;
    };

    [[nodiscard]] Key key_of(const std::initializer_list<int> values)
    {
        Key key;
        key.reserve(values.size());
        for (const int value : values)
            key.push_back(static_cast<std::uint8_t>(static_cast<unsigned>(value) & 0xFFu));
        return key;
    }
} // namespace

TEST_CASE("public WolfPro::GetDxArcKey recovers a variant-1 companion key", "[wolfrpg][pro][wolfpro]")
{
    // Frozen from the same fixtures::v1_companion() the sparse reader tests use, through the normal
    // WolfPro constructor and public accessor. The final selected byte already decodes to 0x00, so
    // the archive-layer normalization appends nothing.
    const companion_directory directory(fixtures::v1_companion(), L"Game.wolf");

    WolfPro pro(directory.path());
    CHECK(pro.IsWolfPro());

    const Key key = pro.GetDxArcKey();
    CHECK(key == key_of({0x66, 0xBD, 0x18, 0x00}));
    CHECK(pro.GetCryptVersion() == 1000);
}

TEST_CASE("public WolfPro::GetDxArcKey recovers a variant-2 companion key", "[wolfrpg][pro][wolfpro]")
{
    // The v2 companion selects only in-range positions, so the whole-buffer entry returns the key
    // without error and GetDxArcKey leaves the binary tail untouched.
    const companion_directory directory(fixtures::v2_companion(), L"Game.wolf");

    WolfPro pro(directory.path());
    const Key key = pro.GetDxArcKey();
    CHECK(key == key_of({0x6F, 0x69, 0xD5, 0x24, 0xF9, 0x7F, 0x1A, 0x32, 0xC2, 0x89, 0x36,
                         0xBF, 0xD6, 0x3D, 0x71, 0xC0, 0x00, 0x2F, 0x1A, 0x89, 0xD0}));
    CHECK(pro.GetCryptVersion() == 1010);
}

TEST_CASE("public WolfPro::GetDxArcKey recovers a cc2 companion key", "[wolfrpg][pro][wolfpro]")
{
    // The first byte 0xF1 selects the length-only cc2 derivation, which never reads beyond the
    // first byte, so the companion's remaining bytes do not affect the key.
    const companion_directory directory(fixtures::cc2_companion(), L"Game.wolf");

    WolfPro pro(directory.path());
    const Key key = pro.GetDxArcKey();
    CHECK(key ==
          key_of({0x9E, 0x14, 0x79, 0xBE, 0x7B, 0x05, 0x1F, 0x89, 0x59, 0x2A, 0x3D, 0x98, 0x3F, 0x1B, 0xD8, 0xAB,
                  0x1D, 0x08, 0xFE, 0xBA, 0xFB, 0x39, 0x9C, 0x85, 0xD9, 0x2E, 0xBA, 0x94, 0xBF, 0x1F, 0x58, 0xA7,
                  0x9D, 0x0C, 0x7E, 0xB6, 0x7A, 0x3D, 0x1C, 0x81, 0x58, 0x22, 0x3A, 0x90, 0x3E, 0x13, 0xD9, 0xA3,
                  0x1C, 0x00, 0xFF, 0xB2, 0xFA, 0x31, 0x9D, 0x9D, 0xD8, 0x26, 0xBB, 0xAC, 0xBE, 0x17, 0x59, 0xBF}));
    CHECK(pro.GetCryptVersion() == 0xC8);
}

TEST_CASE("overlapping companion directories keep independent WolfPro keys", "[wolfrpg][pro][wolfpro]")
{
    // Two companion directories can be alive at the same time: a caller may hold one while another
    // is created, and the second constructor and destructor must not clobber or delete the first
    // companion's bytes. The first directory's key must stay the variant-1 key throughout, and the
    // second must recover its own variant-2 key.
    const companion_directory first(fixtures::v1_companion(), L"Game.wolf");

    {
        const companion_directory second(fixtures::v2_companion(), L"Game.wolf");

        // While both directories are alive neither has replaced the other.
        WolfPro first_pro(first.path());
        CHECK(first_pro.GetDxArcKey() == key_of({0x66, 0xBD, 0x18, 0x00}));
        CHECK(first_pro.GetCryptVersion() == 1000);

        WolfPro second_pro(second.path());
        CHECK(second_pro.GetDxArcKey() == key_of({0x6F, 0x69, 0xD5, 0x24, 0xF9, 0x7F, 0x1A, 0x32, 0xC2, 0x89, 0x36,
                                                  0xBF, 0xD6, 0x3D, 0x71, 0xC0, 0x00, 0x2F, 0x1A, 0x89, 0xD0}));
        CHECK(second_pro.GetCryptVersion() == 1010);
    }

    // Destroying the second directory must not have removed the first companion.
    WolfPro first_after(first.path());
    CHECK(first_after.GetDxArcKey() == key_of({0x66, 0xBD, 0x18, 0x00}));
    CHECK(first_after.GetCryptVersion() == 1000);
}

TEST_CASE("a folder without a companion yields an empty WolfPro key", "[wolfrpg][pro][wolfpro]")
{
    // The normal constructor finds none of the special DxArc companion names, so the accessor's
    // empty-path branch runs and no key is derived.
    const companion_directory directory(fixtures::v1_companion(), L"not-a-companion.bin");

    WolfPro pro(directory.path());
    CHECK_FALSE(pro.IsWolfPro());
    CHECK(pro.GetDxArcKey().empty());
}
