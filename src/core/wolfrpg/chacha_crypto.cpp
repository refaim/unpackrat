#include "chacha_crypto.h"

// <algorithm> must precede the vendor header: its execute() calls std::min but never includes it.
#include <algorithm>
#include <array>
#include <limits>

#include <uberwolf/WolfCrypt/WolfChaCha20.hpp>

namespace unpackrat::wolfrpg::codec
{
    namespace
    {
        constexpr std::size_t state_words = 16;
        constexpr std::size_t block_size = 64;
        constexpr std::size_t counter_low_word = 12;
        constexpr std::size_t counter_high_word = 13;

        [[nodiscard]] std::uint64_t checked_add(std::uint64_t left, std::uint64_t right, const char *message)
        {
            if (left > std::numeric_limits<std::uint64_t>::max() - right) {
                throw chacha_error(message);
            }
            return left + right;
        }
    } // namespace

    wolf_chacha_context::wolf_chacha_context(std::span<const std::byte> key_blob)
    {
        if (key_blob.size() != key_blob_size) {
            throw chacha_error("wolf chacha key blob must be exactly 64 bytes");
        }

        std::copy_n(key_blob.begin() + static_cast<std::ptrdiff_t>(key_offset), key_size, key_.begin());
        std::copy_n(key_blob.begin() + static_cast<std::ptrdiff_t>(nonce_offset), nonce_size, nonce_.begin());
    }

    void wolf_chacha_context::apply_call_range(std::span<std::byte> data, std::uint64_t original_call_start,
                                               std::uint64_t offset_within_call) const
    {
        if (data.empty()) {
            return;
        }

        // DXLib truncates the original call's absolute position to 32 bits once, before running the
        // whole call. Adding the within-call offset to that truncated value with 64-bit arithmetic
        // keeps the high bits, so repeated slices never re-truncate an absolute chunk position.
        const std::uint64_t call_start = static_cast<std::uint32_t>(original_call_start);
        const std::uint64_t position =
            checked_add(call_start, offset_within_call, "wolf chacha call position overflows 64 bits");
        if (static_cast<std::uint64_t>(data.size()) > std::numeric_limits<std::uint64_t>::max() - position) {
            throw chacha_error("wolf chacha call range overflows 64 bits");
        }

        std::array<std::uint32_t, state_words> state{};
        std::array<std::uint32_t, state_words> key_stream{};

        wolf::crypt::chacha20::initBlock(state.data(), reinterpret_cast<const std::uint8_t *>(key_.data()),
                                         reinterpret_cast<const std::uint8_t *>(nonce_.data()));

        // The vendor counter is the 64-bit value state[13]:state[12]. Adding the whole-block seek to
        // that combined value carries into the nonce word exactly as blockNext does, and the vendor
        // stream then restarts inside the target block. The bytes before the slice are never visited.
        std::uint64_t counter = (static_cast<std::uint64_t>(state[counter_high_word]) << 32u) | state[counter_low_word];
        counter += position / block_size;
        state[counter_low_word] = static_cast<std::uint32_t>(counter);
        state[counter_high_word] = static_cast<std::uint32_t>(counter >> 32u);

        wolf::crypt::chacha20::execute(
            state.data(), key_stream.data(), static_cast<std::uint32_t>(position % block_size),
            reinterpret_cast<std::uint8_t *>(data.data()), static_cast<std::uint64_t>(data.size()));
    }
} // namespace unpackrat::wolfrpg::codec
