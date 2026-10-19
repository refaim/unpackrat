// Unit tests for the private Wolf RPG classic-crypto adapter
// (unpackrat::wolfrpg::crypto). The expectations below are frozen literals produced
// independently by an operational oracle that links the real pinned UberWolfLib
// primitives (DXArchive::KeyCreate, DXArchive::KeyConv, WolfDec::DefaultCryptModes);
// see out/sol-team/crypto-tests/oracle/oracle_wolfrpg_crypto.cpp and
// out/sol-team/crypto-tests/logs/oracle-x64.txt. This translation unit depends on the
// adapter's own API only: it never includes vendor headers and never uses the adapter
// to compute its own expectations.
//
// Contract under test:
//   derive_key(material): accepts 0..2039 bytes, zero-pads a local 2040-byte buffer
//     and forwards it to the real DXArchive::KeyCreate. Lengths < 4 keep the library
//     DefaultKeyString fallback and embedded-NUL truncation. 2040+ is rejected with
//     crypto_error and an empty span is never handed to the library as nullptr.
//   apply_xor(data, position, key): the real classic DXArchive::KeyConv, with the
//     position reduced modulo 7 in 64-bit arithmetic before any s64 conversion; an
//     empty span is a no-op. The classic transform is applied through the shared,
//     globals-free kernel, so the process-wide DXArchive crypt-mode flags
//     (g_newCrypt / g_chacha20 / g_cryptVersion) can never change the result.
//   default_classic_password(): a borrowed, stable span over the real mode-3 key
//     bytes without the trailing NUL terminator (the Wolf RPG v2.225 default).

#include "../../core/wolfrpg/crypto.h"

#include <array>
#include <atomic>
#include <barrier>
#include <cstddef>
#include <cstdint>
#include <span>
#include <thread>
#include <vector>

#include <catch2/catch_test_macros.hpp>

namespace wolf = unpackrat::wolfrpg::crypto;

// The pinned DXArchive.cpp translation unit defines its crypt-mode flags as namespace-scope
// globals with external linkage (declared nowhere in a header). Declaring them here lets the
// regression drive the real library into a non-classic mode without inventing a fake backend.
extern bool g_newCrypt;
extern bool g_chacha20;
extern std::uint16_t g_cryptVersion;

namespace
{
    // ---- frozen expectations (see the oracle) ----

    constexpr std::array<std::byte, 30> kDefaultClassicPassword = {
        std::byte{0x57}, std::byte{0x4C}, std::byte{0x46}, std::byte{0x52}, std::byte{0x50}, std::byte{0x72},
        std::byte{0x4F}, std::byte{0x21}, std::byte{0x70}, std::byte{0x28}, std::byte{0x3B}, std::byte{0x73},
        std::byte{0x35}, std::byte{0x28}, std::byte{0x28}, std::byte{0x38}, std::byte{0x50}, std::byte{0x40},
        std::byte{0x28}, std::byte{0x28}, std::byte{0x55}, std::byte{0x46}, std::byte{0x57}, std::byte{0x6C},
        std::byte{0x75}, std::byte{0x24}, std::byte{0x23}, std::byte{0x35}, std::byte{0x28}, std::byte{0x3D}};
    constexpr std::size_t kDefaultClassicPasswordSize = 30;

