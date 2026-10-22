#include "../../core/wolfrpg/archive.h"
#include "../../core/wolfrpg/crypto.h"
#include "../../core/wolfrpg/game_context.h"
#include "../../core/wolfrpg/system_codepage.h"
#include "../../core/wolfrpg/text_encoding.h"
#include "../../core/wolfrpg/wolfx_game_metadata.h"
#include "../../core/wolfrpg/wolfx_metadata.h"
#include "../support/wolfrpg_fixtures.h"
#include "../support/wolfrpg_modern_archive_fixtures.h"
#include <array>
#include <catch2/catch_test_macros.hpp>
#include <cstddef>
#include <cstring>
#include <filesystem>
#include <sstream>
#include <uberwolf/DXLib/DXArchive.h>

namespace
{
    template <typename Error, typename Operation, typename Code>
    void require_error(Operation operation, const Code expected)
    {
        bool caught = false;
        try {
            operation();
        } catch (const Error &error) {
            caught = true;
            CHECK(error.code() == expected);
        }
        CHECK(caught);
    }
} // namespace

TEST_CASE("recovered metadata accounting preserves malformed work and rejects preflight without charging",
          "[wolfrpg][recovery]")
{
    using namespace unpackrat::wolfrpg;
    metadata_limits limits;
    limits.max_input_bytes = 1;
    limits.max_total_read_bytes = 1;
    limits.max_image_expanded_bytes = 1;
    limits.max_total_expanded_bytes = 1;
    metadata_collector collector(limits);
    wolfx_candidates candidates;
    candidates.add_key("retained");
    const std::array<std::byte, 2> oversized{};
    require_error<metadata_error>([&] { collector.collect(metadata_kind::map, oversized, candidates); },
                                  metadata_error_code::input_too_large);
    CHECK(collector.total_read_bytes() == 0);
    CHECK(collector.total_expanded_bytes() == 0);
    const std::array<std::byte, 1> malformed{};
    require_error<metadata_error>([&] { collector.collect(metadata_kind::map, malformed, candidates); },
                                  metadata_error_code::malformed);
    CHECK(collector.total_read_bytes() == 1);
    CHECK(collector.total_units() == 0);
    CHECK(collector.total_expanded_bytes() == 0);
    REQUIRE(candidates.keys().size() == 1);
    CHECK(candidates.keys()[0] == "retained");
    require_error<metadata_error>([&] { collector.collect(metadata_kind::common_events, malformed, candidates); },
                                  metadata_error_code::read_budget_exceeded);
    CHECK(collector.total_read_bytes() == 1);
}

TEST_CASE("game metadata collection reads selected regular files and keeps candidates on failure",
          "[wolfrpg][recovery]")
{
    using namespace unpackrat::wolfrpg;
    const auto root = std::filesystem::path(__FILE__).parent_path().parent_path() / "fixtures/wolfrpg/recovery-game";
    wolfx_candidates candidates;
    candidates.add_key("retained");
    REQUIRE_NOTHROW(collect_game_metadata(root, {}, candidates));
    REQUIRE(candidates.keys().size() == 1);
    CHECK(candidates.keys()[0] == "retained");
    game_metadata_limits tiny;
    tiny.max_source_bytes = 1;
    require_error<game_metadata_error>([&] { collect_game_metadata(root, tiny, candidates); },
                                       game_metadata_error_code::source_too_large);
    CHECK(candidates.keys()[0] == "retained");
    require_error<game_metadata_error>([&] { collect_game_metadata(root / "absent", {}, candidates); },
                                       game_metadata_error_code::root_missing);
    CHECK(candidates.keys()[0] == "retained");
}

