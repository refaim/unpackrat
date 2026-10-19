#include "../../core/wolfrpg/pro_keys.h"
#include "../support/wolfrpg_pro_fixtures.h"

#include <algorithm>
#include <array>
#include <barrier>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <initializer_list>
#include <optional>
#include <span>
#include <stdexcept>
#include <string_view>
#include <thread>
#include <variant>
#include <vector>

#include <catch2/catch_test_macros.hpp>

// Independent contract tests for the WolfRPG Pro companion key recovery.
//
// The expected keys are frozen from the unmodified pinned UberWolf source: each fixture is written
// to a controlled own "Game.wolf" and the upstream WolfPro::GetDxArcKey path (which dispatches to
// findDxArcKeyV1 / dxarckey::v2::calcKey / dxarckey::cc2::calcKey) produced the bytes below. The
// oracle is a throwaway program (out/sol-team/wolfrpg_pro_oracle.cpp) that includes the same fixture
// header; only its frozen output lives here (out/sol-team/logs/oracle-frozen.log).
//
// Every expectation compares the recovered key bytes themselves, so a wrong algorithm fails even
// when it happens to choose the right variant and crypt version.
namespace
{
    template <class Error> std::optional<Error> observed_exception(const std::function<void()> &operation)
    {
        try {
            operation();
        } catch (const Error &error) {
            return error;
        }
        return {};
    }
    template <class Error> void require_message(const std::function<void()> &operation, const std::string_view message)
    {
        const auto error = observed_exception<Error>(operation);
        REQUIRE(error.has_value());
        CHECK(std::string_view(error->what()) == message);
    }
    namespace pro = unpackrat::wolfrpg::pro;
    namespace fixtures = test::support::wolfrpg_pro;

    // Raised by a faulting companion reader. Its concrete type and payload must reach the caller of
    // recover_key unchanged: a source failure is not a diagnosed key error and must not be swallowed.
    struct source_fault : std::runtime_error
    {
        explicit source_fault(int payload) : std::runtime_error("companion read failed"), payload(payload)
        {
        }

        int payload;
    };

    // A companion reader over an in-memory backing buffer. Reads within the backing buffer are
    // served verbatim; reads past it fall back to the deterministic fixture pattern so a test can
    // declare a companion far larger than the bytes it hands over. Traffic is recorded and capped,
    // so an implementation that tries to slurp the whole companion fails fast instead of hanging.
    struct companion_source
    {
        std::span<const std::byte> bytes;
        std::uint64_t declared_size = 0;
        std::size_t cap = std::size_t{1} << 20;

        std::size_t calls = 0;
        std::size_t total_read = 0;
        std::size_t max_request = 0;

        [[nodiscard]] pro::source make()
        {
            return [this](std::uint64_t offset, std::span<std::byte> destination) {
                ++calls;
                if (offset > declared_size || destination.size() > declared_size - static_cast<std::size_t>(offset))
                    throw std::out_of_range("read past the declared companion size");
                if (destination.size() > cap || total_read > cap - destination.size())
                    throw std::runtime_error("companion read budget exceeded");

                for (std::size_t i = 0; i < destination.size(); ++i) {
                    const std::uint64_t at = offset + i;
                    destination[i] = at < bytes.size() ? bytes[static_cast<std::size_t>(at)]
                                                       : fixtures::pattern_byte(static_cast<std::size_t>(at));
                }

                total_read += destination.size();
                max_request = std::max(max_request, destination.size());
            };
        }
    };

    [[nodiscard]] companion_source source_for(const std::vector<std::byte> &bytes)
    {
        return companion_source{.bytes = std::span<const std::byte>(bytes), .declared_size = bytes.size()};
    }

    // Explicit std::byte conversion: a bare int is never implicitly convertible to std::byte.
    [[nodiscard]] std::vector<std::byte> key_of(const std::initializer_list<int> values)
    {
        std::vector<std::byte> key;
        key.reserve(values.size());
        for (const int value : values)
            key.push_back(static_cast<std::byte>(value & 0xFF));
        return key;
    }

    [[nodiscard]] const pro::recovered_key &require_key(const pro::key_result &result)
    {
        REQUIRE(std::holds_alternative<pro::recovered_key>(result));
        return std::get<pro::recovered_key>(result);
    }

