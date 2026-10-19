#include "../../core/wolfrpg/modern_context.h"
#include "../support/wolfrpg_shared_crypto_oracle.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include <uberwolf/WolfCrypt/ModernContext.hpp>
#include <uberwolf/WolfCrypt/WolfAes.hpp>
#include <uberwolf/WolfCrypt/WolfCrypt.hpp>
#include <uberwolf/WolfCrypt/WolfRng.hpp>

#include <catch2/catch_test_macros.hpp>

// Behavioural regression for the shared AES-CTR counter/range helpers and the operation-local
// RNG key schedule that shared-rng-aes.patch factors out of the vendor WolfAes / WolfRng /
// WolfCrypt headers. The oracle is *not* a second call into the refactored helpers: every
// expected byte and fingerprint below is a frozen literal emitted by the UNMODIFIED pinned
// primitives (see wolfrpg_shared_crypto_oracle.h and out/modern-stage/gen_shared_oracle.cpp), so
// a copied twin, a wrong recurrence or a wrong counter step fails here. The legacy CRT entry
// points are asserted against the same literals, which pins the shared template to the original
// algorithm rather than only to itself.
namespace
{
    namespace aes = wolf::crypt::aes;
    namespace rng = wolf::crypt::rng;
    namespace oracle = test::support::wolfrpg_shared_crypto_oracle;
    using wolf::crypt::CryptData;

    [[nodiscard]] std::uint8_t hex_nibble(const char digit)
    {
        if (digit >= '0' && digit <= '9') {
            return static_cast<std::uint8_t>(digit - '0');
        }
        return static_cast<std::uint8_t>(digit - 'a' + 10);
    }

    [[nodiscard]] std::vector<std::uint8_t> unhex(const std::string_view hex)
    {
        std::vector<std::uint8_t> bytes;
        bytes.reserve(hex.size() / 2);
        for (std::size_t i = 0; i + 1 < hex.size(); i += 2) {
            bytes.push_back(static_cast<std::uint8_t>((hex_nibble(hex[i]) << 4) | hex_nibble(hex[i + 1])));
        }
        return bytes;
    }

    template <std::size_t N>
    [[nodiscard]] std::array<std::uint8_t, N> unhex_array(const std::string_view hex)
    {
        const std::vector<std::uint8_t> bytes = unhex(hex);
        REQUIRE(bytes.size() == N);
        std::array<std::uint8_t, N> out{};
        std::copy(bytes.begin(), bytes.end(), out.begin());
        return out;
    }

    [[nodiscard]] std::string hex(const std::uint8_t *p, const std::size_t n)
    {
        static constexpr char digits[] = "0123456789abcdef";
        std::string out;
        out.reserve(n * 2);
        for (std::size_t i = 0; i < n; ++i) {
            out.push_back(digits[p[i] >> 4]);
            out.push_back(digits[p[i] & 0x0F]);
        }
        return out;
    }

    // FNV-1a over the explicit RngData fields (seed1, seed2, counter and every data word, each
    // little-endian). It never reads padding or the C++ object representation, matching the
    // generator that produced the frozen fingerprints.
    [[nodiscard]] std::uint64_t fingerprint(const rng::RngData &rd)
    {
        std::uint64_t h = 1469598103934665603ull;
        auto feed32 = [&](const std::uint32_t word) {
            std::uint8_t bytes[4];
            for (int i = 0; i < 4; ++i) {
                bytes[i] = static_cast<std::uint8_t>(word >> (8 * i));
            }
            for (const std::uint8_t byte : bytes) {
                h ^= byte;
                h *= 1099511628211ull;
            }
        };
        feed32(rd.seed1);
        feed32(rd.seed2);
        feed32(rd.counter);
        for (const auto &outer : rd.data) {
            for (const std::uint32_t word : outer) {
                feed32(word);
            }
        }
        return h;
    }

