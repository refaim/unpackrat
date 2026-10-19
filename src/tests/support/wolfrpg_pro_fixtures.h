#ifndef UNPACKRAT_TESTS_SUPPORT_WOLFRPG_PRO_FIXTURES_H
#define UNPACKRAT_TESTS_SUPPORT_WOLFRPG_PRO_FIXTURES_H

#include <cstddef>
#include <cstdint>
#include <initializer_list>
#include <vector>

// Controlled WolfRPG Pro companion-file fixtures for the DxArc key-recovery unit tests.
//
// These builders produce the *input* bytes only. They deliberately contain no key-recovery
// logic: the expected keys are frozen from the unmodified pinned UberWolf source
// (WolfPro::findDxArcKeyV1 and wolf::crypt::dxarckey::{v2,cc2}::calcKey) via a throwaway oracle,
// never from the adapter under test. Both the unit tests and that oracle include this header so
// the fixture bytes cannot drift between them.
namespace test::support::wolfrpg_pro
{
    // A deterministic, non-cryptographic byte pattern. Fixture-only: it exists so a companion is
    // reproducible, and is unrelated to any WolfRPG algorithm.
    [[nodiscard]] inline std::byte pattern_byte(const std::size_t index)
    {
        return static_cast<std::byte>((index * 37u + 11u) & 0xFFu);
    }

    struct byte_override
    {
        std::size_t offset = 0;
        std::uint8_t value = 0;
    };

    // A ``size``-byte companion whose body follows ``pattern_byte`` with the listed bytes forced
    // to chosen values. Used for both the valid variants and the malformed boundary cases.
    [[nodiscard]] inline std::vector<std::byte> make_companion(
        const std::uint64_t size, const std::initializer_list<byte_override> overrides = {})
    {
        std::vector<std::byte> bytes(static_cast<std::size_t>(size));
        for (std::size_t i = 0; i < bytes.size(); ++i)
            bytes[i] = pattern_byte(i);

        for (const byte_override &entry : overrides) {
            if (entry.offset < bytes.size())
                bytes[entry.offset] = static_cast<std::byte>(entry.value);
        }

        return bytes;
    }

    // ---- Variant 1: first byte 0xA0. ----
    // Upstream rejects companions below MIN_FILESIZE (0x5CB8 = 23736), so the fixture sits just
    // above it. With keyLen == 4 the upstream step is 0x58B1 / 4 == 5676 and the selected offsets
    // are 30, 5706, 11382 and 17058.
    inline constexpr std::uint64_t v1_size = 24000;
    inline constexpr std::uint8_t v1_first_byte = 0xA0;
    inline constexpr std::uint8_t v1_seed_byte = 0x2A;    // read as byte[4], seeds the MSVC LCG
    inline constexpr std::uint8_t v1_key_len_byte = 0x04; // read as byte[19], the recovered length

    // The final selected key position. Its byte is pinned so the derived key's last byte is 0x00,
    // which keeps the raw variant derivation identical to the archive-layer key upstream exposes
    // through WolfPro::GetDxArcKey (no trailing-NUL ambiguity to freeze).
    inline constexpr std::size_t v1_last_selected_offset = 30 + 3 * (22705 / 4);
    inline constexpr std::uint8_t v1_last_selected_byte = 0x03;

    [[nodiscard]] inline std::vector<std::byte> v1_companion()
    {
        return make_companion(v1_size, {
                                           {0, v1_first_byte},
                                           {4, v1_seed_byte},
                                           {19, v1_key_len_byte},
                                           {v1_last_selected_offset, v1_last_selected_byte},
                                       });
    }

    // A second v1 companion whose final selected byte decodes to a nonzero value, so the upstream
    // accessor appends the trailing 0x00 that the archive-layer normalization always adds. The
    // pinned byte below (0x00) decodes to 0x03 under the seed-driven XOR stream, exercising the
    // append path without touching the shape of the rest of the key.
    inline constexpr std::uint8_t v1_nonzero_last_selected_byte = 0x00;

    [[nodiscard]] inline std::vector<std::byte> v1_nonzero_companion()
    {
        return make_companion(v1_size, {
                                           {0, v1_first_byte},
                                           {4, v1_seed_byte},
                                           {19, v1_key_len_byte},
                                           {v1_last_selected_offset, v1_nonzero_last_selected_byte},
                                       });
    }

    // A third v1 companion with a different byte[4] LCG seed. Both seeds must recover independently
    // even when two recoveries overlap, which is why the expected keys are frozen separately.
    inline constexpr std::uint8_t v1_seed_b_byte = 0x7E;

    [[nodiscard]] inline std::vector<std::byte> v1_seed_b_companion()
    {
        return make_companion(v1_size, {
                                           {0, v1_first_byte},
                                           {4, v1_seed_b_byte},
                                           {19, v1_key_len_byte},
                                           {v1_last_selected_offset, v1_nonzero_last_selected_byte},
                                       });
    }