    [[nodiscard]] pro::key_error require_error(const pro::key_result &result)
    {
        REQUIRE(std::holds_alternative<pro::key_error>(result));
        return std::get<pro::key_error>(result);
    }

    // Frozen from the upstream WolfPro::GetDxArcKey() path over fixtures::v1_companion(). The final
    // selected byte already decodes to 0x00, so upstream appends nothing.
    const std::vector<std::byte> v1_expected = key_of({0x66, 0xBD, 0x18, 0x00});

    // Frozen over fixtures::v1_nonzero_companion(): the final selected byte decodes to 0x03, so
    // upstream appends exactly one trailing 0x00. This is the archive-layer normalization the public
    // accessor always performs for variant 1.
    const std::vector<std::byte> v1_nonzero_expected = key_of({0x66, 0xBD, 0x18, 0x03, 0x00});

    // Frozen over fixtures::v1_seed_b_companion(): a different byte[4] LCG seed yields a distinct key
    // (also ending in a nonzero byte, hence its own trailing 0x00).
    const std::vector<std::byte> v1_seed_b_expected = key_of({0x65, 0xBD, 0x1A, 0x02, 0x00});

    // Frozen over fixtures::v2_companion(): 16 selected bytes, an embedded NUL, then the four
    // dynamic key bytes. GetDxArcKey never touches this binary tail.
    const std::vector<std::byte> v2_expected = key_of({
        0x6F, 0x69, 0xD5, 0x24, 0xF9, 0x7F, 0x1A, 0x32, 0xC2, 0x89, 0x36,
        0xBF, 0xD6, 0x3D, 0x71, 0xC0, 0x00, 0x2F, 0x1A, 0x89, 0xD0,
    });

    // Frozen over fixtures::v2_compact_companion(): the single selected position is the last physical
    // byte, which lives outside the AES-CTR range [30, size - 1) and is therefore the raw byte.
    const std::vector<std::byte> v2_compact_expected = key_of({0x86, 0x00, 0x1F, 0x0A, 0x19, 0xD8});

    // Frozen over fixtures::v2_offset2_companion(): byte[7] == 2 selects a raw byte one past the AES
    // range, so the remaining key bytes come from the raw companion too.
    const std::vector<std::byte> v2_offset2_expected = key_of({0xAB, 0x00, 0x1C, 0x0C, 0x17, 0x86});

    // Frozen over fixtures::v2_sparse_companion() (1 MiB): the scattered selection must be served by
    // a handful of bounded reads, never a whole-file scan.
    const std::vector<std::byte> v2_sparse_expected = key_of({
        0x84, 0xB5, 0xD0, 0xB2, 0xDA, 0x33, 0x9D, 0x41, 0x45, 0xB6, 0xDF,
        0x3E, 0x8B, 0xB1, 0x29, 0x21, 0x00, 0x6A, 0xDA, 0x99, 0x78,
    });

    // Frozen from the upstream path over fixtures::cc2_companion().
    const std::vector<std::byte> cc2_expected = key_of({
        0x9E, 0x14, 0x79, 0xBE, 0x7B, 0x05, 0x1F, 0x89, 0x59, 0x2A, 0x3D, 0x98, 0x3F, 0x1B, 0xD8, 0xAB,
        0x1D, 0x08, 0xFE, 0xBA, 0xFB, 0x39, 0x9C, 0x85, 0xD9, 0x2E, 0xBA, 0x94, 0xBF, 0x1F, 0x58, 0xA7,
        0x9D, 0x0C, 0x7E, 0xB6, 0x7A, 0x3D, 0x1C, 0x81, 0x58, 0x22, 0x3A, 0x90, 0x3E, 0x13, 0xD9, 0xA3,
        0x1C, 0x00, 0xFF, 0xB2, 0xFA, 0x31, 0x9D, 0x9D, 0xD8, 0x26, 0xBB, 0xAC, 0xBE, 0x17, 0x59, 0xBF,
    });