    // The frozen 192-byte schedule, rebuilt through the (unmodified vendor) initAES128 so the
    // input derivation is asserted too rather than copied from the oracle hex.
    [[nodiscard]] std::array<std::uint8_t, aes::ROUND_KEY_SIZE> make_schedule()
    {
        std::array<std::uint8_t, aes::ROUND_KEY_SIZE> schedule{};
        std::array<std::uint8_t, 15> password{};
        for (std::size_t i = 0; i < password.size(); ++i) {
            password[i] = static_cast<std::uint8_t>(0x30 + i * 5);
        }
        aes::initAES128(schedule.data(), password.data(), nullptr, 331);
        return schedule;
    }

    [[nodiscard]] std::array<std::uint8_t, 16> base_iv_of(
        const std::array<std::uint8_t, aes::ROUND_KEY_SIZE> &schedule)
    {
        std::array<std::uint8_t, 16> iv{};
        std::memcpy(iv.data(), schedule.data() + aes::KEY_EXP_SIZE, iv.size());
        return iv;
    }

    [[nodiscard]] std::vector<std::uint8_t> patterned(const std::size_t n)
    {
        std::vector<std::uint8_t> bytes(n);
        for (std::size_t i = 0; i < n; ++i) {
            bytes[i] = oracle::patterned_byte(i);
        }
        return bytes;
    }
} // namespace

TEST_CASE("shared AES: the pinned initAES128 schedule matches the frozen oracle", "[wolfrpg][sharedaes]")
{
    const auto schedule = make_schedule();
    CHECK(hex(schedule.data(), schedule.size()) == oracle::ROUND_KEY_HEX);

    const auto base_iv = base_iv_of(schedule);
    CHECK(hex(base_iv.data(), base_iv.size()) == oracle::RANGE_BASE_IV_HEX);
}

TEST_CASE("shared AES: the seekable range cipher reproduces the frozen sequential oracle",
          "[wolfrpg][sharedaes]")
{
    const auto schedule = make_schedule();
    const auto base_iv = base_iv_of(schedule);
    const std::vector<std::uint8_t> cipher = unhex(oracle::RANGE_CIPHER_HEX);
    REQUIRE(cipher.size() == oracle::RANGE_TOTAL);

    // The whole range is byte-identical to one original sequential call and leaves the same IV.
    {
        std::vector<std::uint8_t> whole = patterned(oracle::RANGE_TOTAL);
        std::array<std::uint8_t, 16> iv = base_iv;
        aes::aesCtrXCryptRange(whole.data(), schedule.data(), iv.data(), 0, oracle::RANGE_TOTAL);
        CHECK(whole == cipher);
        CHECK(hex(iv.data(), iv.size()) == oracle::RANGE_FINAL_IV_HEX);
    }

    // Every unaligned span matches the frozen cipher's matching sub-range.
    for (const std::uint32_t offset : {0u, 1u, 15u, 16u, 17u, 31u, 32u, 271u}) {
        for (const std::size_t size : {std::size_t{1}, std::size_t{15}, std::size_t{16}, std::size_t{17},
                                       std::size_t{32}, std::size_t{33}, std::size_t{100}}) {
            INFO("offset " << offset << " size " << size);
            std::vector<std::uint8_t> buffer = patterned(oracle::RANGE_TOTAL);
            std::array<std::uint8_t, 16> iv = base_iv;
            aes::aesCtrXCryptRange(buffer.data() + offset, schedule.data(), iv.data(), offset, size);
            CHECK(std::equal(buffer.begin() + static_cast<std::ptrdiff_t>(offset),
                             buffer.begin() + static_cast<std::ptrdiff_t>(offset + size),
                             cipher.begin() + static_cast<std::ptrdiff_t>(offset)));
        }
    }
}

TEST_CASE("shared AES: the range counter advances to the frozen sequential counter", "[wolfrpg][sharedaes]")
{
    const auto schedule = make_schedule();
    const auto base_iv = base_iv_of(schedule);

    for (const oracle::range_run &run : oracle::RANGE_RUNS) {
        INFO("offset " << run.offset << " size " << run.size);
        std::vector<std::uint8_t> buffer = patterned(static_cast<std::size_t>(run.offset) + run.size);
        std::array<std::uint8_t, 16> iv = base_iv;
        aes::aesCtrXCryptRange(buffer.data() + run.offset, schedule.data(), iv.data(), run.offset, run.size);
        CHECK(hex(iv.data(), iv.size()) == run.iv_hex);
    }
}

