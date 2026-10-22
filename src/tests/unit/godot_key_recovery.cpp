#include "../../core/godot/key_recovery.h"
#include "../support/godot_fixtures.h"
#include "../support/godot_key_fixtures.h"
#include <algorithm>
#include <catch2/catch_test_macros.hpp>
#include <cstddef>
#include <cstdint>
#include <initializer_list>
#include <limits>
#include <sstream>
#include <string>
#include <vector>
namespace
{
    std::string as_string(const std::vector<std::byte> &bytes)
    {
        return {reinterpret_cast<const char *>(bytes.data()), bytes.size()};
    }

    // Wraps an in-memory PE image and records the largest single ``read`` request the
    // recovery scan issues, so bounded-window reads can be asserted directly.
    class counting_stream final
    {
      public:
        explicit counting_stream(std::vector<std::byte> image)
            : buffer_(std::string(reinterpret_cast<const char *>(image.data()), image.size()), std::ios::in),
              stream_(&buffer_)
        {
        }
        std::istream &stream()
        {
            return stream_;
        }
        std::size_t max_single_read() const
        {
            return buffer_.max_read;
        }

      private:
        class counting_buffer final : public std::stringbuf
        {
          public:
            using std::stringbuf::stringbuf;
            std::size_t max_read = 0;

          protected:
            std::streamsize xsgetn(char *destination, std::streamsize count) override
            {
                max_read = std::max(max_read, static_cast<std::size_t>(count));
                return std::stringbuf::xsgetn(destination, count);
            }
        };
        counting_buffer buffer_;
        std::istream stream_;
    };

