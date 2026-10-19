// Public archive facade failure contracts; all byte sources and serialized inputs are owned.
#include "../../core/wolfrpg/archive.h"
#include "../../core/wolfrpg/archive_source.h"
#include "../../core/wolfrpg/crypto.h"
#include "../../core/wolfrpg/legacy_archive.h"
#include "../../core/wolfrpg/modern_crypto.h"
#include "../support/wolfrpg_default_mode_fixtures.h"
#include "../support/wolfrpg_fixtures.h"
#include "../support/wolfrpg_modern_archive_fixtures.h"
#include "../support/wolfrpg_pro_fixtures.h"
#include <array>
#include <catch2/catch_test_macros.hpp>
#include <cstring>
#include <fstream>
#include <functional>
#include <limits>
#include <sstream>
#include <tuple>
#include <uberwolf/DXLib/DXArchive.h>
#include <uberwolf/DXLib/DXArchiveVer5.h>
#include <uberwolf/WolfX/Stream.hpp>
#include <utility>

namespace
{
    namespace wolf = unpackrat::wolfrpg;
    struct caller_fault
    {
    };
    void no_action()
    {
    }
    std::istringstream input(const std::vector<std::byte> &bytes)
    {
        return std::istringstream(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()),
                                  std::ios::binary);
    }
    template <class Error, class Operation> void expect_error(const Operation &operation)
    {
        bool caught = false;
        try {
            operation();
        } catch (const Error &) {
            caught = true;
        }
        REQUIRE(caught);
    }
    class observed_source final : public std::stringbuf
    {
      public:
        explicit observed_source(const std::vector<std::byte> &bytes)
            : std::stringbuf(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()), std::ios::in)
        {
        }
        std::function<void()> before_read = no_action;
        std::function<void(std::ios::seekdir)> before_seek = [](std::ios::seekdir) {};
        std::streamsize read_limit = INT64_MAX;
        std::size_t reads = 0;
        std::size_t size_queries = 0;
        std::size_t refused_size_query = SIZE_MAX;
        std::size_t truncated_size_query = SIZE_MAX;
        bool misplace_position = false;

      protected:
        std::streamsize xsgetn(char *out, std::streamsize size) override
        {
            ++reads;
            before_read();
            if (size_queries >= truncated_size_query)
                return 0;
            return std::stringbuf::xsgetn(out, std::min(size, read_limit));
        }
        pos_type seekoff(off_type offset, std::ios::seekdir dir, std::ios::openmode mode) override
        {
            before_seek(dir);
            if (dir == std::ios::end) {
                ++size_queries;
                if (size_queries == refused_size_query)
                    return pos_type(off_type(-1));
                if (size_queries >= truncated_size_query)
                    return pos_type(off_type(0));
            }
            if (dir == std::ios::beg && misplace_position)
                return pos_type(offset + 1);
            return std::stringbuf::seekoff(offset, dir, mode);
        }
    };
    constexpr std::array<void (*)(), 3> faults{
        [] { throw caller_fault{}; }, // NOLINT(bugprone-std-exception-baseclass): intentional non-std caller fault
        [] { throw std::bad_alloc{}; }, [] { throw std::length_error("owned source extent"); }};
    void expect_source_fault(std::size_t kind, const std::function<void()> &operation)
    {
        const std::array<std::function<void()>, 3> checks{[&] { expect_error<wolf::read_error>(operation); },
                                                          [&] { expect_error<std::bad_alloc>(operation); },
                                                          [&] { expect_error<std::length_error>(operation); }};
        checks.at(kind)();
    }
    std::filesystem::path fixtures()
    {
        return std::filesystem::path(__FILE__).parent_path().parent_path() / "fixtures/wolfrpg";
    }
    // Fixed DX5 image: a valid ten-byte compressed extent with a dangling escape token.
    // Header advertises one output byte, so construction succeeds and decoding must fail.
    std::vector<std::byte> compressed_legacy(const std::byte token)
    {
        const std::array<std::byte, 10> body{std::byte{1}, std::byte{0}, std::byte{0}, std::byte{0},    std::byte{10},
                                             std::byte{0}, std::byte{0}, std::byte{0}, std::byte{0xff}, token};
        DARC_HEAD_VER5 header{};
        header.Head = 0x5844;
        header.Version = 5;
        header.HeadSize = 12 + sizeof(DARC_FILEHEAD_VER5) + sizeof(DARC_DIRECTORY_VER5);
        header.DataStartAddress = sizeof(header);
        constexpr std::size_t body_extent = std::tuple_size_v<decltype(body)>;
        constexpr std::size_t names_offset = sizeof(header) + body_extent;
        static_assert(names_offset <= std::numeric_limits<decltype(header.FileNameTableStartAddress)>::max());
        header.FileNameTableStartAddress = static_cast<decltype(header.FileNameTableStartAddress)>(names_offset);
        header.FileTableStartAddress = 12;
        header.DirectoryTableStartAddress = 12 + sizeof(DARC_FILEHEAD_VER5);
        std::vector<std::byte> bytes(header.FileNameTableStartAddress + header.HeadSize);
        std::memcpy(bytes.data(), &header, sizeof(header));
        std::memcpy(bytes.data() + sizeof(header), body.data(), body.size());
        const auto meta = header.FileNameTableStartAddress;
        bytes[meta] = std::byte{1};
        bytes[meta + 2] = std::byte{'b'};
        bytes[meta + 4] = std::byte{'B'};
        bytes[meta + 8] = std::byte{'b'};
        DARC_FILEHEAD_VER5 file{};
        file.DataSize = 1;
        file.PressDataSize = 10;
        std::memcpy(bytes.data() + meta + 12, &file, sizeof(file));
        const DARC_DIRECTORY_VER5 directory{UINT32_MAX, UINT32_MAX, 1, 0};
        std::memcpy(bytes.data() + meta + 12 + sizeof(file), &directory, sizeof(directory));
        std::array<unsigned char, 12> key{};
        DXArchive_VER5::KeyCreate("secret", key.data());
        DXArchive_VER5::KeyConv(bytes.data(), sizeof(header), 0, key.data());
        DXArchive_VER5::KeyConv(bytes.data() + meta, static_cast<int>(header.HeadSize), 0, key.data());
        DXArchive_VER5::KeyConv(bytes.data() + sizeof(header), 10, 1, key.data());
        return bytes;
    }
} // namespace

