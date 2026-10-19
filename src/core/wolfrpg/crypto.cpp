#include "crypto.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <span>

// Vendor headers and types stay confined to this translation unit; the public contract
// exposes only our own key, error and span types.
#include <uberwolf/Archive/ClassicContext.hpp>
#include <uberwolf/DXLib/DXArchive.h>
#include <uberwolf/WolfDec.h>

namespace unpackrat::wolfrpg::crypto
{
    namespace
    {
        constexpr std::size_t kKeyBytes = key_bytes;

        // KeyCreate writes exactly the classic key length (DXA_KEY_BYTES, 7 bytes) into a
        // caller-owned output and reads the input as a NUL-terminated key string. The
        // scratch buffer only needs to hold the largest accepted material plus its
        // terminator, and doubles as a valid non-null source for an empty span.
        constexpr std::size_t kScratchBytes = max_material_bytes + 1;

        // The real DXArchive::KeyCreate builds its CRC table once behind a function-local static,
        // so it is safe to call directly, from any thread, on its first use.
        std::array<unsigned char, kKeyBytes> key_create(std::span<const std::byte> material)
        {
            // Zero-padded, caller-owned scratch: the length we forward is the real material
            // length, so padding never enters the CRC and an empty span is a non-null source.
            std::array<char, kScratchBytes> scratch{};
            const std::size_t count = std::min(material.size(), kScratchBytes);
            if (count > 0) {
                std::memcpy(scratch.data(), material.data(), count);
            }

            std::array<unsigned char, kKeyBytes> raw{};
            DXArchive::KeyCreate(scratch.data(), count, raw.data());
            return raw;
        }

        std::array<unsigned char, kKeyBytes> to_raw(const key &cipher)
        {
            std::array<unsigned char, kKeyBytes> raw{};
            for (std::size_t i = 0; i < kKeyBytes; ++i) {
                raw[i] = static_cast<unsigned char>(cipher.bytes[i]);
            }
            return raw;
        }
    } // namespace

    key derive_key(std::span<const std::byte> material)
    {
        if (material.size() > max_material_bytes) {
            throw crypto_error("wolfrpg: key material exceeds the 2039-byte limit");
        }

        const std::array<unsigned char, kKeyBytes> raw = key_create(material);

        key derived{};
        for (std::size_t i = 0; i < kKeyBytes; ++i) {
            derived.bytes[i] = static_cast<std::byte>(raw[i]);
        }
        return derived;
    }

    void apply_xor(std::span<std::byte> data, std::uint64_t position, const key &cipher)
    {
        if (data.empty()) {
            return;
        }

        const std::array<unsigned char, kKeyBytes> raw = to_raw(cipher);

        // Reduce the phase modulo 7 in unsigned 64-bit arithmetic, then hand the explicit key and
        // phase to the shared classic kernel. Going straight to the kernel keeps the classic
        // transform independent of the process-wide DXArchive crypt-mode flags that
        // DXArchive::KeyConv dispatches on.
        const std::size_t phase = static_cast<std::size_t>(position % kKeyBytes);
        wolf::archive::classic::apply_key_xor(data, phase, raw);
    }

    std::span<const std::byte> default_classic_password() noexcept
    {
        const CryptModes &modes = WolfDec::DefaultCryptModes();
        const std::vector<char> &mode_key = modes[3].key;
        // The constructor appends a 0x00 terminator that is not part of the password.
        const std::size_t size = mode_key.size() - 1;
        const std::byte *bytes = reinterpret_cast<const std::byte *>(mode_key.data());
        return std::span<const std::byte>(bytes, size);
    }
} // namespace unpackrat::wolfrpg::crypto