    // A generated x64 PE with a 512 KiB executable section whose key consumer sits one
    // byte before the 64 KiB window boundary and whose key reference is only reachable
    // through a call into a second executable section.
    std::vector<std::byte> build_large_pe()
    {
        using test::support::godot_bytes;
        constexpr std::uint64_t base = 0x140000000ULL;
        constexpr std::uint32_t text_rva = 0x1000, data_rva = 0x200000, text2_rva = 0x300000;
        constexpr std::size_t text_size = 0x80000, data_size = 0x1000;
        constexpr std::size_t text_offset = 0x200, data_offset = 0x200 + text_size;
        constexpr std::size_t text2_offset = data_offset + data_size;
        godot_bytes bytes(0x200 + text_size + 2 * data_size, 0xcc);
        bytes[0] = 'M';
        bytes[1] = 'Z';
        test::support::patch_u32(bytes, 0x3c, 0x80);
        test::support::patch_u32(bytes, 0x80, 0x4550);
        test::support::patch_u16(bytes, 0x84, 0x8664);
        test::support::patch_u16(bytes, 0x86, 3);
        test::support::patch_u16(bytes, 0x94, 0xf0);
        test::support::patch_u16(bytes, 0x98, 0x20b);
        test::support::patch_u64(bytes, 0xb0, base);
        const auto section = [&](std::size_t at, const char *name, std::uint32_t rva, std::uint32_t raw,
                                 std::uint32_t raw_size, std::uint32_t virtual_size, std::uint32_t flags) {
            for (std::size_t i = 0; name[i] != '\0'; ++i)
                bytes[at + i] = static_cast<std::uint8_t>(name[i]);
            test::support::patch_u32(bytes, at + 8, virtual_size);
            test::support::patch_u32(bytes, at + 12, rva);
            test::support::patch_u32(bytes, at + 16, raw_size);
            test::support::patch_u32(bytes, at + 20, raw);
            test::support::patch_u32(bytes, at + 36, flags);
        };
        section(0x188, ".text", text_rva, 0x200, text_size, text_size, 0x60000020);
        section(0x1b0, ".rdata", data_rva, static_cast<std::uint32_t>(data_offset), data_size, data_size, 0x40000040);
        section(0x1d8, ".text2", text2_rva, static_cast<std::uint32_t>(text2_offset), data_size, data_size, 0x60000020);
        for (std::size_t i = 0; i < 32; ++i)
            bytes[data_offset + i] = static_cast<std::uint8_t>(i + 1);
        std::size_t cursor = text_offset + 0xffff;
        const auto emit = [&](std::initializer_list<std::uint8_t> code) {
            for (const auto value : code)
                bytes[cursor++] = value;
        };
        const auto u32 = [&](std::uint32_t value) {
            test::support::patch_u32(bytes, cursor, value);
            cursor += 4;
        };
        const auto relative = [&](std::uint64_t instruction_address, std::size_t length, std::uint64_t target) {
            u32(static_cast<std::uint32_t>(static_cast<std::int64_t>(target) -
                                           static_cast<std::int64_t>(instruction_address + length)));
        };
        // .text: anchor whose 'GDPC' literal and decoding instruction cross the 64 KiB boundary.
        emit({0x3d, 0x47, 0x44, 0x50, 0x43, 0xba, 0x20, 0, 0, 0});
        {
            const auto call_offset = static_cast<std::uint64_t>(cursor - text_offset);
            emit({0xe8});
            relative(base + text_rva + call_offset, 5, base + text2_rva);
        }
        emit({0xc3});
        // .text2: call target whose window carries the only key reference.
        cursor = text2_offset;
        emit({0xba, 0x20, 0, 0, 0});
        {
            const auto lea_offset = static_cast<std::uint64_t>(cursor - text2_offset);
            emit({0x48, 0x8d, 0x0d});
            relative(base + text2_rva + lea_offset, 7, base + data_rva);
        }
        emit({0x0f, 0xb6, 0x01, 0xc3});
        std::vector<std::byte> image(bytes.size());
        for (std::size_t i = 0; i < bytes.size(); ++i)
            image[i] = static_cast<std::byte>(bytes[i]);
        return image;
    }
} // namespace
TEST_CASE("godot key recovery: recognizes bounded scalar and SIMD constant patterns", "[godot][key-recovery]")
{
    using test::support::key_shape;
    for (const auto shape : {key_shape::direct_scalar, key_shape::pointer_scalar, key_shape::direct_simd,
                             key_shape::pointer_simd, key_shape::xor3_scalar, key_shape::xor3_simd}) {
        for (bool wide : {false, true}) {
            CAPTURE(static_cast<unsigned>(shape), wide);
            const auto fixture = test::support::make_key_fixture(shape, wide);
            std::istringstream stream(as_string(fixture.image));
            unpackrat::godot::key_recovery_budget budget;
            const auto keys = unpackrat::godot::recover_key_candidates(stream, budget);
            REQUIRE(std::ranges::find(keys, fixture.expected) != keys.end());
            CHECK(budget.candidates == keys.size());
            CHECK(budget.instructions <= unpackrat::godot::key_recovery_limits{}.max_instructions);
        }
    }
}
TEST_CASE("godot key recovery: ordinary data and unanchored code never yield candidates", "[godot][key-recovery]")
{
    std::istringstream stream("ordinary file");
    unpackrat::godot::key_recovery_budget budget;
    CHECK(unpackrat::godot::recover_key_candidates(stream, budget).empty());
    auto fixture = test::support::make_key_fixture();
    fixture.image[0x201] = std::byte{0};
    std::istringstream unanchored(as_string(fixture.image));
    CHECK(unpackrat::godot::recover_key_candidates(unanchored, budget).empty());
}
TEST_CASE("godot key recovery: aggregate limits fail closed", "[godot][key-recovery]")
{
    const auto fixture = test::support::make_key_fixture();
    for (int kind = 0; kind < 4; ++kind) {
        CAPTURE(kind);
        unpackrat::godot::key_recovery_limits limits;
        if (kind == 0)
            limits.max_scanned_bytes = 0;
        if (kind == 1)
            limits.max_instructions = 0;
        if (kind == 2)
            limits.max_candidates = 0;
        if (kind == 3)
            limits.max_pattern_instructions = 0;
        unpackrat::godot::key_recovery_budget budget;
        std::istringstream stream(as_string(fixture.image));
        CHECK(unpackrat::godot::recover_key_candidates(stream, budget, limits).empty());
    }
}

