// Behavioral tests for unpackrat::wolfrpg::codec::wolf_chacha_context, the operation-local Wolf
// RPG ChaCha20 adapter.
//
// Oracle provenance: the reference keystream is produced directly by the pinned, unmodified
// UberWolf cipher. The vendor sources are the immutable revision
// f4191375d289171b1f29dbc5ca320133b2b4b96a (Sinflower/UberWolf 0.6.4); the installed header is
// <uberwolf/WolfCrypt/WolfChaCha20.hpp>, SHA-256
// 57EA2B3C33BEA024BB4DF0D7AF10A6A3391FAE79ED0C2461FE794FBD5A9BF0BA. The DXLib call site this
// adapter mirrors is 3rdParty/DXLib/DXArchive.cpp KeyConv(): for crypt versions 0x64 (default /
// "Wolf RPG ChaCha2 v1") and 0xC8 (Pro / "Wolf RPG ChaCha2 v2") it runs
// initBlock(state, g_cc20Key, g_cc20Nonce) once and then
// execute(state, keystream32, (uint32_t)Position, Data, Size), with g_cc20Key = KeyString[0,32)
// and g_cc20Nonce = KeyString[34,46). The two 32-byte key / 12-byte nonce constants below are the
// pinned DXLib ChaCha defaults byte-equal to that KeyString.
//
// The frozen vectors F1/F2/F3 were emitted once by a throwaway generator that drove that same
// vendor cipher with the counter words named beside each vector, and are asserted verbatim so a
// wrong seek cannot hide behind a matching oracle.

#include "../../core/wolfrpg/chacha_crypto.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <barrier>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <exception>
#include <functional>
#include <limits>
#include <mutex>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

#include <catch2/catch_test_macros.hpp>

// Reused unchanged for the independent oracle; <algorithm> above supplies std::min, which the
// vendor execute() uses but never includes.
#include <uberwolf/WolfCrypt/WolfChaCha20.hpp>

namespace
{
    using unpackrat::wolfrpg::codec::chacha_error;
    using unpackrat::wolfrpg::codec::wolf_chacha_context;

    using byte_vector = std::vector<std::byte>;

    // 3rdParty/DXLib/DXArchive.cpp g_cc20Key / g_cc20Nonce (the 0x64 path never overwrites them).
    constexpr std::array<unsigned, 32> default_key = {0xC9, 0x82, 0xF8, 0xB4, 0x2C, 0x93, 0x9E, 0x83, 0x0E, 0xBC, 0xBC,
                                                      0x92, 0x68, 0x8D, 0x59, 0xA1, 0x4A, 0x9E, 0x7F, 0xB0, 0xAC, 0xAF,
                                                      0x1D, 0x8F, 0x8E, 0xB8, 0x3B, 0x9E, 0xE8, 0x89, 0xD9, 0xAD};
    constexpr std::array<unsigned, 12> default_nonce = {0xFF, 0xBC, 0x2D, 0xAB, 0x9D, 0x8B,
                                                        0x0F, 0xB4, 0xBB, 0x9A, 0x69, 0x85};
    // A nonce whose first word is 0xFFFFFFFF, so a block-counter carry overflows the nonce word.
    constexpr std::array<unsigned, 12> maxword_nonce = {0xFF, 0xFF, 0xFF, 0xFF, 0x9D, 0x8B,
                                                        0x0F, 0xB4, 0xBB, 0x9A, 0x69, 0x85};

    constexpr std::size_t key_size = 32;
    constexpr std::size_t nonce_size = 12;
    constexpr std::size_t blob_size = 64;

    template <std::size_t N> std::array<std::uint8_t, N> to_u8(const std::array<unsigned, N> &src)
    {
        std::array<std::uint8_t, N> out{};
        for (std::size_t i = 0; i < N; ++i) {
            out[i] = static_cast<std::uint8_t>(src[i]);
        }
        return out;
    }

    // Lays out a 64-byte blob: key in [0,32), nonce in [34,46), and recognizable filler in the
    // two unused gaps [32,34) and [46,64).
    std::array<std::byte, blob_size> make_blob(const std::array<unsigned, key_size> &key,
                                               const std::array<unsigned, nonce_size> &nonce)
    {
        std::array<std::byte, blob_size> blob{};
        for (std::size_t i = 0; i < key_size; ++i) {
            blob[i] = static_cast<std::byte>(key[i]);
        }
        blob[32] = std::byte{0x5A};
        blob[33] = std::byte{0x5B};
        for (std::size_t i = 0; i < nonce_size; ++i) {
            blob[34 + i] = static_cast<std::byte>(nonce[i]);
        }
        for (std::size_t i = 46; i < blob_size; ++i) {
            blob[i] = std::byte{0xA5};
        }
        return blob;
    }

