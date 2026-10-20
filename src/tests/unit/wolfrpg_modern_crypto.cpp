#include "../../core/wolfrpg/modern_context.h"
#include "../../core/wolfrpg/modern_crypto.h"
#include "../support/wolfrpg_modern_archive_fixtures.h"
#include "../support/wolfrpg_modern_golden.h"
#include "../support/wolfrpg_modern_variants_oracle.h"

#include <algorithm>
#include <array>
#include <charconv>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <functional>
#include <iterator>
#include <limits>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include <catch2/catch_test_macros.hpp>

// Behavioural tests for the seekable Wolf RPG v3.31 / v3.50 crypto adapter. Every expected value
// comes from the frozen golden header produced by out/sol-team/oracle/gen_golden.cpp, which runs
// the unmodified pinned UberWolfLib primitives; nothing here is a round trip of the adapter.
namespace
{
    using unpackrat::wolfrpg::modern_crypto;
    using unpackrat::wolfrpg::modern_crypto_error;
    namespace golden = test::support::wolfrpg_modern_golden;
    namespace variants = test::support::wolfrpg_modern_variants;

    [[nodiscard]] std::vector<std::uint8_t> unhex(const std::string_view hex)
    {
        std::vector<std::uint8_t> bytes;
        bytes.reserve(hex.size() / 2);
        for (std::size_t i = 0; i + 1 < hex.size(); i += 2) {
            unsigned int value = 0;
            const char *const end = hex.data() + i + 2;
            const auto parsed = std::from_chars(hex.data() + i, end, value, 16);
            REQUIRE(parsed.ec == std::errc{});
            REQUIRE(parsed.ptr == end);
            bytes.push_back(static_cast<std::uint8_t>(value));
        }
        return bytes;
    }

    std::optional<modern_crypto_error> observed_crypto_error(const std::function<void()> &operation)
    {
        try {
            operation();
        } catch (const modern_crypto_error &error) {
            return error;
        }
        return {};
    }

    void require_crypto_error(const std::function<void()> &operation)
    {
        const auto error = observed_crypto_error(operation);
        REQUIRE(error.has_value());
        if (!error.has_value()) {
            return;
        }
        CHECK(std::string_view(error.value().what()) == "WolfRPG modern archive crypto failed");
    }

    [[nodiscard]] std::array<std::uint8_t, 64> unhex_header(const std::string_view hex)
    {
        const std::vector<std::uint8_t> bytes = unhex(hex);
        REQUIRE(bytes.size() == 64);
        std::array<std::uint8_t, 64> header{};
        std::copy(bytes.begin(), bytes.end(), header.begin());
        return header;
    }

    // Passwords travel as their raw byte image so embedded NUL bytes survive (string_view from a
    // literal would stop at the first NUL). create() still applies strlen semantics internally.
    [[nodiscard]] std::string password_of(const golden::golden_case &entry)
    {
        const std::vector<std::uint8_t> bytes = unhex(entry.password_hex);
        return std::string(bytes.begin(), bytes.end());
    }

    // A binary password image built straight from its oracle hex, so embedded NUL bytes survive.
    [[nodiscard]] std::string password_of_hex(const std::string_view hex)
    {
        const std::vector<std::uint8_t> bytes = unhex(hex);
        return std::string(bytes.begin(), bytes.end());
    }

    [[nodiscard]] const golden::golden_case &find_case(const std::string_view name)
    {
        const auto found = std::ranges::find(golden::CASES, name, &golden::golden_case::name);
        REQUIRE(found != std::end(golden::CASES));
        return *found;
    }

    [[nodiscard]] const variants::variant_case &find_variant(const std::string_view name)
    {
        const auto found = std::ranges::find(variants::CASES, name, &variants::variant_case::name);
        REQUIRE(found != std::end(variants::CASES));
        return *found;
    }

    // The on-disk image (stored header + raw body) for a variant oracle case.
    [[nodiscard]] std::vector<std::uint8_t> archive_of_variant(const variants::variant_case &entry)
    {
        std::vector<std::uint8_t> archive = unhex(entry.stored_header_hex);
        const std::vector<std::uint8_t> body = unhex(entry.body_hex);
        archive.insert(archive.end(), body.begin(), body.end());
        return archive;
    }

    // Rebuilds the on-disk archive: stored (address-scrambled) header followed by the body.
    [[nodiscard]] std::vector<std::uint8_t> archive_of(const golden::golden_case &entry)
    {
        std::vector<std::uint8_t> archive = unhex(entry.stored_header_hex);
        const std::vector<std::uint8_t> body = unhex(entry.body_hex);
        archive.insert(archive.end(), body.begin(), body.end());
        return archive;
    }

    // Applies the outer transforms to a fresh copy of the archive and returns the whole file,
    // reconstructing the plaintext header the way a reader would (create() + decode_outer_at).
    [[nodiscard]] std::vector<std::uint8_t> decode_with(const modern_crypto &context,
                                                        const std::vector<std::uint8_t> &archive)
    {
        std::vector<std::uint8_t> out = archive;
        std::copy(context.header().begin(), context.header().end(), out.begin());
        context.decode_outer_at(std::span<std::uint8_t>(out).subspan(64), 64);
        return out;
    }
} // namespace

TEST_CASE("modern crypto: decoded header and metadata match the upstream oracle", "[wolfrpg][moderncrypto]")
{
    for (const golden::golden_case &entry : golden::CASES) {
        INFO("case " << entry.name);
        const std::vector<std::uint8_t> archive = archive_of(entry);

        if (entry.rejected) {
            require_crypto_error([&] {
                static_cast<void>(
                    modern_crypto::create(unhex_header(entry.stored_header_hex), archive.size(), password_of(entry)));
            });
            continue;
        }

        const std::array<std::uint8_t, 64> stored = unhex_header(entry.stored_header_hex);
        const modern_crypto context = modern_crypto::create(stored, archive.size(), password_of(entry));

        CHECK(context.crypt_version() == entry.version);
        CHECK(context.has_inner_key() == !entry.no_key);
        CHECK(context.aes_body_size() == entry.body_size);
        CHECK(context.name_table_offset() == entry.name_table_offset);

        const std::vector<std::uint8_t> expected_header = unhex(entry.decoded_header_hex);
        CHECK(std::equal(context.header().begin(), context.header().end(), expected_header.begin()));
    }
}

