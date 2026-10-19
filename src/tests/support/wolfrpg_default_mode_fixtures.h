#ifndef UNPACKRAT_TESTS_SUPPORT_WOLFRPG_DEFAULT_MODE_FIXTURES_H
#define UNPACKRAT_TESTS_SUPPORT_WOLFRPG_DEFAULT_MODE_FIXTURES_H

#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <vector>

// Owned, repo-generated end-to-end fixtures for the classic/legacy *default* crypt modes of the
// WolfRPG container family. Unlike the partial-Huffman matrix (which the reader opens with an
// explicit key), these archives carry only the built-in default key each mode ships with, so the
// reader has to select the mode's key / cipher automatically from the container itself.
//
// Each archive was produced by the pinned UberWolf library - the enc/dec pair taken from
// WolfDec::DefaultCryptModes() - and round-tripped back through the matching decFunc, verifying
// every produced path and byte against the owned input tree. The tiny input tree is byte-for-byte
// identical across modes: hello.txt (a 30-byte literal), repeat.bin ("0123456789abcdef" repeated
// 512 times, 8192 bytes) and the nested directory entry nested/leaf.bin (64 deterministic bytes).
//
// The two VER5 modes (v2.01 / v2.10) are stored-body archives: their shared VER5 encoder does not
// terminate when it is asked to compress (Press=true), but the same entry point with Press=false
// returns rc=0 and its output round-trips through the unchanged VER5 decoder. They are therefore
// frozen exactly as the library produced them with Press=false - the mode's cipher is still applied,
// only the compression stage is skipped - so every fixture still exercises the automatic default-key
// selection this suite cares about. Whether a body is stored or compressed is a property of the
// container, not a requirement on the variant.
//
// No cipher, Huffman or LZ encoder is reimplemented here and no container field is re-derived:
// the fixtures are consumed as opaque bytes and the expected payloads are the same frozen
// generators the throwaway oracle used, each carrying an FNV-1a 64-bit fingerprint. Only the
// per-entry *unpacked* size is frozen; the encoded physical extent depends on the vendor's
// container/press layout and is deliberately not asserted here.
namespace test::support::wolfrpg_default_mode
{
    // The exact payload generators the oracle used (see out/scratch/wolfrpg-default-modes/main.cpp).
    [[nodiscard]] std::vector<std::byte> short_body();  // "Owned WolfRPG fixture payload\n" (30 bytes)
    [[nodiscard]] std::vector<std::byte> repeat_body(); // "0123456789abcdef" repeated 512 times (8192 bytes)
    [[nodiscard]] std::vector<std::byte> leaf_body();   // 64 deterministic xorshift bytes

    // FNV-1a 64-bit fingerprint used to freeze the payloads and the archived bytes.
    [[nodiscard]] std::uint64_t fingerprint(std::span<const std::byte> data);

    struct expected_entry
    {
        std::string path;
        std::int64_t unpacked_size = 0;
        std::uint64_t payload_fingerprint = 0;
        std::vector<std::byte> payload; // frozen expected decoded bytes
    };

    struct fixture
    {
        std::string file_name;
        int mode_index = 0;                 // index into WolfDec::DefaultCryptModes()
        std::string mode_name;              // the mode's own name
        std::uint16_t crypt_version = 0;    // the mode's cryptVersion
        std::string key_hex;                // the mode's default key (provenance / cross-check)
        std::uint16_t container_version = 0;// the container header version the reader must report
        std::size_t file_size = 0;
        std::uint64_t file_fingerprint = 0;
        std::vector<expected_entry> entries;
    };

    // The classic / legacy default modes that the pinned library can both encode and decode:
    // v2.01 / v2.10 (VER5, stored body), v2.20 (VER6), v3.00 / v3.14 (DX8, cryptVersion 300/314)
    // and the two One Way Heroics key sets.
    [[nodiscard]] const std::vector<const fixture *> &all();

    // Reads one owned ``.wolf`` fixture from src/tests/fixtures/wolfrpg. These archives carry only
    // the mode's built-in default key; pass an empty password to exercise the automatic selection.
    [[nodiscard]] std::vector<std::byte> load_bytes(const std::string &file_name);
} // namespace test::support::wolfrpg_default_mode
#endif