    wolf_chacha_context make_context(const std::array<unsigned, key_size> &key,
                                     const std::array<unsigned, nonce_size> &nonce)
    {
        const auto blob = make_blob(key, nonce);
        return wolf_chacha_context(blob);
    }

    // Independent oracle #1: one unmodified vendor call, exactly like DXLib KeyConv().
    byte_vector vendor_keystream(const std::array<unsigned, key_size> &key,
                                 const std::array<unsigned, nonce_size> &nonce, std::uint32_t start_pos,
                                 std::size_t length)
    {
        const auto k = to_u8(key);
        const auto n = to_u8(nonce);

        byte_vector out(length, std::byte{0});
        std::uint32_t state[16];
        std::uint32_t keystream[16];
        std::memset(state, 0, sizeof(state));
        std::memset(keystream, 0, sizeof(keystream));
        wolf::crypt::chacha20::initBlock(state, k.data(), n.data());
        wolf::crypt::chacha20::execute(state, keystream, start_pos, reinterpret_cast<std::uint8_t *>(out.data()),
                                       static_cast<std::uint64_t>(length));
        return out;
    }

    // Independent oracle #2 for offsets too distant to walk: preset the vendor block counter words
    // by hand and run one bounded vendor call from within the block.
    byte_vector vendor_keystream_at_counter(const std::array<unsigned, key_size> &key,
                                            const std::array<unsigned, nonce_size> &nonce, std::uint32_t counter_low,
                                            std::uint32_t counter_high, std::uint32_t start_pos, std::size_t length)
    {
        const auto k = to_u8(key);
        const auto n = to_u8(nonce);

        byte_vector out(length, std::byte{0});
        std::uint32_t state[16];
        std::uint32_t keystream[16];
        std::memset(state, 0, sizeof(state));
        std::memset(keystream, 0, sizeof(keystream));
        wolf::crypt::chacha20::initBlock(state, k.data(), n.data());
        state[12] = counter_low;
        state[13] = counter_high;
        wolf::crypt::chacha20::execute(state, keystream, start_pos, reinterpret_cast<std::uint8_t *>(out.data()),
                                       static_cast<std::uint64_t>(length));
        return out;
    }

    // Independent oracle for a slice whose absolute position may have stepped past 2^32. Inside the
    // low 32 bits this is one plain DXLib-shaped vendor call; a crossing slice is carved out of a
    // single plain vendor call anchored at a reachable, block-aligned origin, so the reference bytes
    // always come from the unmodified vendor cipher rather than from the adapter under test.
    byte_vector vendor_expected(const std::array<unsigned, key_size> &key,
                                const std::array<unsigned, nonce_size> &nonce, std::uint64_t call_start,
                                std::uint64_t offset, std::size_t length)
    {
        const std::uint64_t position = static_cast<std::uint64_t>(static_cast<std::uint32_t>(call_start)) + offset;
        if (position <= 0xFFFFFFFFULL) {
            return vendor_keystream(key, nonce, static_cast<std::uint32_t>(position), length);
        }

        constexpr std::uint64_t reachable_origin = 0xFFFFFF00ULL; // < 2u^32u and a multiple of 64
        const std::size_t slice = static_cast<std::size_t>(position - reachable_origin);
        const auto whole = vendor_keystream(key, nonce, static_cast<std::uint32_t>(reachable_origin), slice + length);
        return byte_vector(whole.begin() + static_cast<std::ptrdiff_t>(slice), whole.end());
    }

    byte_vector apply_range(const wolf_chacha_context &context, std::uint64_t call_start, std::uint64_t offset,
                            std::size_t length)
    {
        byte_vector data(length, std::byte{0});
        context.apply_call_range(data, call_start, offset);
        return data;
    }

    std::string hex(std::span<const std::byte> data)
    {
        static constexpr char digits[] = "0123456789ABCDEF";
        std::string out;
        out.reserve(data.size() * 3);
        for (std::size_t i = 0; i < data.size(); ++i) {
            const auto value = std::to_integer<unsigned>(data[i]);
            if (i != 0) {
                out.push_back(' ');
            }
            out.push_back(digits[value >> 4u]);
            out.push_back(digits[value & 0x0Fu]);
        }
        return out;
    }