TEST_CASE("shared AES: the addCounter step carries and wraps exactly like the original loop",
          "[wolfrpg][sharedaes]")
{
    for (const oracle::counter_case &test : oracle::COUNTER_CASES) {
        INFO("case " << test.name);
        std::array<std::uint8_t, 16> iv = unhex_array<16>(test.base_iv_hex);
        aes::addCounter(iv.data(), test.blocks);
        CHECK(hex(iv.data(), iv.size()) == test.next_iv_hex);
    }
}

TEST_CASE("shared AES: an empty range emits nothing and leaves its counter untouched",
          "[wolfrpg][sharedaes]")
{
    const auto schedule = make_schedule();
    const auto base_iv = base_iv_of(schedule);

    for (const std::uint32_t offset : {0u, 1u, 16u, 4096u}) {
        INFO("offset " << offset);
        std::array<std::uint8_t, 16> iv = base_iv;
        std::vector<std::uint8_t> buffer(8, 0xAB);
        aes::aesCtrXCryptRange(buffer.data(), schedule.data(), iv.data(), offset, 0);
        CHECK(std::all_of(buffer.begin(), buffer.end(), [](const std::uint8_t b) { return b == 0xAB; }));
        CHECK(hex(iv.data(), iv.size()) == oracle::RANGE_BASE_IV_HEX);
    }
}

TEST_CASE("shared AES: a split range agrees with one call and the frozen oracle", "[wolfrpg][sharedaes]")
{
    const auto schedule = make_schedule();
    const auto base_iv = base_iv_of(schedule);
    const std::vector<std::uint8_t> cipher = unhex(oracle::RANGE_CIPHER_HEX);

    const std::uint32_t offset = 7;
    const std::size_t total = 100;

    // The range helper always starts from the caller's origin IV and seeks internally, so each
    // call gets a fresh copy of the base IV exactly like a real seekable reader.
    auto run_at = [&](std::vector<std::uint8_t> &bytes, const std::uint32_t start, const std::size_t size) {
        std::array<std::uint8_t, 16> iv = base_iv;
        aes::aesCtrXCryptRange(bytes.data() + start, schedule.data(), iv.data(), start, size);
        return iv;
    };

    std::vector<std::uint8_t> whole = patterned(offset + total);
    const std::array<std::uint8_t, 16> whole_iv = run_at(whole, offset, total);

    std::vector<std::uint8_t> split = patterned(offset + total);
    run_at(split, offset, 40);
    const std::array<std::uint8_t, 16> split_iv = run_at(split, offset + 40, total - 40);

    CHECK(split == whole);
    CHECK(std::equal(whole.begin() + static_cast<std::ptrdiff_t>(offset), whole.end(),
                     cipher.begin() + static_cast<std::ptrdiff_t>(offset)));
    CHECK(hex(split_iv.data(), split_iv.size()) == hex(whole_iv.data(), whole_iv.size()));
}

TEST_CASE("shared thin header: aesCtrXor and advanceCounter match the frozen oracle", "[wolfrpg][sharedaes]")
{
    using wolf::crypt::modern::advanceCounter;
    using wolf::crypt::modern::aesCtrXor;

    const auto schedule = make_schedule();
    const auto base_iv = base_iv_of(schedule);
    const std::vector<std::uint8_t> cipher = unhex(oracle::RANGE_CIPHER_HEX);
    const auto schedule_span = std::span<const std::uint8_t, aes::KEY_EXP_SIZE>(schedule.data(), aes::KEY_EXP_SIZE);

    std::vector<std::uint8_t> whole = patterned(oracle::RANGE_TOTAL);
    aesCtrXor(std::span<std::uint8_t>(whole), schedule_span,
              std::span<const std::uint8_t, 16>(base_iv.data(), 16), 0);
    CHECK(whole == cipher);

    // An unaligned sub-range through the thin header, whose base IV must stay immutable.
    std::vector<std::uint8_t> sub = patterned(107);
    std::array<std::uint8_t, 16> sub_iv = base_iv;
    aesCtrXor(std::span<std::uint8_t>(sub).subspan(7), schedule_span,
              std::span<const std::uint8_t, 16>(sub_iv.data(), 16), 7);
    CHECK(std::equal(sub.begin() + 7, sub.end(), cipher.begin() + 7));
    CHECK(sub_iv == base_iv);

    for (const oracle::counter_case &test : oracle::COUNTER_CASES) {
        INFO("case " << test.name);
        std::array<std::uint8_t, 16> iv = unhex_array<16>(test.base_iv_hex);
        advanceCounter(std::span<std::uint8_t, 16>(iv.data(), 16), test.blocks);
        CHECK(hex(iv.data(), iv.size()) == test.next_iv_hex);
    }
}