    constexpr std::array<unsigned char, 7> kKeyLen0 = {0x5C, 0xAE, 0x2B, 0xB1, 0x23, 0xB3, 0xD9};
    constexpr std::array<unsigned char, 7> kKeyLen1 = {0xF2, 0xE1, 0x1F, 0xFE, 0x5C, 0xAE, 0x2B};
    constexpr std::array<unsigned char, 7> kKeyLen2 = {0x8D, 0xFC, 0xED, 0x11, 0x83, 0x83, 0x0F};
    constexpr std::array<unsigned char, 7> kKeyLen3 = {0x00, 0x01, 0x4B, 0x15, 0xFC, 0x9E, 0xFD};
    constexpr std::array<unsigned char, 7> kKeyLen4 = {0xCE, 0x61, 0x44, 0x00, 0x23, 0x62, 0x5A};
    constexpr std::array<unsigned char, 7> kKeyLen5 = {0xAE, 0x49, 0xD6, 0x23, 0x23, 0x62, 0x5A};
    constexpr std::array<unsigned char, 7> kKeyLen6 = {0xAE, 0x49, 0xD6, 0x23, 0x60, 0x41, 0xC0};
    constexpr std::array<unsigned char, 7> kKeyLen7 = {0x5E, 0xEB, 0x90, 0xA6, 0x60, 0x41, 0xC0};
    constexpr std::array<unsigned char, 7> kKeyLen1024 = {0x96, 0x87, 0x56, 0xD0, 0xE3, 0xFD, 0x63};
    constexpr std::array<unsigned char, 7> kKeyLen1025 = {0xC1, 0xDE, 0xB7, 0x52, 0xE3, 0xFD, 0x63};
    constexpr std::array<unsigned char, 7> kKeyLen2039 = {0xD1, 0xB9, 0x86, 0xD0, 0x61, 0x1B, 0xCA};
    constexpr std::array<unsigned char, 7> kKeyMode3 = {0x56, 0xF0, 0x6C, 0xA7, 0x87, 0xCF, 0x0E};

