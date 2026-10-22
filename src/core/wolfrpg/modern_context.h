#ifndef UNPACKRAT_CORE_WOLFRPG_MODERN_CONTEXT_H
#define UNPACKRAT_CORE_WOLFRPG_MODERN_CONTEXT_H

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <span>

#include <uberwolf/WolfCrypt/ModernContext.hpp>

// Pure glue for the "new crypt" Wolf RPG DX archive container. Every stateful derivation and
// the AES-CTR counter/range machinery live once in the patched vendor headers (WolfCrypt.hpp,
// WolfRng.hpp, WolfAes.hpp) and are reached through <uberwolf/WolfCrypt/ModernContext.hpp>.
// This header only adapts the std::span-based shapes the unpackrat adapter uses onto those
// vendor helpers, so no cipher, counter or RNG loop is implemented here.
//
// Vendor-only header: included by the format adapter translation units, not part of the public
// unpackrat API.
namespace wolf::crypt::modern
{
    // Big-endian increment of the 16-byte AES counter by ``blocks``, forwarding to the one
    // shared counter step in WolfAes.hpp.
    inline void advanceCounter(std::span<std::uint8_t, 16> counter, std::uint64_t blocks) noexcept
    {
        aes::addCounter(counter.data(), blocks);
    }

    // Seekable AES-CTR-XOR over one contiguous range, forwarding to the shared range helper in
    // WolfAes.hpp. ``base_iv`` is copied, so the immutable caller state is never advanced.
    inline void aesCtrXor(std::span<std::uint8_t> data, std::span<const std::uint8_t, aes::KEY_EXP_SIZE> schedule,
                          std::span<const std::uint8_t, 16> base_iv, std::uint64_t logical_offset) noexcept
    {
        std::array<std::uint8_t, 16> iv{};
        std::memcpy(iv.data(), base_iv.data(), iv.size());

        aes::aesCtrXCryptRange(data.data(), schedule.data(), iv.data(), logical_offset, data.size());
    }
} // namespace wolf::crypt::modern
#endif
