#ifndef UNPACKRAT_CORE_WOLFRPG_CRYPTO_H
#define UNPACKRAT_CORE_WOLFRPG_CRYPTO_H

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <stdexcept>

namespace unpackrat::wolfrpg::crypto
{
    // Number of key bytes the classic Wolf RPG DX archive format uses.
    inline constexpr std::size_t key_bytes = 7;

    // Longest key material the underlying KeyCreate primitive accepts; the archive
    // key string (path + password) fits in this bound.
    inline constexpr std::size_t max_material_bytes = 2039;

    struct key
    {
        std::array<std::byte, key_bytes> bytes{};
    };

    class crypto_error final : public std::runtime_error
    {
      public:
        using std::runtime_error::runtime_error;
    };

    // Derives the 7-byte classic archive key from 0..2039 bytes of key material.
    // Material shorter than four bytes keeps the library DefaultKeyString fallback and
    // embedded-NUL truncation. Material longer than max_material_bytes is rejected.
    key derive_key(std::span<const std::byte> material);

    // Applies the classic key XOR in place. The starting position is reduced modulo key_bytes in
    // 64-bit arithmetic, so any uint64 value selects a real key phase. The result is a pure
    // function of the span, position and key: it never consults the process-wide UberWolf DXArchive
    // crypt-mode flags, so another consumer switching the library into a modern mode cannot re-key
    // the classic stream. An empty span is a no-op.
    void apply_xor(std::span<std::byte> data, std::uint64_t position, const key &cipher);

    // Borrowed, stable span over the mode-3 Wolf RPG v2.225 default password bytes,
    // excluding the trailing NUL terminator.
    std::span<const std::byte> default_classic_password() noexcept;
} // namespace unpackrat::wolfrpg::crypto

#endif