    void expect_bytes(std::span<const std::byte> actual, std::span<const std::byte> expected, std::string_view what)
    {
        INFO(what);
        INFO("actual:   " << hex(actual));
        INFO("expected: " << hex(expected));
        REQUIRE(actual.size() == expected.size());
        CHECK(std::equal(actual.begin(), actual.end(), expected.begin()));
    }

    byte_vector from_hex(std::string_view text)
    {
        byte_vector out;
        int high = -1;
        for (const char c : text) {
            int value = -1;
            if (c >= '0' && c <= '9') {
                value = c - '0';
            } else if (c >= 'A' && c <= 'F') {
                value = c - 'A' + 10;
            } else if (c >= 'a' && c <= 'f') {
                value = c - 'a' + 10;
            } else {
                continue;
            }

            if (high < 0) {
                high = value;
            } else {
                out.push_back(
                    static_cast<std::byte>((static_cast<unsigned>(high) << 4u) | static_cast<unsigned>(value)));
                high = -1;
            }
        }
        return out;
    }

    constexpr std::uint64_t boundary_start = 0xFFFFFFFFULL;         // (uint32_t) truncates to itself
    constexpr std::uint64_t boundary_cross = 256;                   // offset that steps past 2^32u
    constexpr std::uint64_t high_block_offset = 0xFFFFFFFEULL * 64; // block index 2^32u - 2

    // Concurrency launch scaffolding. The concurrency cases start several participants that must
    // all meet at one std::barrier. A barrier's participant count is fixed when it is built, so a
    // participant that never arrives - for example because its thread could not be created - would
    // leave the participants that did start waiting at the barrier forever. The tests below drive
    // the launch through a spawn factory so that failure is reproducible without exhausting the
    // real system.

    // A distinct, self-describing failure so a test can prove the launcher rethrows the exact
    // creation exception it was handed rather than swallowing or substituting it.
    class launch_fault final : public std::runtime_error
    {
      public:
        explicit launch_fault(std::size_t participant)
            : std::runtime_error("participant " + std::to_string(participant) + " could not start"),
              participant_(participant)
        {
        }

        [[nodiscard]] std::size_t participant() const noexcept
        {
            return participant_;
        }

      private:
        std::size_t participant_;
    };

    // Holds every started participant just before the barrier until the launcher has created the
    // whole group. A participant that is told to abort leaves without ever touching the barrier, so
    // a group that never reached full strength cannot strand its members on an arrival that will
    // never come.
    class launch_gate
    {
      public:
        // Worker side: block until the launcher commits or aborts. Returns false exactly when the
        // launch was aborted, i.e. when the participant must leave without touching the barrier.
        [[nodiscard]] bool wait()
        {
            std::unique_lock lock(mutex_);
            cv_.wait(lock, [this] { return decided_; });
            return !aborted_;
        }

        void commit()
        {
            decide(false);
        }

        void abort()
        {
            decide(true);
        }

      private:
        void decide(bool aborted)
        {
            {
                std::lock_guard lock(mutex_);
                aborted_ = aborted;
                decided_ = true;
            }
            cv_.notify_all();
        }

        std::mutex mutex_;
        std::condition_variable cv_;
        bool decided_ = false;
        bool aborted_ = false;
    };

    // The default participant handle: a real, joinable thread.
    std::jthread spawn_thread(std::function<void()> task)
    {
        return std::jthread{std::move(task)};
    }

    // Runs worker(index) on worker_count participants that the barrier inside worker expects to
    // meet. spawn creates one participant, may throw (the runtime cannot create another thread, or
    // a fault injected by a test) and must produce a joinable handle. exited counts the
    // participants whose thread body ran to completion.
    //
    // If any spawn throws, the already-started participants are released from the gate, all started
    // participants are joined, and the original exception is rethrown unchanged. A participant that
    // never started therefore cannot leave its peers waiting at the barrier.
    template <class Spawn>
    void launch_together(std::size_t worker_count, const std::function<void(std::size_t)> &worker, Spawn spawn,
                         std::atomic<std::size_t> &exited)
    {
        launch_gate gate;
        std::vector<std::jthread> participants;
        participants.reserve(worker_count);
        try {
            for (std::size_t index = 0; index < worker_count; ++index) {
                participants.push_back(spawn([&gate, &worker, &exited, index] {
                    if (gate.wait()) {
                        worker(index);
                    }
                    exited.fetch_add(1, std::memory_order_relaxed);
                }));
            }
        } catch (...) {
            gate.abort();
            throw; // participants joins every started thread while the exception leaves this frame
        }
        gate.commit();
    } // participants joins every started thread before returning
} // namespace