    // Frozen over fixtures::cc2_min_companion() (31 bytes): calcDyn divides only by constants, so the
    // zero data size is defined and the key is valid.
    const std::vector<std::byte> cc2_min_expected = key_of({
        0xD1, 0x88, 0x3F, 0xB0, 0x34, 0x99, 0x59, 0x87, 0x16, 0xB6, 0x7B, 0x96, 0x70, 0x87, 0x9E, 0xA5,
        0x52, 0x94, 0xB8, 0xB4, 0xB4, 0xA5, 0xDA, 0x8B, 0x96, 0xB2, 0xFC, 0x9A, 0xF0, 0x83, 0x1E, 0xA9,
        0xD2, 0x90, 0x38, 0xB8, 0x35, 0xA1, 0x5A, 0x8F, 0x17, 0xBE, 0x7C, 0x9E, 0x71, 0x8F, 0x9F, 0xAD,
        0x53, 0x9C, 0xB9, 0xBC, 0xB5, 0xAD, 0xDB, 0x93, 0x97, 0xBA, 0xFD, 0xA2, 0xF1, 0x8B, 0x1F, 0xB1,
    });
} // namespace

TEST_CASE("v1 companion yields the normalized upstream key and metadata", "[wolfrpg][pro][keys]")
{
    const std::vector<std::byte> bytes = fixtures::v1_companion();
    companion_source source = source_for(bytes);

    const pro::key_result result = pro::recover_key(source.declared_size, source.make());
    const pro::recovered_key &key = require_key(result);

    CHECK(key.archive_variant == pro::variant::v1);
    CHECK(key.crypt_version == 1000);
    CHECK(key.key == v1_expected);
    // A key that already ends in 0x00 is left untouched: no second NUL is appended.
    REQUIRE(key.key.size() == std::size_t{4});
    CHECK(key.key.back() == std::byte{0x00});
    CHECK(source.calls > 0);
}

TEST_CASE("v1 companion ending in a nonzero selected byte gains exactly one trailing NUL", "[wolfrpg][pro][keys]")
{
    const std::vector<std::byte> bytes = fixtures::v1_nonzero_companion();
    companion_source source = source_for(bytes);

    const pro::key_result result = pro::recover_key(source.declared_size, source.make());
    const pro::recovered_key &key = require_key(result);

    CHECK(key.archive_variant == pro::variant::v1);
    CHECK(key.crypt_version == 1000);
    CHECK(key.key == v1_nonzero_expected);
    REQUIRE(key.key.size() == std::size_t{5});
    CHECK(key.key[3] == std::byte{0x03}); // the raw final selected byte, left as-is
    CHECK(key.key.back() == std::byte{0x00});
}

TEST_CASE("v1 companions with different seed bytes recover their own keys", "[wolfrpg][pro][keys]")
{
    const std::vector<std::byte> seed_a_bytes = fixtures::v1_companion();
    const std::vector<std::byte> seed_b_bytes = fixtures::v1_seed_b_companion();

    companion_source seed_a = source_for(seed_a_bytes);
    companion_source seed_b = source_for(seed_b_bytes);

    // Hold each key_result in a named local: require_key returns a reference into the variant, so
    // binding it to a temporary key_result would leave the reference dangling at the end of the
    // full expression.
    const pro::key_result result_a = pro::recover_key(seed_a.declared_size, seed_a.make());
    const pro::key_result result_b = pro::recover_key(seed_b.declared_size, seed_b.make());
    const pro::recovered_key &key_a = require_key(result_a);
    const pro::recovered_key &key_b = require_key(result_b);

    CHECK(key_a.key == v1_expected);
    CHECK(key_b.key == v1_seed_b_expected);
    CHECK(key_a.key != key_b.key);
}

TEST_CASE("v2 companion yields the frozen key with embedded NUL and untouched binary tail", "[wolfrpg][pro][keys]")
{
    const std::vector<std::byte> bytes = fixtures::v2_companion();
    companion_source source = source_for(bytes);

    const pro::key_result result = pro::recover_key(source.declared_size, source.make());
    const pro::recovered_key &key = require_key(result);

    CHECK(key.archive_variant == pro::variant::v2);
    CHECK(key.crypt_version == 1010);
    CHECK(key.key == v2_expected);
    REQUIRE(key.key.size() == std::size_t{21});
    CHECK(key.key[16] == std::byte{0x00});
    // GetDxArcKey never rewrites the v2 tail: the four dynamic bytes after the separator are frozen.
    CHECK(std::vector<std::byte>(key.key.begin() + 17, key.key.end()) == key_of({0x2F, 0x1A, 0x89, 0xD0}));
}