TEST_CASE("archive prefix and body source failures preserve resource identity", "[recovery-archive-contract]")
{
    const auto bytes = test::support::classic_dx8_payload();
    for (std::size_t kind = 0; kind != faults.size(); ++kind) {
        for (std::size_t failure_read : {std::size_t{1}, std::size_t{2}, std::size_t{3}}) {
            observed_source buffer(bytes);
            buffer.before_read = [&] {
                if (buffer.reads == failure_read)
                    faults[kind]();
            };
            std::istream source(&buffer);
            expect_source_fault(kind, [&] { wolf::archive::open(source, "owned.wolf"); });
            REQUIRE(buffer.reads == failure_read);
        }
        auto original = input(bytes);
        auto reader = wolf::archive::open(original, "owned.wolf");
        observed_source buffer(bytes);
        buffer.before_read = faults[kind];
        std::istream source(&buffer);
        std::ostringstream output;
        std::int64_t progress = 0;
        const auto observe = [&](std::int64_t n) { progress += n; };
        reader->extract(0, original, output, observe);
        REQUIRE(progress == 30);
        output.str({});
        progress = 0;
        expect_source_fault(kind, [&] { reader->extract(0, source, output, observe); });
        REQUIRE(output.str().empty());
        REQUIRE(progress == 0);
        REQUIRE(buffer.reads == 1);
    }
}

TEST_CASE("archive probes refuse short positive reads and keep the borrowed source reusable",
          "[recovery-archive-contract]")
{
    const auto bytes = test::support::classic_dx8_payload();
    for (std::size_t failed_read : {std::size_t{1}, std::size_t{2}}) {
        observed_source buffer(bytes);
        buffer.before_read = [&] {
            if (buffer.reads == failed_read)
                buffer.read_limit = 1;
        };
        std::istream source(&buffer);
        expect_error<wolf::read_error>([&] { wolf::archive::open(source, "owned.wolf"); });
        REQUIRE(buffer.reads == failed_read);
        buffer.before_read = no_action;
        buffer.read_limit = INT64_MAX;
        auto reader = wolf::archive::open(source, "owned.wolf");
        REQUIRE(reader->entries().size() == 2);
    }
}