TEST_CASE("modern crypto: outer decode reproduces the whole decrypted file", "[wolfrpg][moderncrypto]")
{
    for (const golden::golden_case &entry : golden::CASES) {
        INFO("case " << entry.name);
        if (entry.rejected) {
            continue;
        }
        const std::vector<std::uint8_t> archive = archive_of(entry);
        const modern_crypto context =
            modern_crypto::create(unhex_header(entry.stored_header_hex), archive.size(), password_of(entry));

        const std::vector<std::uint8_t> expected = unhex(entry.decoded_file_hex);
        CHECK((decode_with(context, archive) == expected));
    }
}

TEST_CASE("modern crypto: split, unaligned and out-of-order reads agree", "[wolfrpg][moderncrypto]")
{
    for (const golden::golden_case &entry : golden::CASES) {
        INFO("case " << entry.name);
        if (entry.rejected) {
            continue;
        }
        const std::vector<std::uint8_t> archive = archive_of(entry);
        const modern_crypto context =
            modern_crypto::create(unhex_header(entry.stored_header_hex), archive.size(), password_of(entry));
        const std::vector<std::uint8_t> whole = unhex(entry.decoded_file_hex);

        // Forward, irregular chunk sizes starting at unaligned offsets.
        std::vector<std::uint8_t> chunked = archive;
        std::copy(context.header().begin(), context.header().end(), chunked.begin());
        for (std::uint64_t offset = 64; offset < archive.size();) {
            const std::size_t chunk = static_cast<std::size_t>(1 + ((offset * 2654435761u) % 97));
            const std::size_t remaining = static_cast<std::size_t>(archive.size() - offset);
            const std::size_t take = chunk < remaining ? chunk : remaining;
            context.decode_outer_at(std::span<std::uint8_t>(chunked).subspan(static_cast<std::size_t>(offset), take),
                                    offset);
            offset += take;
        }
        CHECK((chunked == whole));

        // Same ranges in reverse order into a second copy.
        std::vector<std::uint8_t> reversed = archive;
        std::copy(context.header().begin(), context.header().end(), reversed.begin());
        std::vector<std::pair<std::uint64_t, std::size_t>> ranges;
        for (std::uint64_t offset = 64; offset < archive.size();) {
            const std::size_t chunk = static_cast<std::size_t>(1 + ((offset * 40503u) % 53));
            const std::size_t remaining = static_cast<std::size_t>(archive.size() - offset);
            const std::size_t take = chunk < remaining ? chunk : remaining;
            ranges.emplace_back(offset, take);
            offset += take;
        }
        for (auto it = ranges.rbegin(); it != ranges.rend(); ++it) {
            context.decode_outer_at(
                std::span<std::uint8_t>(reversed).subspan(static_cast<std::size_t>(it->first), it->second), it->first);
        }
        CHECK((reversed == whole));

        // The same transform applied twice cancels (every outer transform is a pure XOR).
        std::vector<std::uint8_t> round_trip = archive;
        context.decode_outer_at(std::span<std::uint8_t>(round_trip).subspan(64), 64);
        context.decode_outer_at(std::span<std::uint8_t>(round_trip).subspan(64), 64);
        CHECK((round_trip == archive));
    }
}

TEST_CASE("modern crypto: inner key transform matches the oracle and cancels", "[wolfrpg][moderncrypto]")
{
    for (const golden::golden_case &entry : golden::CASES) {
        INFO("case " << entry.name);
        if (entry.rejected) {
            continue;
        }
        const std::vector<std::uint8_t> archive = archive_of(entry);
        const modern_crypto context =
            modern_crypto::create(unhex_header(entry.stored_header_hex), archive.size(), password_of(entry));

        std::vector<std::uint8_t> sample_a(256);
        for (std::size_t i = 0; i < sample_a.size(); ++i) {
            sample_a[i] = static_cast<std::uint8_t>(i);
        }
        context.apply_inner(sample_a, 0);
        CHECK((sample_a == unhex(entry.inner_sample_a_hex)));

        std::vector<std::uint8_t> sample_b(200);
        for (std::size_t i = 0; i < sample_b.size(); ++i) {
            sample_b[i] = static_cast<std::uint8_t>((i * 3 + 7) & 0xffu);
        }
        const std::vector<std::uint8_t> sample_b_before = sample_b;
        context.apply_inner(sample_b, entry.inner_sample_b_pos);
        CHECK((sample_b == unhex(entry.inner_sample_b_hex)));

        // A second application cancels, at the same unaligned position.
        context.apply_inner(sample_b, entry.inner_sample_b_pos);
        CHECK((sample_b == sample_b_before));

        // Splitting one logical range into two halves matches one call over the whole range.
        std::vector<std::uint8_t> split(320);
        for (std::size_t i = 0; i < split.size(); ++i) {
            split[i] = static_cast<std::uint8_t>((i * 5 + 1) & 0xffu);
        }
        std::vector<std::uint8_t> whole = split;
        context.apply_inner(whole, 700);
        context.apply_inner(std::span<std::uint8_t>(split).subspan(0, 111), 700);
        context.apply_inner(std::span<std::uint8_t>(split).subspan(111), 700 + 111);
        CHECK((split == whole));
    }
}

