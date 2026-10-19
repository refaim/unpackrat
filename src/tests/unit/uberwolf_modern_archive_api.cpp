// Maintained behavioural test for the ORIGINAL public DXArchive archive API at the modern
// Wolf RPG v3.50 crypt version (0x15E), the caller behind the modern outer/inner/body path.
//
// The modern adapter (modern_crypto.cpp) drives the seekable range decode directly and never
// links DXArchive.cpp, so the two v3.50 body-size delegation statements that
// modern-context.patch installs inside DXArchive::EncodeArchive and DXArchive::DecodeArchive
// are not reached by that suite. This translation unit exercises them through the real public
// filesystem entry points on an owned archive: it encodes a nested payload of more than 1 KiB
// with crypt version 350, decodes it again with the same key, and compares every output byte
// with the input. The archive is large enough that the (size - 64) < 0x400u early return is not
// taken, so the delegating body-size draw runs on both the encode and the decode side.
//
// The container is written and read on disk by the vendor itself (it switches the process
// current directory while decoding and drops a fixed ``decrypt_temp`` there), so the test keeps
// every artefact inside one test::support::temporary_directory and restores the working
// directory on the way out.
//
// The round trip is closed by comparing the complete recursive file+directory entry set of the
// decoded tree against the owned input tree, so a stray decoder artefact cannot pass unnoticed.

#include "../support/archive_lazy_fixtures.h"

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <system_error>
#include <vector>

#include <uberwolf/DXLib/DXArchive.h>

namespace
{
    constexpr std::uint16_t kCryptVersion350 = 350;
    constexpr const char *kKeyString = "uberwolf-350-key";
    constexpr std::size_t kNestedPayloadBytes = 2048;

    // Restores the process working directory on every exit path: the vendor decode changes it
    // to the output directory and only restores it at the very end of a successful decode.
    class working_directory_guard final
    {
      public:
        working_directory_guard() : saved_(std::filesystem::current_path())
        {
        }
        ~working_directory_guard()
        {
            std::error_code error;
            std::filesystem::current_path(saved_, error);
        }
        working_directory_guard(const working_directory_guard &) = delete;
        working_directory_guard &operator=(const working_directory_guard &) = delete;

      private:
        std::filesystem::path saved_;
    };

    [[nodiscard]] std::vector<std::uint8_t> short_payload()
    {
        static constexpr char kText[] = "modern archive roundtrip\n";
        return std::vector<std::uint8_t>(kText, kText + sizeof(kText) - 1);
    }

    // A pseudo-random, incompressible body so the LZ encoder cannot shrink the container below the
    // 0x400-byte body floor that gates the v3.50 delegation.
    [[nodiscard]] std::vector<std::uint8_t> nested_payload()
    {
        std::vector<std::uint8_t> bytes(kNestedPayloadBytes);
        std::uint32_t state = 0x51ED2701u;
        for (std::uint8_t &value : bytes) {
            state ^= state << 13u;
            state ^= state >> 17u;
            state ^= state << 5u;
            value = static_cast<std::uint8_t>(state >> 24u);
        }
        return bytes;
    }

    void write_file(const std::filesystem::path &path, const std::vector<std::uint8_t> &contents)
    {
        std::ofstream output(path, static_cast<std::ios_base::openmode>(static_cast<unsigned>(std::ios::binary) |
                                                                        static_cast<unsigned>(std::ios::trunc)));
        REQUIRE(output.is_open());
        output.write(reinterpret_cast<const char *>(contents.data()), static_cast<std::streamsize>(contents.size()));
        output.close();
        REQUIRE(output.good());
    }

    [[nodiscard]] std::vector<std::uint8_t> read_file(const std::filesystem::path &path)
    {
        std::ifstream input(path, std::ios::binary);
        REQUIRE(input.is_open());
        return std::vector<std::uint8_t>(std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>());
    }

    [[nodiscard]] std::uint32_t read_le32(const std::vector<std::uint8_t> &bytes, std::size_t offset)
    {
        std::uint32_t value = 0;
        for (std::size_t i = 0; i < 4u; ++i) {
            value |= static_cast<std::uint32_t>(bytes[offset + i]) << (8 * i);
        }
        return value;
    }

