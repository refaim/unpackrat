#include "../../core/wolfrpg/archive.h"
#include "../support/wolfrpg_default_mode_fixtures.h"

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <map>
#include <memory>
#include <span>
#include <sstream>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

// End-to-end contract tests for the classic/legacy *default* crypt modes of the WolfRPG container
// family. Each archive carries only the built-in default key its mode ships with, so the public
// reader must pick the mode's key / cipher from the container itself and be opened WITHOUT a
// password. Every expectation (container version, entry set, decoded bytes, progress accounting)
// is derived from the pinned UberWolf encoder/decoder, never from the reader under test.
//
// Covers all seven classic/legacy default modes (v2.01, v2.10, v2.20, v3.00, v3.14, One Way Heroics,
// One Way Heroics Plus). The two VER5 fixtures carry stored bodies because the vendor's shared VER5
// encoder only terminates with Press=false; the compression codec itself is exercised elsewhere, so
// no variant is required to round-trip through a compressed body here.
//
// The current raw slice implements only the keyless, head-unpressed DX8 container, so every fixture
// here - keyed and (where applicable) head-pressed - is expected to fail at open()/extract() until
// the default-key / VER5 / VER6 / DX8-crypt paths land. These tests are meant to be RED.
namespace
{
    namespace mode = test::support::wolfrpg_default_mode;
    namespace wolfrpg = unpackrat::wolfrpg;

    std::uint16_t read_u16(const std::span<const std::byte> bytes, const std::size_t offset)
    {
        return static_cast<std::uint16_t>(std::to_integer<std::uint8_t>(bytes[offset]) |
                                          (static_cast<std::uint16_t>(std::to_integer<std::uint8_t>(bytes[offset + 1])) << 8));
    }

    std::istringstream to_stream(const std::vector<std::byte> &bytes)
    {
        return std::istringstream(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()), std::ios::binary);
    }

    std::vector<std::byte> to_bytes(const std::string &text)
    {
        std::vector<std::byte> out(text.size());
        for (std::size_t i = 0; i < text.size(); ++i) {
            out[i] = static_cast<std::byte>(static_cast<unsigned char>(text[i]));
        }
        return out;
    }

    // Path -> first-entry index for an opened archive. The container lists entries in file-table
    // order, which the raw metadata walk this phase must not duplicate, so the contract is asserted
    // over the entry *set* rather than a frozen position.
    std::map<std::string, std::size_t> entry_index(const wolfrpg::archive &reader)
    {
        std::map<std::string, std::size_t> index;
        const std::span<const wolfrpg::entry> entries = reader.entries();
        for (std::size_t i = 0; i < entries.size(); ++i) {
            index.emplace(entries[i].path, i);
        }
        return index;
    }
} // namespace

TEST_CASE("classic/legacy default-mode fixtures carry the expected container header", "[wolfrpg][default-mode]")
{
    for (const mode::fixture *fixture : mode::all()) {
        DYNAMIC_SECTION(fixture->file_name) {
            const std::vector<std::byte> bytes = mode::load_bytes(fixture->file_name);

            // Drift control: the exact archived bytes are frozen, so a swapped or regenerated
            // fixture fails here before any reader expectation is consulted.
            REQUIRE(bytes.size() == fixture->file_size);
            REQUIRE(mode::fingerprint(bytes) == fixture->file_fingerprint);

            const std::span<const std::byte> raw(bytes);
            if (fixture->container_version == 8) {
                // A DX8 DARC_HEAD is stored in the clear: "DX" then the version word.
                REQUIRE(std::to_integer<std::uint8_t>(raw[0]) == static_cast<std::uint8_t>('D'));
                REQUIRE(std::to_integer<std::uint8_t>(raw[1]) == static_cast<std::uint8_t>('X'));
                REQUIRE(read_u16(raw, 2) == 8);
            }
            // The VER5/VER6 headers are key-XORed on disk, so neither their magic nor their version
            // word is a raw-byte invariant to assert here: a keyed header is not guaranteed to
            // differ from the DX8 magic for every key, and the version only becomes observable once
            // the reader decrypts the header with the mode's default key. The exact size and
            // fingerprint above are the frozen drift control; the container version itself is
            // asserted through the reader in the open/list case below.
        }
    }
}