TEST_CASE("modern crypto: interleaved independent contexts stay isolated", "[wolfrpg][moderncrypto]")
{
    const golden::golden_case &a = find_case("v331-basic");
    const golden::golden_case &b = find_case("v350-basic");
    const std::vector<std::uint8_t> archive_a = archive_of(a);
    const std::vector<std::uint8_t> archive_b = archive_of(b);
    const modern_crypto context_a =
        modern_crypto::create(unhex_header(a.stored_header_hex), archive_a.size(), password_of(a));
    const modern_crypto context_b =
        modern_crypto::create(unhex_header(b.stored_header_hex), archive_b.size(), password_of(b));

    std::vector<std::uint8_t> out_a = archive_a;
    std::vector<std::uint8_t> out_b = archive_b;
    std::copy(context_a.header().begin(), context_a.header().end(), out_a.begin());
    std::copy(context_b.header().begin(), context_b.header().end(), out_b.begin());

    const std::array strides{std::pair<std::size_t, std::size_t>{128, 333},
                             std::pair<std::size_t, std::size_t>{333, 128}};
    for (const auto [step_a, step_b] : strides) {
        out_a = archive_a;
        out_b = archive_b;
        std::copy(context_a.header().begin(), context_a.header().end(), out_a.begin());
        std::copy(context_b.header().begin(), context_b.header().end(), out_b.begin());
        // Walk both archives with different strides, alternating between the two contexts.
        std::size_t cursor_a = 64;
        std::size_t cursor_b = 64;
        while (cursor_a < out_a.size() || cursor_b < out_b.size()) {
            if (cursor_a < out_a.size()) {
                const std::size_t take = std::min<std::size_t>(step_a, out_a.size() - cursor_a);
                context_a.decode_outer_at(std::span<std::uint8_t>(out_a).subspan(cursor_a, take), cursor_a);
                cursor_a += take;
            }
            if (cursor_b < out_b.size()) {
                const std::size_t take = std::min<std::size_t>(step_b, out_b.size() - cursor_b);
                context_b.decode_outer_at(std::span<std::uint8_t>(out_b).subspan(cursor_b, take), cursor_b);
                cursor_b += take;
            }
        }

        CHECK((out_a == unhex(a.decoded_file_hex)));
        CHECK((out_b == unhex(b.decoded_file_hex)));
    }
}
TEST_CASE("modern crypto: context is move-only and preserves its state", "[wolfrpg][moderncrypto]")
{
    const golden::golden_case &entry = find_case("v350-basic");
    const std::vector<std::uint8_t> archive = archive_of(entry);

    modern_crypto source =
        modern_crypto::create(unhex_header(entry.stored_header_hex), archive.size(), password_of(entry));
    modern_crypto moved = std::move(source);
    CHECK((decode_with(moved, archive) == unhex(entry.decoded_file_hex)));

    modern_crypto assigned =
        modern_crypto::create(unhex_header(entry.stored_header_hex), archive.size(), password_of(entry));
    assigned = std::move(moved);
    CHECK((decode_with(assigned, archive) == unhex(entry.decoded_file_hex)));
}

TEST_CASE("modern crypto: rejects unsupported versions, empty passwords and out-of-range ranges",
          "[wolfrpg][moderncrypto]")
{
    const golden::golden_case &basic = find_case("v331-basic");
    const std::array<std::uint8_t, 64> stored = unhex_header(basic.stored_header_hex);
    const std::vector<std::uint8_t> archive = archive_of(basic);

    // Crypt version outside {331, 350}.
    std::array<std::uint8_t, 64> unsupported = stored;
    unsupported[45] = 0x01; // Flags >> 16u == 0x0101 == 257
    unsupported[46] = 0x01;
    require_crypto_error([&] { static_cast<void>(modern_crypto::create(unsupported, archive.size(), "DXLIBARC")); });

    // Empty password for the password-salted version.
    require_crypto_error([&] { static_cast<void>(modern_crypto::create(stored, archive.size(), "")); });

    // The frozen v331-basic name table is at offset 3000, outside this 1087-byte image.
    require_crypto_error([&] { static_cast<void>(modern_crypto::create(stored, 1087, "DXLIBARC")); });

    // A context rejects ranges outside [64, size).
    const modern_crypto context = modern_crypto::create(stored, archive.size(), "DXLIBARC");
    std::vector<std::uint8_t> buffer(16, 0);
    require_crypto_error([&] { static_cast<void>(context.decode_outer_at(buffer, 0)); });
    require_crypto_error([&] { static_cast<void>(context.decode_outer_at(buffer, archive.size())); });
    require_crypto_error([&] {
        static_cast<void>(context.decode_outer_at(std::span<std::uint8_t>(buffer).subspan(0, 4), archive.size() - 2));
    });

    // A range whose length alone exceeds the archive is rejected before any transform runs.
    std::vector<std::uint8_t> oversized(archive.size() + 16, 0);
    require_crypto_error([&] { static_cast<void>(context.decode_outer_at(oversized, 64)); });

    // An empty range exactly at the end is valid, and stays untouched.
    CHECK_NOTHROW(context.decode_outer_at(std::span<std::uint8_t>(buffer).subspan(0, 0), archive.size()));
}

