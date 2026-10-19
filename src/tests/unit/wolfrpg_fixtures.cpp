#include "../support/wolfrpg_fixtures.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

// Test-only: the pinned upstream DXLib archive layout. Naming the real DARC_HEAD / DARC_FILEHEAD /
// DARC_DIRECTORY structures here ties the fixture assertions to the format DXLib actually writes,
// rather than to whatever our synthetic builder happens to emit. The header never reaches
// production code.
#include <uberwolf/DXLib/DXArchive.h>

namespace
{
    constexpr std::uint64_t uncompressed = 0xffffffffffffffffULL;

    std::uint16_t read_u16(const std::vector<std::byte> &bytes, const std::size_t offset)
    {
        return static_cast<std::uint16_t>(
            std::to_integer<std::uint16_t>(bytes[offset]) |
            (static_cast<std::uint32_t>(std::to_integer<std::uint16_t>(bytes[offset + 1])) << 8u));
    }

    std::uint32_t read_u32(const std::vector<std::byte> &bytes, const std::size_t offset)
    {
        std::uint32_t value = 0;
        for (unsigned i = 0; i < 4u; ++i) {
            value |=
                static_cast<std::uint32_t>(std::to_integer<std::uint8_t>(bytes[offset + static_cast<std::size_t>(i)]))
                << (8u * static_cast<unsigned>(i));
        }
        return value;
    }

    std::uint64_t read_u64(const std::vector<std::byte> &bytes, const std::size_t offset)
    {
        std::uint64_t value = 0;
        for (unsigned i = 0; i < 8u; ++i) {
            value |=
                static_cast<std::uint64_t>(std::to_integer<std::uint8_t>(bytes[offset + static_cast<std::size_t>(i)]))
                << (8u * static_cast<unsigned>(i));
        }
        return value;
    }

    std::string text_at(const std::vector<std::byte> &bytes, const std::size_t offset, const std::size_t size)
    {
        std::string text;
        text.reserve(size);
        for (std::size_t i = 0; i < size; ++i) {
            text.push_back(static_cast<char>(std::to_integer<unsigned char>(bytes[offset + i])));
        }
        return text;
    }
} // namespace

TEST_CASE("classic DX8 payload is the pinned golden bytes", "[wolfrpg][fixtures]")
{
    const std::vector<std::byte> golden = test::support::classic_dx8_payload();
    REQUIRE(golden.size() == 966);
    CHECK(text_at(golden, offsetof(DARC_HEAD, Head), 2) == "DX");
    CHECK(read_u16(golden, offsetof(DARC_HEAD, Version)) == 8);
    CHECK(read_u64(golden, offsetof(DARC_HEAD, DataStartAddress)) == 64);
    CHECK(read_u32(golden, offsetof(DARC_HEAD, CharCodeFormat)) == 932);
}

TEST_CASE("the upstream DXLib record layout used by the fixtures", "[wolfrpg][fixtures]")
{
    CHECK(offsetof(DARC_HEAD, Head) == 0);
    CHECK(offsetof(DARC_HEAD, Version) == 2);
    CHECK(offsetof(DARC_HEAD, HeadSize) == 4);
    CHECK(offsetof(DARC_HEAD, DataStartAddress) == 8);
    CHECK(offsetof(DARC_HEAD, FileNameTableStartAddress) == 16);
    CHECK(offsetof(DARC_HEAD, FileTableStartAddress) == 24);
    CHECK(offsetof(DARC_HEAD, DirectoryTableStartAddress) == 32);
    CHECK(offsetof(DARC_HEAD, CharCodeFormat) == 40);
    CHECK(offsetof(DARC_HEAD, Flags) == 44);
    CHECK(offsetof(DARC_HEAD, HuffmanEncodeKB) == 48);
    CHECK(sizeof(DARC_HEAD) == 64);

    CHECK(offsetof(DARC_FILEHEAD, NameAddress) == 0);
    CHECK(offsetof(DARC_FILEHEAD, Attributes) == 8);
    CHECK(offsetof(DARC_FILEHEAD, Time) == 16);
    CHECK(offsetof(DARC_FILEHEAD, DataAddress) == 40);
    CHECK(offsetof(DARC_FILEHEAD, DataSize) == 48);
    CHECK(offsetof(DARC_FILEHEAD, PressDataSize) == 56);
    CHECK(offsetof(DARC_FILEHEAD, HuffPressDataSize) == 64);
    CHECK(sizeof(DARC_FILEHEAD) == 72);

    CHECK(sizeof(DARC_DIRECTORY) == 32);
}