TEST_CASE("game metadata discovery failures retain their exact public classification", "[wolfrpg][recovery]")
{
    using namespace unpackrat::wolfrpg;
    const auto fixtures = std::filesystem::path(__FILE__).parent_path().parent_path() / "fixtures/wolfrpg";
    const auto root = fixtures / "recovery-game";
    game_metadata_limits depth;
    depth.max_depth = 0;
    game_metadata_limits entries;
    entries.max_visited_entries = 0;
    game_metadata_limits sources;
    sources.max_sources = 0;
    game_metadata_limits paths;
    paths.max_total_path_bytes = 0;
    game_metadata_limits bytes;
    bytes.max_source_bytes = 1;
    struct row
    {
        std::filesystem::path path;
        game_metadata_limits limits;
        game_metadata_error_code expected;
    };
    const std::array rows{
        row{root / "absent", {}, game_metadata_error_code::root_missing},
        row{root / "BasicData/CommonEvent.dat", {}, game_metadata_error_code::root_not_directory},
        row{root / "bad<name", {}, game_metadata_error_code::root_io_failure},
        row{fixtures / "recovery-game-missing-common", {}, game_metadata_error_code::common_event_missing},
        row{fixtures / "recovery-game-refused", {}, game_metadata_error_code::common_event_refused},
        row{root, depth, game_metadata_error_code::depth_exceeded},
        row{root, entries, game_metadata_error_code::entry_budget_exceeded},
        row{root, sources, game_metadata_error_code::source_budget_exceeded},
        row{root, paths, game_metadata_error_code::path_byte_budget_exceeded},
        row{root, bytes, game_metadata_error_code::source_too_large}};
    for (const auto &test : rows) {
        wolfx_candidates candidates;
        candidates.add_key("retained");
        bool caught = false;
        try {
            collect_game_metadata(test.path, test.limits, candidates);
        } catch (const game_metadata_error &error) {
            caught = true;
            CHECK(error.code() == test.expected);
        }
        CHECK(caught);
        REQUIRE(candidates.keys().size() == 1);
        CHECK(candidates.keys()[0] == "retained");
    }
}

TEST_CASE("compressed common-event images charge decoded bytes before allocation and retain charged work",
          "[wolfrpg][recovery]")
{
    using namespace unpackrat::wolfrpg;
    // One LZ4 literal run: zero events and the 0x89 terminator, with independent physical/decoded sizes.
    const std::array<std::byte, 25> compressed{
        std::byte{0},    std::byte{0x57}, std::byte{0}, std::byte{0},    std::byte{0x4f}, std::byte{0x4c}, std::byte{0},
        std::byte{0x46}, std::byte{0x43}, std::byte{0}, std::byte{0x93}, std::byte{5},    std::byte{0},    std::byte{0},
        std::byte{0},    std::byte{6},    std::byte{0}, std::byte{0},    std::byte{0},    std::byte{0x50}, std::byte{0},
        std::byte{0},    std::byte{0},    std::byte{0}, std::byte{0x89}};
    metadata_limits limits;
    limits.max_image_expanded_bytes = 5;
    limits.max_total_expanded_bytes = 5;
    metadata_collector collector(limits);
    wolfx_candidates candidates;
    REQUIRE_NOTHROW(collector.collect(metadata_kind::common_events, compressed, candidates));
    CHECK(collector.total_expanded_bytes() == 5);
    CHECK(collector.total_read_bytes() == 25);
    CHECK(collector.total_units() == 0);
    CHECK(candidates.keys().empty());
    bool caught = false;
    try {
        collector.collect(metadata_kind::common_events, compressed, candidates);
    } catch (const metadata_error &error) {
        caught = true;
        CHECK(error.code() == metadata_error_code::expanded_budget_exceeded);
    }
    CHECK(caught);
    CHECK(collector.total_expanded_bytes() == 5);
    CHECK(collector.total_read_bytes() == 50);
}

TEST_CASE("frozen classic fixture records physical Huffman extents independently of archive facade",
          "[wolfrpg][recovery]")
{
    auto bytes = test::support::classic_dx8_payload();
    REQUIRE(bytes.size() == 966);
    DARC_HEAD header{};
    std::memcpy(&header, bytes.data(), sizeof(header));
    CHECK(header.DataStartAddress == 64);
    CHECK(header.FileNameTableStartAddress == 568);
    // The complete physical body region is 504 bytes, including one alignment byte.
    CHECK(header.FileNameTableStartAddress - header.DataStartAddress == 504);
    const auto password = unpackrat::wolfrpg::crypto::default_classic_password();
    const std::string key(reinterpret_cast<const char *>(password.data()), password.size());
    DXArchive raw;
    REQUIRE(raw.OpenArchiveMem(bytes.data(), static_cast<s64>(bytes.size()), key.c_str()) == 0);
    DARC_FILEHEAD hello{};
    DARC_FILEHEAD repeat{};
    // Root directory occupies record zero; inspect the two file records directly. This bypasses
    // the facade and vendor GetEntryPlan, so its physical-size assertion cannot validate itself.
    std::memcpy(&hello, raw.GetFileHeadTable() + sizeof(DARC_FILEHEAD), sizeof(hello));
    std::memcpy(&repeat, raw.GetFileHeadTable() + 2 * sizeof(DARC_FILEHEAD), sizeof(repeat));
    CHECK(hello.HuffPressDataSize == 252);
    CHECK(repeat.HuffPressDataSize == 251);
    CHECK(hello.DataSize == 30);
    CHECK(repeat.DataSize == 131072);
    CHECK(hello.DataAddress == 0);
    CHECK(repeat.DataAddress == 252);
}