TEST_CASE("Pro companion recovery distinguishes absence inaccessible and unusable material",
          "[recovery-archive-contract]")
{
    auto bytes = test::support::classic_dx8_payload();
    bytes[46] = std::byte{0xe8};
    bytes[47] = std::byte{3};
    std::size_t resolutions = 0;
    wolf::game_context_result context;
    const wolf::game_context_resolver resolver = [&](const std::filesystem::path &path) {
        ++resolutions;
        REQUIRE(path == "owned.wolf");
        return context;
    };
    auto source = input(bytes);
    expect_error<wolf::key_required>([&] { wolf::archive::open(source, "owned.wolf", {}, resolver); });
    REQUIRE(resolutions == 1);
    context.status = wolf::game_context_status::resolved;
    context.context.companion = fixtures() / "absent-companion.wolf";
    expect_error<wolf::read_error>([&] { wolf::archive::open(source, "owned.wolf", {}, resolver); });
    REQUIRE(resolutions == 2);
    context.context.companion = fixtures() / "recovery-context/Data/Game.wolf";
    expect_error<wolf::key_required>([&] { wolf::archive::open(source, "owned.wolf", {}, resolver); });
    REQUIRE(resolutions == 3);
    const auto runtime = std::filesystem::path(__FILE__).parent_path().parent_path().parent_path().parent_path() /
                         "out/recovery-archive-contract/runtime";
    std::filesystem::create_directories(runtime);
    const auto companion = test::support::wolfrpg_pro::v1_companion();
    context.context.companion = runtime / "Game.wolf";
    {
        std::ofstream file(context.context.companion, std::ios::binary);
        file.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::badbit) |
                                                            static_cast<unsigned>(std::ios::failbit)));
        file.write(reinterpret_cast<const char *>(companion.data()), static_cast<std::streamsize>(companion.size()));
    }
    expect_error<wolf::key_required>([&] { wolf::archive::open(source, "owned.wolf", {}, resolver); });
    REQUIRE(resolutions == 4);
    // A keyless owned container still follows recovery dispatch and publishes only validated entries.
    const std::array entries{test::support::synthetic_entry{"owned.txt", "owned", false}};
    auto plain = test::support::build_plain_archive(entries);
    plain.bytes[46] = std::byte{0xe8};
    plain.bytes[47] = std::byte{3};
    auto keyless = input(plain.bytes);
    auto opened = wolf::archive::open(keyless, "owned.wolf", {}, resolver);
    REQUIRE(resolutions == 5);
    REQUIRE(opened->entries().size() == 1);
    std::ostringstream output;
    opened->extract(0, keyless, output, [](std::int64_t) {});
    REQUIRE(output.str() == "owned");
}

TEST_CASE("archive legacy delegation and moved-from extraction retain public bounds", "[recovery-archive-contract]")
{
    auto source = input(test::support::wolfrpg_default_mode::load_bytes("default-mode-0.wolf"));
    auto reader = wolf::archive::open(source, "owned.wolf");
    REQUIRE(reader->version() == 5);
    REQUIRE(reader->backend() == wolf::container_backend::dx);
    std::ostringstream output;
    std::int64_t progress = 0;
    const auto observe = [&](std::int64_t n) { progress += n; };
    reader->extract(0, source, output, observe);
    REQUIRE(progress == reader->entries()[0].unpacked_size);
    REQUIRE(output.str().size() == static_cast<std::size_t>(progress));
    wolf::archive moved = std::move(*reader);
    REQUIRE(reader->backend() == wolf::container_backend::dx);
    REQUIRE(reader->version() == 0);
    REQUIRE(reader->entries().empty());
    expect_error<std::out_of_range>([&] { reader->extract(0, source, output, observe); });
    expect_error<std::out_of_range>([&] { moved.extract(moved.entries().size(), source, output, observe); });
}

TEST_CASE("legacy corrupt compressed tokens fail during extraction without publishing bytes",
          "[recovery-archive-contract]")
{
    auto source = input(compressed_legacy(std::byte{0xff}));
    auto reader = wolf::legacy_archive::open(source, "owned.wolf", "secret");
    REQUIRE(reader->entries().size() == 1);
    REQUIRE(reader->entries()[0].unpacked_size == 1);
    std::ostringstream output;
    std::int64_t progress = 0;
    const auto observe = [&](std::int64_t n) { progress += n; };
    auto valid = input(compressed_legacy(std::byte{'A'}));
    auto control = wolf::legacy_archive::open(valid, "owned.wolf", "secret");
    control->extract(0, valid, output, observe);
    REQUIRE(output.str() == "A");
    REQUIRE(progress == 1);
    output.str({});
    progress = 0;
    expect_error<wolf::read_error>([&] { reader->extract(0, source, output, observe); });
    REQUIRE(output.str().empty());
    REQUIRE(progress == 0);
}