TEST_CASE("modern crypto: arbitrary headers are handled without crashing and deterministically",
          "[wolfrpg][moderncrypto]")
{
    const golden::golden_case &base = find_case("v350-basic");
    const std::array<std::uint8_t, 64> template_header = unhex_header(base.stored_header_hex);

    auto header_with = [&](const std::uint16_t version, const std::uint8_t seed_a, const std::uint8_t seed_b,
                           const std::uint8_t seed_c, const std::uint32_t noise) {
        std::array<std::uint8_t, 64> header = template_header;
        header[44] = 0;
        header[45] = 0;
        header[46] = static_cast<std::uint8_t>(version & 0xffu);
        header[47] = static_cast<std::uint8_t>(version >> 8u);
        for (std::size_t k = 0; k < 15u; ++k) {
            header[49 + k] = static_cast<std::uint8_t>((noise >> ((k % 4) * 8)) ^ (k * 31));
        }
        header[49 + 2] = seed_a;  // Reserve[2]
        header[49 + 4] = seed_b;  // Reserve[4]
        header[49 + 12] = seed_c; // Reserve[12]
        return header;
    };

    std::uint32_t rng = 0x12345678u;
    for (unsigned iteration = 0; iteration < 3000u; ++iteration) {
        rng = rng * 1664525u + 1013904223u;
        const std::uint16_t version = (iteration % 2 == 0) ? 350 : 331;
        const std::uint64_t size = (iteration % 3 == 0) ? 1088 : ((iteration % 3 == 1) ? 2048 : 4096);
        const bool empty_password = (iteration % 4 == 0);
        const std::string_view password = empty_password ? std::string_view{} : std::string_view{"DXLIBARC"};

        const std::array<std::uint8_t, 64> header =
            header_with(version, static_cast<std::uint8_t>(rng >> 13u), static_cast<std::uint8_t>(rng >> 21u),
                        static_cast<std::uint8_t>(rng >> 5u), rng);

        try {
            const modern_crypto context = modern_crypto::create(header, size, password);
            // A context that is created must report the version it was built from.
            REQUIRE(context.crypt_version() == version);
        } catch (const modern_crypto_error &error) {
            // Only a documented rejection may escape; the rejection itself is not inspected.
            static_cast<void>(error);
        }
    }

    // create() is a pure function of its inputs.
    const std::array<std::uint8_t, 64> deterministic = unhex_header(find_case("v350-zero-seed").stored_header_hex);
    const modern_crypto first = modern_crypto::create(deterministic, 4096, "");
    const modern_crypto second = modern_crypto::create(deterministic, 4096, "");
    CHECK(first.header() == second.header());
    CHECK(first.aes_body_size() == second.aes_body_size());
    CHECK(first.name_table_offset() == second.name_table_offset());
}

TEST_CASE("modern crypto: inner transform bounds and empty ranges", "[wolfrpg][moderncrypto]")
{
    const golden::golden_case &entry = find_case("v331-basic");
    const std::vector<std::uint8_t> archive = archive_of(entry);
    const modern_crypto context =
        modern_crypto::create(unhex_header(entry.stored_header_hex), archive.size(), password_of(entry));

    std::vector<std::uint8_t> buffer(8, 0);
    // An empty range is accepted at any position.
    CHECK_NOTHROW(context.apply_inner(std::span<std::uint8_t>(buffer).subspan(0, 0), 12345));
    // A logical range that would overflow the positive int64 range is rejected.
    require_crypto_error(
        [&] { static_cast<void>(context.apply_inner(buffer, static_cast<std::uint64_t>(INT64_MAX))); });
}

TEST_CASE("modern crypto: rejects an archive size beyond the signed 64-bit range", "[wolfrpg][moderncrypto]")
{
    const golden::golden_case &basic = find_case("v331-basic");
    const std::array<std::uint8_t, 64> stored = unhex_header(basic.stored_header_hex);
    constexpr std::uint64_t max_signed = static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max());

    // Exactly INT64_MAX stays representable for the signed engine offsets.
    CHECK_NOTHROW(modern_crypto::create(stored, max_signed, "DXLIBARC"));

    // One past it cannot be addressed by the signed Wolf XOR offsets: the negative overlap must be
    // rejected up front instead of turning into a negative vendor index. create() derives a fixed
    // context and must never allocate proportional to the declared size.
    require_crypto_error([&] { static_cast<void>(modern_crypto::create(stored, max_signed + 1, "DXLIBARC")); });
    require_crypto_error([&] {
        static_cast<void>(modern_crypto::create(stored, std::numeric_limits<std::uint64_t>::max(), "DXLIBARC"));
    });

    // A context constructed at INT64_MAX still transforms a small in-range range coherently.
    const modern_crypto context = modern_crypto::create(stored, max_signed, "DXLIBARC");
    std::vector<std::uint8_t> bytes(64, 0x5a);
    CHECK_NOTHROW(context.decode_outer_at(bytes, 64));
}

TEST_CASE("modern crypto: v3.50 body size keeps 64-bit precision before bounding", "[wolfrpg][moderncrypto]")
{
    // The v3.50 body size is derived as `physical_size - 64` and only then bounded by a random
    // threshold. Narrowing that subtraction to 32 bits first makes 2^32u + 64 wrap to 0, so the
    // region-1 length and the region-2 IV are derived from the wrong value. The pinned derivation
    // seeds xorshift32 with 1 (all-zero reserve), draws threshold 1199, then bounds to 1133.
    const golden::golden_case &zero = find_case("v350-zero-seed");
    const std::array<std::uint8_t, 64> stored = unhex_header(zero.stored_header_hex);
    const std::uint64_t size = 0x1'0000'0040ull; // 2^32u + 64

    const modern_crypto context = modern_crypto::create(stored, size, "");
    CHECK(context.crypt_version() == 350);
    CHECK(context.aes_body_size() == 1133u);
    CHECK(context.name_table_offset() == zero.name_table_offset);
}