TEST_CASE("a fresh context reproduces the DXLib keystream of one call from its start", "[wolfrpg][chacha]")
{
    const auto context = make_context(default_key, default_nonce);

    for (const std::uint64_t start :
         {std::uint64_t{0}, std::uint64_t{1}, std::uint64_t{63}, std::uint64_t{64}, std::uint64_t{65}}) {
        const auto oracle = vendor_keystream(default_key, default_nonce, static_cast<std::uint32_t>(start), 200);
        expect_bytes(apply_range(context, start, 0, 200), oracle, "keystream from call start");
    }
}

TEST_CASE("apply_call_range xors the keystream into the caller's buffer in place", "[wolfrpg][chacha]")
{
    const auto context = make_context(default_key, default_nonce);
    const auto oracle = vendor_keystream(default_key, default_nonce, 0, 96);

    byte_vector data(96);
    for (std::size_t i = 0; i < data.size(); ++i) {
        data[i] = static_cast<std::byte>((i * 7 + 3) & 0xFFu);
    }
    byte_vector expected = data;
    for (std::size_t i = 0; i < expected.size(); ++i) {
        expected[i] =
            static_cast<std::byte>(std::to_integer<unsigned>(expected[i]) ^ std::to_integer<unsigned>(oracle[i]));
    }

    context.apply_call_range(data, 0, 0);
    expect_bytes(data, expected, "in-place xor");
}

TEST_CASE("the original call start is truncated to 32 bits exactly once", "[wolfrpg][chacha]")
{
    const auto context = make_context(default_key, default_nonce);

    const std::uint64_t starts[] = {0xFFFFFFEFULL, 0x100000007ULL, 0x20000000BULL, 0x30000FFFFULL,
                                    0xFFFFFFFFFFFFFFFFULL};
    for (const std::uint64_t start : starts) {
        const auto oracle = vendor_keystream(default_key, default_nonce, static_cast<std::uint32_t>(start), 128);
        expect_bytes(apply_range(context, start, 0, 128), oracle, "truncated call start");
    }
}

TEST_CASE("a within-call offset survives the 2^32u position step", "[wolfrpg][chacha]")
{
    const auto context = make_context(default_key, default_nonce);
    constexpr std::size_t span = 700;
    const auto oracle = vendor_keystream(default_key, default_nonce, static_cast<std::uint32_t>(boundary_start), span);

    const std::uint64_t offsets[] = {0,   1,   63, 64, 255, boundary_cross - 1, boundary_cross, boundary_cross + 1,
                                     384, 512, 600};
    for (const std::uint64_t offset : offsets) {
        const std::size_t length = 64;
        const auto slice = apply_range(context, boundary_start, offset, length);
        const auto expected = std::span<const std::byte>(oracle).subspan(static_cast<std::size_t>(offset), length);
        expect_bytes(slice, expected, "slice straddling the 32-bit boundary");
    }
}

TEST_CASE("distant within-call offsets seek straight to the target block", "[wolfrpg][chacha]")
{
    const auto context = make_context(default_key, default_nonce);

    // Offset 0xFFFFFFFE * 64 sits at block index 2^32u - 2, i.e. counter words 0xFFFFFFFF / 0xAB2DBCFF.
    const auto frozen =
        from_hex("9C 1E CC 80 C9 F6 66 C9 39 87 B9 DB 9E AA D4 52 F0 37 42 76 63 68 C2 01 7F FB B6 20 E9 6F E1 29"
                 "03 7F 88 81 26 BD B3 FC 5E B0 1F B6 D4 12 C2 1D E5 3F 3B A4 7E DD 94 19 AA ED 17 21 BB 02 23 A5"
                 "40 16 06 F7 6E 9F 46 A0 B7 AB 93 BC 19 7C 08 25 E1 0F 89 BE 40 9E 69 25 A3 D6 0D D7 49 2A A0 94"
                 "84 6E DF A6 BD 48 19 75 57 E8 FA 3B 7B FF 2D 7C BA FD E7 9A B9 6B F1 04 A2 C3 E9 8D B2 10 C8 68");
    expect_bytes(apply_range(context, 0, high_block_offset, frozen.size()), frozen, "frozen wrap keystream");

    // One more block carries the low counter word back to zero.
    const auto oracle_next = vendor_keystream_at_counter(default_key, default_nonce, 0u, 0xAB2DBD00u, 0u, 64);
    expect_bytes(apply_range(context, 0, high_block_offset + 64, 64), oracle_next, "post-carry block");
}