TEST_CASE("build_plain_archive serialises the header tables at the upstream offsets", "[wolfrpg][fixtures]")
{
    const std::array entries{
        test::support::synthetic_entry{"hello.txt", "hello world", false},
        test::support::synthetic_entry{"packed.dat", "abcabcabc", true},
    };
    const test::support::built_archive archive = test::support::build_plain_archive(entries);
    const std::vector<std::byte> &bytes = archive.bytes;

    REQUIRE(bytes.size() == 401);

    // DARC_HEAD
    CHECK(read_u16(bytes, offsetof(DARC_HEAD, Head)) ==
          static_cast<std::uint16_t>((static_cast<unsigned>('X') << 8u) | static_cast<unsigned>('D')));
    CHECK(read_u16(bytes, offsetof(DARC_HEAD, Version)) == 8);
    CHECK(read_u32(bytes, offsetof(DARC_HEAD, HeadSize)) == 308);
    CHECK(read_u64(bytes, offsetof(DARC_HEAD, DataStartAddress)) == 64);
    CHECK(read_u64(bytes, offsetof(DARC_HEAD, FileNameTableStartAddress)) == 93);
    CHECK(read_u64(bytes, offsetof(DARC_HEAD, FileTableStartAddress)) == 60);
    CHECK(read_u64(bytes, offsetof(DARC_HEAD, DirectoryTableStartAddress)) == 276);
    CHECK(read_u32(bytes, offsetof(DARC_HEAD, CharCodeFormat)) == 932);
    CHECK(read_u32(bytes, offsetof(DARC_HEAD, Flags)) == 3);
    CHECK(std::to_integer<std::uint8_t>(bytes[offsetof(DARC_HEAD, HuffmanEncodeKB)]) == 0xff);

    const std::uint64_t header_offset = read_u64(bytes, offsetof(DARC_HEAD, FileNameTableStartAddress));
    REQUIRE(header_offset <= bytes.size());
    const std::size_t header = static_cast<std::size_t>(header_offset);
    REQUIRE(bytes.size() - header >= 60); // complete root and both packed names

    const std::uint64_t file_table_offset = read_u64(bytes, offsetof(DARC_HEAD, FileTableStartAddress));
    REQUIRE(file_table_offset <= bytes.size() - header);
    const std::size_t file_table = header + static_cast<std::size_t>(file_table_offset);
    REQUIRE(bytes.size() - file_table >= 3 * sizeof(DARC_FILEHEAD));

    const std::uint64_t directory_offset = read_u64(bytes, offsetof(DARC_HEAD, DirectoryTableStartAddress));
    REQUIRE(directory_offset <= bytes.size() - header);
    const std::size_t directory = header + static_cast<std::size_t>(directory_offset);
    REQUIRE(bytes.size() - directory >= sizeof(DARC_DIRECTORY));

    // The name table opens with the 4-byte empty root name entry (pack count and parity both zero),
    // then one packed name per file. Upstream reserves 4 + 2 * blocks * 4 bytes per entry
    // (blocks = byte length / 4 + 1), zero padding BOTH the upper-case and the original-case copy to
    // blocks * 4 bytes. A 9- or 10-byte name packs to 3 blocks, so each entry is 28 bytes and the
    // second begins 28 bytes after the first.
    CHECK(read_u32(bytes, header) == 0);
    CHECK(read_u16(bytes, header + 4) == 3);
    CHECK(read_u16(bytes, header + 6) == 674);
    CHECK(text_at(bytes, header + 8, 9) == "HELLO.TXT");
    CHECK(text_at(bytes, header + 20, 9) == "hello.txt");
    CHECK(read_u16(bytes, header + 32) == 3);
    CHECK(read_u16(bytes, header + 34) == 687);
    CHECK(text_at(bytes, header + 36, 10) == "PACKED.DAT");
    CHECK(text_at(bytes, header + 48, 10) == "packed.dat");

    // The file table opens with the root's own DARC_FILEHEAD: NameAddress 0, the DIRECTORY attribute
    // (0x10) and DataAddress 0, which references the root DARC_DIRECTORY at directory-relative 0.
    const std::size_t root = file_table;
    CHECK(read_u64(bytes, root + offsetof(DARC_FILEHEAD, NameAddress)) == 0);
    CHECK(read_u64(bytes, root + offsetof(DARC_FILEHEAD, Attributes)) == 0x10);
    CHECK(read_u64(bytes, root + offsetof(DARC_FILEHEAD, Time)) == 0);
    CHECK(read_u64(bytes, root + offsetof(DARC_FILEHEAD, DataAddress)) == 0);
    CHECK(read_u64(bytes, root + offsetof(DARC_FILEHEAD, DataSize)) == 0);
    CHECK(read_u64(bytes, root + offsetof(DARC_FILEHEAD, PressDataSize)) == uncompressed);

    // The child records follow the root record at file-relative 72 with zero times and the data
    // fields at their upstream offsets.
    const std::size_t first = file_table + sizeof(DARC_FILEHEAD);
    const std::size_t second = first + sizeof(DARC_FILEHEAD);
    CHECK(read_u64(bytes, first + offsetof(DARC_FILEHEAD, NameAddress)) == 4);
    CHECK(read_u64(bytes, first + offsetof(DARC_FILEHEAD, Attributes)) == 0);
    CHECK(read_u64(bytes, first + offsetof(DARC_FILEHEAD, Time)) == 0);
    CHECK(read_u64(bytes, first + offsetof(DARC_FILEHEAD, DataAddress)) == 0);
    CHECK(read_u64(bytes, first + offsetof(DARC_FILEHEAD, DataSize)) == 11);
    CHECK(read_u64(bytes, first + offsetof(DARC_FILEHEAD, PressDataSize)) == uncompressed);
    CHECK(read_u64(bytes, first + offsetof(DARC_FILEHEAD, HuffPressDataSize)) == uncompressed);

    CHECK(read_u64(bytes, second + offsetof(DARC_FILEHEAD, NameAddress)) == 32);
    CHECK(read_u64(bytes, second + offsetof(DARC_FILEHEAD, Attributes)) == 0);
    CHECK(read_u64(bytes, second + offsetof(DARC_FILEHEAD, DataAddress)) == 11);
    CHECK(read_u64(bytes, second + offsetof(DARC_FILEHEAD, DataSize)) == 9);
    CHECK(read_u64(bytes, second + offsetof(DARC_FILEHEAD, PressDataSize)) == 18);
    CHECK(read_u64(bytes, second + offsetof(DARC_FILEHEAD, HuffPressDataSize)) == uncompressed);

    // Root DARC_DIRECTORY: DirectoryAddress, ParentDirectoryAddress, FileHeadNum and FileHeadAddress,
    // where the file heads begin right after the root record at file-relative 72.
    CHECK(read_u64(bytes, directory + 0) == 0);
    CHECK(read_u64(bytes, directory + 8) == uncompressed);
    CHECK(read_u64(bytes, directory + 16) == 2);
    CHECK(read_u64(bytes, directory + 24) == 72);

    // Bodies begin at DataStartAddress. The stored entry is its plaintext; the LZ entry is a
    // literal-only stream (unpacked size, stored size, 0xff keycode, then the bytes).
    CHECK(text_at(bytes, 64, 11) == "hello world");
    CHECK(read_u32(bytes, 75) == 9);
    CHECK(read_u32(bytes, 79) == 18);
    CHECK(std::to_integer<std::uint8_t>(bytes[83]) == 0xff);
    CHECK(text_at(bytes, 84, 9) == "abcabcabc");
}