TEST_CASE("modern crypto: salt decision table and NUL controls match the oracle", "[wolfrpg][moderncrypto]")
{
    // Each password selects a different salt, so the outer-key schedule takes different round
    // branches. Asserting the whole decrypted file against the frozen oracle (not just the
    // version tag) means any wrong key schedule fails here instead of passing silently.
    for (const char *name :
         {"v331-dec-1byte", "v331-dec-2byte", "v331-dec-3byte", "v331-dec-5byte", "v331-dec-8byte", "v331-dec-16byte",
          "v331-dec-31byte", "v331-dec-64byte", "v331-dec-case4", "v350-dec-1", "v350-dec-nul", "v350-narrow-body"}) {
        INFO("case " << name);
        const golden::golden_case &entry = find_case(name);
        REQUIRE(!entry.rejected);
        const std::vector<std::uint8_t> archive = archive_of(entry);
        const modern_crypto context =
            modern_crypto::create(unhex_header(entry.stored_header_hex), archive.size(), password_of(entry));
        CHECK((decode_with(context, archive) == unhex(entry.decoded_file_hex)));
    }

    // Embedded-NUL control: the byte string after the first "\0" must be ignored, exactly as the
    // upstream strlen-based calcSalt does. Both cases share one stored header and body, so a
    // length-aware salt would make the tail case decode differently from the control and fail.
    const golden::golden_case &control = find_case("v331-nul-control");
    const golden::golden_case &tail = find_case("v331-nul-tail");
    REQUIRE(password_of(control).size() == 8);
    REQUIRE(password_of(tail).size() == 15);
    const std::vector<std::uint8_t> control_archive = archive_of(control);
    const std::vector<std::uint8_t> tail_archive = archive_of(tail);
    CHECK(control_archive == tail_archive);

    const modern_crypto control_ctx =
        modern_crypto::create(unhex_header(control.stored_header_hex), control_archive.size(), password_of(control));
    const modern_crypto tail_ctx =
        modern_crypto::create(unhex_header(tail.stored_header_hex), tail_archive.size(), password_of(tail));
    CHECK((decode_with(control_ctx, control_archive) == unhex(control.decoded_file_hex)));
    CHECK((decode_with(tail_ctx, tail_archive) == unhex(tail.decoded_file_hex)));
}

TEST_CASE("modern crypto: deriving contexts leaves the process-global rand state untouched", "[wolfrpg][moderncrypto]")
{
    // On Windows the pinned UberWolf msvc_srand/msvc_rand forward straight to the CRT srand/rand,
    // so an implementation that derived addresses, Wolf keys or the body size through them would
    // clobber this stream. Two archives are derived back to back (address scramble, both key
    // schedules and the v3.50 body-size draw) and the following rand() draws must be unchanged.
    const golden::golden_case &basic = find_case("v331-basic");
    const golden::golden_case &v350 = find_case("v350-basic");
    const std::vector<std::uint8_t> archive_basic = archive_of(basic);
    const std::vector<std::uint8_t> archive_v350 = archive_of(v350);

    std::srand(0x2468ACE0u); // NOLINT(bugprone-random-generator-seed): fixed seed for deterministic comparison
    const int control_a = std::rand();
    const int control_b = std::rand();
    const int control_c = std::rand();

    std::srand(0x2468ACE0u); // NOLINT(bugprone-random-generator-seed): fixed seed for deterministic comparison
    const modern_crypto ctx_a =
        modern_crypto::create(unhex_header(basic.stored_header_hex), archive_basic.size(), password_of(basic));
    const modern_crypto ctx_b =
        modern_crypto::create(unhex_header(v350.stored_header_hex), archive_v350.size(), password_of(v350));

    CHECK(std::rand() == control_a);
    CHECK(std::rand() == control_b);
    CHECK(std::rand() == control_c);

    // Both contexts still decode their archive correctly, so the isolation is not an early out.
    CHECK((decode_with(ctx_a, archive_basic) == unhex(basic.decoded_file_hex)));
    CHECK((decode_with(ctx_b, archive_v350) == unhex(v350.decoded_file_hex)));
}

TEST_CASE("modern context: the AES block counter is big-endian and carries across the block", "[wolfrpg][moderncrypto]")
{
    using wolf::crypt::modern::advanceCounter;

    // A carry that stays inside the low byte.
    std::array<std::uint8_t, 16> counter{};
    advanceCounter(counter, 5);
    CHECK(counter[15] == 5);
    for (std::size_t i = 0; i < 15u; ++i) {
        CHECK(counter[i] == 0);
    }

    // A multi-block step with a byte carry: 0x1FF + 1 == 0x200 in the trailing bytes.
    counter.fill(0);
    counter[14] = 0x01;
    counter[15] = 0xFF;
    advanceCounter(counter, 1);
    CHECK(counter[13] == 0);
    CHECK(counter[14] == 0x02);
    CHECK(counter[15] == 0x00);

    // A carry that runs out the top of the block wraps to all zeros rather than indexing past the
    // 16-byte block, which is the path a saturated counter takes after a full-block advance.
    counter.fill(0xFF);
    advanceCounter(counter, 1);
    for (const std::uint8_t byte : counter) {
        CHECK(byte == 0);
    }
}

TEST_CASE("modern context: the owned LCG reproduces the CrtRng derivation bit for bit", "[wolfrpg][moderncrypto]")
{
    using wolf::crypt::deriveWolfKeyWithRandom;
    using wolf::crypt::rng::CrtRng;
    using wolf::crypt::rng::MsvcLcg;

    std::array<std::uint8_t, 15> reserve{};
    for (std::size_t i = 0; i < reserve.size(); ++i) {
        reserve[i] = static_cast<std::uint8_t>(0x20 + i * 7);
    }

    std::array<std::uint8_t, 768> owned{};
    std::array<std::uint8_t, 768> crt{};
    {
        MsvcLcg rng;
        deriveWolfKeyWithRandom(331, reserve.data(), owned.data(), nullptr, rng, "DXLIBARC", true);
    }
    {
        CrtRng rng;
        deriveWolfKeyWithRandom(331, reserve.data(), crt.data(), nullptr, rng, "DXLIBARC", true);
    }

    // The owned LCG is a faithful drop-in for the process-global CRT stream: any wrong recurrence
    // or draw order would diverge here even though both paths would still decode *something*.
    CHECK(owned == crt);

    // The inner (unsalted) derivation shares the recurrence but a different seed path, so it must
    // not silently coincide with the outer key.
    std::array<std::uint8_t, 768> inner{};
    {
        MsvcLcg rng;
        deriveWolfKeyWithRandom(331, reserve.data(), inner.data(), nullptr, rng, "DXLIBARC", false);
    }
    CHECK(owned != inner);
}