TEST_CASE("godot key recovery: a large pack reader still exposes its bounded key consumer",
          "[godot][key-recovery][key-consumer-window]")
{
    auto fixture = test::support::make_key_fixture();
    std::copy_n(fixture.image.begin() + 0x20a, 11, fixture.image.begin() + 0x40a);
    std::fill(fixture.image.begin() + 0x20a, fixture.image.begin() + 0x40a, std::byte{0x90});
    // Relocate the RIP-relative LEA along with its instruction.
    fixture.image[0x40d] = std::byte{0xef};
    fixture.image[0x40e] = std::byte{0x0d};
    std::istringstream stream(as_string(fixture.image));
    unpackrat::godot::key_recovery_budget budget;
    const auto keys = unpackrat::godot::recover_key_candidates(stream, budget);
    CHECK(std::ranges::find(keys, fixture.expected) != keys.end());
}

TEST_CASE("godot key recovery: candidate material has a fixed upper bound even for relaxed caller limits",
          "[godot][key-recovery][key-material-bound]")
{
    auto fixture = test::support::make_key_fixture();
    fixture.image.resize(0x2400);
    const auto put32 = [&](std::size_t at, std::uint32_t value) {
        for (std::size_t i = 0; i < 4; ++i)
            fixture.image[at + i] = static_cast<std::byte>(value >> (8 * i));
    };
    put32(0x1b0 + 8, 0x1200);
    put32(0x1b0 + 16, 0x1200);
    std::size_t cursor = 0x20a;
    for (std::size_t index = 0; index < 129; ++index) {
        fixture.image[cursor++] = std::byte{0x48};
        fixture.image[cursor++] = std::byte{0x8d};
        fixture.image[cursor++] = std::byte{0x0d};
        const auto target = std::uint64_t{0x140002000} + static_cast<std::uint64_t>(index) * 32;
        const auto next = std::uint64_t{0x140001000} + cursor - 0x200 + 4;
        put32(cursor, static_cast<std::uint32_t>(target - next));
        cursor += 4;
        std::fill_n(fixture.image.begin() +
                        static_cast<std::ptrdiff_t>(0x1200 + static_cast<std::uint64_t>(index) * 32),
                    32, static_cast<std::byte>(index + 1));
    }
    fixture.image[cursor] = std::byte{0xc3};
    unpackrat::godot::key_recovery_limits limits;
    limits.max_candidates = std::numeric_limits<std::uint64_t>::max();
    unpackrat::godot::key_recovery_budget budget;
    std::istringstream stream(as_string(fixture.image));
    const auto keys = unpackrat::godot::recover_key_candidates(stream, budget, limits);
    REQUIRE(keys.size() == 128);
    CHECK(budget.candidates == 128);
    CHECK(keys.front() != keys.back());
}