TEST_CASE("archive size and positioning faults preserve caller resource failures", "[recovery-archive-contract]")
{
    const auto bytes = test::support::classic_dx8_payload();
    for (std::size_t kind = 0; kind != faults.size(); ++kind)
        for (const auto failed_direction : {std::ios::end, std::ios::beg}) {
            observed_source buffer(bytes);
            buffer.before_seek = [&](std::ios::seekdir direction) {
                if (direction == failed_direction)
                    faults[kind]();
            };
            std::istream source(&buffer);
            expect_source_fault(kind, [&] { wolf::archive::open(source, "owned.wolf"); });
            REQUIRE(buffer.reads == 0);
        }
}

TEST_CASE("modern source-size refusals cannot turn into password retries", "[recovery-archive-contract]")
{
    for (const auto &fixture : test::support::modern_archive_fixtures()) {
        const auto bytes = test::support::read_modern_fixture(fixtures() / fixture.file_name);
        observed_source buffer(bytes);
        buffer.refused_size_query = 2; // Initial facade size succeeds; kernel source size refuses.
        std::istream source(&buffer);
        expect_error<wolf::read_error>([&] { wolf::archive::open(source, "owned.wolf"); });
        REQUIRE(buffer.size_queries == 2);
        REQUIRE(buffer.reads <= 3); // Bounded header work, with no retry after the refused size.
    }
}
TEST_CASE("a truncated ChaCha container fails metadata open with its valid builtin key", "[recovery-archive-contract]")
{
    const auto &fixture = test::support::modern_archive_fixtures().back();
    auto bytes = test::support::read_modern_fixture(fixtures() / fixture.file_name);
    bytes.resize(64);
    auto source = input(bytes);
    expect_error<wolf::read_error>([&] { wolf::archive::open(source, "owned.wolf"); });
}
TEST_CASE("CC2 companion recovery requires a full binary key and never accepts unrelated material",
          "[recovery-archive-contract]")
{
    auto bytes = test::support::classic_dx8_payload();
    bytes[46] = std::byte{0xc8};
    bytes[47] = std::byte{0};
    auto source = input(bytes);
    wolf::game_context_result context;
    context.status = wolf::game_context_status::resolved;
    context.context.companion =
        std::filesystem::path(__FILE__).parent_path().parent_path().parent_path().parent_path() /
        "out/recovery-archive-contract/runtime/CC2.wolf";
    const auto companion = test::support::wolfrpg_pro::cc2_companion();
    std::filesystem::create_directories(context.context.companion.parent_path());
    {
        std::ofstream file(context.context.companion, std::ios::binary);
        file.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::badbit) |
                                                            static_cast<unsigned>(std::ios::failbit)));
        file.write(reinterpret_cast<const char *>(companion.data()), static_cast<std::streamsize>(companion.size()));
    }
    std::size_t resolutions = 0;
    const wolf::game_context_resolver resolver = [&](const std::filesystem::path &) {
        ++resolutions;
        return context;
    };
    expect_error<wolf::key_required>([&] { wolf::archive::open(source, "owned.wolf", "short", resolver); });
    REQUIRE(resolutions == 1);
}

