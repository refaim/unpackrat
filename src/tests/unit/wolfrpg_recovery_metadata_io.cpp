#include "../../core/wolfrpg/wolfx_game_metadata.h"
#include <catch2/catch_test_macros.hpp>
#include <fstream>
#include <functional>
#include <memory>
#include <optional>
namespace
{
    template <typename Error> std::optional<Error> observed_error(const std::function<void()> &operation)
    {
        try {
            operation();
        } catch (const Error &error) {
            return error;
        }
        return std::nullopt;
    }
    std::filesystem::path fixture_root()
    {
        return std::filesystem::path(__FILE__).parent_path().parent_path() / "fixtures/wolfrpg/recovery-game";
    }
    std::unique_ptr<std::istream> open_owned_source(const std::filesystem::path &path)
    {
        return std::make_unique<std::ifstream>(
            path, static_cast<std::ios_base::openmode>(static_cast<unsigned>(std::ios::binary) |
                                                       static_cast<unsigned>(std::ios::ate)));
    }
} // namespace
TEST_CASE("metadata reader borrows a source opener and commits valid files", "[metadata-io]")
{
    unpackrat::wolfrpg::wolfx_candidates candidates;
    candidates.add_key("retained");
    CHECK_NOTHROW(unpackrat::wolfrpg::collect_game_metadata(fixture_root, {}, candidates, open_owned_source));
    REQUIRE(candidates.keys().size() == 1);
    CHECK(candidates.keys()[0] == "retained");
}
namespace
{
    std::unique_ptr<std::istream> absent_source(const std::filesystem::path &)
    {
        return {};
    }
} // namespace
TEST_CASE("metadata reader rejects an absent source without committing candidates", "[metadata-io]")
{
    unpackrat::wolfrpg::wolfx_candidates candidates;
    candidates.add_key("retained");
    const auto error = observed_error<unpackrat::wolfrpg::game_metadata_error>(
        [&] { unpackrat::wolfrpg::collect_game_metadata(fixture_root, {}, candidates, absent_source); });
    REQUIRE(error.has_value());
    if (!error.has_value()) {
        return;
    }
    CHECK(error.value().code() == unpackrat::wolfrpg::game_metadata_error_code::source_read_failed);
    REQUIRE(candidates.keys().size() == 1);
    CHECK(candidates.keys()[0] == "retained");
}
#include <algorithm>
#include <array>
#include <exception>
#include <iterator>
#include <sstream>

namespace
{
    enum class operation : std::uint8_t
    {
        none,
        size,
        rewind,
        read
    };
    struct failure_plan
    {
        operation at = operation::none;
        std::exception_ptr exception;
        bool report_wrong_landing = false;
        int read_calls = 0;
    };
    // Each test installs the complete owned reader plan before invoking the public opener.
    thread_local failure_plan planned_failure;
    class fault_buffer final : public std::stringbuf
    {
        bool permits(const operation call)
        {
            if (call != planned_failure.at)
                return true;
            if (planned_failure.exception)
                std::rethrow_exception(planned_failure.exception);
            return false;
        }
        pos_type seekoff(off_type offset, std::ios_base::seekdir way, std::ios_base::openmode mode) override
        {
            return permits(operation::size) ? std::stringbuf::seekoff(offset, way, mode) : pos_type(off_type(-1));
        }
        pos_type seekpos(pos_type offset, std::ios_base::openmode mode) override
        {
            const auto actual =
                permits(operation::rewind) ? std::stringbuf::seekpos(offset, mode) : pos_type(off_type(-1));
            return planned_failure.report_wrong_landing ? pos_type(off_type(1)) : actual;
        }
        std::streamsize xsgetn(char *data, std::streamsize count) override
        {
            ++planned_failure.read_calls;
            return std::stringbuf::xsgetn(data, permits(operation::read) ? count : std::min<std::streamsize>(count, 1));
        }

