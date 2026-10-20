// Public-source failures retain provenance even when their concrete type is the vendor's
// own format-error type. Vendor headers are used only as independent test inputs here.
#include "../../core/wolfrpg/lz_reader.h"
#include "../../core/wolfrpg/system_codepage.h"
#include "../../core/wolfrpg/text_encoding.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <exception>
#include <limits>
#include <new>
#include <span>
#include <string_view>
#include <vector>

#include <catch2/catch_test_macros.hpp>
#include <uberwolf/Archive/BoundedLz.hpp>

namespace
{
    namespace codec = unpackrat::wolfrpg::codec;
    namespace platform = unpackrat::wolfrpg::text::platform;
    using source_error = wolf::archive::boundedlz::error;

    // One literal 'A', declared decoded extent 1 and encoded extent 10.
    constexpr std::array literal{std::byte{1}, std::byte{0}, std::byte{0}, std::byte{0},    std::byte{10},
                                 std::byte{0}, std::byte{0}, std::byte{0}, std::byte{0xff}, std::byte{'A'}};
} // namespace

TEST_CASE("LZ construction preserves a source exception of the vendor format-error type", "[recovery-failure-contract]")
{
    const auto original = std::make_exception_ptr(source_error("source header failure: 481"));
    const codec::lz_reader::source input = [&](std::span<std::byte>) -> std::size_t {
        std::rethrow_exception(original);
    };
    bool caught = false;
    try {
        codec::lz_reader reader(input, literal.size(), 1);
    } catch (const source_error &error) {
        caught = true;
        REQUIRE(std::string_view(error.what()) == "source header failure: 481");
    }
    REQUIRE(caught);
}

TEST_CASE("LZ reads preserve a source exception of the vendor format-error type", "[recovery-failure-contract]")
{
    const auto original = std::make_exception_ptr(source_error("source body failure: 739"));
    std::size_t delivered = 0;
    const codec::lz_reader::source input = [&](std::span<std::byte> destination) -> std::size_t {
        // Short positive input is permitted: provide the complete header, then fail the body pull.
        if (delivered == 9) {
            std::rethrow_exception(original);
        }
        const auto count = std::min(destination.size(), std::size_t{9} - delivered);
        std::memcpy(destination.data(), literal.data() + delivered, count);
        delivered += count;
        return count;
    };
    codec::lz_reader reader(input, literal.size(), 1);
    REQUIRE(delivered == 9);
    REQUIRE(reader.remaining() == 1);
    bool caught = false;
    try {
        std::array<std::byte, 1> output{};
        (void)reader.read(output);
    } catch (const source_error &error) {
        caught = true;
        REQUIRE(std::string_view(error.what()) == "source body failure: 739");
    }
    REQUIRE(caught);
}

TEST_CASE("LZ kernel errors remain project errors when the source did not throw", "[recovery-failure-contract]")
{
    // A complete, corrupt header is likewise translated instead of leaking the vendor type.
    std::size_t delivered = 0;
    const codec::lz_reader::source corrupt = [&](std::span<std::byte> destination) -> std::size_t {
        const auto count = std::min(destination.size(), literal.size() - delivered);
        std::memcpy(destination.data(), literal.data() + delivered, count);
        delivered += count;
        return count;
    };
    bool caught = false;
    try {
        codec::lz_reader reader(corrupt, literal.size(), 2);
    } catch (const codec::lz_error &) {
        caught = true;
    }
    REQUIRE(caught);
    REQUIRE(delivered == literal.size());
}

TEST_CASE("the host CP1251 adapter maps every byte and reports refused conversion", "[recovery-failure-contract]")
{
    for (unsigned value = 0; value < 256; ++value) {
        CAPTURE(value);
        REQUIRE(platform::single_byte_to_scalar(1251, static_cast<unsigned char>(value)).has_value());
    }
    REQUIRE(platform::single_byte_to_scalar(1251, 0) == 0);
    REQUIRE(platform::single_byte_to_scalar(1251, 0x80) == 0x0402);
    REQUIRE(platform::single_byte_to_scalar(1251, 0x98) == 0x0098);
    REQUIRE(platform::single_byte_to_scalar(1251, 0xc0) == 0x0410);
    REQUIRE(platform::single_byte_to_scalar(1251, 0xff) == 0x044f);
    REQUIRE_FALSE(platform::single_byte_to_scalar(std::numeric_limits<std::uint32_t>::max(), 'A').has_value());
    REQUIRE_FALSE(platform::single_byte_to_scalar(65001, 0xc2).has_value());
}

