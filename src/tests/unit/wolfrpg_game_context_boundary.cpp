// Tests call the same out-of-line adapter conversion used by the public resolvers.
#include "../../core/wolfrpg/game_context_vendor_adapter.h"
#include <array>
#include <catch2/catch_test_macros.hpp>
#include <utility>

unpackrat::wolfrpg::game_context_result recovery_context_fault(const std::filesystem::path &path)
{
    wolf::game::Context context;
    context.status = wolf::game::ContextStatus::IoFailure;
    context.executablePath = path.wstring();
    context.gameFolder = path.parent_path().wstring();
    return unpackrat::wolfrpg::detail::convert_vendor_context(context);
}
TEST_CASE("game context conversion maps real named vendor statuses and Unicode paths", "[recovery-context-boundary]")
{
    using own = unpackrat::wolfrpg::game_context_status;
    using vendor = wolf::game::ContextStatus;
    const std::wstring executable = L"C:/\u0418\u0433\u0440\u0430/Game.exe";
    const std::wstring folder = L"C:/\u0418\u0433\u0440\u0430";
    const std::wstring data = folder + L"/Data";
    struct row
    {
        wolf::game::Context context;
        own expected;
    };
    const std::array rows{
        row{{executable, folder, folder, folder + L"/Game.wolf", true, vendor::Resolved}, own::resolved},
        row{{executable, folder, data, data + L"/Game.wolf", false, vendor::Resolved}, own::resolved},
        row{{}, own::missing_context},
        row{{executable, folder, {}, {}, false, vendor::MissingContext}, own::missing_context},
        row{{executable, folder, folder, {}, true, vendor::MissingCompanion}, own::missing_companion},
        row{{executable, folder, data, {}, false, vendor::MissingCompanion}, own::missing_companion},
        row{{{}, {}, {}, {}, false, vendor::IoFailure}, own::io_failure},
        row{{executable, folder, data, {}, false, vendor::IoFailure}, own::io_failure},
        row{{executable, folder, folder, {}, true, vendor::IoFailure}, own::io_failure}};
    for (const auto &row : rows) {
        const auto &context = row.context;
        const auto result = unpackrat::wolfrpg::detail::convert_vendor_context(context);
        REQUIRE(result.status == row.expected);
        REQUIRE(result.context.executable == std::filesystem::path(context.executablePath));
        REQUIRE(result.context.game_folder == std::filesystem::path(context.gameFolder));
        REQUIRE(result.context.data_folder == std::filesystem::path(context.dataFolder));
        REQUIRE(result.context.companion == std::filesystem::path(context.companionFile));
        REQUIRE(result.context.data_in_base_folder == context.dataInBaseFolder);
    }
}
TEST_CASE("game context conversion retains the partial answer on a provider IO failure", "[recovery-context-boundary]")
{
    const auto path = std::filesystem::path(L"C:/\u0418\u0433\u0440\u0430/owned/Game.exe");
    const auto result = recovery_context_fault(path);
    REQUIRE(result.status == unpackrat::wolfrpg::game_context_status::io_failure);
    REQUIRE(result.context.game_folder == path.parent_path());
    REQUIRE(result.context.executable == path);
    REQUIRE(result.context.data_folder.empty());
    REQUIRE(result.context.companion.empty());
    REQUIRE_FALSE(result.context.data_in_base_folder);
}