      public:
        explicit fault_buffer(std::string bytes) : std::stringbuf(std::move(bytes), std::ios::in)
        {
            std::stringbuf::seekoff(0, std::ios::end, std::ios::in);
        }
    };
    class owned_fault_stream final : public std::istream
    {
        fault_buffer buffer_;

      public:
        explicit owned_fault_stream(std::string bytes) : std::istream(nullptr), buffer_(std::move(bytes))
        {
            rdbuf(&buffer_);
        }
    };
    std::unique_ptr<std::istream> open_fault_source(const std::filesystem::path &path)
    {
        std::ifstream file(path, std::ios::binary);
        std::string bytes(std::istreambuf_iterator<char>(file), {});
        return std::make_unique<owned_fault_stream>(std::move(bytes));
    }
    unpackrat::wolfrpg::wolfx_candidates seeded_candidates()
    {
        unpackrat::wolfrpg::wolfx_candidates result;
        result.add_key("retained");
        result.add_string_value(7, "stable");
        result.add_int_value(9, 42);
        return result;
    }
    void check_retained(const unpackrat::wolfrpg::wolfx_candidates &candidates)
    {
        REQUIRE(candidates.keys().size() == 1);
        CHECK(candidates.keys()[0] == "retained");
        REQUIRE(candidates.string_values().size() == 1);
        CHECK(candidates.string_values()[0].id == 7);
        CHECK(candidates.string_values()[0].value == "stable");
        REQUIRE(candidates.int_values().size() == 1);
        CHECK(candidates.int_values()[0].id == 9);
        CHECK(candidates.int_values()[0].value == 42);
    }
} // namespace

namespace
{
    struct identity_bad_alloc final : std::bad_alloc
    {
        const void *identity;
        explicit identity_bad_alloc(const void *token) : identity(token)
        {
        }
    };
    struct identity_length_error final : std::length_error
    {
        const void *identity;
        explicit identity_length_error(const void *token)
            : std::length_error("owned metadata length payload"), identity(token)
        {
        }
    };
    template <typename Error> void check_resource_identity(const Error &expected)
    {
        for (const auto at : {operation::none, operation::size, operation::rewind, operation::read}) {
            planned_failure = {at, std::make_exception_ptr(expected)};
            auto candidates = seeded_candidates();
            const auto error = observed_error<Error>(
                [&] { unpackrat::wolfrpg::collect_game_metadata(fixture_root, {}, candidates, open_fault_source); });
            if (at == operation::none) {
                CHECK_FALSE(error.has_value());
            } else {
                REQUIRE(error.has_value());
                if (!error.has_value()) {
                    return;
                }
                CHECK(error.value().identity == expected.identity);
                CHECK(std::string_view(error.value().what()) == expected.what());
            }
            check_retained(candidates);
        }
    }
} // namespace
TEST_CASE("metadata reader preserves resource exception identity at size rewind and read",
          "[metadata-io][metadata-resource]")
{
    const int allocation_token = 1, length_token = 2;
    check_resource_identity(identity_bad_alloc(&allocation_token));
    check_resource_identity(identity_length_error(&length_token));
}
TEST_CASE("metadata reader classifies ordinary seek and short reads without partial candidate commit", "[metadata-io]")
{
    for (const auto at : {operation::size, operation::rewind, operation::read}) {
        planned_failure = {at, {}};
        auto candidates = seeded_candidates();
        const auto error = observed_error<unpackrat::wolfrpg::game_metadata_error>(
            [&] { unpackrat::wolfrpg::collect_game_metadata(fixture_root, {}, candidates, open_fault_source); });
        REQUIRE(error.has_value());
        if (!error.has_value()) {
            return;
        }
        CHECK(error.value().code() == unpackrat::wolfrpg::game_metadata_error_code::source_read_failed);
        check_retained(candidates);
    }
}

