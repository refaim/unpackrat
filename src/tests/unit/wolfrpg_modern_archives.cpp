#include "../../core/wolfrpg/archive.h"
#include "../support/wolfrpg_modern_archive_fixtures.h"

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <ios>
#include <memory>
#include <sstream>
#include <span>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>
#include <catch2/generators/catch_generators.hpp>

// End-to-end contract tests for the modern WolfRPG default crypt modes (default table indices 6, 7
// and 8: Wolf RPG v3.31, v3.50 and ChaCha2 v1). Each fixture is a real container emitted by the
// unchanged pinned vendor encoder and frozen byte-for-byte, so these tests exercise the reader
// against genuine modern-format archives rather than a hand-built layout or an isolated cipher
// oracle. The current reader slice implements only the uncompressed DX8 container, so the positive
// cases are expected to fail (read_error) until the modern modes are implemented; the v3.31 negative
// control keeps its meaning once they land.
//
// Two expectations were corrected after the fixtures were frozen, and both are test-only fixes:
//   * stored_size is the physical stored extent recorded in the frozen container (hello.txt 252,
//     sub/repeat.bin 2402), not the plaintext length. The bodies are pressed, so asserting
//     stored_size == plaintext is simply false.
//   * the password contract is not "always reject a wrong password": v3.50 applies the fixed literal
//     "958" as an outer transform and default ChaCha2 keeps a fixed key/nonce, so those two modes
//     ignore the caller's password entirely. Only v3.31 folds the password into its outer salting
//     and must reject a wrong one.
namespace
{
    using unpackrat::wolfrpg::archive;
    using unpackrat::wolfrpg::read_error;
    namespace support = test::support;

    std::filesystem::path fixtures_dir()
    {
        return std::filesystem::path(__FILE__).parent_path().parent_path() / "fixtures" / "wolfrpg";
    }

    std::istringstream to_stream(const std::vector<std::byte> &bytes)
    {
        return std::istringstream(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()), std::ios::binary);
    }

    // Extracts entry ``index`` and returns the produced bytes. The reader streams progress as a
    // sequence of signed deltas: every delta must be non-negative and the deltas must sum to exactly
    // the entry's declared ``unpacked_size``, so a reader that skips, double-counts or mis-bounds its
    // payload cannot pass. Each delta is range-checked against the remaining budget *before* it is
    // added, so a pathological value cannot wrap the accumulator and masquerade as progress. No
    // particular chunking is prescribed; only the total and each delta's sign are constrained.
    std::string extract_all(const archive &source, const std::size_t index, std::istream &stream)
    {
        const std::int64_t expected = source.entries()[index].unpacked_size;
        REQUIRE(expected >= 0);

        std::int64_t received = 0;
        bool every_delta_non_negative = true;
        bool every_delta_within_budget = true;

        std::ostringstream output;
        source.extract(index, stream, output, [&](const std::int64_t delta) {
            if (delta < 0) {
                every_delta_non_negative = false;
            } else if (delta > expected - received) {
                every_delta_within_budget = false;
            } else {
                received += delta;
            }
        });

        CHECK(every_delta_non_negative);
        CHECK(every_delta_within_budget);
        CHECK(received == expected);
        return output.str();
    }

    std::string as_text(const std::vector<std::byte> &bytes)
    {
        return std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size());
    }

    // Returns the reader index for a fixture path, or entries.size() when it is absent. Matching by
    // path keeps the comparison independent of the reader's traversal order while still checking
    // every produced name, size and byte.
    std::size_t index_of(const std::span<const unpackrat::wolfrpg::entry> entries, const std::string &path)
    {
        for (std::size_t i = 0; i < entries.size(); ++i) {
            if (entries[i].path == path) {
                return i;
            }
        }
        return entries.size();
    }
} // namespace

