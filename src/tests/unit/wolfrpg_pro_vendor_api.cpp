#include "../support/wolfrpg_pro_fixtures.h"

#include <cstddef>
#include <cstdint>
#include <initializer_list>
#include <numeric>
#include <stdexcept>
#include <vector>

#include <catch2/catch_test_macros.hpp>

// Direct contract tests for the shared DxArc key kernels extracted into the pinned UberWolf
// headers (<uberwolf/Archive/WolfDxArcKey.hpp>). They exercise the whole-buffer entry points the
// archive adapter now shares, plus the variant-2 remainder selector, so the shared kernel is
// pinned independently of the adapter: the expected key bytes are the same frozen oracle bytes the
// adapter tests compare against, and the selected positions are asserted exactly.
#include <uberwolf/Archive/WolfDxArcKey.hpp>

namespace
{
    namespace dxarckey = wolf::crypt::dxarckey;
    namespace fixtures = test::support::wolfrpg_pro;

    [[nodiscard]] std::vector<std::uint8_t> raw(const std::vector<std::byte> &bytes)
    {
        std::vector<std::uint8_t> out(bytes.size());
        for (std::size_t i = 0; i < bytes.size(); ++i)
            out[i] = std::to_integer<std::uint8_t>(bytes[i]);
        return out;
    }

    [[nodiscard]] std::vector<std::uint8_t> expect(const std::initializer_list<int> values)
    {
        std::vector<std::uint8_t> out;
        out.reserve(values.size());
        for (const int value : values)
            out.push_back(static_cast<std::uint8_t>(value & 0xFF));
        return out;
    }
} // namespace

TEST_CASE("the shared v1 kernel reads exactly the stepless selected positions", "[wolfrpg][pro][vendor]")
{
    // With keyLen == 4 the step is 0x58B1 / 4 == 5676, so the selected physical positions are
    // 30, 5706, 11382 and 17058: a wrong step divisor or a wrong LCG advance changes both the
    // positions read and the key bytes.
    const std::vector<std::uint8_t> bytes = raw(fixtures::v1_companion());
    std::vector<std::uint32_t> reads;

    const auto readRaw = [&bytes, &reads](const std::uint32_t at) -> std::uint8_t {
        reads.push_back(at);
        return bytes[at];
    };

    const std::vector<std::uint8_t> key =
        dxarckey::v1::calcKey(bytes[dxarckey::v1::SEED_OFFSET], bytes[dxarckey::v1::KEY_LEN_OFFSET], readRaw);

    CHECK(key == expect({ 0x66, 0xBD, 0x18, 0x00 }));
    CHECK(reads == std::vector<std::uint32_t>{ 30, 5706, 11382, 17058 });
}

TEST_CASE("the shared v1 kernel stops before reading for a zero key length", "[wolfrpg][pro][vendor]")
{
    int reads = 0;
    const auto readRaw = [&reads](const std::uint32_t) -> std::uint8_t {
        ++reads;
        return 0;
    };

    CHECK(dxarckey::v1::calcKey(fixtures::v1_seed_byte, 0, readRaw).empty());
    CHECK(reads == 0);

    // The same callback instantiation driven with a nonzero length reaches the key stream: keyLen
    // == 1 selects only offset 30, so exactly one byte is read.
    const std::vector<std::uint8_t> single = dxarckey::v1::calcKey(fixtures::v1_seed_byte, 1, readRaw);
    CHECK(single.size() == std::size_t{ 1 });
    CHECK(reads == 1);
}

TEST_CASE("the shared v1 whole-buffer entry reproduces the frozen raw keys", "[wolfrpg][pro][vendor]")
{
    // The whole-buffer entry is the raw derivation: the archive-layer trailing NUL is not applied
    // here, so the nonzero-tail companion keeps its 0x03 last byte.
    CHECK(dxarckey::v1::calcKey(raw(fixtures::v1_companion()), static_cast<std::uint32_t>(fixtures::v1_size)) ==
          expect({ 0x66, 0xBD, 0x18, 0x00 }));
    CHECK(dxarckey::v1::calcKey(raw(fixtures::v1_nonzero_companion()), static_cast<std::uint32_t>(fixtures::v1_size)) ==
          expect({ 0x66, 0xBD, 0x18, 0x03 }));
    CHECK(dxarckey::v1::calcKey(raw(fixtures::v1_seed_b_companion()), static_cast<std::uint32_t>(fixtures::v1_size)) ==
          expect({ 0x65, 0xBD, 0x1A, 0x02 }));
}

TEST_CASE("the shared v1 whole-buffer entry rejects a companion below the minimum size", "[wolfrpg][pro][vendor]")
{
    CHECK(dxarckey::v1::calcKey(raw(fixtures::v1_companion()), 48).empty());
}

TEST_CASE("the shared v2 remainder search advances to the first coprime", "[wolfrpg][pro][vendor]")
{
    // dataSize == 169 == 13 * 13, so k == 13 is not coprime and the search must advance to 14; a
    // wrong search would leave a shared factor and select an index upstream never selected.
    CHECK(dxarckey::v2::selectK(169, 13) == 14);
    CHECK(dxarckey::v2::selectK(169, 94) == 94);
    CHECK(std::gcd(dxarckey::v2::selectK(169, 13), 169) == 1);
}

TEST_CASE("the shared v2 whole-buffer entry reproduces the frozen key", "[wolfrpg][pro][vendor]")
{
    CHECK(dxarckey::v2::calcKey(raw(fixtures::v2_companion())) ==
          expect({ 0x6F, 0x69, 0xD5, 0x24, 0xF9, 0x7F, 0x1A, 0x32, 0xC2, 0x89, 0x36, 0xBF, 0xD6, 0x3D, 0x71, 0xC0, 0x00,
                   0x2F, 0x1A, 0x89, 0xD0 }));
    CHECK(dxarckey::v2::calcKey(raw(fixtures::v2_compact_companion())) == expect({ 0x86, 0x00, 0x1F, 0x0A, 0x19, 0xD8 }));
    CHECK(dxarckey::v2::calcKey(raw(fixtures::v2_offset2_companion())) == expect({ 0xAB, 0x00, 0x1C, 0x0C, 0x17, 0x86 }));
}

TEST_CASE("the shared v2 whole-buffer entry rejects a companion with no data region", "[wolfrpg][pro][vendor]")
{
    // Anything at or below the 31-byte header has a zero (or negative) data size; the pinned path
    // had no data region to dereference there.
    CHECK(dxarckey::v2::calcKey(std::vector<std::uint8_t>(31, 0)).empty());
    CHECK(dxarckey::v2::calcKey(std::vector<std::uint8_t>{}).empty());
}

TEST_CASE("the shared v2 whole-buffer entry refuses a companion whose selected key byte is out of range",
          "[wolfrpg][pro][vendor]")
{
    // byte[7] == 0xFF on the 200-byte companion drives the first selected position to
    // KEY_START_OFFSET + 0xFF == 285, past the 200-byte buffer. The pinned whole-buffer path read the
    // companion through bounds-checked access, so the shared entry must surface std::out_of_range
    // rather than dereference past the buffer end.
    std::vector<std::uint8_t> bytes = raw(fixtures::v2_companion());
    bytes[7] = 0xFF;

    bool out_of_range = false;
    try {
        (void)dxarckey::v2::calcKey(bytes);
    } catch (const std::out_of_range &) {
        out_of_range = true;
    }

    CHECK(out_of_range);
}