TEST_CASE("metadata reader accepts a complete owned stream and retains existing candidate values", "[metadata-io]")
{
    planned_failure = {};
    auto candidates = seeded_candidates();
    CHECK_NOTHROW(unpackrat::wolfrpg::collect_game_metadata(fixture_root, {}, candidates, open_fault_source));
    check_retained(candidates);
    const auto error = observed_error<unpackrat::wolfrpg::game_metadata_error>(
        [&] { unpackrat::wolfrpg::collect_game_metadata(fixture_root, {}, candidates); });
    CHECK_FALSE(error.has_value());
    check_retained(candidates);
    const auto collector_error = observed_error<unpackrat::wolfrpg::metadata_error>(
        [&] { unpackrat::wolfrpg::collect_game_metadata(fixture_root, {}, candidates, open_owned_source); });
    CHECK_FALSE(collector_error.has_value());
    check_retained(candidates);
}
namespace
{
    std::unique_ptr<std::istream> no_buffer_source(const std::filesystem::path &)
    {
        return std::make_unique<std::istream>(nullptr);
    }
} // namespace
TEST_CASE("metadata reader reports an owned stream without a buffer as a source failure", "[metadata-io]")
{
    auto candidates = seeded_candidates();
    const auto error = observed_error<unpackrat::wolfrpg::game_metadata_error>(
        [&] { unpackrat::wolfrpg::collect_game_metadata(fixture_root, {}, candidates, no_buffer_source); });
    REQUIRE(error.has_value());
    if (!error.has_value()) {
        return;
    }
    CHECK(error.value().code() == unpackrat::wolfrpg::game_metadata_error_code::source_read_failed);
    check_retained(candidates);
}
TEST_CASE("metadata reader enforces source and cumulative collector budgets before allocation", "[metadata-io]")
{
    using namespace unpackrat::wolfrpg;
    auto candidates = seeded_candidates();
    game_metadata_limits source;
    source.max_source_bytes = 1;
    const auto source_error =
        observed_error<game_metadata_error>([&] { collect_game_metadata(fixture_root, source, candidates); });
    REQUIRE(source_error.has_value());
    if (!source_error.has_value()) {
        return;
    }
    CHECK(source_error.value().code() == game_metadata_error_code::source_too_large);
    check_retained(candidates);
    game_metadata_limits input;
    input.collector.max_input_bytes = 1;
    const auto input_error =
        observed_error<metadata_error>([&] { collect_game_metadata(fixture_root, input, candidates); });
    REQUIRE(input_error.has_value());
    if (!input_error.has_value()) {
        return;
    }
    CHECK(input_error.value().code() == metadata_error_code::input_too_large);
    check_retained(candidates);
    game_metadata_limits cumulative;
    cumulative.collector.max_total_read_bytes = 50;
    const auto budget_error =
        observed_error<metadata_error>([&] { collect_game_metadata(fixture_root, cumulative, candidates); });
    REQUIRE(budget_error.has_value());
    if (!budget_error.has_value()) {
        return;
    }
    CHECK(budget_error.value().code() == metadata_error_code::read_budget_exceeded);
    check_retained(candidates);
    const auto discovery_error =
        observed_error<game_metadata_error>([&] { collect_game_metadata(fixture_root / "absent", {}, candidates); });
    REQUIRE(discovery_error.has_value());
    if (!discovery_error.has_value()) {
        return;
    }
    CHECK(discovery_error.value().code() == game_metadata_error_code::root_missing);
    check_retained(candidates);
}
TEST_CASE("metadata reader rejects a positive wrong rewind result before reading valid bytes",
          "[metadata-io][metadata-landing]")
{
    planned_failure = {operation::none, {}, true};
    auto candidates = seeded_candidates();
    const auto error = observed_error<unpackrat::wolfrpg::game_metadata_error>(
        [&] { unpackrat::wolfrpg::collect_game_metadata(fixture_root, {}, candidates, open_fault_source); });
    check_retained(candidates);
    CHECK(planned_failure.read_calls == 0);
    REQUIRE(error.has_value());
    if (!error.has_value()) {
        return;
    }
    CHECK(error.value().code() == unpackrat::wolfrpg::game_metadata_error_code::source_read_failed);
}