TEST_CASE("v2 companion selecting the last physical byte keeps it raw", "[wolfrpg][pro][keys]")
{
    // size == 32, dataSize == 1, keyLen == 1, byte[7] == 1: the single selected index is 31, the
    // final physical byte, which the AES-CTR pass over [30, size - 1) never decrypts.
    const std::vector<std::byte> bytes = fixtures::v2_compact_companion();
    companion_source source = source_for(bytes);

    const pro::key_result result = pro::recover_key(source.declared_size, source.make());
    const pro::recovered_key &key = require_key(result);

    CHECK(key.archive_variant == pro::variant::v2);
    CHECK(key.crypt_version == 1010);
    CHECK(key.key == v2_compact_expected);
}

TEST_CASE("v2 companion with a nonzero offset past the AES window keeps the byte raw", "[wolfrpg][pro][keys]")
{
    // size == 33, dataSize == 2, byte[7] == 2: the selected index is 32, inside the companion but
    // outside the decrypted window [30, 32), so the key starts from the raw companion byte.
    const std::vector<std::byte> bytes = fixtures::v2_offset2_companion();
    companion_source source = source_for(bytes);

    const pro::key_result result = pro::recover_key(source.declared_size, source.make());
    const pro::recovered_key &key = require_key(result);

    CHECK(key.archive_variant == pro::variant::v2);
    CHECK(key.key == v2_offset2_expected);
}

TEST_CASE("a large sparse v2 companion is read with bounded traffic", "[wolfrpg][pro][keys]")
{
    // 1 MiB companion whose whole file must never be read: the key selection touches the header plus
    // keyLen scattered positions, so a correct implementation serves a handful of small reads.
    const std::vector<std::byte> bytes = fixtures::v2_sparse_companion();
    companion_source source = source_for(bytes);

    const pro::key_result result = pro::recover_key(source.declared_size, source.make());
    const pro::recovered_key &key = require_key(result);

    CHECK(key.archive_variant == pro::variant::v2);
    CHECK(key.key == v2_sparse_expected);
    CHECK(source.max_request <= 4096);
    CHECK(source.total_read <= 4096);
}

TEST_CASE("cc2 companion key depends only on the companion length", "[wolfrpg][pro][keys]")
{
    const std::vector<std::byte> bytes = fixtures::cc2_companion();
    companion_source source = source_for(bytes);

    const pro::key_result result = pro::recover_key(source.declared_size, source.make());
    const pro::recovered_key &key = require_key(result);

    CHECK(key.archive_variant == pro::variant::cc2);
    CHECK(key.crypt_version == 0xC8);
    CHECK(key.key == cc2_expected);

    // Nothing beyond variant detection and a small header window is needed.
    CHECK(source.calls > 0);
    CHECK(source.total_read <= std::size_t{64});
}

TEST_CASE("a 31-byte cc2 companion is valid", "[wolfrpg][pro][keys]")
{
    // calcDyn computes size - 31 == 0 and divides only by constants, so the key is defined. Only
    // companions shorter than 31 bytes are rejected, and no division by the data size occurs.
    const std::vector<std::byte> bytes = fixtures::cc2_min_companion();
    companion_source source = source_for(bytes);

    const pro::key_result result = pro::recover_key(source.declared_size, source.make());
    const pro::recovered_key &key = require_key(result);

    CHECK(key.archive_variant == pro::variant::cc2);
    CHECK(key.crypt_version == 0xC8);
    CHECK(key.key == cc2_min_expected);
}

TEST_CASE("truncated companions are rejected before any algorithm runs", "[wolfrpg][pro][keys]")
{
    SECTION("empty companion")
    {
        const std::vector<std::byte> empty;
        companion_source source = source_for(empty);
        source.declared_size = 0;

        const pro::key_error error = require_error(pro::recover_key(0, source.make()));
        CHECK(error.code == pro::key_error_code::truncated);
        CHECK(source.calls == std::size_t{0});
    }

    SECTION("v1 companion below the minimum file size")
    {
        const std::vector<std::byte> bytes = fixtures::make_companion(48, {{0, fixtures::v1_first_byte}});
        companion_source source = source_for(bytes);

        const pro::key_error error = require_error(pro::recover_key(source.declared_size, source.make()));
        CHECK(error.code == pro::key_error_code::truncated);
    }

    SECTION("v2 companion below the 31-byte header window")
    {
        const std::vector<std::byte> bytes = fixtures::make_companion(30, {{0, fixtures::v2_first_byte}});
        companion_source source = source_for(bytes);

        const pro::key_error error = require_error(pro::recover_key(source.declared_size, source.make()));
        CHECK(error.code == pro::key_error_code::truncated);
    }

    SECTION("cc2 companion below the 31-byte window")
    {
        const std::vector<std::byte> bytes = fixtures::make_companion(30, {{0, fixtures::cc2_first_byte}});
        companion_source source = source_for(bytes);

        const pro::key_error error = require_error(pro::recover_key(source.declared_size, source.make()));
        CHECK(error.code == pro::key_error_code::truncated);
    }
}

