#include "../../modules/renpy/rpa1_index.h"
#include "../support/zlib_fixture.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#include <catch2/catch_test_macros.hpp>

namespace
{
    using unpackrat::renpy::parse_rpa1_index;
    using unpackrat::renpy::rpa1_fragment;
    using unpackrat::renpy::rpa1_limits;

    struct fixture_entry final
    {
        std::string path;
        std::vector<rpa1_fragment> fragments;
    };

    void append_string(std::string &pickle, const std::string_view value)
    {
        pickle.push_back('X');
        const auto size = static_cast<std::uint32_t>(value.size());
        for (unsigned shift = 0; shift < 32; shift += 8) {
            pickle.push_back(static_cast<char>((size >> shift) & 0xffU));
        }
        pickle.append(value);
    }

    std::string raw_index(const std::vector<fixture_entry> &entries)
    {
        std::string pickle = "}";
        for (const auto &entry : entries) {
            append_string(pickle, entry.path);
            pickle.push_back(']');
            for (const auto &fragment : entry.fragments) {
                pickle.append("I").append(std::to_string(fragment.offset)).append("\n");
                pickle.append("I").append(std::to_string(fragment.size)).append("\n");
                pickle.push_back(static_cast<char>(0x86));
                pickle.push_back('a');
            }
            pickle.push_back('s');
        }
        pickle.push_back('.');
        return pickle;
    }

    std::vector<std::byte> compressed_pickle(const std::string_view pickle)
    {
        return test::support::compress_zlib_fixture(std::as_bytes(std::span(pickle.data(), pickle.size())));
    }

    std::vector<std::byte> index(const std::vector<fixture_entry> &entries)
    {
        return compressed_pickle(raw_index(entries));
    }

    std::vector<std::byte> malformed_value(const std::string_view value)
    {
        std::string pickle = "}";
        append_string(pickle, "file");
        pickle.append(value).append("s.");
        return compressed_pickle(pickle);
    }
} // namespace

TEST_CASE("RenPy RPA1 index: fragments retain their order and logical sizes", "[renpy][rpa1]")
{
    const auto result =
        parse_rpa1_index(index({{"dir/hello.txt", {{8, 2}, {0, 3}, {10, 0}}}, {"other\\file.bin", {{3, 5}}}}), 10);
    REQUIRE(result.size() == 2);
    const auto found = std::ranges::find(result, "dir/hello.txt", &unpackrat::renpy::rpa1_entry::path);
    REQUIRE(found != result.end());
    CHECK(found->size == 5);
    REQUIRE(found->fragments.size() == 3);
    CHECK(found->fragments[0].offset == 8);
    CHECK(found->fragments[0].size == 2);
    CHECK(found->fragments[1].offset == 0);
    CHECK(found->fragments[1].size == 3);
    CHECK(found->fragments[2].offset == 10);
    CHECK(found->fragments[2].size == 0);
    const auto other = std::ranges::find(result, "other\\file.bin", &unpackrat::renpy::rpa1_entry::path);
    REQUIRE(other != result.end());
    CHECK(other->size == 5);
}

TEST_CASE("RenPy RPA1 index: empty archives and empty entries are valid", "[renpy][rpa1]")
{
    CHECK(parse_rpa1_index(index({}), 0).empty());
    const auto result = parse_rpa1_index(index({{"empty", {{0, 0}}}}), 0);
    REQUIRE(result.size() == 1);
    CHECK(result[0].path == "empty");
    CHECK(result[0].size == 0);
    REQUIRE(result[0].fragments.size() == 1);
    CHECK(result[0].fragments[0].offset == 0);
    CHECK(result[0].fragments[0].size == 0);
    CHECK_THROWS_AS(parse_rpa1_index(index({}), -1), std::runtime_error);
}

TEST_CASE("RenPy RPA1 index: compressed and expanded budgets have exact boundaries", "[renpy][rpa1]")
{
    const std::vector<fixture_entry> entries{{"file", {{0, 1}}}};
    const auto pickle = raw_index(entries);
    const auto compressed = compressed_pickle(pickle);
    rpa1_limits limits;
    limits.max_compressed_index_bytes = compressed.size();
    limits.max_decompressed_index_bytes = pickle.size();
    const auto accepted = parse_rpa1_index(compressed, 1, limits);
    REQUIRE(accepted.size() == 1);
    CHECK(accepted[0].size == 1);

    --limits.max_compressed_index_bytes;
    CHECK_THROWS_AS(parse_rpa1_index(compressed, 1, limits), std::runtime_error);
    ++limits.max_compressed_index_bytes;
    --limits.max_decompressed_index_bytes;
    CHECK_THROWS_AS(parse_rpa1_index(compressed, 1, limits), std::runtime_error);
}

TEST_CASE("RenPy RPA1 index: corrupt compressed data and malformed pickle fail closed", "[renpy][rpa1]")
{
    CHECK_THROWS_AS(parse_rpa1_index({}, 1), std::runtime_error);
    const std::array invalid{std::byte{'n'}, std::byte{'o'}};
    CHECK_THROWS_AS(parse_rpa1_index(invalid, 1), std::runtime_error);
    auto truncated = index({{"file", {{0, 1}}}});
    truncated.pop_back();
    CHECK_THROWS_AS(parse_rpa1_index(truncated, 1), std::runtime_error);
    auto trailing = index({{"file", {{0, 1}}}});
    trailing.push_back(std::byte{0});
    CHECK_THROWS_AS(parse_rpa1_index(trailing, 1), std::runtime_error);
    CHECK_THROWS_AS(parse_rpa1_index(compressed_pickle("N."), 1), std::runtime_error);
    CHECK_THROWS_AS(parse_rpa1_index(compressed_pickle("?"), 1), std::runtime_error);
    CHECK_THROWS_AS(parse_rpa1_index(compressed_pickle("Ibad\n."), 1), std::runtime_error);
    CHECK_THROWS_AS(parse_rpa1_index(compressed_pickle("I999999999999999999999999999999\n."), 1), std::runtime_error);
}