namespace
{
    using unpackrat::godot::key_recovery_budget;
    using unpackrat::godot::key_recovery_error;
    using unpackrat::godot::key_recovery_limits;
    void patch(test::support::key_fixture &fixture, std::size_t offset, std::uint64_t value, std::size_t width = 4)
    {
        for (std::size_t i = 0; i < width; ++i)
            fixture.image[offset + i] = static_cast<std::byte>(value >> (8 * i));
    }
    auto recover(const test::support::key_fixture &fixture, const key_recovery_limits &limits = {})
    {
        std::istringstream stream(as_string(fixture.image));
        key_recovery_budget budget;
        return unpackrat::godot::recover_key_candidates(stream, budget, limits);
    }
} // namespace
TEST_CASE("godot key recovery: malformed PE ranges and modes are rejected before scanning", "[godot][key-recovery]")
{
    struct mutation
    {
        std::uint64_t value;
        std::size_t offset;
        std::size_t width;
        constexpr mutation(std::size_t changed_offset, std::uint64_t changed_value, std::size_t changed_width)
            : value(changed_value), offset(changed_offset), width(changed_width)
        {
        }
    };
    const mutation cases[] = {{0x80, 0, 4},
                              {0x84, 0x9999, 2},
                              {0x86, 0, 2},
                              {0x86, 97, 2},
                              {0x94, 63, 2},
                              {0x94, 4097, 2},
                              {0x98, 0x10b, 2},
                              {0xb0, std::numeric_limits<std::uint64_t>::max() - 0x800, 8},
                              {0xb0, std::numeric_limits<std::uint64_t>::max() - 0x1800, 8},
                              {0x188 + 20, 0x2000, 4},
                              {0x188 + 16, 0x2000, 4},
                              {0x1b0 + 12, 0x1000, 4},
                              {0x3c, 0x1601, 4},
                              {0x3c, 0x15f8, 4}};
    for (const auto &row : cases) {
        CAPTURE(row.offset, row.value);
        auto fixture = test::support::make_key_fixture();
        patch(fixture, row.offset, row.value, row.width);
        CHECK_THROWS_AS(recover(fixture), key_recovery_error);
    }
    auto ordinary = test::support::make_key_fixture();
    ordinary.image[0] = std::byte{0};
    CHECK(recover(ordinary).empty());
    std::istringstream failed("data");
    failed.setstate(std::ios::badbit);
    key_recovery_budget budget;
    CHECK_THROWS_AS(unpackrat::godot::recover_key_candidates(failed, budget), key_recovery_error);
}
TEST_CASE("godot key recovery: section order and empty sections have explicit range semantics", "[godot][key-recovery]")
{
    auto reversed = test::support::make_key_fixture();
    for (std::size_t i = 0; i < 40; ++i)
        std::swap(reversed.image[0x188 + i], reversed.image[0x1b0 + i]);
    const auto keys = recover(reversed);
    CHECK(std::ranges::find(keys, reversed.expected) != keys.end());
    for (const std::size_t row : {0x188U, 0x1b0U}) {
        auto empty = test::support::make_key_fixture();
        patch(empty, row + 8, 0);
        patch(empty, row + 16, 0);
        CHECK(recover(empty).empty());
    }
    auto short_target = test::support::make_key_fixture(test::support::key_shape::pointer_scalar);
    patch(short_target, 0x1300, 0x1400023f0, 8);
    const auto absent = recover(short_target);
    CHECK(std::ranges::find(absent, short_target.expected) == absent.end());
}
TEST_CASE("godot key recovery: instruction boundaries and consumer windows fail closed", "[godot][key-recovery]")
{
    SECTION("an anchor after a prefix is decoded from a bounded lookback")
    {
        auto fixture = test::support::make_key_fixture();
        std::copy_n(fixture.image.begin() + 0x200, 21, fixture.image.begin() + 0x240);
        std::fill(fixture.image.begin() + 0x200, fixture.image.begin() + 0x240, std::byte{0xcc});
        patch(fixture, 0x24d, 0xfaf);
        const auto keys = recover(fixture);
        CHECK(std::ranges::find(keys, fixture.expected) != keys.end());
    }
    SECTION("adjacent pack literals cannot validate the wrong instruction")
    {
        auto fixture = test::support::make_key_fixture();
        std::copy_n(fixture.image.begin() + 0x200, 21, fixture.image.begin() + 0x205);
        patch(fixture, 0x212, 0xfea);
        const auto keys = recover(fixture);
        CHECK(std::ranges::find(keys, fixture.expected) != keys.end());
    }
    SECTION("truncated instructions end a recognized window")
    {
        auto fixture = test::support::make_key_fixture();
        patch(fixture, 0x188 + 16, 11);
        patch(fixture, 0x188 + 8, 11);
        fixture.image[0x20a] = std::byte{0x0f};
        CHECK(recover(fixture).empty());
    }
    SECTION("a window can end at the last complete instruction")
    {
        auto fixture = test::support::make_key_fixture();
        patch(fixture, 0x188 + 16, 17);
        patch(fixture, 0x188 + 8, 17);
        const auto keys = recover(fixture);
        CHECK(std::ranges::find(keys, fixture.expected) != keys.end());
    }
    SECTION("a deliberately narrow instruction allowance cannot reach a key")
    {
        key_recovery_limits limits;
        limits.max_pattern_instructions = 1;
        CHECK(recover(test::support::make_key_fixture(), limits).empty());
    }
    SECTION("calls outside backed executable sections are not followed")
    {
        auto fixture = test::support::make_key_fixture(test::support::key_shape::xor3_simd);
        patch(fixture, 0x20b, 0x7fffffff);
        CHECK(recover(fixture).empty());
    }
}
TEST_CASE("godot key recovery: a three-array formula requires every syntactic constant", "[godot][key-recovery]")
{
    for (int change = 0; change < 3; ++change) {
        auto fixture = test::support::make_key_fixture(test::support::key_shape::xor3_scalar);
        if (change == 0) {
            fixture.image[0xe2b] = std::byte{0x90};
            fixture.image[0xe2c] = std::byte{0x90};
        }
        if (change == 1)
            patch(fixture, 0xe27, 0x7fffffff);
        if (change == 2)
            patch(fixture, 0xe27, 0xffffffff);
        CAPTURE(change);
        const auto keys = recover(fixture);
        CHECK(std::ranges::find(keys, fixture.expected) == keys.end());
    }
}
TEST_CASE("godot key recovery: initializer searches share byte and instruction allowances", "[godot][key-recovery]")
{
    auto fixture = test::support::make_key_fixture(test::support::key_shape::xor3_scalar);
    SECTION("a second scan cannot exceed the byte allowance")
    {
        key_recovery_limits limits;
        limits.max_scanned_bytes = 4504;
        CHECK(recover(fixture, limits).empty());
    }
    SECTION("an already spent allowance cannot wrap")
    {
        key_recovery_limits limits;
        key_recovery_budget budget;
        budget.instructions = limits.max_instructions + 1;
        std::istringstream stream(as_string(fixture.image));
        CHECK(unpackrat::godot::recover_key_candidates(stream, budget, limits).empty());
        CHECK(budget.instructions == limits.max_instructions + 1);
    }
    SECTION("malformed address-reference prefixes are ignored in the initializer pass")
    {
        fixture.image[0x11ff] = std::byte{0x48};
        fixture.image[0x1100] = std::byte{0x8b};
        fixture.image[0x1101] = std::byte{0xc0};
        const auto keys = recover(fixture);
        CHECK(std::ranges::find(keys, fixture.expected) != keys.end());
    }
}