TEST_CASE("a name whose byte length is a multiple of four packs and pads like any other", "[wolfrpg][fixtures]")
{
    // An 8-byte name is exactly two blocks, but upstream adds the terminating block, so the entry is
    // 4 + 2 * 3 * 4 = 28 bytes; such a name must not be rejected.
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "AB", false},
    };
    const test::support::built_archive archive = test::support::build_plain_archive(entries);
    const std::vector<std::byte> &bytes = archive.bytes;
    const std::uint64_t header_offset = read_u64(bytes, offsetof(DARC_HEAD, FileNameTableStartAddress));
    REQUIRE(header_offset <= bytes.size());
    const std::size_t header = static_cast<std::size_t>(header_offset);
    REQUIRE(bytes.size() - header >= 32); // root name plus the complete packed name

    // The 4-byte root name entry plus the 28-byte file name entry place the file table at 32.
    CHECK(read_u64(bytes, offsetof(DARC_HEAD, FileTableStartAddress)) == 32);
    CHECK(read_u16(bytes, header + 4) == 3);
    CHECK(read_u16(bytes, header + 6) == 612);
    CHECK(text_at(bytes, header + 8, 8) == "NOTE.TXT");
    CHECK(text_at(bytes, header + 20, 8) == "note.txt");
}

TEST_CASE("build_lz_backref_archive serialises a record and a distance-2 copy token", "[wolfrpg][fixtures]")
{
    const std::vector<std::byte> bytes = test::support::build_lz_backref_archive();

    REQUIRE(bytes.size() == 286);
    CHECK(read_u16(bytes, offsetof(DARC_HEAD, Head)) ==
          static_cast<std::uint16_t>((static_cast<unsigned>('X') << 8u) | static_cast<unsigned>('D')));
    CHECK(read_u16(bytes, offsetof(DARC_HEAD, Version)) == 8);
    CHECK(read_u64(bytes, offsetof(DARC_HEAD, DataStartAddress)) == 64);
    CHECK(read_u64(bytes, offsetof(DARC_HEAD, FileNameTableStartAddress)) == 78);
    CHECK(read_u64(bytes, offsetof(DARC_HEAD, FileTableStartAddress)) == 32);
    CHECK(read_u64(bytes, offsetof(DARC_HEAD, DirectoryTableStartAddress)) == 176);

    const std::size_t file_table = 78 + 32;
    const std::size_t directory = 78 + 176;

    // The root's own record precedes the single child at file-relative 72.
    CHECK(read_u64(bytes, file_table + offsetof(DARC_FILEHEAD, NameAddress)) == 0);
    CHECK(read_u64(bytes, file_table + offsetof(DARC_FILEHEAD, Attributes)) == 0x10);
    CHECK(read_u64(bytes, file_table + offsetof(DARC_FILEHEAD, DataAddress)) == 0);
    CHECK(read_u64(bytes, file_table + offsetof(DARC_FILEHEAD, DataSize)) == 0);

    const std::size_t record = file_table + sizeof(DARC_FILEHEAD);
    CHECK(read_u64(bytes, record + offsetof(DARC_FILEHEAD, NameAddress)) == 4);
    CHECK(read_u64(bytes, record + offsetof(DARC_FILEHEAD, Attributes)) == 0);
    CHECK(read_u64(bytes, record + offsetof(DARC_FILEHEAD, DataAddress)) == 0);
    CHECK(read_u64(bytes, record + offsetof(DARC_FILEHEAD, DataSize)) == 6);
    CHECK(read_u64(bytes, record + offsetof(DARC_FILEHEAD, PressDataSize)) == 14);
    CHECK(read_u64(bytes, record + offsetof(DARC_FILEHEAD, HuffPressDataSize)) == uncompressed);

    // Body: 6-byte plaintext, 14-byte stored stream, 0xff keycode, literals "ab", then the copy
    // token 0xff 0x00 0x01 (length 4, distance 2) which expands "ab" to "ababab".
    CHECK(read_u32(bytes, 64) == 6);
    CHECK(read_u32(bytes, 68) == 14);
    CHECK(std::to_integer<std::uint8_t>(bytes[72]) == 0xff);
    CHECK(text_at(bytes, 73, 2) == "ab");
    CHECK(std::to_integer<std::uint8_t>(bytes[75]) == 0xff);
    CHECK(std::to_integer<std::uint8_t>(bytes[76]) == 0x00);
    CHECK(std::to_integer<std::uint8_t>(bytes[77]) == 0x01);

    // Name table: the empty root entry, then the upper-cased name, its parity and the original-case
    // copy at name-relative 4.
    CHECK(read_u32(bytes, 78) == 0);
    CHECK(read_u16(bytes, 82) == 3);
    CHECK(read_u16(bytes, 84) == 757);
    CHECK(text_at(bytes, 86, 11) == "BACKREF.BIN");
    CHECK(text_at(bytes, 98, 11) == "backref.bin");

    // Root directory holds a single file head that begins after the root record at file-relative 72.
    CHECK(read_u64(bytes, directory + 16) == 1);
    CHECK(read_u64(bytes, directory + 24) == 72);
}

TEST_CASE("literal-only LZ entries reject the 0xff keycode byte", "[wolfrpg][fixtures]")
{
    const std::array entries{
        test::support::synthetic_entry{"bad.bin",
                                       std::string("a\xff"
                                                   "b"),
                                       true},
    };
    std::string message;
    try {
        (void)test::support::build_plain_archive(entries);
    } catch (const std::invalid_argument &error) {
        message = error.what();
    }
    CHECK(message == "literal-only LZ stream cannot carry the 0xff keycode byte");
}