    constexpr std::array<unsigned char, 7> kKeyNulOnly = {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
    constexpr std::array<unsigned char, 7> kKeyANul = {0x8B, 0x9E, 0xD9, 0xD3, 0x00, 0x00, 0x00};
    constexpr std::array<unsigned char, 7> kKeyABNul = {0x8B, 0x9E, 0xD9, 0xD3, 0x31, 0xCF, 0xD0};
    constexpr std::array<unsigned char, 7> kKeyABNulC = {0xBB, 0x6C, 0xBB, 0xA8, 0x52, 0x2F, 0x43};

    constexpr std::array<std::byte, 16> kXorInput = {
        std::byte{0x01}, std::byte{0x02}, std::byte{0x03}, std::byte{0x04}, std::byte{0x05}, std::byte{0x06},
        std::byte{0x07}, std::byte{0x08}, std::byte{0x09}, std::byte{0x0A}, std::byte{0x0B}, std::byte{0x0C},
        std::byte{0x0D}, std::byte{0x0E}, std::byte{0x0F}, std::byte{0x10}};
    constexpr std::array<unsigned char, 7> kXorKey = {0x56, 0xF0, 0x6C, 0xA7, 0x87, 0xCF, 0x0E};
    constexpr std::array<std::byte, 16> kXorExpectedPos0 = {
        std::byte{0x57}, std::byte{0xF2}, std::byte{0x6F}, std::byte{0xA3}, std::byte{0x82}, std::byte{0xC9},
        std::byte{0x09}, std::byte{0x5E}, std::byte{0xF9}, std::byte{0x66}, std::byte{0xAC}, std::byte{0x8B},
        std::byte{0xC2}, std::byte{0x00}, std::byte{0x59}, std::byte{0xE0}};
    constexpr std::array<std::byte, 16> kXorExpectedPos1 = {
        std::byte{0xF1}, std::byte{0x6E}, std::byte{0xA4}, std::byte{0x83}, std::byte{0xCA}, std::byte{0x08},
        std::byte{0x51}, std::byte{0xF8}, std::byte{0x65}, std::byte{0xAD}, std::byte{0x8C}, std::byte{0xC3},
        std::byte{0x03}, std::byte{0x58}, std::byte{0xFF}, std::byte{0x7C}};
    constexpr std::array<std::byte, 16> kXorExpectedPos2 = {
        std::byte{0x6D}, std::byte{0xA5}, std::byte{0x84}, std::byte{0xCB}, std::byte{0x0B}, std::byte{0x50},
        std::byte{0xF7}, std::byte{0x64}, std::byte{0xAE}, std::byte{0x8D}, std::byte{0xC4}, std::byte{0x02},
        std::byte{0x5B}, std::byte{0xFE}, std::byte{0x63}, std::byte{0xB7}};
    constexpr std::array<std::byte, 16> kXorExpectedPos3 = {
        std::byte{0xA6}, std::byte{0x85}, std::byte{0xCC}, std::byte{0x0A}, std::byte{0x53}, std::byte{0xF6},
        std::byte{0x6B}, std::byte{0xAF}, std::byte{0x8E}, std::byte{0xC5}, std::byte{0x05}, std::byte{0x5A},
        std::byte{0xFD}, std::byte{0x62}, std::byte{0xA8}, std::byte{0x97}};
    constexpr std::array<std::byte, 16> kXorExpectedPos4 = {
        std::byte{0x86}, std::byte{0xCD}, std::byte{0x0D}, std::byte{0x52}, std::byte{0xF5}, std::byte{0x6A},
        std::byte{0xA0}, std::byte{0x8F}, std::byte{0xC6}, std::byte{0x04}, std::byte{0x5D}, std::byte{0xFC},
        std::byte{0x61}, std::byte{0xA9}, std::byte{0x88}, std::byte{0xDF}};
    constexpr std::array<std::byte, 16> kXorExpectedPos5 = {
        std::byte{0xCE}, std::byte{0x0C}, std::byte{0x55}, std::byte{0xF4}, std::byte{0x69}, std::byte{0xA1},
        std::byte{0x80}, std::byte{0xC7}, std::byte{0x07}, std::byte{0x5C}, std::byte{0xFB}, std::byte{0x60},
        std::byte{0xAA}, std::byte{0x89}, std::byte{0xC0}, std::byte{0x1E}};
    constexpr std::array<std::byte, 16> kXorExpectedPos6 = {
        std::byte{0x0F}, std::byte{0x54}, std::byte{0xF3}, std::byte{0x68}, std::byte{0xA2}, std::byte{0x81},
        std::byte{0xC8}, std::byte{0x06}, std::byte{0x5F}, std::byte{0xFA}, std::byte{0x67}, std::byte{0xAB},
        std::byte{0x8A}, std::byte{0xC1}, std::byte{0x01}, std::byte{0x46}};
    // UINT64_MAX % 7 == 1, so the maximum position must match position 1 exactly.
    constexpr std::array<std::byte, 16> kXorExpectedPosMax = {
        std::byte{0xF1}, std::byte{0x6E}, std::byte{0xA4}, std::byte{0x83}, std::byte{0xCA}, std::byte{0x08},
        std::byte{0x51}, std::byte{0xF8}, std::byte{0x65}, std::byte{0xAD}, std::byte{0x8C}, std::byte{0xC3},
        std::byte{0x03}, std::byte{0x58}, std::byte{0xFF}, std::byte{0x7C}};

    constexpr std::array<unsigned char, 7> kInterleaveKeyA = {0xAE, 0x49, 0xD6, 0x23, 0x23, 0x62, 0x5A};
    constexpr std::array<unsigned char, 7> kInterleaveKeyB = {0xAE, 0x49, 0xD6, 0x23, 0x60, 0x41, 0xC0};
    constexpr std::array<std::byte, 14> kInterleaveInput = {
        std::byte{0x80}, std::byte{0x81}, std::byte{0x82}, std::byte{0x83}, std::byte{0x84},
        std::byte{0x85}, std::byte{0x86}, std::byte{0x87}, std::byte{0x88}, std::byte{0x89},
        std::byte{0x8A}, std::byte{0x8B}, std::byte{0x8C}, std::byte{0x8D}};
    constexpr std::array<std::byte, 14> kInterleaveExpected = {
        std::byte{0x2E}, std::byte{0xC8}, std::byte{0x54}, std::byte{0xA0}, std::byte{0xE4},
        std::byte{0xE7}, std::byte{0xDC}, std::byte{0x29}, std::byte{0xC1}, std::byte{0x5F},
        std::byte{0xA9}, std::byte{0xEB}, std::byte{0xCD}, std::byte{0x4D}};

    // ---- helpers ----

    std::array<std::byte, 7> to_bytes(const std::array<unsigned char, 7> &raw)
    {
        std::array<std::byte, 7> out{};
        for (std::size_t i = 0; i < out.size(); ++i) {
            out[i] = static_cast<std::byte>(raw[i]);
        }
        return out;
    }

    wolf::key make_key(const std::array<unsigned char, 7> &raw)
    {
        return wolf::key{to_bytes(raw)};
    }

    // Deterministic, non-trivial material: byte i = (i * 7 + 3) & 0xFF. Identical to the
    // generator used by the oracle.
    std::vector<std::byte> patterned(std::size_t length)
    {
        std::vector<std::byte> material(length);
        for (std::size_t i = 0; i < length; ++i) {
            material[i] = static_cast<std::byte>((i * 7u + 3u) & 0xFFu);
        }
        return material;
    }

    void check_key(const wolf::key &actual, const std::array<unsigned char, 7> &expected)
    {
        CHECK(actual.bytes == to_bytes(expected));
    }

    struct derive_case
    {
        std::size_t length;
        std::array<unsigned char, 7> expected;
        const char *name;
    };

    // Restores the real library's process-wide crypt-mode flags on scope exit, so a
    // contamination case cannot leak its mode into any later test.
    class mode_guard
    {
      public:
        mode_guard() : new_crypt_(g_newCrypt), chacha20_(g_chacha20), version_(g_cryptVersion)
        {
        }
        ~mode_guard()
        {
            g_newCrypt = new_crypt_;
            g_chacha20 = chacha20_;
            g_cryptVersion = version_;
        }
        mode_guard(const mode_guard &) = delete;
        mode_guard &operator=(const mode_guard &) = delete;

      private:
        bool new_crypt_;
        bool chacha20_;
        std::uint16_t version_;
    };
} // namespace

TEST_CASE("derive_key matches the pinned KeyCreate for patterned lengths", "[wolfrpg][crypto]")
{
    const std::array<derive_case, 10> cases = {{
        {0, kKeyLen0, "length 0"},
        {1, kKeyLen1, "length 1"},
        {2, kKeyLen2, "length 2"},
        {3, kKeyLen3, "length 3"},
        {4, kKeyLen4, "length 4"},
        {5, kKeyLen5, "length 5 (odd)"},
        {6, kKeyLen6, "length 6 (even)"},
        {7, kKeyLen7, "length 7 (odd)"},
        {1024, kKeyLen1024, "length 1024"},
        {1025, kKeyLen1025, "length 1025"},
    }};

    for (const derive_case &entry : cases) {
        INFO(entry.name);
        const std::vector<std::byte> material = patterned(entry.length);
        check_key(wolf::derive_key(material), entry.expected);
    }
}

TEST_CASE("derive_key handles the upper 2039-byte bound", "[wolfrpg][crypto]")
{
    const std::vector<std::byte> material = patterned(2039);
    check_key(wolf::derive_key(material), kKeyLen2039);
}

TEST_CASE("derive_key of the mode-3 default password matches the frozen key", "[wolfrpg][crypto]")
{
    const std::span<const std::byte> password = wolf::default_classic_password();
    check_key(wolf::derive_key(password), kKeyMode3);
}

TEST_CASE("derive_key preserves the library DefaultKeyString fallback for short inputs", "[wolfrpg][crypto]")
{
    // A zero-length span must not be forwarded as a nullptr source.
    const std::span<const std::byte> empty{};
    check_key(wolf::derive_key(empty), kKeyLen0);

    const std::array<std::byte, 1> a{std::byte{0x03}};
    check_key(wolf::derive_key(a), kKeyLen1);
}

TEST_CASE("derive_key truncates short material at an embedded NUL", "[wolfrpg][crypto]")
{
    const std::array<std::byte, 1> only_nul{std::byte{0x00}};
    check_key(wolf::derive_key(only_nul), kKeyNulOnly);

    const std::array<std::byte, 2> a_nul{std::byte{0x41}, std::byte{0x00}};
    check_key(wolf::derive_key(a_nul), kKeyANul);

    const std::array<std::byte, 3> ab_nul{std::byte{0x41}, std::byte{0x42}, std::byte{0x00}};
    check_key(wolf::derive_key(ab_nul), kKeyABNul);

    // Once the material reaches four bytes the library reads the exact bytes, so an
    // embedded NUL is treated as real key material rather than a terminator.
    const std::array<std::byte, 4> ab_nul_c{std::byte{0x41}, std::byte{0x42}, std::byte{0x00}, std::byte{0x43}};
    check_key(wolf::derive_key(ab_nul_c), kKeyABNulC);
}

TEST_CASE("derive_key rejects oversize material with crypto_error", "[wolfrpg][crypto]")
{
    const std::vector<std::byte> at_limit = patterned(2039);
    const std::vector<std::byte> just_over = patterned(2040);
    const std::vector<std::byte> way_over = patterned(4096);

    CHECK_NOTHROW(wolf::derive_key(at_limit));
    CHECK_THROWS_AS(wolf::derive_key(just_over), wolf::crypto_error);
    CHECK_THROWS_AS(wolf::derive_key(way_over), wolf::crypto_error);
}

TEST_CASE("apply_xor matches the pinned classic KeyConv for every key phase", "[wolfrpg][crypto]")
{
    const wolf::key cipher = make_key(kXorKey);

    struct xor_case
    {
        std::uint64_t position;
        const std::array<std::byte, 16> *expected;
    };

    const std::array<xor_case, 8> cases = {{
        {0, &kXorExpectedPos0},
        {1, &kXorExpectedPos1},
        {2, &kXorExpectedPos2},
        {3, &kXorExpectedPos3},
        {4, &kXorExpectedPos4},
        {5, &kXorExpectedPos5},
        {6, &kXorExpectedPos6},
        {UINT64_MAX, &kXorExpectedPosMax},
    }};

    for (const xor_case &entry : cases) {
        INFO("position " << entry.position);
        std::array<std::byte, 16> data = kXorInput;
        wolf::apply_xor(data, entry.position, cipher);
        CHECK(data == *entry.expected);
    }
}

TEST_CASE("apply_xor is independent of chunk boundaries", "[wolfrpg][crypto]")
{
    const wolf::key cipher = make_key(kXorKey);

    // Multi-way splits that re-enter the primitive at an advanced position must reproduce
    // the single-shot frozen result exactly.
    const std::array<std::size_t, 3> split_a = {1, 8, 16};
    const std::array<std::size_t, 4> split_b = {3, 7, 12, 16};

    {
        std::array<std::byte, 16> data = kXorInput;
        std::span<std::byte> view(data);
        std::size_t start = 0;
        for (const std::size_t stop : split_a) {
            wolf::apply_xor(view.subspan(start, stop - start), start, cipher);
            start = stop;
        }
        CHECK(data == kXorExpectedPos0);
    }

    {
        std::array<std::byte, 16> data = kXorInput;
        std::span<std::byte> view(data);
        std::size_t start = 0;
        for (const std::size_t stop : split_b) {
            wolf::apply_xor(view.subspan(start, stop - start), start, cipher);
            start = stop;
        }
        CHECK(data == kXorExpectedPos0);
    }
}

TEST_CASE("apply_xor on an empty span is a no-op", "[wolfrpg][crypto]")
{
    const wolf::key cipher = make_key(kXorKey);
    std::array<std::byte, 16> data = kXorInput;

    wolf::apply_xor(std::span<std::byte>{}, 0, cipher);
    wolf::apply_xor(std::span<std::byte>{}, UINT64_MAX, cipher);
    wolf::apply_xor(std::span<std::byte>(data).subspan(0, 0), 5, cipher);
    wolf::apply_xor(std::span<std::byte>(data).subspan(16, 0), 6, cipher);

    CHECK(data == kXorInput);
}

TEST_CASE("apply_xor is independent of the process-wide DXArchive crypt mode", "[wolfrpg][crypto]")
{
    // DXArchive::KeyConv used to be reached directly and checks g_newCrypt / g_chacha20 before it
    // ever looks at the key, so any other consumer of the pinned library that switched the
    // process-wide mode silently re-keyed the classic transform. The adapter must produce the
    // accepted classic bytes for a frozen 7-byte key whatever mode the library is left in.
    const wolf::key cipher = make_key(kXorKey);

    struct mode_case
    {
        const char *name;
        bool new_crypt;
        bool chacha20;
        std::uint16_t version;
    };

    const std::array<mode_case, 3> modes = {{
        {"classic (default)", false, false, 0},
        {"new crypt v3.50", true, false, 350},
        {"ChaCha20 (0x64)", false, true, 0x64},
    }};

    for (const mode_case &mode : modes) {
        INFO(mode.name);
        mode_guard guard;
        g_newCrypt = mode.new_crypt;
        g_chacha20 = mode.chacha20;
        g_cryptVersion = mode.version;

        {
            std::array<std::byte, 16> data = kXorInput;
            wolf::apply_xor(data, 0, cipher);
            CHECK(data == kXorExpectedPos0);
        }
        {
            std::array<std::byte, 16> data = kXorInput;
            wolf::apply_xor(data, 1, cipher);
            CHECK(data == kXorExpectedPos1);
        }
        {
            std::array<std::byte, 16> data = kXorInput;
            wolf::apply_xor(data, UINT64_MAX, cipher);
            CHECK(data == kXorExpectedPosMax);
        }
    }
}

TEST_CASE("default_classic_password is the exact stable mode-3 key", "[wolfrpg][crypto]")
{
    const std::span<const std::byte> password = wolf::default_classic_password();

    REQUIRE(password.size() == kDefaultClassicPasswordSize);
    CHECK(password.data() != nullptr);
    for (std::size_t i = 0; i < password.size(); ++i) {
        INFO("byte " << i);
        CHECK(password[i] == kDefaultClassicPassword[i]);
    }

    // Borrowed, stable lifetime: repeated calls expose the same storage.
    const std::span<const std::byte> again = wolf::default_classic_password();
    CHECK(again.data() == password.data());
    CHECK(again.size() == password.size());
}

TEST_CASE("apply_xor keeps interleaved keys independent", "[wolfrpg][crypto]")
{
    const wolf::key key_a = make_key(kInterleaveKeyA);
    const wolf::key key_b = make_key(kInterleaveKeyB);

    std::array<std::byte, 14> data = kInterleaveInput;
    std::span<std::byte> view(data);
    wolf::apply_xor(view.subspan(0, 2), 0, key_a);
    wolf::apply_xor(view.subspan(2, 3), 2, key_b);
    wolf::apply_xor(view.subspan(5, 4), 5, key_a);
    wolf::apply_xor(view.subspan(9, 5), 9, key_b);

    CHECK(data == kInterleaveExpected);
}

TEST_CASE("derive_key is stable across concurrent first use in a fresh process", "[wolfrpg][crypto]")
{
    // The real KeyCreate lazily builds an unsynchronised CRC table, so the first derives
    // of a fresh process race unless the adapter serialises them. Every thread here must
    // observe the frozen key for its material regardless of interleaving.
    constexpr std::size_t kThreads = 8;
    constexpr std::size_t kIterations = 64;

    const std::array<derive_case, 5> cases = {{
        {0, kKeyLen0, "length 0"},
        {4, kKeyLen4, "length 4"},
        {1024, kKeyLen1024, "length 1024"},
        {1025, kKeyLen1025, "length 1025"},
        {2039, kKeyLen2039, "length 2039"},
    }};

    std::array<std::array<std::byte, 7>, kThreads * cases.size()> results{};
    std::array<std::thread, kThreads> workers;
    std::atomic<std::size_t> failures{0};
    std::barrier rendezvous(kThreads);

    for (std::size_t t = 0; t < kThreads; ++t) {
        workers[t] = std::thread([&cases, &results, &failures, &rendezvous, t] {
            // Release every worker into its first derive together, so the process-wide
            // first use of the vendor CRC table is genuinely contended.
            rendezvous.arrive_and_wait();
            for (std::size_t iter = 0; iter < kIterations; ++iter) {
                for (std::size_t c = 0; c < cases.size(); ++c) {
                    const std::vector<std::byte> material = patterned(cases[c].length);
                    const wolf::key derived = wolf::derive_key(material);
                    results[t * cases.size() + c] = derived.bytes;
                    if (derived.bytes != to_bytes(cases[c].expected)) {
                        failures.fetch_add(1, std::memory_order_relaxed);
                    }
                }
            }
        });
    }

    for (std::thread &worker : workers) {
        worker.join();
    }

    CHECK(failures.load() == 0);
    for (std::size_t t = 0; t < kThreads; ++t) {
        for (std::size_t c = 0; c < cases.size(); ++c) {
            INFO("thread " << t << " case " << cases[c].name);
            CHECK(results[t * cases.size() + c] == to_bytes(cases[c].expected));
        }
    }
}