TEST_CASE("a zero Pro1 key length is rejected instead of dividing by zero", "[wolfrpg][pro][keys]")
{
    const std::vector<std::byte> bytes = fixtures::make_companion(
        fixtures::v1_size, {{0, fixtures::v1_first_byte}, {4, fixtures::v1_seed_byte}, {19, 0x00}});
    companion_source source = source_for(bytes);

    const pro::key_error error = require_error(pro::recover_key(source.declared_size, source.make()));
    CHECK(error.code == pro::key_error_code::invalid_key_length);
}

TEST_CASE("a v2 companion one byte past the header has zero data size and is rejected", "[wolfrpg][pro][keys]")
{
    SECTION("companion smaller than the fixed header is truncated")
    {
        const std::vector<std::byte> bytes = fixtures::make_companion(30, {{0, fixtures::v2_first_byte}});
        companion_source source = source_for(bytes);

        const pro::key_error error = require_error(pro::recover_key(source.declared_size, source.make()));
        CHECK(error.code == pro::key_error_code::truncated);
    }

    SECTION("companion whose data size is exactly zero is an invalid data size")
    {
        const std::vector<std::byte> bytes = fixtures::make_companion(31, {{0, fixtures::v2_first_byte}});
        companion_source source = source_for(bytes);

        const pro::key_error error = require_error(pro::recover_key(source.declared_size, source.make()));
        CHECK(error.code == pro::key_error_code::invalid_data_size);
    }
}

TEST_CASE("a selected v2 key position outside the companion is rejected", "[wolfrpg][pro][keys]")
{
    // byte[7] == 0xFF pushes every selected index past the companion end.
    const std::vector<std::byte> bytes = fixtures::make_companion(fixtures::v2_size, {{0, fixtures::v2_first_byte},
                                                                                      {3, fixtures::v2_byte3},
                                                                                      {4, fixtures::v2_byte4},
                                                                                      {5, fixtures::v2_byte5},
                                                                                      {6, fixtures::v2_byte6},
                                                                                      {7, 0xFF},
                                                                                      {19, fixtures::v2_key_len_byte}});
    companion_source source = source_for(bytes);

    const pro::key_error error = require_error(pro::recover_key(source.declared_size, source.make()));
    CHECK(error.code == pro::key_error_code::key_out_of_range);
}

TEST_CASE("a companion at or above the 32-bit format bound is refused, not fabricated", "[wolfrpg][pro][keys]")
{
    const std::uint64_t too_large = std::uint64_t{1} << 32; // first size the uint32 field cannot hold

    for (const std::uint8_t first : {fixtures::v1_first_byte, fixtures::v2_first_byte, fixtures::cc2_first_byte}) {
        const std::vector<std::byte> bytes = fixtures::make_companion(64, {{0, first}});
        companion_source source = source_for(bytes);
        source.declared_size = too_large;

        const pro::key_error error = require_error(pro::recover_key(too_large, source.make()));
        CHECK(error.code == pro::key_error_code::unsupported_size);
    }
}

TEST_CASE("a v2 companion whose data size exceeds the signed 32-bit search range is refused", "[wolfrpg][pro][keys]")
{
    // dataSize == physical_size - 31. The upstream remainder search runs over signed 32-bit values,
    // so the first data size past INT32_MAX is refused rather than wrapping into an unbounded search.
    const std::uint64_t first_beyond = (std::uint64_t{1} << 31) + 31; // dataSize == 2^31
    const std::uint64_t three_gib = std::uint64_t{3} << 30;           // below 2^32, dataSize still > INT32_MAX

    for (const std::uint64_t physical : {first_beyond, three_gib}) {
        const std::vector<std::byte> bytes = fixtures::v2_companion();
        companion_source source = source_for(bytes);
        source.declared_size = physical;

        const pro::key_error error = require_error(pro::recover_key(physical, source.make()));
        CHECK(error.code == pro::key_error_code::unsupported_size);
    }
}