TEST_CASE("classic/legacy default-mode archives open without a password and list the expected entries",
          "[wolfrpg][default-mode]")
{
    for (const mode::fixture *fixture : mode::all()) {
        DYNAMIC_SECTION(fixture->file_name) {
            const std::vector<std::byte> bytes = mode::load_bytes(fixture->file_name);
            std::istringstream stream = to_stream(bytes);

            // No password: the reader has to select the mode's built-in default key itself.
            const std::unique_ptr<wolfrpg::archive> reader =
                wolfrpg::archive::open(stream, std::filesystem::path(fixture->file_name));
            REQUIRE(reader != nullptr);
            REQUIRE(reader->version() == fixture->container_version);

            // The container's raw file table is the authority on which paths exist; the map below
            // would silently collapse a duplicated path, so the reader's own entry count has to
            // match the frozen set before the path -> index map is built.
            REQUIRE(reader->entries().size() == fixture->entries.size());
            const std::map<std::string, std::size_t> index = entry_index(*reader);
            REQUIRE(index.size() == fixture->entries.size());
            for (const mode::expected_entry &expected : fixture->entries) {
                CAPTURE(expected.path);
                const auto found = index.find(expected.path);
                REQUIRE(found != index.end());
                REQUIRE(reader->entries()[found->second].unpacked_size == expected.unpacked_size);
            }
        }
    }
}

TEST_CASE("classic/legacy default-mode archives extract the exact payloads and account progress",
          "[wolfrpg][default-mode]")
{
    for (const mode::fixture *fixture : mode::all()) {
        DYNAMIC_SECTION(fixture->file_name) {
            const std::vector<std::byte> bytes = mode::load_bytes(fixture->file_name);
            std::istringstream stream = to_stream(bytes);

            const std::unique_ptr<wolfrpg::archive> reader =
                wolfrpg::archive::open(stream, std::filesystem::path(fixture->file_name));
            REQUIRE(reader != nullptr);

            const std::map<std::string, std::size_t> index = entry_index(*reader);
            for (const mode::expected_entry &expected : fixture->entries) {
                CAPTURE(expected.path);
                // The frozen generator must still match the frozen fingerprint.
                REQUIRE(mode::fingerprint(expected.payload) == expected.payload_fingerprint);
                const auto found = index.find(expected.path);
                REQUIRE(found != index.end());

                std::ostringstream output(std::ios::binary);
                const std::int64_t budget = expected.unpacked_size;
                std::int64_t produced = 0;
                std::size_t calls = 0;
                bool negative_delta = false;
                bool over_budget = false;
                reader->extract(found->second, stream, output, [&](const std::int64_t delta) {
                    // Check the remaining budget *before* the signed addition: an erroneous
                    // non-positive or oversized delta is recorded as a flag instead of being
                    // folded into `produced`, so a wrong progress report fails cleanly here
                    // rather than overflowing the running sum.
                    if (delta < 0) {
                        negative_delta = true;
                        return;
                    }
                    if (delta > budget - produced) {
                        over_budget = true;
                        return;
                    }
                    produced += delta;
                    ++calls;
                });

                const std::vector<std::byte> payload = to_bytes(output.str());
                REQUIRE(payload == expected.payload);
                REQUIRE_FALSE(negative_delta);
                REQUIRE_FALSE(over_budget);
                REQUIRE(produced == expected.unpacked_size);
                REQUIRE(calls >= 1U);
            }
        }
    }
}

TEST_CASE("classic/legacy default-mode archives reject a wrong key", "[wolfrpg][default-mode]")
{
    for (const mode::fixture *fixture : mode::all()) {
        DYNAMIC_SECTION(fixture->file_name) {
            // Control: even though the built-in default key opens the archive, an explicit wrong
            // key must not. A keyed container read with the wrong key yields garbage tables and has
            // to surface as a failure rather than a bogus index.
            const std::vector<std::byte> bytes = mode::load_bytes(fixture->file_name);
            std::istringstream stream = to_stream(bytes);
            REQUIRE_THROWS(wolfrpg::archive::open(stream, std::filesystem::path(fixture->file_name), "not-the-default-key"));
        }
    }
}
