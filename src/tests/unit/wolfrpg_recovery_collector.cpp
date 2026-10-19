#include "../../core/wolfrpg/wolfx_metadata.h"
#include <catch2/catch_test_macros.hpp>

#include <array>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string_view>
#include <utility>
#include <vector>

namespace
{
    using namespace unpackrat::wolfrpg;
    using image = std::vector<std::byte>;

    // Minimal classic CommonEvent records, following CommonEvents.hpp and Command.hpp.
    // These writers produce input bytes; the production collector uses the real vendor parser.
    void number(image &out, std::uint32_t n)
    {
        for (unsigned shift = 0; shift != 32; shift += 8)
            out.push_back(static_cast<std::byte>((n >> shift) & 255));
    }
    void text(image &out, std::string_view value)
    {
        number(out, static_cast<std::uint32_t>(value.size() + 1));
        for (const auto ch : value)
            out.push_back(static_cast<std::byte>(ch));
        out.push_back(std::byte{});
    }
    struct instruction
    {
        std::uint32_t id;
        std::vector<std::uint32_t> args;
        std::vector<std::string_view> strings;
        bool movement = false;
    };
    image common(const std::vector<instruction> &commands)
    {
        image out{std::byte{0}, std::byte{0x57}, std::byte{0},    std::byte{0}, std::byte{0x4f}, std::byte{0x4c},
                  std::byte{0}, std::byte{0x46}, std::byte{0x43}, std::byte{0}, std::byte{0x8f}};
        number(out, 1);
        out.push_back(std::byte{0x8e});
        number(out, 0);
        number(out, 0);
        out.insert(out.end(), 7, std::byte{});
        text(out, "");
        number(out, static_cast<std::uint32_t>(commands.size()));
        for (const auto &command : commands) {
            out.push_back(static_cast<std::byte>(command.args.size() + 1));
            number(out, command.id);
            for (const auto arg : command.args)
                number(out, arg);
            out.push_back(std::byte{});
            out.push_back(static_cast<std::byte>(command.strings.size()));
            for (const auto value : command.strings)
                text(out, value);
            out.push_back(static_cast<std::byte>(command.movement));
            if (command.movement) {
                out.insert(out.end(), 6, std::byte{});
                number(out, 0);
            }
        }
        text(out, "");
        text(out, "");
        out.push_back(std::byte{0x8f});
        for (int i = 0; i != 4; ++i)
            number(out, 0);
        out.insert(out.end(), 29, std::byte{});
        for (int i = 0; i != 100; ++i)
            text(out, "");
        out.push_back(std::byte{0x91});
        text(out, "");
        out.push_back(std::byte{0x91});
        out.push_back(std::byte{0x89});
        return out;
    }
    template <class F> void failure(F &&action, metadata_error_code code)
    {
        bool observed = false;
        try {
            action();
        } catch (const metadata_error &error) {
            observed = true;
            CHECK(error.code() == code);
        }
        CHECK(observed);
    }
    image owned_map()
    {
        const auto path =
            std::filesystem::path(__FILE__).parent_path().parent_path() / "fixtures/wolfrpg/recovery-game/empty.mps";
        std::ifstream source(path, std::ios::binary);
        REQUIRE(source.good());
        const std::vector<char> chars{std::istreambuf_iterator<char>(source), std::istreambuf_iterator<char>()};
        image bytes;
        for (const char ch : chars)
            bytes.push_back(static_cast<std::byte>(ch));
        return bytes;
    }
} // namespace