TEST_CASE("a multi-gigabyte v1 companion stays supported and is read with bounded traffic", "[wolfrpg][pro][keys]")
{
    // 3 GiB is past INT32_MAX + 31 yet below 2^32: the signed-range limit is a v2-only contract, so a
    // v1 companion this large is still recovered, and only the sparse key positions may be read.
    const std::vector<std::byte> bytes = fixtures::v1_companion();
    companion_source source = source_for(bytes);
    source.declared_size = std::uint64_t{3} << 30;

    const pro::key_result result = pro::recover_key(source.declared_size, source.make());
    const pro::recovered_key &key = require_key(result);

    CHECK(key.key == v1_expected);
    CHECK(source.max_request <= 4096);
    CHECK(source.total_read <= 4096);
}

TEST_CASE("source failures propagate with their concrete type", "[wolfrpg][pro][keys]")
{
    SECTION("a custom fault is preserved verbatim")
    {
        const pro::source failing = [](std::uint64_t, std::span<std::byte>) { throw source_fault(0x51C0); };

        bool caught = false;
        try {
            (void)pro::recover_key(fixtures::v1_size, failing);
        } catch (const source_fault &fault) {
            caught = true;
            CHECK(fault.payload == 0x51C0);
        }
        CHECK(caught);
    }

    SECTION("a standard library failure is not swallowed into a key_error")
    {
        const pro::source failing = [](std::uint64_t, std::span<std::byte>) { throw std::logic_error("io"); };

        bool caught = false;
        try {
            (void)pro::recover_key(fixtures::v2_size, failing);
        } catch (const std::logic_error &) {
            caught = true;
        }
        CHECK(caught);
    }

    SECTION("a fault raised while reading a selected key position is forwarded, not converted")
    {
        // The reader serves the header and the earlier key positions, then fails on the final selected
        // position. A correct implementation forwards that late failure instead of committing a
        // partial key; the fault is keyed to the final position so only that read trips it.
        const std::vector<std::byte> bytes = fixtures::v1_companion();
        const std::uint64_t fault_cover = fixtures::v1_last_selected_offset;
        std::size_t calls = 0;

        auto backing = source_for(bytes);
        const auto backing_read = backing.make();
        const pro::source failing = [&backing_read, &calls](std::uint64_t offset, std::span<std::byte> destination) {
            ++calls;
            if (offset <= fault_cover && fault_cover < offset + destination.size())
                throw source_fault(0x51C0);

            backing_read(offset, destination);
        };
        std::array<std::byte, 1> after{};
        failing(fault_cover + 1, after);
        CHECK(after[0] == bytes[static_cast<std::size_t>(fault_cover + 1)]);
        calls = 0; // keep the recovery call-count assertion independent of this boundary control

        bool caught = false;
        try {
            (void)pro::recover_key(static_cast<std::uint64_t>(bytes.size()), failing);
        } catch (const source_fault &fault) {
            caught = true;
            CHECK(fault.payload == 0x51C0);
        }
        CHECK(caught);
        CHECK(calls > 0);
    }

    SECTION("a Pro2 fault raised on a selected key byte after the header is forwarded, not converted")
    {
        // The v2 path reads the header first, then reads the AES region [30, size - 1) and the final
        // raw key position to obtain the selected key bytes. The reader serves the header and the
        // earlier key positions, then fails on the read that covers a chosen Pro2 key byte (the final
        // selected position). A correct implementation forwards that late failure instead of
        // committing a partial key; keying the fault to a key position well past the header proves it
        // fires only after the header has been read.
        const std::vector<std::byte> bytes = fixtures::v2_companion();
        const std::uint64_t fault_cover = fixtures::v2_last_selected_offset;
        std::size_t calls = 0;

        auto backing = source_for(bytes);
        const auto backing_read = backing.make();
        const pro::source failing = [&backing_read, &calls](std::uint64_t offset, std::span<std::byte> destination) {
            ++calls;
            if (offset <= fault_cover && fault_cover < offset + destination.size())
                throw source_fault(0x2B2E);

            backing_read(offset, destination);
        };
        std::array<std::byte, 1> after{};
        failing(fault_cover + 1, after);
        CHECK(after[0] == bytes[static_cast<std::size_t>(fault_cover + 1)]);
        calls = 0; // keep the recovery call-count assertion independent of this boundary control

        bool caught = false;
        try {
            (void)pro::recover_key(static_cast<std::uint64_t>(bytes.size()), failing);
        } catch (const source_fault &fault) {
            caught = true;
            CHECK(fault.payload == 0x2B2E);
        }
        CHECK(caught);
        CHECK(calls > 0);
    }
}