TEST_CASE("modern context: the pKey2 rekey branch is deterministic and changes the key", "[wolfrpg][moderncrypto]")
{
    using wolf::crypt::deriveWolfKeyWithRandom;
    using wolf::crypt::rng::MsvcLcg;

    std::array<std::uint8_t, 15> reserve{};
    for (std::size_t i = 0; i < reserve.size(); ++i) {
        reserve[i] = static_cast<std::uint8_t>(0x40 + i * 5);
    }
    const std::array<std::uint8_t, 4> key2{0x11, 0x22, 0x33, 0x44};

    std::array<std::uint8_t, 768> without{};
    std::array<std::uint8_t, 768> with{};
    std::array<std::uint8_t, 768> again{};
    {
        MsvcLcg rng;
        deriveWolfKeyWithRandom(350, reserve.data(), without.data(), nullptr, rng, "DXLIBARC", false);
    }
    {
        MsvcLcg rng;
        deriveWolfKeyWithRandom(350, reserve.data(), with.data(), const_cast<std::uint8_t *>(key2.data()), rng,
                                "DXLIBARC", false);
    }
    {
        MsvcLcg rng;
        deriveWolfKeyWithRandom(350, reserve.data(), again.data(), const_cast<std::uint8_t *>(key2.data()), rng,
                                "DXLIBARC", false);
    }

    CHECK(with != without);
    CHECK(with == again);
}

TEST_CASE("modern context: the shared body-size seed covers the Pro and classic branches", "[wolfrpg][moderncrypto]")
{
    using wolf::crypt::archiveBodySeed;

    std::array<std::uint8_t, 15> pwd{};
    pwd[2] = 0x11;
    pwd[4] = 0x22;
    pwd[11] = 0x33;
    pwd[12] = 0x44;
    const std::array<std::uint8_t, 4> key2{0x05, 0x06, 0x07, 0x08};

    // The >= 1020 Pro branch folds in both Pro-key bytes.
    CHECK(archiveBodySeed(key2.data(), pwd.data(), 1020) ==
          static_cast<std::uint32_t>(0x05) * 0x06 + static_cast<std::uint32_t>(0x11) * 0x22 + 0x33);
    // The classic branch folds in password byte 12, exactly as the legacy reader did.
    CHECK(archiveBodySeed(nullptr, pwd.data(), 350) == static_cast<std::uint32_t>(0x11) * 0x22 + 0x44);
}

TEST_CASE("modern context: the body-size draw keeps the 64-bit candidate and pins the legacy one",
          "[wolfrpg][moderncrypto]")
{
    using wolf::crypt::drawArchiveBodySize;

    const std::uint64_t size = 0x1'0000'0040ull; // 2^32u + 64

    // An all-zero reserve forces the seed to 1 and the full 64-bit candidate is bounded to the
    // same value the frozen v350-zero-seed oracle reports.
    std::uint32_t state = 0;
    CHECK(drawArchiveBodySize(0, static_cast<std::int64_t>(size), size - 64, state) == 1133u);

    // The legacy caller keeps its historical behaviour: the 32-bit narrowed candidate wraps to 0
    // and is left unbounded. This is the exact large-file divergence the refactor isolates.
    std::uint32_t legacyState = 0;
    CHECK(drawArchiveBodySize(0, static_cast<std::int64_t>(size), static_cast<std::uint32_t>(size - 64), legacyState) ==
          0u);
}