TEST_CASE("godot key recovery: unbacked initializer targets are retained under a fixed bound", "[godot][key-recovery]")
{
    auto fixture = test::support::make_key_fixture();
    patch(fixture, 0x1b0 + 8, 0x1000);
    std::size_t cursor = 0x20a;
    for (std::size_t i = 0; i < 33; ++i) {
        fixture.image[cursor++] = std::byte{0x48};
        fixture.image[cursor++] = std::byte{0x8d};
        fixture.image[cursor++] = std::byte{0x0d};
        const auto next = std::uint64_t{0x140001000} + cursor - 0x200 + 4;
        patch(fixture, cursor, static_cast<std::uint32_t>(0x140002600ULL + static_cast<std::uint64_t>(i) * 32 - next));
        cursor += 4;
    }
    fixture.image[cursor] = std::byte{0xc3};
    CHECK(recover(fixture).empty());
}

TEST_CASE("godot key recovery: window reads stay bounded for a large executable section",
          "[godot][key-recovery][key-window-bound]")
{
    const auto image = build_large_pe();
    counting_stream stream(image);
    unpackrat::godot::key_recovery_budget budget;
    const auto keys = unpackrat::godot::recover_key_candidates(stream.stream(), budget);
    unpackrat::codecs::key256 expected{};
    for (std::size_t i = 0; i < 32; ++i)
        expected[i] = static_cast<std::byte>(i + 1);
    // The key is only reachable through the call into the second executable section, and its
    // anchor literal crosses the first cache boundary.
    CHECK(std::ranges::find(keys, expected) != keys.end());
    // No single source read may pull in a whole executable section.
    CHECK(stream.max_single_read() <= std::size_t{64} * 1024);
}