TEST_CASE("the block counter wraps into the nonce word and stops there", "[wolfrpg][chacha]")
{
    const auto context = make_context(default_key, maxword_nonce);

    const auto frozen =
        from_hex("1E DE BA A0 A7 F7 BD B0 66 FF F0 64 1A 61 2A D3 22 1E 13 B8 CA 96 03 36 F0 80 14 FA 8C 67 E9 50"
                 "75 C5 4F BC 5A 8A B9 CA 56 DC BD EA 2B FA 78 E6 96 A6 05 1B B3 33 A8 3E D4 41 DD 72 22 01 C3 69"
                 "2F 4D A4 1E 0D 01 67 C9 F0 FC E6 30 F1 15 3D C0 17 ED BA 9C B3 77 A2 F8 71 BF EB 7F 65 EA 1A 40"
                 "E3 BC 20 F2 FC 8E E9 AE 0A 82 3F 93 4A 71 2F FE 87 D4 0B 6B 41 51 93 11 8E FC 1D 2D 9F E0 22 3C");
    expect_bytes(apply_range(context, 0, high_block_offset, frozen.size()), frozen, "frozen nonce-word wrap keystream");

    // The nonce word is 0xFFFFFFFF, so the second block carries it to 0 without touching word 2.
    const auto oracle_wrapped = vendor_keystream_at_counter(default_key, maxword_nonce, 0u, 0u, 0u, 64);
    expect_bytes(apply_range(context, 0, high_block_offset + 64, 64), oracle_wrapped, "nonce word wrapped to zero");
}

TEST_CASE("empty ranges are a no-op even at an extreme position", "[wolfrpg][chacha]")
{
    const auto context = make_context(default_key, default_nonce);

    CHECK_NOTHROW(context.apply_call_range(std::span<std::byte>{}, 0, 0));
    CHECK_NOTHROW(context.apply_call_range(std::span<std::byte>{}, 0xFFFFFFFFFFFFFFFFULL, 0xFFFFFFFFFFFFFFFFULL));
}

TEST_CASE("unrepresentable call ranges are rejected and leave the data untouched", "[wolfrpg][chacha]")
{
    const auto context = make_context(default_key, default_nonce);

    const auto guard = [&](std::uint64_t call_start, std::uint64_t offset, std::size_t length) {
        byte_vector data(length, std::byte{0x11});
        const byte_vector before = data;
        CHECK_THROWS_AS(context.apply_call_range(data, call_start, offset), chacha_error);
        CHECK(data == before);
    };

    guard(0xFFFFFFFFULL, 0xFFFFFFFFFFFFFFFFULL, 1); // call_start + offset overflows
    guard(0, 0xFFFFFFFFFFFFFFFFULL, 1);             // position + size overflows
    guard(0, 0xFFFFFFFFFFFFFFC0ULL, 64);            // position + size overflows by one

    // Just inside the range: 2^64u - 64 followed by 63 bytes still fits.
    byte_vector ok(63, std::byte{0});
    CHECK_NOTHROW(context.apply_call_range(ok, 0, 0xFFFFFFFFFFFFFFC0ULL));
    // One byte more does not.
    byte_vector too_big(64, std::byte{0});
    CHECK_THROWS_AS(context.apply_call_range(too_big, 0, 0xFFFFFFFFFFFFFFC0ULL), chacha_error);
}

TEST_CASE("the key blob must be exactly 64 bytes", "[wolfrpg][chacha]")
{
    for (const std::size_t size :
         {std::size_t{0}, std::size_t{1}, std::size_t{32}, std::size_t{63}, std::size_t{65}, std::size_t{128}}) {
        const byte_vector bad(size, std::byte{0});
        CHECK_THROWS_AS(wolf_chacha_context{bad}, chacha_error);
    }

    const auto good = make_blob(default_key, default_nonce);
    CHECK_NOTHROW(wolf_chacha_context{good});
}