namespace
{
    // A controlled single-file stream built with the pinned upstream public generator primitives.
    // Sentinel variable IDs mean the key alone suffices; checksum samples remain part of the payload.
    struct wolfx_input
    {
        std::vector<std::byte> bytes;
        std::string payload;
    };
    wolfx_input keyed_wolfx()
    {
        const std::string key = "archive-owned-key";
        const wolfx::WolfXKeyData key_data(key.begin(), key.end());
        const auto static_blob = wolfx::detail::generator::generateWolfxStaticBlob(key_data);
        constexpr std::size_t size = 4096;
        const std::size_t start = 512U + static_blob[0] + static_blob[1];
        std::array<std::uint8_t, 20> header{0x57, 0x4f, 0x4c, 0x46, 0x58, 0x11, 0x22, 0x33, 0x44, 0x99};
        const auto seed =
            wolfx::detail::generator::fnv1(static_blob) ^ wolfx::detail::utils::combineBytes<4u>(header, 5);
        auto blob = wolfx::detail::generator::generateWolfxDecryptBlob(seed, static_blob, size);
        constexpr std::array<std::uint8_t, 2> string_id{0x4e, 0x20};               // 20000: no string candidate.
        const auto integer_id = wolfx::detail::utils::extractBytes<3u>(0xfffff0U); // No integer candidate.
        for (std::size_t i = 0; i != 2; ++i)
            header[10 + i] = static_cast<std::uint8_t>(string_id[i] ^ blob[i]);
        const auto empty_hash = wolfx::detail::generator::fnv1(std::string{});
        const auto tail = wolfx::detail::utils::extractBytes<3u>(0xfffff0U ^ ((empty_hash & 0xffff0000U) >> 8u) ^
                                                                 (empty_hash & 0xffffU));
        for (std::size_t i = 0; i != 3; ++i)
            header[12 + i] = static_cast<std::uint8_t>(tail[i] ^ blob[2 + i]);
        for (std::size_t i = 0; i != 5; ++i)
            header[15 + i] = static_cast<std::uint8_t>(0xd0 + i);
        for (std::size_t i = 0; i != blob.size(); ++i)
            blob[i] ^= static_cast<std::uint8_t>(string_id[i % 2] ^ integer_id[i % 3]);
        std::vector<std::uint8_t> plain(size, static_cast<std::uint8_t>('q'));
        for (std::size_t i = 0; i != 5; ++i) {
            const auto at =
                start + static_cast<std::size_t>(static_cast<double>(size - start - 1) * 0.25 * static_cast<double>(i));
            plain[at] = static_cast<std::uint8_t>(header[15 + i] ^ blob[5 + i]);
        }
        wolfx_input result;
        result.payload.assign(reinterpret_cast<const char *>(plain.data() + start), size - start);
        result.bytes.resize(size);
        for (std::size_t i = 0; i != 15; ++i)
            result.bytes[i] = static_cast<std::byte>(header[i]);
        for (std::size_t i = 15; i != size; ++i)
            result.bytes[i] = static_cast<std::byte>(plain[i] ^ blob[(i - 10) % blob.size()]);
        for (std::size_t i = 0; i != 5; ++i)
            result.bytes[15 + i] = static_cast<std::byte>(header[15 + i]);
        return result;
    }
} // namespace
TEST_CASE("WolfX facade keeps its family and uses context metadata only after key rejection",
          "[recovery-archive-contract]")
{
    const auto fixture = keyed_wolfx();
    auto source = input(fixture.bytes);
    std::size_t resolutions = 0;
    wolf::game_context_result context;
    const wolf::game_context_resolver resolver = [&](const std::filesystem::path &) {
        ++resolutions;
        return context;
    };
    auto reader = wolf::archive::open(source, "owned.wolfx", "archive-owned-key", resolver);
    REQUIRE(resolutions == 0);
    REQUIRE(reader->version() == 1);
    REQUIRE(reader->backend() == wolf::container_backend::wolfx);
    REQUIRE(reader->entries().size() == 1);
    REQUIRE(reader->entries()[0].unpacked_size == static_cast<std::int64_t>(fixture.payload.size()));
    std::ostringstream output;
    std::int64_t progress = 0;
    reader->extract(0, source, output, [&](std::int64_t n) { progress += n; });
    REQUIRE(output.str() == fixture.payload);
    REQUIRE(progress == static_cast<std::int64_t>(fixture.payload.size()));
    expect_error<wolf::key_required>([&] { wolf::archive::open(source, "owned.wolfx", {}, resolver); });
    REQUIRE(resolutions == 1);
    context.status = wolf::game_context_status::missing_companion;
    context.context.data_folder = fixtures() / "recovery-game";
    expect_error<wolf::key_required>([&] { wolf::archive::open(source, "owned.wolfx", {}, resolver); });
    REQUIRE(resolutions == 2);
}

TEST_CASE("archive extraction rejects short reads and positive wrong seek positions", "[recovery-archive-contract]")
{
    const auto bytes = test::support::classic_dx8_payload();
    auto original = input(bytes);
    auto reader = wolf::archive::open(original, "owned.wolf");
    std::ostringstream output;
    std::int64_t progress = 0;
    const auto observe = [&](std::int64_t n) { progress += n; };
    observed_source misplaced(bytes);
    misplaced.misplace_position = true;
    std::istream mispositioned(&misplaced);
    expect_error<wolf::read_error>([&] { wolf::archive::open(mispositioned, "owned.wolf"); });
    REQUIRE(misplaced.reads == 0);
    expect_error<wolf::read_error>([&] { reader->extract(0, mispositioned, output, observe); });
    REQUIRE(misplaced.reads == 0);
    REQUIRE(output.str().empty());
    REQUIRE(progress == 0);
    observed_source short_buffer(bytes);
    short_buffer.read_limit = 1;
    std::istream short_source(&short_buffer);
    expect_error<wolf::read_error>([&] { reader->extract(0, short_source, output, observe); });
    REQUIRE(short_buffer.reads == 1);
    REQUIRE(output.str().empty());
    REQUIRE(progress == 0);
    misplaced.misplace_position = false;
    reader->extract(0, mispositioned, output, observe);
    REQUIRE(output.str() == "Owned WolfRPG fixture payload\n");
    REQUIRE(progress == 30);
}