TEST_CASE("system single-byte conversion keeps scalars and refuses unsupported or incomplete pages",
          "[wolfrpg][recovery]")
{
    using unpackrat::wolfrpg::text::platform::single_byte_to_scalar;
    CHECK(single_byte_to_scalar(1251, 0xc0) == 0x410U);
    CHECK(single_byte_to_scalar(1252, 0x80) == 0x20acU);
    CHECK(single_byte_to_scalar(1251, 0) == 0U);
    CHECK(single_byte_to_scalar(1251, 'A') == 0x41U);
    CHECK_FALSE(single_byte_to_scalar(0xffffffffU, 'A').has_value());
    CHECK_FALSE(single_byte_to_scalar(932, 0x81).has_value());
}

TEST_CASE("legacy Windows-1251 names decode through the project platform boundary", "[wolfrpg][recovery]")
{
    namespace text = unpackrat::wolfrpg::text;
    REQUIRE(text::is_supported_codepage(1251));
    CHECK(text::encoding_unit_size(1251) == 1);
    const std::array bytes{std::byte{0xc0}, std::byte{0xa8}, std::byte{0x41}};
    CHECK(text::convert(bytes, 1251) == "\xd0\x90\xd0\x81"
                                        "A");
}

TEST_CASE("game context resolution preserves paths and classifies real filesystem outcomes", "[wolfrpg][recovery]")
{
    using namespace unpackrat::wolfrpg;
    const auto fixtures = std::filesystem::path(__FILE__).parent_path().parent_path() / "fixtures/wolfrpg";
    const auto root = fixtures / "recovery-context";
    const auto resolved = resolve_from_archive(root / "Data/test.wolf");
    REQUIRE(resolved.status == game_context_status::resolved);
    CHECK(resolved.context.executable.lexically_normal() == (root / "Game.exe").lexically_normal());
    CHECK(resolved.context.data_folder.lexically_normal() == (root / "Data").lexically_normal());
    CHECK(resolved.context.companion.lexically_normal() == (root / "Data/Game.wolf").lexically_normal());
    CHECK_FALSE(resolved.context.data_in_base_folder);
    const auto direct = resolve_from_executable(root / "Game.exe");
    CHECK(direct.status == game_context_status::resolved);
    CHECK(direct.context.executable == resolved.context.executable);
    const auto missing = resolve_from_archive(root / "absent/test.wolf");
    CHECK(missing.status == game_context_status::missing_context);
    CHECK(missing.context.executable.empty());
    const auto companion = resolve_from_executable(fixtures / "recovery-context-missing/Game.exe");
    CHECK(companion.status == game_context_status::missing_companion);
    CHECK_FALSE(companion.context.data_folder.empty());
    CHECK(companion.context.companion.empty());
    CHECK(resolve_from_archive(root / std::string(300, 'x') / "test.wolf").status ==
          game_context_status::missing_context);
    CHECK(resolve_from_executable(root / std::string(300, 'x') / "Game.exe").status ==
          game_context_status::missing_context);
}