TEST_CASE("only blob bytes 0..31 and 34..45 steer the cipher", "[wolfrpg][chacha]")
{
    const auto base_blob = make_blob(default_key, default_nonce);
    const auto base_keystream = vendor_keystream(default_key, default_nonce, 0, 64);
    const auto base_context = wolf_chacha_context{base_blob};
    expect_bytes(apply_range(base_context, 0, 0, 64), base_keystream, "baseline");

    // Unused bytes [32,34) and [46,64) must not change the keystream.
    auto ignored_blob = base_blob;
    ignored_blob[32] = std::byte{0x01};
    ignored_blob[33] = std::byte{0xFE};
    for (std::size_t i = 46; i < blob_size; ++i) {
        ignored_blob[i] = std::byte{0x7E};
    }
    const auto ignored_context = wolf_chacha_context{ignored_blob};
    expect_bytes(apply_range(ignored_context, 0, 0, 64), base_keystream, "ignored blob bytes");

    // Any key byte is significant.
    auto key_blob = base_blob;
    key_blob[31] = static_cast<std::byte>(std::to_integer<unsigned>(key_blob[31]) ^ 0x01u);
    const auto key_keystream = apply_range(wolf_chacha_context{key_blob}, 0, 0, 64);
    CHECK(!std::equal(key_keystream.begin(), key_keystream.end(), base_keystream.begin()));

    // The nonce lives at offset 34, not 32: stamping the same 12 bytes at [32,44) changes nothing.
    auto misaligned_blob = base_blob;
    misaligned_blob[32] = static_cast<std::byte>(default_nonce[0]);
    misaligned_blob[33] = static_cast<std::byte>(default_nonce[1]);
    misaligned_blob[34] = static_cast<std::byte>(default_nonce[2]);
    const auto misaligned_keystream = apply_range(wolf_chacha_context{misaligned_blob}, 0, 0, 64);
    CHECK(!std::equal(misaligned_keystream.begin(), misaligned_keystream.end(), base_keystream.begin()));

    // A nonce byte at offset 45 is significant.
    auto nonce_blob = base_blob;
    nonce_blob[45] = static_cast<std::byte>(std::to_integer<unsigned>(nonce_blob[45]) ^ 0x80u);
    const auto nonce_keystream = apply_range(wolf_chacha_context{nonce_blob}, 0, 0, 64);
    CHECK(!std::equal(nonce_keystream.begin(), nonce_keystream.end(), base_keystream.begin()));
}

TEST_CASE("arbitrary slice partitions rebuild one original call", "[wolfrpg][chacha]")
{
    const auto context = make_context(default_key, default_nonce);
    constexpr std::uint64_t call_start = 0x1234567;
    constexpr std::size_t total = 400;
    const auto oracle = vendor_keystream(default_key, default_nonce, static_cast<std::uint32_t>(call_start), total);

    const std::size_t slices[] = {1, 63, 64, 65, 1, 100, 31, 64, 11};
    std::size_t offset = 0;
    for (const std::size_t length : slices) {
        const auto slice = apply_range(context, call_start, offset, length);
        const auto expected = std::span<const std::byte>(oracle).subspan(offset, length);
        expect_bytes(slice, expected, "partition slice");
        offset += length;
    }
    CHECK(offset == total);
}

TEST_CASE("distinct contexts and repeated reads stay independent", "[wolfrpg][chacha]")
{
    auto other_key = default_key;
    other_key[0] = static_cast<unsigned>(other_key[0] ^ 0xFFu);
    const auto first = make_context(default_key, default_nonce);
    const auto second = make_context(other_key, default_nonce);

    const auto first_oracle = vendor_keystream(default_key, default_nonce, 0, 64);
    const auto second_oracle = vendor_keystream(other_key, default_nonce, 0, 64);

    const auto read_first = apply_range(first, 0, 0, 64);
    const auto read_second = apply_range(second, 0, 0, 64);
    const auto read_first_again = apply_range(first, 0, 0, 64);

    expect_bytes(read_first, first_oracle, "first context");
    expect_bytes(read_second, second_oracle, "second context");
    expect_bytes(read_first_again, first_oracle, "first context unchanged by the second");
}

TEST_CASE("the frozen canonical keystream matches at the call origin", "[wolfrpg][chacha]")
{
    const auto context = make_context(default_key, default_nonce);
    const auto frozen =
        from_hex("E4 73 73 99 4C D1 7B 64 F6 C6 C9 E8 8B 10 B1 0A 28 DA 2C 42 98 AD 50 E9 2B 6C 3D 70 29 D7 17"
                 "3A 81 38 02 D5 AA 36 47 51 C2 FA 4F 26 7B 45 CE 80 A2 1B DC 78 D9 E8 22 A8 75 39 A8 5C 72 E1 E0 E6");
    expect_bytes(apply_range(context, 0, 0, frozen.size()), frozen, "frozen canonical keystream");
}