TEST_CASE("unknown exact crypt revisions fail without invoking companion recovery", "[recovery-archive-contract]")
{
    auto bytes = test::support::classic_dx8_payload();
    bytes[46] = std::byte{0xe7};
    bytes[47] = std::byte{3}; // Revision 999 is absent from the frozen mode table.
    auto source = input(bytes);
    expect_error<wolf::read_error>(
        [&] { wolf::archive::open(source, "owned.wolf", {}, wolf::game_context_resolver{}); });
}

TEST_CASE("archive source requests honor empty and negative policies before consulting the source",
          "[recovery-archive-contract]")
{
    const std::vector<std::byte> bytes{std::byte{'a'}, std::byte{'b'}, std::byte{'c'}, std::byte{'d'}};
    struct request
    {
        std::int64_t offset;
        std::size_t length;
        bool accepted;
        std::size_t reads;
        std::size_t seeks;
        std::array<std::uint8_t, 3> expected;
    };
    const std::array requests{
        request{-1, 0, true, 0, 0, {0xa5, 0xa5, 0xa5}}, request{0, 0, true, 0, 0, {0xa5, 0xa5, 0xa5}},
        request{-1, 3, false, 0, 0, {0xa5, 0xa5, 0xa5}}, request{1, 3, true, 1, 1, {'b', 'c', 'd'}}};
    for (const auto &test : requests) {
        observed_source buffer(bytes);
        std::size_t seeks = 0;
        buffer.before_seek = [&](std::ios::seekdir) { ++seeks; };
        std::istream source(&buffer);
        std::array<std::uint8_t, 3> destination{0xa5, 0xa5, 0xa5};
        REQUIRE(wolf::detail::source_read_at(source, test.offset, std::span(destination).first(test.length), nullptr) ==
                test.accepted);
        REQUIRE(destination == test.expected);
        REQUIRE(buffer.reads == test.reads);
        REQUIRE(seeks == test.seeks);
        REQUIRE(buffer.size_queries == 0);
    }
}
TEST_CASE("archive modern transform rejects logical-position overflow before mutating the range",
          "[recovery-archive-contract]")
{
    const auto &fixture = test::support::modern_archive_fixtures().front();
    const auto bytes = test::support::read_modern_fixture(fixtures() / fixture.file_name);
    REQUIRE(bytes.size() >= 64);
    std::array<std::uint8_t, 64> header{};
    std::memcpy(header.data(), bytes.data(), header.size());
    const std::string_view password(reinterpret_cast<const char *>(fixture.key.data()), fixture.key.size());
    const auto context = wolf::modern_crypto::create(header, bytes.size(), password);
    const std::array<std::uint8_t, 4> original{0x61, 0x62, 0x63, 0x64};
    auto expected = original;
    context.apply_inner(expected, 5);
    auto actual = original;
    wolf::detail::transform_modern_at(context, actual, 2, 3);
    REQUIRE(actual == expected);
    const auto max = std::numeric_limits<std::uint64_t>::max();
    for (const auto position : {std::pair{max, std::uint64_t{1}}, std::pair{max - 1, std::uint64_t{2}}}) {
        actual = original;
        expect_error<wolf::read_error>(
            [&] { wolf::detail::transform_modern_at(context, actual, position.first, position.second); });
        REQUIRE(actual == original);
    }
}

TEST_CASE("archive planning observes the source after metadata has been validated", "[recovery-archive-contract]")
{
    const std::array entries{test::support::synthetic_entry{"owned.txt", "owned", false}};
    const auto built = test::support::build_plain_archive(entries);
    observed_source buffer(built.bytes);
    std::istream source(&buffer);
    auto reader = wolf::archive::open(source, "owned.wolf");
    CAPTURE(buffer.size_queries, buffer.reads);
    REQUIRE(reader->entries().size() == 1);
    std::ostringstream output;
    reader->extract(0, source, output, [](std::int64_t) {});
    REQUIRE(output.str() == "owned");
}