TEST_CASE("collector recovers normalized commands and accounts repeated images", "[recovery-collector]")
{
    const auto bytes = common({{122, {3000007}, {"alpha"}},
                               {121, {2000009, 42}, {}},
                               {1000, {7}, {"folder", "secret"}},
                               {1000, {7}, {"folder", ""}},
                               {1000, {1}, {}},
                               {101, {}, {}},
                               {122, {77}, {"raw"}},
                               {121, {88, 99}, {}}});
    metadata_collector collector;
    wolfx_candidates candidates;
    collector.collect(metadata_kind::common_events, bytes, candidates);
    REQUIRE(candidates.keys().size() == 1);
    CHECK(candidates.keys()[0] == "secret");
    REQUIRE(candidates.string_values().size() == 2);
    CHECK(candidates.string_values()[0].id == 7);
    CHECK(candidates.string_values()[0].value == "alpha");
    CHECK(candidates.string_values()[1].id == 77);
    CHECK(candidates.string_values()[1].value == "raw");
    REQUIRE(candidates.int_values().size() == 2);
    CHECK(candidates.int_values()[0].id == 9);
    CHECK(candidates.int_values()[0].value == 42);
    CHECK(candidates.int_values()[1].id == 88);
    CHECK(candidates.int_values()[1].value == 99);
    collector.collect(metadata_kind::common_events, bytes, candidates);
    CHECK(candidates.keys().size() == 2);
    CHECK(candidates.string_values().size() == 4);
    CHECK(candidates.int_values().size() == 4);
    CHECK(collector.total_read_bytes() == 2 * bytes.size());
    CHECK(collector.total_expanded_bytes() == 0);
    CHECK(collector.total_units() == 48);
    CHECK(collector.limits().max_total_units == metadata_limits{}.max_total_units);
    metadata_collector moved(std::move(collector));
    metadata_collector assigned;
    assigned = std::move(moved);
    CHECK(assigned.total_read_bytes() == 2 * bytes.size());
}

TEST_CASE("collector commits no candidates after malformed command identities or budget refusal",
          "[recovery-collector]")
{
    for (const auto id : {122U, 121U, 1000U}) {
        const auto bytes = common({{122, {1}, {"staged"}}, {id, {}, {}, true}});
        metadata_collector collector;
        wolfx_candidates candidates;
        candidates.add_key("retained");
        failure([&] { collector.collect(metadata_kind::common_events, bytes, candidates); },
                metadata_error_code::malformed);
        CHECK(candidates.string_values().empty());
        REQUIRE(candidates.keys().size() == 1);
        CHECK(candidates.keys()[0] == "retained");
        CHECK(collector.total_read_bytes() == bytes.size());
    }
    const auto bytes = common({{122, {1}, {"staged"}}, {1000, {7}, {"", "excess"}}});
    wolfx_candidate_limits limits;
    limits.max_key_length = 2;
    wolfx_candidates candidates(limits);
    candidates.add_key("ok");
    metadata_collector collector;
    failure([&] { collector.collect(metadata_kind::common_events, bytes, candidates); },
            metadata_error_code::candidate_budget_exceeded);
    CHECK(candidates.string_values().empty());
    REQUIRE(candidates.keys().size() == 1);
    CHECK(candidates.keys()[0] == "ok");
}

TEST_CASE("collector retains parse work and rejects preflight without charging", "[recovery-collector]")
{
    const auto bytes = common({});
    metadata_limits limits;
    limits.max_input_bytes = bytes.size();
    limits.max_total_read_bytes = bytes.size();
    metadata_collector collector(limits);
    wolfx_candidates candidates;
    auto excessive = bytes;
    excessive.push_back(std::byte{});
    failure([&] { collector.collect(metadata_kind::common_events, excessive, candidates); },
            metadata_error_code::input_too_large);
    CHECK(collector.total_read_bytes() == 0);
    collector.collect(metadata_kind::common_events, bytes, candidates);
    CHECK(collector.total_read_bytes() == bytes.size());
    CHECK(collector.total_units() == 1);
    failure([&] { collector.collect(metadata_kind::common_events, bytes, candidates); },
            metadata_error_code::read_budget_exceeded);
    CHECK(collector.total_read_bytes() == bytes.size());
    limits.max_total_units = 1;
    const auto command = common({{121, {2, 4}, {}}});
    limits.max_input_bytes = command.size();
    limits.max_total_read_bytes = command.size();
    metadata_collector unit_limited(limits);
    failure([&] { unit_limited.collect(metadata_kind::common_events, command, candidates); },
            metadata_error_code::unit_budget_exceeded);
    CHECK(unit_limited.total_units() == 1);
    CHECK(unit_limited.total_read_bytes() == command.size());
    CHECK(candidates.int_values().empty());
    const auto map = owned_map();
    metadata_collector maps;
    maps.collect(metadata_kind::map, map, candidates);
    CHECK(maps.total_read_bytes() == map.size());
    CHECK(maps.total_units() == 0);
    image truncated{std::byte{0}};
    failure([&] { maps.collect(metadata_kind::map, truncated, candidates); }, metadata_error_code::malformed);
    CHECK(maps.total_read_bytes() == map.size() + 1);
    auto invalid = bytes;
    invalid.back() = std::byte{};
    metadata_collector bad;
    failure([&] { bad.collect(metadata_kind::common_events, invalid, candidates); }, metadata_error_code::malformed);
    CHECK(bad.total_units() == 1);
}