    // ---- Variant 2: any first byte other than 0xA0 / 0xF1. ----
    // dataSize == size - 31 == 169. The header bytes below drive k == 94 (coprime with 169) and a
    // 16-byte recovered key; byte[7] is 0 so every selected index stays inside the companion.
    inline constexpr std::uint64_t v2_size = 200;
    inline constexpr std::uint8_t v2_first_byte = 0x11;
    inline constexpr std::uint8_t v2_byte3 = 0x05;
    inline constexpr std::uint8_t v2_byte4 = 0x09;
    inline constexpr std::uint8_t v2_byte5 = 0x40;
    inline constexpr std::uint8_t v2_byte6 = 0x11;
    inline constexpr std::uint8_t v2_byte7 = 0x00;
    inline constexpr std::uint8_t v2_key_len_byte = 16; // read as byte[19]

    [[nodiscard]] inline std::vector<std::byte> v2_companion()
    {
        return make_companion(v2_size, {
                                           {0, v2_first_byte},
                                           {3, v2_byte3},
                                           {4, v2_byte4},
                                           {5, v2_byte5},
                                           {6, v2_byte6},
                                           {7, v2_byte7},
                                           {19, v2_key_len_byte},
                                       });
    }

    // The final selected key position for v2_companion(): with dataSize == 169 and the k == 94 the
    // header bytes drive, upstream reads physical offset (i * k) % dataSize + 30 for i in
    // [0, keyLen); the last index i == 15 lands on offset 88, inside the AES-decrypted window
    // [30, size - 1) and past every header byte a reader consults. A fault keyed to this offset
    // fires only on the read that serves a chosen key byte, after the header has already been read.
    inline constexpr std::size_t v2_last_selected_offset = 30 + (15u * 94u) % 169u;

    // ---- Variant 2 boundary: the final selected position is the last physical byte. ----
    // dataSize == 1, byte[7] == 1 and keyLen == 1 select physical byte 31 == size - 1, which the
    // AES-CTR pass (covering [30, size - 1)) never decrypts, so the correct key byte is the raw
    // companion byte at that offset.
    inline constexpr std::uint64_t v2_compact_size = 32;
    inline constexpr std::uint8_t v2_compact_byte7 = 0x01;
    inline constexpr std::uint8_t v2_compact_key_len_byte = 0x01;

    [[nodiscard]] inline std::vector<std::byte> v2_compact_companion()
    {
        return make_companion(v2_compact_size, {
                                                   {0, v2_first_byte},
                                                   {3, v2_byte3},
                                                   {4, v2_byte4},
                                                   {5, v2_byte5},
                                                   {6, v2_byte6},
                                                   {7, v2_compact_byte7},
                                                   {19, v2_compact_key_len_byte},
                                               });
    }

    // ---- Variant 2 nonzero offset: byte[7] == 2 selects a raw byte one past the AES range. ----
    // dataSize == 2, byte[7] == 2 and keyLen == 1 select physical byte 32, which is inside the
    // companion but outside the decrypted window [30, 32), so it too is the raw byte.
    inline constexpr std::uint64_t v2_offset2_size = 33;
    inline constexpr std::uint8_t v2_offset2_byte7 = 0x02;

    [[nodiscard]] inline std::vector<std::byte> v2_offset2_companion()
    {
        return make_companion(v2_offset2_size, {
                                                   {0, v2_first_byte},
                                                   {3, v2_byte3},
                                                   {4, v2_byte4},
                                                   {5, v2_byte5},
                                                   {6, v2_byte6},
                                                   {7, v2_offset2_byte7},
                                                   {19, v2_compact_key_len_byte},
                                               });
    }

    // ---- Variant 2 sparse sample: 1 MiB companion whose whole file must never be read. ----
    // The key selection touches only the 31-byte header plus keyLen scattered positions, so a
    // correct implementation serves a handful of small reads even though the companion is large.
    inline constexpr std::uint64_t v2_sparse_size = std::uint64_t{1} << 20u;
    inline constexpr std::uint8_t v2_sparse_byte7 = 0x00;

    [[nodiscard]] inline std::vector<std::byte> v2_sparse_companion()
    {
        return make_companion(v2_sparse_size, {
                                                  {0, v2_first_byte},
                                                  {3, v2_byte3},
                                                  {4, v2_byte4},
                                                  {5, v2_byte5},
                                                  {6, v2_byte6},
                                                  {7, v2_sparse_byte7},
                                                  {19, v2_key_len_byte},
                                              });
    }

    // ---- CC2: first byte 0xF1. The key depends only on the 32-bit companion length. ----
    inline constexpr std::uint64_t cc2_size = 500;
    inline constexpr std::uint8_t cc2_first_byte = 0xF1;

    [[nodiscard]] inline std::vector<std::byte> cc2_companion()
    {
        return make_companion(cc2_size, {{0, cc2_first_byte}});
    }

    // ---- CC2 boundary: a 31-byte companion is valid. ----
    // calcDyn computes size - 31 == 0 and divides only by constants, so the key is defined; 31 is
    // the smallest acceptable CC2 companion and only shorter companions are rejected.
    inline constexpr std::uint64_t cc2_min_size = 31;

    [[nodiscard]] inline std::vector<std::byte> cc2_min_companion()
    {
        return make_companion(cc2_min_size, {{0, cc2_first_byte}});
    }

    // The longest companion prefix any variant reads before the key positions themselves: every
    // header field lives below offset 20. Building just this prefix (instead of a whole large
    // companion) lets the sparse tests reproduce a large companion's bytes exactly, because
    // pattern_byte(virtual_index) matches the byte make_companion would have placed there.
    inline constexpr std::size_t v2_header_prefix = 20;
} // namespace test::support::wolfrpg_pro
#endif