TEST_CASE("CC2 recovery refuses a valid V1 companion before reading encrypted members", "[recovery-archive-contract]")
{
    const auto &fixture = test::support::modern_archive_fixtures().back();
    auto bytes = test::support::read_modern_fixture(fixtures() / fixture.file_name);
    bytes[46] = std::byte{0xc8};
    bytes[47] = std::byte{0};
    wolf::game_context_result context;
    context.status = wolf::game_context_status::resolved;
    context.context.companion =
        std::filesystem::path(__FILE__).parent_path().parent_path().parent_path().parent_path() /
        "out/recovery-archive-contract/runtime/CC2-with-V1.wolf";
    std::filesystem::create_directories(context.context.companion.parent_path());
    const auto companion = test::support::wolfrpg_pro::v1_companion();
    {
        std::ofstream file(context.context.companion, std::ios::binary);
        file.exceptions(static_cast<std::ios_base::iostate>(static_cast<unsigned>(std::ios::badbit) |
                                                            static_cast<unsigned>(std::ios::failbit)));
        file.write(reinterpret_cast<const char *>(companion.data()), static_cast<std::streamsize>(companion.size()));
    }
    std::size_t resolutions = 0;
    const wolf::game_context_resolver resolver = [&](const std::filesystem::path &) {
        ++resolutions;
        return context;
    };
    observed_source buffer(bytes);
    std::istream source(&buffer);
    std::unique_ptr<wolf::archive> reader;
    expect_error<wolf::key_required>([&] { reader = wolf::archive::open(source, "owned.wolf", {}, resolver); });
    REQUIRE_FALSE(reader);
    REQUIRE(resolutions == 1);
    REQUIRE(buffer.reads == 2);
    REQUIRE(buffer.size_queries == 1);
}
TEST_CASE("archive refuses unsafe decoded components before publishing partial entries", "[recovery-archive-contract]")
{
    const std::array unsafe{std::string("."),   std::string(".."),   std::string("x:y"),
                            std::string("x/y"), std::string("x\\y"), std::string("x") + char(0x1f) + "y"};
    for (const auto &name : unsafe) {
        const std::array entries{test::support::synthetic_entry{"retained.txt", "first", false},
                                 test::support::synthetic_entry{name, "second", false}};
        const auto built = test::support::build_plain_archive(entries);
        observed_source buffer(built.bytes);
        std::istream source(&buffer);
        std::unique_ptr<wolf::archive> reader;
        expect_error<wolf::read_error>([&] { reader = wolf::archive::open(source, "owned.wolf"); });
        REQUIRE_FALSE(reader);
        REQUIRE(buffer.size_queries == 3); // Only the valid first entry reaches planning.
        REQUIRE(buffer.reads == 4);        // Header and metadata only; member bodies remain untouched.
    }
    const std::array entries{test::support::synthetic_entry{"\x95\x5c.dat", "owned", false}};
    auto source = input(test::support::build_plain_archive(entries).bytes);
    auto reader = wolf::archive::open(source, "owned.wolf");
    REQUIRE(reader->entries().size() == 1);
    REQUIRE(reader->entries()[0].path == "\xe8\xa1\xa8.dat");
    std::ostringstream output;
    reader->extract(0, source, output, [](std::int64_t) {});
    REQUIRE(output.str() == "owned");
}
TEST_CASE("archive planning detects a newly refused or truncated source and preserves borrowing",
          "[recovery-archive-contract]")
{
    const std::array entries{test::support::synthetic_entry{"owned.txt", "owned", false}};
    const auto built = test::support::build_plain_archive(entries);
    for (const bool shrink : {false, true}) {
        observed_source buffer(built.bytes);
        if (shrink)
            buffer.truncated_size_query = 3;
        else
            buffer.refused_size_query = 3;
        std::istream source(&buffer);
        std::unique_ptr<wolf::archive> reader;
        expect_error<wolf::read_error>([&] { reader = wolf::archive::open(source, "owned.wolf"); });
        REQUIRE_FALSE(reader);
        REQUIRE(buffer.size_queries == 3);
        REQUIRE(buffer.reads == 4);
        if (shrink) {
            source.seekg(0);
            char sentinel = 'x';
            source.read(&sentinel, 1);
            REQUIRE(source.gcount() == 0);
            REQUIRE(sentinel == 'x');
            source.clear();
        }
        buffer.truncated_size_query = SIZE_MAX;
        buffer.refused_size_query = SIZE_MAX;
        buffer.size_queries = 0;
        buffer.reads = 0;
        reader = wolf::archive::open(source, "owned.wolf");
        REQUIRE(reader->entries().size() == 1);
        std::ostringstream output;
        std::int64_t progress = 0;
        reader->extract(0, source, output, [&](std::int64_t n) { progress += n; });
        REQUIRE(output.str() == "owned");
        REQUIRE(progress == 5);
    }
}