TEST_CASE("barrier-synchronised concurrent reads observe no shared mutable state", "[wolfrpg][chacha][concurrency]")
{
    // A cluster of distinct contexts. Entry 0 is one immutable context that every worker reads at
    // the same time; entries 1..3 carry different keys and nonces. If the adapter kept any hidden
    // scratch, concurrent reads would cross-contaminate and stop matching this context's own bytes.
    auto alt_key = default_key;
    alt_key[0] = static_cast<unsigned>(alt_key[0] ^ 0x5Cu);
    alt_key[31] = static_cast<unsigned>(alt_key[31] ^ 0x11u);
    auto alt_nonce = default_nonce;
    alt_nonce[11] = static_cast<unsigned>(alt_nonce[11] ^ 0x3Eu);
    auto third_key = default_key;
    third_key[17] = static_cast<unsigned>(third_key[17] ^ 0xA7u);
    auto fourth_nonce = default_nonce;
    fourth_nonce[5] = static_cast<unsigned>(fourth_nonce[5] ^ 0x82u);

    const std::vector<wolf_chacha_context> contexts = {
        make_context(default_key, default_nonce),
        make_context(alt_key, default_nonce),
        make_context(default_key, alt_nonce),
        make_context(third_key, fourth_nonce),
    };
    const std::array<std::array<unsigned, key_size>, 4> keys = {default_key, alt_key, default_key, third_key};
    const std::array<std::array<unsigned, nonce_size>, 4> nonces = {default_nonce, default_nonce, alt_nonce,
                                                                    fourth_nonce};

    struct read_job
    {
        std::size_t context;
        std::uint64_t call_start;
        std::uint64_t offset;
        std::size_t length;
        byte_vector expected;
    };

    // Original-call origins, slice offsets and lengths around the 64- and 256-byte block
    // boundaries and both sides of the low-32 wrap point, over the shared context and the others.
    std::array<read_job, 12> jobs = {
        read_job{0, 0, 0, 64, {}},            // call origin
        read_job{0, 1, 0, 64, {}},            // origin off a block boundary
        read_job{0, 0, 63, 2, {}},            // slice straddling the 64-byte block boundary
        read_job{0, 0, 64, 64, {}},           // block-aligned slice
        read_job{0, 0, 255, 70, {}},          // slice straddling the 256-byte boundary
        read_job{0, 0xFFFFFFFF, 0, 64, {}},   // origin sitting on the low-32 wrap point
        read_job{0, 0xFFFFFFFF, 1, 64, {}},   // a 1-byte offset steps past 2^32u
        read_job{0, 0xFFFFFFFF, 256, 64, {}}, // a 256-byte offset steps well past 2^32u
        read_job{0, 0x100000007, 64, 64, {}}, // high bits truncate; the low 32 bits still steer
        read_job{1, 0, 0, 64, {}},            // a distinct key
        read_job{2, 0x1234, 32, 96, {}},      // a distinct nonce
        read_job{3, 0xFFFFFFFF, 128, 64, {}}, // distinct key and nonce crossing 2^32u
    };
    for (auto &job : jobs) {
        // Every reference vector is precomputed here, sequentially and outside any concurrent region.
        job.expected = vendor_expected(keys[job.context], nonces[job.context], job.call_start, job.offset, job.length);
    }

    constexpr std::size_t worker_count = 4;
    constexpr std::size_t repeats = 8;
    constexpr std::size_t rotation = 3;

    struct worker_output
    {
        std::vector<byte_vector> reads;
        byte_vector rejected_buffer;
        std::exception_ptr error;
    };

    std::vector<worker_output> outputs(worker_count);
    for (auto &out : outputs) {
        out.reads.resize(repeats * jobs.size());
    }

    // Every worker meets at the barrier before each round, so the same immutable context is read by
    // several threads at once while the staggered start makes different contexts run side by side.
    std::barrier start_line{static_cast<std::ptrdiff_t>(worker_count)};

    const auto read_rounds = [&](std::size_t index) {
        for (std::size_t round = 0; round < repeats; ++round) {
            start_line.arrive_and_wait();
            for (std::size_t step = 0; step < jobs.size(); ++step) {
                const std::size_t j = (step + index * rotation) % jobs.size();
                byte_vector data(jobs[j].length, std::byte{0});
                contexts[jobs[j].context].apply_call_range(data, jobs[j].call_start, jobs[j].offset);
                outputs[index].reads[round * jobs.size() + j] = std::move(data);
            }
        }

        // A rejected range raised inside the concurrent region keeps its own buffer untouched and
        // must not perturb any of the other workers.
        byte_vector rejected(64, std::byte{0xCD});
        try {
            contexts[0].apply_call_range(rejected, 0, 0xFFFFFFFFFFFFFFC0ULL);
        } catch (...) {
            outputs[index].error = std::current_exception();
        }
        outputs[index].rejected_buffer = std::move(rejected);
    };

    std::atomic<std::size_t> workers_exited{0};
    launch_together(worker_count, read_rounds, spawn_thread, workers_exited);
    // Every participant was joined before launch_together returned, so its body has finished.
    REQUIRE(workers_exited.load() == worker_count);

    // The workers only store an exception_ptr; the main thread rethrows it to prove the rejected
    // range surfaced as the codec contract error rather than as some other failure.
    const auto rejects_with_codec_error = [](const std::exception_ptr &error) {
        try {
            std::rethrow_exception(error);
        } catch (const chacha_error &) {
            return true;
        } catch (...) {
            return false;
        }
    };
    // The control must tell the codec contract error apart from any other failure type.
    CHECK(!rejects_with_codec_error(std::make_exception_ptr(std::runtime_error("probe"))));

    for (std::size_t m = 0; m < worker_count; ++m) {
        INFO("worker " << m);
        for (std::size_t j = 0; j < jobs.size(); ++j) {
            INFO("job " << j);
            for (std::size_t round = 0; round < repeats; ++round) {
                expect_bytes(outputs[m].reads[round * jobs.size() + j], jobs[j].expected,
                             "concurrent read matches its own context");
            }
        }
        REQUIRE(outputs[m].error != nullptr);
        CHECK(rejects_with_codec_error(outputs[m].error));
        CHECK(outputs[m].rejected_buffer == byte_vector(64, std::byte{0xCD}));
    }

    // Re-read every context in sequence once the storm is over: the concurrent readers left all of
    // them bit-identical to their vendor reference.
    for (const auto &job : jobs) {
        expect_bytes(apply_range(contexts[job.context], job.call_start, job.offset, job.length), job.expected,
                     "context unchanged after concurrent reads");
    }
}