TEST_CASE("simultaneous recoveries of different seeds and variants use per-operation state", "[wolfrpg][pro][keys]")
{
    // The v1 path is seeded by byte[4] and the v2 path by an AES/RNG context derived from the header.
    // If any of that state were process-global, running these recoveries simultaneously would corrupt
    // one another. A barrier starts all three at once and each must still reproduce its frozen key.
    const std::vector<std::byte> seed_a_bytes = fixtures::v1_companion();
    const std::vector<std::byte> seed_b_bytes = fixtures::v1_seed_b_companion();
    const std::vector<std::byte> v2_bytes = fixtures::v2_companion();

    companion_source seed_a = source_for(seed_a_bytes);
    companion_source seed_b = source_for(seed_b_bytes);
    companion_source v2 = source_for(v2_bytes);

    std::barrier gate{3};
    pro::key_result result_a;
    pro::key_result result_b;
    pro::key_result result_v2;

    auto worker = [&gate](companion_source &src, pro::key_result &out) {
        gate.arrive_and_wait();
        out = pro::recover_key(src.declared_size, src.make());
    };

    std::thread first(worker, std::ref(seed_a), std::ref(result_a));
    std::thread second(worker, std::ref(seed_b), std::ref(result_b));
    std::thread third(worker, std::ref(v2), std::ref(result_v2));

    first.join();
    second.join();
    third.join();

    CHECK(require_key(result_a).key == v1_expected);
    CHECK(require_key(result_b).key == v1_seed_b_expected);
    CHECK(require_key(result_v2).key == v2_expected);
}

TEST_CASE("companion source bounds reject before writing and preserve the read budget",
          "[wolfrpg][pro][keys][source-helper]")
{
    const std::vector<std::byte> bytes{std::byte{1}, std::byte{2}, std::byte{3}, std::byte{4}};
    std::array<std::byte, 8> destination{};
    companion_source source = source_for(bytes);
    auto read = source.make();
    require_message<std::out_of_range>([&] { read(5, std::span(destination).first(0)); },
                                       "read past the declared companion size");
    require_message<std::out_of_range>([&] { read(3, std::span(destination).first(2)); },
                                       "read past the declared companion size");
    CHECK(source.total_read == 0);
    CHECK(source.max_request == 0);
    CHECK(destination == std::array<std::byte, 8>{});
    source.declared_size = 8;
    source.cap = 4;
    require_message<std::runtime_error>([&] { read(0, destination); }, "companion read budget exceeded");
    CHECK(destination == std::array<std::byte, 8>{});
    read(0, std::span(destination).first(3));
    const auto before_rejection = destination;
    require_message<std::runtime_error>([&] { read(3, std::span(destination).first(2)); },
                                        "companion read budget exceeded");
    CHECK(destination == before_rejection);
    CHECK(source.total_read == 3);
    CHECK(source.max_request == 3);
    CHECK(destination[0] == bytes[0]);
    CHECK(destination[1] == bytes[1]);
    CHECK(destination[2] == bytes[2]);
    CHECK(destination[3] == std::byte{});
    source.cap = 16;
    CHECK_FALSE(observed_exception<std::out_of_range>([&] { read(8, std::span(destination).first(0)); }).has_value());
    read(0, destination);
    CHECK(source.total_read == 11);
    CHECK(source.max_request == 8);
    CHECK(source.calls == 7);
    for (std::size_t i = 0; i < bytes.size(); ++i)
        CHECK(destination[i] == bytes[i]);
    for (std::size_t i = bytes.size(); i < destination.size(); ++i)
        CHECK(destination[i] == fixtures::pattern_byte(i));
}