TEST_CASE("Pro dispatch changes from classic to modern at version 1010", "[recovery-archive-contract]")
{
    const auto password = wolf::crypto::default_classic_password();
    const std::string_view key(reinterpret_cast<const char *>(password.data()), password.size());
    std::size_t resolutions = 0;
    const wolf::game_context_resolver missing = [&](const std::filesystem::path &path) {
        ++resolutions;
        REQUIRE(path == "owned.wolf");
        return wolf::game_context_result{};
    };
    auto bytes = test::support::classic_dx8_payload();
    REQUIRE(bytes.size() == 966);
    // Only the public crypt-version field changes; the frozen keyed DX8 image stays intact.
    bytes[46] = std::byte{0xf1};
    bytes[47] = std::byte{3};
    auto classic = input(bytes);
    auto reader = wolf::archive::open(classic, "owned.wolf", key, missing);
    REQUIRE(resolutions == 0);
    REQUIRE(reader->entries().size() == 2);
    std::ostringstream output;
    std::int64_t progress = 0;
    reader->extract(0, classic, output, [&](std::int64_t n) { progress += n; });
    REQUIRE(output.str() == "Owned WolfRPG fixture payload\n");
    REQUIRE(progress == 30);
    bytes[46] = std::byte{0xf2};
    observed_source buffer(bytes);
    std::istream modern(&buffer);
    std::unique_ptr<wolf::archive> rejected;
    expect_error<wolf::key_required>([&] { rejected = wolf::archive::open(modern, "owned.wolf", key, missing); });
    REQUIRE_FALSE(rejected);
    REQUIRE(resolutions == 1);
    REQUIRE(buffer.size_queries == 1);
    REQUIRE(buffer.reads <= 4);
}
TEST_CASE("archive translates malformed UTF8 name decoding into a read error", "[recovery-archive-contract]")
{
    const std::array valid_entries{test::support::synthetic_entry{"owned.txt", "owned", false}};
    auto valid = test::support::build_plain_archive(valid_entries);
    constexpr std::uint32_t utf8 = 65001;
    REQUIRE(valid.bytes.size() >= sizeof(DARC_HEAD));
    std::memcpy(valid.bytes.data() + offsetof(DARC_HEAD, CharCodeFormat), &utf8, sizeof(utf8));
    auto control = input(valid.bytes);
    auto opened = wolf::archive::open(control, "owned.wolf");
    REQUIRE(opened->entries().size() == 1);
    REQUIRE(opened->entries()[0].path == "owned.txt");
    std::ostringstream output;
    opened->extract(0, control, output, [](std::int64_t) {});
    REQUIRE(output.str() == "owned");
    const std::array invalid_entries{test::support::synthetic_entry{"retained.txt", "first", false},
                                     test::support::synthetic_entry{"\xc3(", "second", false}};
    auto invalid = test::support::build_plain_archive(invalid_entries);
    REQUIRE(invalid.bytes.size() < 1024u);
    std::memcpy(invalid.bytes.data() + offsetof(DARC_HEAD, CharCodeFormat), &utf8, sizeof(utf8));
    observed_source buffer(invalid.bytes);
    std::istream source(&buffer);
    std::unique_ptr<wolf::archive> rejected;
    expect_error<wolf::read_error>([&] { rejected = wolf::archive::open(source, "owned.wolf"); });
    REQUIRE_FALSE(rejected);
    REQUIRE(buffer.size_queries == 3); // The valid first entry reaches planning, the malformed one cannot.
    REQUIRE(buffer.reads == 4);        // Only header and metadata; neither member body is read.
}

TEST_CASE("official WOLF RPG Editor 3.396 archive extracts the owned binary payload",
          "[official-3396][recovery-archive-contract]")
{
    const auto directory = fixtures() / "official-3396";
    const auto path = directory / "OwnedFixture.wolf";
    const auto expected = test::support::read_modern_fixture(directory / "payload.bin");
    REQUIRE(expected.size() == 256);
    for (std::size_t index = 0; index < expected.size(); ++index) {
        REQUIRE(expected[index] == static_cast<std::byte>(index));
    }
    std::ifstream source(path, std::ios::binary);
    REQUIRE(source.is_open());
    const auto reader = wolf::archive::open(source, path);
    REQUIRE(reader);
    REQUIRE(reader->entries().size() == 1);
    CHECK(reader->entries()[0].path == "payload.bin");
    CHECK(reader->entries()[0].unpacked_size == 256);
    std::ostringstream output;
    std::int64_t progress = 0;
    reader->extract(0, source, output, [&progress](const std::int64_t count) { progress += count; });
    CHECK(output.str() == std::string(reinterpret_cast<const char *>(expected.data()), expected.size()));
    CHECK(progress == 256);
}