TEST_CASE("a participant that cannot start releases the others without deadlocking",
          "[wolfrpg][chacha][concurrency][launch]")
{
    constexpr std::size_t worker_count = 4;
    constexpr std::size_t repeats = 8;

    // The same barrier shape as the green concurrency case: every participant must arrive, so a
    // participant that is never created would strand the started ones here forever.
    std::barrier start_line{static_cast<std::ptrdiff_t>(worker_count)};
    std::atomic<std::size_t> rounds_done{0};
    const auto body = [&](std::size_t) {
        for (std::size_t round = 0; round < repeats; ++round) {
            start_line.arrive_and_wait();
        }
        rounds_done.fetch_add(1, std::memory_order_relaxed);
    };

    // Deterministic, exhaustion-free controls: participant 1, then 2, then 3 cannot start, so 3, 2
    // and 1 participants respectively would be stranded at the barrier by a launcher that simply
    // joins the started threads while unwinding.
    for (const std::size_t fail_at : {std::size_t{1}, std::size_t{2}, std::size_t{3}}) {
        INFO("participant " << fail_at << " of " << worker_count << " cannot start");

        std::size_t created = 0;
        const auto failing_spawn = [&](std::function<void()> task) -> std::jthread {
            if (created == fail_at) {
                throw launch_fault{fail_at};
            }
            ++created;
            return std::jthread{std::move(task)};
        };

        std::atomic<std::size_t> exited{0};
        bool threw = false;
        try {
            launch_together(worker_count, body, failing_spawn, exited);
        } catch (const launch_fault &fault) {
            threw = true;
            // The launcher must surface the exact creation failure it was handed, not a substitute.
            CHECK(fault.participant() == fail_at);
        }

        CHECK(threw);
        // Exactly the participants that did start ran to completion; none was left waiting on the
        // barrier, and the failed participant was never created.
        CHECK(created == fail_at);
        CHECK(exited.load() == fail_at);
        // No started participant entered the barrier, so no round was ever completed.
        CHECK(rounds_done.load() == 0);
    }

    // Positive control: with a spawn that never fails, all four participants run and the barrier
    // completes every round, so the counters above really do track participation rather than a
    // constant.
    {
        std::size_t created = 0;
        const auto counting_spawn = [&](std::function<void()> task) -> std::jthread {
            ++created;
            return std::jthread{std::move(task)};
        };
        std::atomic<std::size_t> exited{0};
        launch_together(worker_count, body, counting_spawn, exited);
        CHECK(created == worker_count);
        CHECK(exited.load() == worker_count);
        CHECK(rounds_done.load() == worker_count);
    }
}