namespace
{
    namespace text = unpackrat::wolfrpg::text;
    struct dependency_called
    {
    };
    std::optional<std::uint32_t> mapped_single_byte(std::uint32_t page, unsigned char byte)
    {
        REQUIRE(page == 1251);
        return 0x350U + byte;
    }
    std::optional<std::uint32_t> refused_single_byte(std::uint32_t page, unsigned char byte)
    {
        REQUIRE(page == 1251);
        REQUIRE(byte == 0x98);
        return std::nullopt;
    }
    std::optional<std::uint32_t> unexpected_decoder(std::uint32_t, unsigned char)
    {
        throw dependency_called{}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
    }
    std::optional<std::uint32_t> allocating_decoder(std::uint32_t, unsigned char)
    {
        throw std::bad_alloc{};
    }
    std::optional<std::uint32_t> length_decoder(std::uint32_t, unsigned char)
    {
        throw std::length_error("borrowed decoder extent");
    }
    template <class Error, class Operation> void expect_boundary_error(Operation operation)
    {
        bool caught = false;
        try {
            operation();
        } catch (const Error &) {
            caught = true;
        }
        REQUIRE(caught);
    }
    template <class Operation> void expect_encoding_error(Operation operation, text::encoding_error_code expected)
    {
        bool caught = false;
        try {
            operation();
        } catch (const text::encoding_error &error) {
            caught = true;
            REQUIRE(error.code() == expected);
        }
        REQUIRE(caught);
    }
} // namespace
TEST_CASE("text conversion borrows a single-byte decoder without replacing the real platform default",
          "[recovery-failure-contract][recovery-text-boundary]")
{
    const std::array bytes{std::byte{0xc0}, std::byte{0xff}};
    REQUIRE(text::convert(bytes, 1251, mapped_single_byte) == "\xd0\x90\xd1\x8f");
    REQUIRE(text::convert(bytes, 1251) == "\xd0\x90\xd1\x8f");
    const std::array refused{std::byte{0x98}};
    expect_encoding_error([&] { (void)text::convert(refused, 1251, refused_single_byte); },
                          text::encoding_error_code::unmappable_character);
    REQUIRE(platform::single_byte_to_scalar(1251, 0x98) == 0x98);
}
TEST_CASE("text conversion validates input before consulting the borrowed decoder",
          "[recovery-failure-contract][recovery-text-boundary]")
{
    const std::array byte{std::byte{0xc0}};
    const std::vector<std::byte> oversized(text::max_component_bytes + 1, std::byte{0xc0});
    const std::array nul{std::byte{0}};
    expect_encoding_error([&] { (void)text::convert(byte, 555, unexpected_decoder); },
                          text::encoding_error_code::unsupported_codepage);
    expect_encoding_error([&] { (void)text::convert(oversized, 1251, unexpected_decoder); },
                          text::encoding_error_code::input_too_large);
    expect_encoding_error([&] { (void)text::convert(nul, 1251, unexpected_decoder); },
                          text::encoding_error_code::embedded_nul);
    REQUIRE(text::convert({}, 1251, unexpected_decoder).empty());
    const std::array ascii{std::byte{'a'}};
    REQUIRE(text::convert(ascii, 65001, unexpected_decoder) == "a");
    expect_boundary_error<dependency_called>([&] { (void)text::convert(byte, 1251, unexpected_decoder); });
}
TEST_CASE("borrowed decoder resource failures preserve their type and payload",
          "[recovery-failure-contract][recovery-text-boundary]")
{
    const std::array byte{std::byte{0xc0}};
    expect_boundary_error<std::bad_alloc>([&] { (void)text::convert(byte, 1251, allocating_decoder); });
    bool caught = false;
    try {
        (void)text::convert(byte, 1251, length_decoder);
    } catch (const std::length_error &error) {
        caught = true;
        REQUIRE(std::string_view(error.what()) == "borrowed decoder extent");
    }
    REQUIRE(caught);
}