TEST_CASE("RenPy RPA1 index: property lists require nonempty integer pairs", "[renpy][rpa1]")
{
    const std::array<std::string_view, 8> invalid_values{
        "N", "]", "]Na", "])a", "](I0\nta", "](I0\nI1\nI2\nta", "](NI1\nta", "](I0\nNta"};
    for (const auto value : invalid_values) {
        CAPTURE(value);
        CHECK_THROWS_AS(parse_rpa1_index(malformed_value(value), 10), std::runtime_error);
    }
}

TEST_CASE("RenPy RPA1 index: entry and aggregate fragment limits are enforced", "[renpy][rpa1]")
{
    const auto two_entries = index({{"first", {{0, 1}}}, {"second", {{1, 1}}}});
    rpa1_limits limits;
    limits.max_entries = 2;
    limits.max_fragments = 2;
    CHECK(parse_rpa1_index(two_entries, 2, limits).size() == 2);
    limits.max_entries = 1;
    CHECK_THROWS_AS(parse_rpa1_index(two_entries, 2, limits), std::runtime_error);
    limits.max_entries = 2;
    limits.max_fragments = 1;
    CHECK_THROWS_AS(parse_rpa1_index(two_entries, 2, limits), std::runtime_error);
    limits.max_fragments = 2;
    CHECK_THROWS_AS(parse_rpa1_index(index({{"one", {{0, 1}, {1, 1}, {2, 1}}}}), 3, limits), std::runtime_error);
    limits.max_entries = 0;
    limits.max_fragments = 0;
    CHECK(parse_rpa1_index(index({}), 0, limits).empty());
}

TEST_CASE("RenPy RPA1 index: range decision table rejects invalid extents", "[renpy][rpa1]")
{
    struct range_case final
    {
        std::int64_t offset;
        std::int64_t size;
        bool accepted;
    };
    constexpr std::array cases{
        range_case{0, 0, true},
        range_case{0, 10, true},
        range_case{10, 0, true},
        range_case{9, 1, true},
        range_case{-1, 0, false},
        range_case{0, -1, false},
        range_case{11, 0, false},
        range_case{0, 11, false},
        range_case{9, 2, false},
        range_case{std::numeric_limits<std::int64_t>::max(), 1, false},
        range_case{1, std::numeric_limits<std::int64_t>::max(), false},
    };
    for (const auto &item : cases) {
        CAPTURE(item.offset, item.size);
        const auto compressed = index({{"file", {{item.offset, item.size}}}});
        if (item.accepted) {
            const auto result = parse_rpa1_index(compressed, 10);
            REQUIRE(result.size() == 1);
            CHECK(result[0].size == item.size);
        } else {
            CHECK_THROWS_AS(parse_rpa1_index(compressed, 10), std::runtime_error);
        }
    }
}

TEST_CASE("RenPy RPA1 index: fragment sums are checked before signed overflow", "[renpy][rpa1]")
{
    constexpr auto maximum = std::numeric_limits<std::int64_t>::max();
    const auto accepted = parse_rpa1_index(index({{"file", {{0, maximum - 1}, {0, 1}}}}), maximum);
    REQUIRE(accepted.size() == 1);
    CHECK(accepted[0].size == maximum);
    CHECK_THROWS_AS(parse_rpa1_index(index({{"file", {{0, maximum}, {0, 1}}}}), maximum), std::runtime_error);
}

TEST_CASE("RenPy RPA1 index: paths must remain relative and free of traversal", "[renpy][rpa1]")
{
    const std::vector<std::string> rejected{
        "",        "/root",       "\\root",        "C:file", "dir/file:stream", "..",
        "../file", "dir/../file", "dir\\..\\file", "dir/..", "dir\\..",         std::string("dir\0file", 8)};
    for (const auto &path : rejected) {
        CAPTURE(path);
        CHECK_THROWS_AS(parse_rpa1_index(index({{path, {{0, 0}}}}), 0), std::runtime_error);
    }
    const std::array<std::string_view, 6> accepted{"file", "dir/file", "dir\\file", "...", "a..b", "dir/./file"};
    for (const auto path : accepted) {
        CAPTURE(path);
        const auto result = parse_rpa1_index(index({{std::string(path), {{0, 0}}}}), 0);
        REQUIRE(result.size() == 1);
        CHECK(result[0].path == path);
    }
}

TEST_CASE("RenPy RPA1 index: path byte budgets preserve valid UTF8", "[renpy][rpa1]")
{
    const std::string unicode = "dir/\xd0\xbf\xd1\x83\xd1\x82\xd1\x8c.txt";
    rpa1_limits limits;
    limits.max_path_bytes = unicode.size();
    const auto result = parse_rpa1_index(index({{unicode, {{0, 0}}}}), 0, limits);
    REQUIRE(result.size() == 1);
    CHECK(result[0].path == unicode);
    --limits.max_path_bytes;
    CHECK_THROWS_AS(parse_rpa1_index(index({{unicode, {{0, 0}}}}), 0, limits), std::runtime_error);
}