TEST_CASE("shared RNG: the operation-local chain reproduces the frozen original chain",
          "[wolfrpg][sharedrng]")
{
    for (const oracle::chain_case &test : oracle::CHAIN_CASES) {
        INFO("case " << test.name);

        // The legacy CRT entry point and the operation-local entry point must both match the
        // frozen serialized chain, so the shared recurrence is pinned to the original algorithm
        // instead of only to a sibling refactor.
        rng::RngData legacy;
        rng::runRngChain(legacy, test.seed1, test.seed2);
        rng::RngData owned;
        wolf::crypt::modern::runRngChain(owned, test.seed1, test.seed2);

        // runRngChain mutates the whole state, so the post-chain seed/counter words are frozen
        // fields rather than the inputs.
        CHECK(legacy.seed1 == test.out_seed1);
        CHECK(legacy.seed2 == test.out_seed2);
        CHECK(legacy.counter == test.out_counter);
        CHECK(fingerprint(legacy) == test.fingerprint);

        CHECK(owned.seed1 == test.out_seed1);
        CHECK(owned.seed2 == test.out_seed2);
        CHECK(owned.counter == test.out_counter);
        CHECK(fingerprint(owned) == test.fingerprint);
    }
}

TEST_CASE("shared RNG: the operation-local AES key schedule reproduces the frozen original",
          "[wolfrpg][sharedrng]")
{
    for (const oracle::keygen_case &test : oracle::KEYGEN_CASES) {
        INFO("case " << test.name);

        CryptData cd;
        std::copy(std::begin(test.seed_bytes), std::end(test.seed_bytes), cd.seedBytes.begin());
        std::copy(std::begin(test.key_bytes), std::end(test.key_bytes), cd.keyBytes.begin());

        rng::RngData legacy_rng;
        aes::AesKey legacy_key{};
        aes::AesIV legacy_iv{};
        wolf::crypt::aesKeyGen(cd, legacy_rng, legacy_key, legacy_iv);

        rng::RngData owned_rng;
        aes::AesKey owned_key{};
        aes::AesIV owned_iv{};
        wolf::crypt::modern::aesKeyGen(cd, owned_rng, owned_key, owned_iv);

        CHECK(hex(legacy_key.data(), legacy_key.size()) == test.key_hex);
        CHECK(hex(legacy_iv.data(), legacy_iv.size()) == test.iv_hex);
        CHECK(legacy_rng.counter == test.rng_counter);
        CHECK(fingerprint(legacy_rng) == test.rng_fingerprint);

        CHECK(hex(owned_key.data(), owned_key.size()) == test.key_hex);
        CHECK(hex(owned_iv.data(), owned_iv.size()) == test.iv_hex);
        CHECK(owned_rng.counter == test.rng_counter);
        CHECK(fingerprint(owned_rng) == test.rng_fingerprint);
    }
}