TEST_CASE("game metadata source reads enforce collector budgets before allocating image storage", "[wolfrpg][recovery]")
{
    using namespace unpackrat::wolfrpg;
    const auto root = std::filesystem::path(__FILE__).parent_path().parent_path() / "fixtures/wolfrpg/recovery-game";
    wolfx_candidates candidates;
    candidates.add_key("retained");
    game_metadata_limits input;
    input.collector.max_input_bytes = 1;
    require_error<metadata_error>([&] { collect_game_metadata(root, input, candidates); },
                                  metadata_error_code::input_too_large);
    game_metadata_limits total;
    total.collector.max_total_read_bytes = 47;
    require_error<metadata_error>([&] { collect_game_metadata(root, total, candidates); },
                                  metadata_error_code::read_budget_exceeded);
    REQUIRE(candidates.keys().size() == 1);
    CHECK(candidates.keys()[0] == "retained");
}

TEST_CASE("explicit Pro1000 key opens without game context or companion", "[recovery-core-review]")
{
    using namespace unpackrat::wolfrpg;
    auto bytes = test::support::classic_dx8_payload();
    // Pro1000 uses the classic body kernel; only the header dispatch tag changes.
    bytes[46] = std::byte{0xe8};
    bytes[47] = std::byte{0x03};
    const auto material = crypto::default_classic_password();
    const std::string password(reinterpret_cast<const char *>(material.data()), material.size());
    const auto path = std::filesystem::path(__FILE__).parent_path() / "absent-pro/owned.wolf";
    std::istringstream source(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()),
                              std::ios::binary);
    std::unique_ptr<unpackrat::wolfrpg::archive> reader;
    REQUIRE_NOTHROW(reader = unpackrat::wolfrpg::archive::open(source, path, password));
    REQUIRE(reader->entries().size() == 2);
    CHECK(reader->entries()[0].path == "hello.txt");
    std::ostringstream out;
    reader->extract(0, source, out, [](std::int64_t) {});
    CHECK(out.str() == "Owned WolfRPG fixture payload\n");
}

TEST_CASE("explicit CC2 binary blob opens its selected ChaCha kernel without companion", "[recovery-core-review]")
{
    const auto &fixture = test::support::modern_archive_fixtures().back();
    const auto fixtures = std::filesystem::path(__FILE__).parent_path().parent_path() / "fixtures/wolfrpg";
    auto bytes = test::support::read_modern_fixture(fixtures / fixture.file_name);
    bytes[46] = std::byte{0xc8};
    bytes[47] = std::byte{0};
    REQUIRE(fixture.key.size() >= 44);
    std::array<char, 64> blob{};
    for (std::size_t i = 0; i < 32; ++i)
        blob[i] = static_cast<char>(fixture.key[i]);
    for (std::size_t i = 0; i < 12; ++i)
        blob[34 + i] = static_cast<char>(fixture.key[32 + i]);
    const std::string password(blob.data(), blob.size());
    std::istringstream source(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()),
                              std::ios::binary);
    std::unique_ptr<unpackrat::wolfrpg::archive> reader;
    REQUIRE_NOTHROW(reader = unpackrat::wolfrpg::archive::open(source, fixtures / "absent-cc2/owned.wolf", password));
    REQUIRE(reader->entries().size() == 2);
    std::ostringstream output;
    reader->extract(0, source, output, [](std::int64_t) {});
    CHECK(output.str() == "Owned WolfRPG fixture payload\n");
}

unpackrat::wolfrpg::game_context_result recovery_context_fault(const std::filesystem::path &path);

TEST_CASE("Pro recovery preserves context IO errors after an absent or rejected explicit key", "[recovery-core-review]")
{
    auto bytes = test::support::classic_dx8_payload();
    bytes[46] = std::byte{0xe8};
    bytes[47] = std::byte{0x03};
    const std::string image(reinterpret_cast<const char *>(bytes.data()), bytes.size());
    std::istringstream no_key(image, std::ios::binary);
    bool no_key_failed = false;
    try {
        unpackrat::wolfrpg::archive::open(no_key, "owned.wolf", {}, recovery_context_fault);
    } catch (const unpackrat::wolfrpg::read_error &) {
        no_key_failed = true;
    }
    CHECK(no_key_failed);
    std::istringstream wrong_key(image, std::ios::binary);
    bool wrong_key_failed = false;
    try {
        unpackrat::wolfrpg::archive::open(wrong_key, "owned.wolf", "rejected key", recovery_context_fault);
    } catch (const unpackrat::wolfrpg::read_error &) {
        wrong_key_failed = true;
    }
    CHECK(wrong_key_failed);
}
