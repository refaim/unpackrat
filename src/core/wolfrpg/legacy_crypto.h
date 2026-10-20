#ifndef UNPACKRAT_CORE_WOLFRPG_LEGACY_CRYPTO_H
#define UNPACKRAT_CORE_WOLFRPG_LEGACY_CRYPTO_H

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>

// Private adapter around the pinned DXLib/WolfRPG "DX1..DX6" obfuscation primitives.
//
// The classic DXLib archiver XORs archive bytes with a 12-byte key derived from a caller
// key string. The key derivation and the byte walk are non-trivial and easy to get subtly
// wrong, so this adapter calls the real DXArchive_VER5 reference routines (the VER6 cipher is
// byte-for-byte identical) behind our own types instead of reimplementing them. Vendor headers
// stay confined to legacy_crypto.cpp; nothing here exposes a vendor type.
namespace unpackrat::wolfrpg::legacy
{
    // Twelve bytes: the width of every classic DXLib key stream.
    constexpr std::size_t key_length = 12;
    using key = std::array<std::uint8_t, key_length>;

    // Derives the 12-byte key from ``source`` exactly as DXLib KeyCreate does. An empty source
    // selects the vendor's NULL default (the 0xaa-based key) rather than an empty C string.
    // The source is copied into a NUL-terminated 13-byte buffer first because the reference
    // routine calls strlen and the raw 12-byte default keys are not NUL-terminated.
    [[nodiscard]] key derive_key(std::string_view source);

    // The vendor fallback key used when the default key does not decrypt the header: twelve
    // 0xff bytes applied directly, with no KeyCreate transform.
    [[nodiscard]] key raw_fallback_key() noexcept;

    // XORs ``data`` with the key starting at byte ``phase``, matching DXLib KeyConv.
    void apply_key(std::span<std::byte> data, std::uint64_t phase, const key &secret);

    // The fixed legacy key strings published by WolfDec::DefaultCryptModes for the classic
    // DX1..DX6 modes (Wolf RPG v2.01/v2.10/v2.20). Read from the pinned accessor at run time;
    // no key table is copied into this repository.
    [[nodiscard]] std::vector<std::string> default_mode_keys();
} // namespace unpackrat::wolfrpg::legacy

#endif