    // Relative paths of every entry (files and directories) below ``root``, in a stable order.
    [[nodiscard]] std::vector<std::filesystem::path> recursive_relative_entries(const std::filesystem::path &root)
    {
        std::vector<std::filesystem::path> entries;
        for (const std::filesystem::directory_entry &entry : std::filesystem::recursive_directory_iterator(root)) {
            entries.push_back(std::filesystem::relative(entry.path(), root));
        }
        std::sort(entries.begin(), entries.end());
        return entries;
    }

    // True when both trees expose exactly the same relative file and directory set, so an extra
    // decoder output (or a missing one) is a comparison failure rather than a silent pass.
    [[nodiscard]] bool same_entry_set(const std::filesystem::path &left, const std::filesystem::path &right)
    {
        return recursive_relative_entries(left) == recursive_relative_entries(right);
    }
} // namespace

TEST_CASE("public DXArchive API encodes and decodes a mode-350 archive byte for byte", "[wolfrpg][modernarchive]")
{
    const test::support::temporary_directory work("wolfrpg-modern-archive-api");

    const std::filesystem::path input_root = work.path() / "payload";
    const std::filesystem::path nested_dir = input_root / "sub";
    std::filesystem::create_directories(nested_dir);

    const std::vector<std::uint8_t> hello = short_payload();
    const std::vector<std::uint8_t> repeat = nested_payload();
    write_file(input_root / "hello.txt", hello);
    write_file(nested_dir / "repeat.bin", repeat);

    const std::filesystem::path archive = work.path() / "mode350.wolf";

    const working_directory_guard guard;

    // The public one-directory entry point is what WolfDec's "Wolf RPG v3.50" default mode calls.
    // It enumerates the relative files under ".", so the process working directory is moved into
    // the owned input tree and the archive is written by absolute path.
    std::filesystem::current_path(input_root);
    const int encoded = DXArchive::EncodeArchiveOneDirectoryWolf(archive.wstring().c_str(), L".", /*Press=*/true,
                                                                 kKeyString, kCryptVersion350);
    REQUIRE(encoded == 0);
    REQUIRE(std::filesystem::exists(archive));

    // A "correct" mode-350 container: the packed header keeps its DX magic and carries the
    // crypt version in the high half of the Flags field, and the body is past the 0x400 gate.
    const std::vector<std::uint8_t> raw = read_file(archive);
    REQUIRE(raw.size() > 64u + 0x400);
    CHECK(raw[0] == static_cast<std::uint8_t>('D'));
    CHECK(raw[1] == static_cast<std::uint8_t>('X'));
    CHECK((read_le32(raw, 44) >> 16u) == kCryptVersion350);

    const std::filesystem::path output_root = work.path() / "out";
    std::filesystem::create_directories(output_root);

    std::wstring archive_name = archive.wstring();
    REQUIRE(DXArchive::DecodeArchive(archive_name.data(), output_root.wstring().c_str(), kKeyString) == 0);

    CHECK(same_entry_set(input_root, output_root));
    CHECK(read_file(output_root / "hello.txt") == hello);
    CHECK(read_file(output_root / "sub" / "repeat.bin") == repeat);
}

TEST_CASE("the decoded entry-set comparison rejects an extra decoder output", "[wolfrpg][modernarchive]")
{
    const test::support::temporary_directory work("wolfrpg-modern-archive-set");

    const std::filesystem::path expected = work.path() / "expected";
    const std::filesystem::path obtained = work.path() / "obtained";
    std::filesystem::create_directories(expected / "sub");
    std::filesystem::create_directories(obtained / "sub");
    write_file(expected / "hello.txt", short_payload());
    write_file(expected / "sub" / "repeat.bin", nested_payload());
    write_file(obtained / "hello.txt", short_payload());
    write_file(obtained / "sub" / "repeat.bin", nested_payload());

    // Matching trees compare equal.
    CHECK(same_entry_set(expected, obtained));

    // An extra decoder artefact - the kind of stray output the byte-for-byte checks alone would
    // ignore - must make the comparison fail.
    write_file(obtained / "decrypt_temp", short_payload());
    CHECK_FALSE(same_entry_set(expected, obtained));

    // Removing the stray output restores equality, so the failure came from the extra entry.
    REQUIRE(std::filesystem::remove(obtained / "decrypt_temp"));
    CHECK(same_entry_set(expected, obtained));
}