TEST_CASE("modern context: xorshift32 keeps the loaded-then-advanced and continues-on-zero semantics",
          "[wolfrpg][moderncrypto]")
{
    using wolf::crypt::xorshift32;
    using wolf::crypt::xorshift32Step;

    // A non-zero seed loads the state and then advances exactly once.
    std::uint32_t state = 0x1234'5678u;
    const std::uint32_t loaded = xorshift32Step(state);
    CHECK(xorshift32(0x1234'5678u) == loaded);

    // A zero seed continues from the current state instead of reloading it.
    const std::uint32_t continued = xorshift32Step(state);
    CHECK(xorshift32() == continued);

    // Re-seeding restarts the sequence from the same loaded state.
    CHECK(xorshift32(0x1234'5678u) == loaded);
}

TEST_CASE("modern context: the legacy wrappers share the modern derivation exactly", "[wolfrpg][moderncrypto]")
{
    // The legacy initWolfCrypt / cryptAddresses wrappers must be nothing more than the same
    // RNG-parameterized bodies driven by the CRT adapter. Deriving the same inputs through both
    // adapters proves the refactor kept one algorithm rather than a copied twin.
    std::array<std::uint8_t, 15> reserve{};
    for (std::size_t i = 0; i < reserve.size(); ++i) {
        reserve[i] = static_cast<std::uint8_t>(0x13 + i * 11);
    }

    std::array<std::uint8_t, 768> legacy_key{};
    std::array<std::uint8_t, 64> sink{};
    // start == end means the trailing archive-body XOR is a no-op, so only the key is derived.
    wolf::crypt::initWolfCrypt(331, reserve.data(), legacy_key.data(), nullptr, sink.data(), 0, 0, true, "DXLIBARC");

    std::array<std::uint8_t, 768> modern_key{};
    {
        wolf::crypt::rng::MsvcLcg rng;
        wolf::crypt::deriveWolfKeyWithRandom(331, reserve.data(), modern_key.data(), nullptr, rng, "DXLIBARC", true);
    }
    CHECK(legacy_key == modern_key);

    std::array<std::uint8_t, 64> legacy_header{};
    std::array<std::uint8_t, 64> modern_header{};
    for (std::size_t i = 0; i < legacy_header.size(); ++i) {
        legacy_header[i] = static_cast<std::uint8_t>(i * 3 + 1);
    }
    modern_header = legacy_header;

    wolf::crypt::cryptAddresses(legacy_header.data(), reserve.data(), 350);
    {
        wolf::crypt::rng::MsvcLcg rng;
        wolf::crypt::cryptAddressesWithRandom(modern_header.data(), reserve.data(), 350, rng);
    }
    CHECK(legacy_header == modern_header);
}

TEST_CASE("modern context: the legacy wrappers cover the inner, rekey and both version paths",
          "[wolfrpg][moderncrypto]")
{
    std::array<std::uint8_t, 15> reserve{};
    for (std::size_t i = 0; i < reserve.size(); ++i) {
        reserve[i] = static_cast<std::uint8_t>(0x71 + i * 3);
    }
    std::array<std::uint8_t, 64> sink{};
    const std::array<std::uint8_t, 4> key2{0x0a, 0x0b, 0x0c, 0x0d};

    // Inner (unsalted) key derivation through the legacy wrapper.
    std::array<std::uint8_t, 768> inner{};
    wolf::crypt::initWolfCrypt(331, reserve.data(), inner.data(), nullptr, sink.data(), 0, 0, false, "DXLIBARC");

    // Outer key with a non-null pKey2 and the v3.50 salt path.
    std::array<std::uint8_t, 768> outer{};
    wolf::crypt::initWolfCrypt(350, reserve.data(), outer.data(), const_cast<std::uint8_t *>(key2.data()), sink.data(),
                               0, 0, true, "DXLIBARC");
    CHECK(inner != outer);

    // Both address-scramble versions through the legacy wrapper.
    std::array<std::uint8_t, 64> h331{};
    std::array<std::uint8_t, 64> h350{};
    wolf::crypt::cryptAddresses(h331.data(), reserve.data(), 331);
    wolf::crypt::cryptAddresses(h350.data(), reserve.data(), 350);
    CHECK(h331 != h350);
}

TEST_CASE("modern variants: every new-crypt boundary version matches the frozen vendor oracle",
          "[wolfrpg][moderncrypto]")
{
    // The oracle replays the original DecodeArchive new-crypt branch through the raw pinned
    // primitives, so the decoded header, the whole decoded file (outer Wolf XOR and both AES
    // regions at their own counter positions) and both inner samples pin every derivation. A
    // version-tag-only implementation, a copied twin or a pK2-ignoring adapter all fail here.
    for (const variants::variant_case &entry : variants::CASES) {
        INFO("case " << entry.name);
        const std::array<std::uint8_t, 64> stored = unhex_header(entry.stored_header_hex);
        const std::string password = password_of_hex(entry.password_hex);
        const std::vector<std::uint8_t> archive = archive_of_variant(entry);

        if (entry.rejected) {
            // A full-size image, so the rejection is the version gate or the Pro password tail,
            // never the too-small-archive gate.
            REQUIRE(archive.size() == 64 + 1984);
            require_crypto_error([&] { static_cast<void>(modern_crypto::create(stored, archive.size(), password)); });
            continue;
        }

        const modern_crypto context = modern_crypto::create(stored, archive.size(), password);
        CHECK(context.crypt_version() == entry.version);
        CHECK(context.has_inner_key() == !entry.no_key);
        CHECK(context.aes_body_size() == entry.body_size);
        CHECK(context.name_table_offset() == entry.name_table_offset);

        const std::vector<std::uint8_t> expected_header = unhex(entry.decoded_header_hex);
        CHECK(std::equal(context.header().begin(), context.header().end(), expected_header.begin()));

        CHECK((decode_with(context, archive) == unhex(entry.decoded_file_hex)));

        std::vector<std::uint8_t> sample_a(256);
        for (std::size_t i = 0; i < sample_a.size(); ++i) {
            sample_a[i] = static_cast<std::uint8_t>(i);
        }
        context.apply_inner(sample_a, 0);
        CHECK((sample_a == unhex(entry.inner_sample_a_hex)));

        std::vector<std::uint8_t> sample_b(200);
        for (std::size_t i = 0; i < sample_b.size(); ++i) {
            sample_b[i] = static_cast<std::uint8_t>((i * 3 + 7) & 0xffu);
        }
        context.apply_inner(sample_b, variants::INNER_SAMPLE_B_POS);
        CHECK((sample_b == unhex(entry.inner_sample_b_hex)));
    }
}

TEST_CASE("modern variants: the dispatcher controls stay outside the modern range", "[wolfrpg][moderncrypto]")
{
    for (const char *name : {"control-330", "control-1000", "control-1009"}) {
        INFO("case " << name);
        const variants::variant_case &entry = find_variant(name);
        REQUIRE(entry.rejected);
        const std::vector<std::uint8_t> archive = archive_of_variant(entry);
        require_crypto_error([&] {
            static_cast<void>(modern_crypto::create(unhex_header(entry.stored_header_hex), archive.size(),
                                                    password_of_hex(entry.password_hex)));
        });
    }
}

TEST_CASE("modern variants: a Pro archive needs a full pK2 tail and an empty prefix is rejected",
          "[wolfrpg][moderncrypto]")
{
    // The Pro-key pointer starts one byte past the 63-clamped first-NUL prefix, so a truncated
    // blob cannot supply the four vendor-read bytes and an all-empty prefix cannot salt the key.
    for (const char *name : {"pro-nul0", "pro-missing-tail"}) {
        INFO("case " << name);
        const variants::variant_case &entry = find_variant(name);
        REQUIRE(entry.rejected);
        const std::vector<std::uint8_t> archive = archive_of_variant(entry);
        require_crypto_error([&] {
            static_cast<void>(modern_crypto::create(unhex_header(entry.stored_header_hex), archive.size(),
                                                    password_of_hex(entry.password_hex)));
        });
    }

    // The accepted Pro blob boundaries each decode to their own frozen archive.
    for (const char *name : {"pro-nul1", "pro-nul63", "pro-nul64", "pro-prefix-long"}) {
        INFO("case " << name);
        const variants::variant_case &entry = find_variant(name);
        REQUIRE(!entry.rejected);
        const std::vector<std::uint8_t> archive = archive_of_variant(entry);
        const modern_crypto context = modern_crypto::create(unhex_header(entry.stored_header_hex), archive.size(),
                                                            password_of_hex(entry.password_hex));
        CHECK((decode_with(context, archive) == unhex(entry.decoded_file_hex)));
    }
}

TEST_CASE("modern variants: v3.50 salts from the literal and ignores the password blob", "[wolfrpg][moderncrypto]")
{
    const variants::variant_case &entry = find_variant("v350");
    const std::vector<std::uint8_t> archive = archive_of_variant(entry);
    const std::array<std::uint8_t, 64> stored = unhex_header(entry.stored_header_hex);

    // 0x15E is the one version whose outer key salts from the literal "958"; an empty blob is
    // therefore accepted and yields the exact same context as a non-empty one.
    const modern_crypto with_password =
        modern_crypto::create(stored, archive.size(), password_of_hex(entry.password_hex));
    const modern_crypto empty_password = modern_crypto::create(stored, archive.size(), std::string_view{});
    CHECK((decode_with(empty_password, archive) == unhex(entry.decoded_file_hex)));
    CHECK((decode_with(empty_password, archive) == decode_with(with_password, archive)));

    // Every other modern version does salt from the blob, so an empty prefix is rejected instead.
    const variants::variant_case &v331 = find_variant("v331");
    const std::vector<std::uint8_t> archive331 = archive_of_variant(v331);
    require_crypto_error([&] {
        static_cast<void>(
            modern_crypto::create(unhex_header(v331.stored_header_hex), archive331.size(), std::string_view{}));
    });
}

TEST_CASE("modern variants: the decoded name table offset is validated before use", "[wolfrpg][moderncrypto]")
{
    // A decoded FileNameTableStartAddress below the 64-byte header, or past the archive, cannot
    // index the second AES region and must be rejected rather than seek outside the image.
    const variants::variant_case &entry = find_variant("v331");
    const std::vector<std::uint8_t> archive = archive_of_variant(entry);
    const std::array<std::uint8_t, 64> stored = unhex_header(entry.stored_header_hex);
    const std::string password = password_of_hex(entry.password_hex);

    // Pre-image the stored header so the decoded name table is exactly 63: build a plaintext
    // header with that field, scramble it, and hand it to create().
    auto stored_with_name_table = [&](const std::uint64_t value) {
        std::array<std::uint8_t, 64> head{};
        const std::vector<std::uint8_t> decoded = unhex(entry.decoded_header_hex);
        std::copy(decoded.begin(), decoded.end(), head.begin());
        for (std::size_t i = 0; i < 8u; ++i) {
            head[16 + i] = static_cast<std::uint8_t>(value >> (8 * i));
        }
        // The adapter reads the stored header and applies this scramble to recover the field, so
        // the image must carry the scrambled form.
        wolf::crypt::cryptAddresses(head.data(), head.data() + 49, entry.version);
        return head;
    };

    // The scrambled header with the unmodified field is accepted.
    CHECK_NOTHROW(modern_crypto::create(stored, archive.size(), password));

    // The 63 field is below the header and must be rejected.
    const std::array<std::uint8_t, 64> low = stored_with_name_table(63);
    require_crypto_error([&] { static_cast<void>(modern_crypto::create(low, archive.size(), password)); });

    // A field past the archive must be rejected as well.
    const std::array<std::uint8_t, 64> high = stored_with_name_table(archive.size() + 8);
    require_crypto_error([&] { static_cast<void>(modern_crypto::create(high, archive.size(), password)); });
}

TEST_CASE("modern crypto: official small v3.31 archive supports bounded seek reads",
          "[wolfrpg][moderncrypto][official-3396-seek]")
{
    const auto path = std::filesystem::path(__FILE__).parent_path().parent_path() /
                      "fixtures/wolfrpg/official-3396/OwnedFixture.wolf";
    const auto bytes = test::support::read_modern_fixture(path);
    REQUIRE(bytes.size() == 866);
    std::vector<std::uint8_t> encrypted(bytes.size());
    std::transform(bytes.begin(), bytes.end(), encrypted.begin(),
                   [](const std::byte value) { return std::to_integer<std::uint8_t>(value); });
    std::array<std::uint8_t, 64> header{};
    std::copy_n(encrypted.begin(), header.size(), header.begin());
    require_crypto_error([&] { static_cast<void>(modern_crypto::create(header, 63, "DXLIBARC")); });
    const auto context = modern_crypto::create(header, encrypted.size(), "DXLIBARC");
    CHECK(context.crypt_version() == 331);
    CHECK(context.aes_body_size() == 1024);
    REQUIRE(context.name_table_offset() >= 64);
    REQUIRE(context.name_table_offset() < encrypted.size());
    const auto whole = decode_with(context, encrypted);

    // One-byte reads include both sides of the actual second-region boundary and the final
    // incomplete AES block. The independently generated fixture is also extracted byte-exact
    // through the public archive facade in the official-3396 regression.
    auto forward = encrypted;
    std::copy(context.header().begin(), context.header().end(), forward.begin());
    for (std::size_t offset = 64; offset < forward.size(); ++offset) {
        context.decode_outer_at(std::span<std::uint8_t>(forward).subspan(offset, 1), offset);
    }
    CHECK(forward == whole);

    auto reverse = encrypted;
    std::copy(context.header().begin(), context.header().end(), reverse.begin());
    for (std::size_t end = reverse.size(); end > 64u;) {
        const auto take = std::min<std::size_t>(17, end - 64);
        const auto begin = end - take;
        context.decode_outer_at(std::span<std::uint8_t>(reverse).subspan(begin, take), begin);
        end = begin;
    }
    CHECK(reverse == whole);
    const auto before_empty_read = reverse;
    context.decode_outer_at(std::span<std::uint8_t>(reverse).subspan(reverse.size()), reverse.size());
    CHECK(reverse == before_empty_read);
    CHECK(!(observed_crypto_error([&] {
                context.decode_outer_at(std::span<std::uint8_t>(reverse).subspan(reverse.size()), reverse.size());
            }).has_value()));
}