TEST_CASE("collector accounts compressed images and resets format state before classic input", "[recovery-collector]")
{
    const image compressed{std::byte{0},    std::byte{0x57}, std::byte{0},    std::byte{0},    std::byte{0x4f},
                           std::byte{0x4c}, std::byte{0},    std::byte{0x46}, std::byte{0x43}, std::byte{0},
                           std::byte{0x93}, std::byte{5},    std::byte{0},    std::byte{0},    std::byte{0},
                           std::byte{6},    std::byte{0},    std::byte{0},    std::byte{0},    std::byte{0x50},
                           std::byte{0},    std::byte{0},    std::byte{0},    std::byte{0},    std::byte{0x89}};
    metadata_limits limits;
    limits.max_image_expanded_bytes = 5;
    limits.max_total_expanded_bytes = 5;
    metadata_collector collector(limits);
    wolfx_candidates candidates;
    collector.collect(metadata_kind::common_events, compressed, candidates);
    CHECK(collector.total_expanded_bytes() == 5);
    CHECK(collector.total_units() == 0);
    const auto classic = common({{121, {2000003, 17}, {}}});
    collector.collect(metadata_kind::common_events, classic, candidates);
    REQUIRE(candidates.int_values().size() == 1);
    CHECK(candidates.int_values()[0].value == 17);
    failure([&] { collector.collect(metadata_kind::common_events, compressed, candidates); },
            metadata_error_code::expanded_budget_exceeded);
    CHECK(collector.total_expanded_bytes() == 5);
    CHECK(collector.total_read_bytes() == 2 * compressed.size() + classic.size());
    limits.max_image_expanded_bytes = 4;
    metadata_collector image_limited(limits);
    failure([&] { image_limited.collect(metadata_kind::common_events, compressed, candidates); },
            metadata_error_code::expanded_budget_exceeded);
    CHECK(image_limited.total_expanded_bytes() == 0);
    auto corrupt = compressed;
    corrupt.back() = std::byte{};
    metadata_collector malformed;
    failure([&] { malformed.collect(metadata_kind::common_events, corrupt, candidates); },
            metadata_error_code::malformed);
    CHECK(malformed.total_expanded_bytes() == 5);
    CHECK(malformed.total_read_bytes() == compressed.size());
    auto protected_map = owned_map();
    protected_map[1] = std::byte{0x50};
    protected_map[5] = std::byte{0x57};
    metadata_collector unsupported;
    failure([&] { unsupported.collect(metadata_kind::map, protected_map, candidates); },
            metadata_error_code::unsupported);
    CHECK(unsupported.total_read_bytes() == protected_map.size());
    REQUIRE(candidates.int_values().size() == 1);
    CHECK(candidates.int_values()[0].id == 3);
}

TEST_CASE("collector visits map event pages and recovers their command values", "[recovery-collector]")
{
    // Extend the repository-owned empty map with one Event/Page using Map.hpp's dump layout.
    auto map = owned_map();
    REQUIRE(map.size() == 47);
    map[42] = std::byte{1};
    map.pop_back();
    map.push_back(std::byte{0x6f});
    number(map, 0x3039);
    number(map, 0);
    text(map, "");
    number(map, 0);
    number(map, 0);
    number(map, 1);
    number(map, 0);
    map.push_back(std::byte{0x79});
    number(map, 0);
    text(map, "");
    map.insert(map.end(), 47, std::byte{});
    number(map, 0);
    number(map, 1);
    map.push_back(std::byte{3});
    number(map, 121);
    number(map, 2000005);
    number(map, 123);
    map.insert(map.end(), 3, std::byte{});
    number(map, 0);
    map.insert(map.end(), 3, std::byte{});
    map.push_back(std::byte{0x7a});
    map.push_back(std::byte{0x70});
    map.push_back(std::byte{0x66});
    metadata_collector collector;
    wolfx_candidates candidates;
    collector.collect(metadata_kind::map, map, candidates);
    REQUIRE(candidates.int_values().size() == 1);
    CHECK(candidates.int_values()[0].id == 5);
    CHECK(candidates.int_values()[0].value == 123);
    CHECK(collector.total_units() == 5);
    CHECK(collector.total_read_bytes() == map.size());
    CHECK(candidates.keys().empty());
    CHECK(candidates.string_values().empty());
}