TEST_CASE("modern default-mode archives open without a password and extract their owned payloads",
          "[wolfrpg][modern]")
{
    const std::vector<support::modern_archive_fixture> &variants = support::modern_archive_fixtures();
    REQUIRE(variants.size() == 3);

    const std::size_t variant = GENERATE(as<std::size_t>{}, 0, 1, 2);
    const support::modern_archive_fixture &fixture = variants[variant];

    DYNAMIC_SECTION("default mode " << fixture.mode_index << " (" << fixture.mode_name << ", cryptVersion "
                                    << fixture.crypt_version << ")")
    {
        const std::vector<std::byte> bytes = support::read_modern_fixture(fixtures_dir() / fixture.file_name);
        REQUIRE(bytes.size() == fixture.archive_size);
        CHECK(support::fnv1a_64(bytes) == fixture.archive_fingerprint);

        std::istringstream stream = to_stream(bytes);
        std::unique_ptr<archive> reader;
        REQUIRE_NOTHROW(reader = archive::open(stream, fixture.file_name));
        REQUIRE(reader);
        CHECK(reader->version() == 8);

        const std::span<const unpackrat::wolfrpg::entry> entries = reader->entries();
        REQUIRE(entries.size() == fixture.entries.size());

        for (const support::modern_payload &expected : fixture.entries) {
            const std::size_t index = index_of(entries, expected.path);
            REQUIRE(index < entries.size());
            // stored_size is the physical stored extent read from the exact frozen container (the
            // pressed HuffPressDataSize), which is deliberately not the plaintext length.
            CHECK(entries[index].stored_size == expected.encoded_size);
            CHECK(entries[index].unpacked_size == static_cast<std::int64_t>(expected.bytes.size()));
            CHECK(extract_all(*reader, index, stream) == as_text(expected.bytes));
        }
    }
}

TEST_CASE("modern Wolf RPG v3.31 archives reject a wrong password", "[wolfrpg][modern][control]")
{
    // v3.31 (cryptVersion 331) folds the caller's password into its outer salting, so a wrong
    // string must be refused rather than silently accepted (the pinned vendor decoder corrupts on a
    // wrong string; see default-mode-provenance.json). This control keeps its meaning once the modern
    // reader lands, and its positive counterpart above already fails while the modes are missing.
    const std::vector<support::modern_archive_fixture> &variants = support::modern_archive_fixtures();
    REQUIRE(variants.size() == 3);
    REQUIRE(variants.at(0).mode_index == 6);
    const support::modern_archive_fixture &fixture = variants.at(0);

    DYNAMIC_SECTION("mode " << fixture.mode_index << " rejects a wrong key")
    {
        const std::vector<std::byte> bytes = support::read_modern_fixture(fixtures_dir() / fixture.file_name);
        std::istringstream stream = to_stream(bytes);
        CHECK_THROWS_AS(archive::open(stream, fixture.file_name, "not-the-default-key"), read_error);
    }
}

TEST_CASE("modern Wolf RPG v3.50 and ChaCha2 archives ignore the password string", "[wolfrpg][modern][control]")
{
    // v3.50 (cryptVersion 350) applies the fixed literal "958" as an outer transform, and default
    // ChaCha2 (cryptVersion 100) keeps a fixed key/nonce; only cryptVersion 200 replaces them from
    // the key string. A wrong password must therefore open, list and extract the exact frozen bytes
    // unchanged. The pinned, unchanged vendor decoder supplies the same bytes for these containers
    // under a wrong string (recorded in default-mode-provenance.json); this asserts the same
    // invariance through the public reader contract instead of a bare cipher comparison.
    const std::vector<support::modern_archive_fixture> &variants = support::modern_archive_fixtures();
    REQUIRE(variants.size() == 3);
    const std::size_t variant = GENERATE(as<std::size_t>{}, 1, 2);
    const support::modern_archive_fixture &fixture = variants.at(variant);
    REQUIRE(fixture.crypt_version != 0x14b); // never the password-sensitive v3.31 fixture

    DYNAMIC_SECTION("mode " << fixture.mode_index << " opens with a wrong password")
    {
        const std::vector<std::byte> bytes = support::read_modern_fixture(fixtures_dir() / fixture.file_name);
        std::istringstream stream = to_stream(bytes);

        std::unique_ptr<archive> reader;
        REQUIRE_NOTHROW(reader = archive::open(stream, fixture.file_name, "not-the-default-key"));
        REQUIRE(reader);
        CHECK(reader->version() == 8);

        const std::span<const unpackrat::wolfrpg::entry> entries = reader->entries();
        REQUIRE(entries.size() == fixture.entries.size());
        for (const support::modern_payload &expected : fixture.entries) {
            const std::size_t index = index_of(entries, expected.path);
            REQUIRE(index < entries.size());
            CHECK(entries[index].stored_size == expected.encoded_size);
            CHECK(entries[index].unpacked_size == static_cast<std::int64_t>(expected.bytes.size()));
            CHECK(extract_all(*reader, index, stream) == as_text(expected.bytes));
        }
    }
}
