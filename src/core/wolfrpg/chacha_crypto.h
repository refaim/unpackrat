#ifndef UNPACKRAT_CORE_WOLFRPG_CHACHA_CRYPTO_H
#define UNPACKRAT_CORE_WOLFRPG_CHACHA_CRYPTO_H

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <stdexcept>

namespace unpackrat::wolfrpg::codec
{
    // Raised when the caller's key blob is not 64 bytes or a call range cannot be represented in
    // 64 bits. The pinned vendor header and its types never appear on this side of the boundary.
    class chacha_error final : public std::runtime_error
    {
      public:
        using std::runtime_error::runtime_error;
    };

    // Operation-local Wolf RPG ChaCha20 adapter. Both the default archive (crypt version 0x64) and
    // the Pro ("ChaCha2 v2", 0xC8) archive drive the same DXLib ChaCha20 stream, so one context
    // covers both. The context owns copies of the 32-byte key and 12-byte nonce lifted from the
    // caller's 64-byte key blob and is immutable, so it can be shared or reused freely.
    class wolf_chacha_context final
    {
      public:
        static constexpr std::size_t key_size = 32;
        static constexpr std::size_t nonce_size = 12;
        static constexpr std::size_t key_blob_size = 64;
        // The cipher uses key_blob[0,32) as the key and key_blob[34,46) as the nonce; the two bytes
        // at [32,34) and the trailing [46,64) are unused and ignored.
        static constexpr std::size_t key_offset = 0;
        static constexpr std::size_t nonce_offset = 34;

        // key_blob must be exactly 64 bytes; any other length throws chacha_error.
        explicit wolf_chacha_context(std::span<const std::byte> key_blob);

        // XORs the cipher keystream into data in place for the slice that starts offset_within_call
        // bytes into an original DXLib call that began at absolute position original_call_start.
        // The call start is truncated to 32 bits exactly once and the offset is then added with
        // checked 64-bit arithmetic, so re-slicing a call never re-truncates an absolute position
        // and skipped bytes are never materialised or iterated. A position or range that overflows
        // 64 bits throws chacha_error and leaves data untouched; an empty data span is a no-op.
        void apply_call_range(std::span<std::byte> data, std::uint64_t original_call_start,
                              std::uint64_t offset_within_call) const;

      private:
        std::array<std::byte, key_size> key_{};
        std::array<std::byte, nonce_size> nonce_{};
    };
} // namespace unpackrat::wolfrpg::codec
#endif
