#ifndef UNPACKRAT_TESTS_SUPPORT_WOLFRPG_MODERN_ARCHIVE_FIXTURES_H
#define UNPACKRAT_TESTS_SUPPORT_WOLFRPG_MODERN_ARCHIVE_FIXTURES_H

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

// Owned WolfRPG "modern" default-mode archive fixtures. Each .wolf under src/tests/fixtures/wolfrpg
// was produced by the *unchanged* pinned vendor encoder (WolfDec::DefaultCryptModes() entry encFunc)
// from a small owned input tree and verified end to end by the unchanged vendor decoder; the raw
// container bytes are frozen here, together with every expected path, the full known plaintext and
// the physical stored extent each entry occupies inside the exact frozen container. No commercial
// game data is involved.
//
// The stored extent is not the plaintext size: the pinned encoder runs with AlwaysHuffman and
// HuffmanEncodeKB=12, so every body is physically stored as a pressed stream. hello.txt (30 plaintext
// bytes) is stored in 252 bytes and sub/repeat.bin (8192 plaintext bytes) in 2402 bytes; those two
// numbers are the DARC_FILEHEAD HuffPressDataSize the reader reports as ``entry::stored_size`` and
// were read back from each frozen container through the pinned vendor decoder's typed metadata, not
// synthesised from the encoder input. extract() still has to reproduce the full plaintext.
namespace test::support
{
    struct modern_payload
    {
        std::string path;              // archive-relative path, forward-slash separated
        std::vector<std::byte> bytes;  // frozen expected plaintext contents
        std::int64_t encoded_size = 0; // frozen physical stored extent in the exact container
    };

    struct modern_archive_fixture
    {
        std::string file_name;                 // fixture file name under src/tests/fixtures/wolfrpg
        int mode_index = 0;                    // index into the pinned WolfDec::DefaultCryptModes() table
        std::string mode_name;                 // pinned table entry name
        std::uint16_t crypt_version = 0;       // pinned table entry cryptVersion
        std::vector<std::byte> key;            // full pinned key blob (trailing NUL terminator included)
        std::string enc_symbol;                // pinned encFunc symbol string
        std::string dec_symbol;                // pinned decFunc symbol string
        std::string archive_sha256;            // frozen container digest, lowercase hex
        std::size_t archive_size = 0;          // frozen container length in bytes
        std::uint64_t archive_fingerprint = 0; // frozen container FNV-1a 64 over the raw bytes
        std::vector<modern_payload> entries;
    };

    // The three frozen modern fixtures (default table indices 6, 7 and 8).
    [[nodiscard]] const std::vector<modern_archive_fixture> &modern_archive_fixtures();

    // The two owned payloads, mirrored byte-for-byte with the fixture generator. hello.txt is the
    // 30-byte literal; sub/repeat.bin is 8192 bytes built from a deterministic 2048-byte block
    // repeated four times.
    [[nodiscard]] std::vector<std::byte> modern_hello_payload();
    [[nodiscard]] std::vector<std::byte> modern_repeat_payload();

    // Reads a frozen fixture file into memory. Throws std::runtime_error when it cannot be opened.
    [[nodiscard]] std::vector<std::byte> read_modern_fixture(const std::filesystem::path &path);

    // FNV-1a 64 over a byte range, so a frozen container can be checked by identity without a
    // cryptographic dependency.
    [[nodiscard]] std::uint64_t fnv1a_64(const std::vector<std::byte> &bytes);
} // namespace test::support
#endif