TEST_CASE("shared RNG: the operation-local derivations leave the CRT stream untouched",
          "[wolfrpg][sharedrng]")
{
    std::srand(0x2468ACE0u);
    const int control_a = std::rand();
    const int control_b = std::rand();
    const int control_c = std::rand();

    std::srand(0x2468ACE0u);
    rng::RngData chain;
    wolf::crypt::modern::runRngChain(chain, oracle::CHAIN_CASES[0].seed1, oracle::CHAIN_CASES[0].seed2);
    CryptData cd;
    std::copy(std::begin(oracle::KEYGEN_CASES[0].seed_bytes), std::end(oracle::KEYGEN_CASES[0].seed_bytes),
              cd.seedBytes.begin());
    std::copy(std::begin(oracle::KEYGEN_CASES[0].key_bytes), std::end(oracle::KEYGEN_CASES[0].key_bytes),
              cd.keyBytes.begin());
    rng::RngData key_rng;
    aes::AesKey key{};
    aes::AesIV iv{};
    wolf::crypt::modern::aesKeyGen(cd, key_rng, key, iv);

    CHECK(std::rand() == control_a);
    CHECK(std::rand() == control_b);
    CHECK(std::rand() == control_c);

    // The isolation is not an early out: both derivations still produced the frozen results.
    CHECK(fingerprint(chain) == oracle::CHAIN_CASES[0].fingerprint);
    CHECK(hex(key.data(), key.size()) == oracle::KEYGEN_CASES[0].key_hex);
    CHECK(hex(iv.data(), iv.size()) == oracle::KEYGEN_CASES[0].iv_hex);
}

TEST_CASE("shared AES: the original sequential entry point matches the frozen oracle",
          "[wolfrpg][sharedaes]")
{
    // The maintainable direct exercise of the original sequential aesCtrXCrypt entry point:
    // shared-rng-aes.patch turned it into a zero-offset delegation to the range helper, so this
    // pins that delegation against the frozen whole-buffer primitives -- a full/partial buffer,
    // an empty range, a multi-block carry and the all-FF counter wrap -- rather than trusting the
    // range helper's own tests. Every literal is a raw vendor output (see the oracle header).
    const auto schedule = make_schedule();
    const auto base_iv = base_iv_of(schedule);

    // Full/partial buffer: 371 bytes is not a whole number of blocks, so the trailing partial
    // block is emitted too, and the advanced counter is the frozen one.
    {
        auto block = schedule;
        std::vector<std::uint8_t> whole = patterned(oracle::RANGE_TOTAL);
        aes::aesCtrXCrypt(whole.data(), block.data(), whole.size());
        CHECK(hex(whole.data(), whole.size()) == oracle::RANGE_CIPHER_HEX);
        CHECK(hex(block.data() + aes::KEY_EXP_SIZE, 16) == oracle::RANGE_FINAL_IV_HEX);
    }

    // Empty range: nothing is emitted and the counter does not move.
    {
        auto block = schedule;
        std::vector<std::uint8_t> buffer(8, 0xAB);
        aes::aesCtrXCrypt(buffer.data(), block.data(), 0);
        CHECK(std::all_of(buffer.begin(), buffer.end(), [](const std::uint8_t b) { return b == 0xAB; }));
        CHECK(hex(block.data() + aes::KEY_EXP_SIZE, 16) == oracle::RANGE_BASE_IV_HEX);
    }

    // Multi-block carry and the all-FF counter wrap, checked through the sequential entry point.
    for (const oracle::counter_case &test : oracle::COUNTER_CASES) {
        INFO("case " << test.name);
        auto block = schedule;
        const auto start_iv = unhex_array<16>(test.base_iv_hex);
        std::memcpy(block.data() + aes::KEY_EXP_SIZE, start_iv.data(), start_iv.size());
        std::vector<std::uint8_t> scratch(static_cast<std::size_t>(test.blocks) * aes::BLOCKLEN, 0);
        aes::aesCtrXCrypt(scratch.data(), block.data(), scratch.size());
        CHECK(hex(block.data() + aes::KEY_EXP_SIZE, 16) == test.next_iv_hex);
    }

    // Every unaligned span the range helper seeks to also matches the sequential counter.
    for (const oracle::range_run &run : oracle::RANGE_RUNS) {
        INFO("offset " << run.offset << " size " << run.size);
        auto block = schedule;
        std::vector<std::uint8_t> scratch(static_cast<std::size_t>(run.offset) + run.size, 0);
        aes::aesCtrXCrypt(scratch.data(), block.data(), scratch.size());
        CHECK(hex(block.data() + aes::KEY_EXP_SIZE, 16) == run.iv_hex);
    }

    // The base IV is only ever read; the oracle's starting counter stays as the schedule left it.
    CHECK(base_iv == base_iv_of(schedule));
}
