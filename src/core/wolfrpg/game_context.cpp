#include "game_context_vendor_adapter.h"

// Thin adapter over the pinned UberWolf context-resolution API (UberWolfLib/GameContext.hpp).
//
// The vendor object returns tString (wchar_t) paths and its own status enum; this translation unit
// and its private implementation header are the only places both vocabularies meet. The conversion is a straight
// std::filesystem::path round trip through wstring, so a non-ASCII path survives unchanged, and the vendor status is
// mapped one-to-one onto this project's game_context_status. The vendor never writes to the
// filesystem here, so the adapter adds no side effects of its own.
namespace unpackrat::wolfrpg
{
    namespace
    {
        [[nodiscard]] game_context_status translate(const wolf::game::ContextStatus status)
        {
            switch (status) {
            case wolf::game::ContextStatus::Resolved:
                return game_context_status::resolved;
            case wolf::game::ContextStatus::MissingCompanion:
                return game_context_status::missing_companion;
            case wolf::game::ContextStatus::IoFailure:
                return game_context_status::io_failure;
            default:
                // MissingContext, and any value the pinned enum does not name, share one outcome.
                return game_context_status::missing_context;
            }
        }

    } // namespace

    game_context_result detail::convert_vendor_context(const wolf::game::Context &context)
    {
        game_context_result result;
        result.status = translate(context.status);
        result.context.executable = std::filesystem::path(context.executablePath);
        result.context.game_folder = std::filesystem::path(context.gameFolder);
        result.context.data_folder = std::filesystem::path(context.dataFolder);
        result.context.companion = std::filesystem::path(context.companionFile);
        result.context.data_in_base_folder = context.dataInBaseFolder;
        return result;
    }

    game_context_result resolve_from_archive(const std::filesystem::path &archive_path)
    {
        return detail::convert_vendor_context(wolf::game::ResolveFromArchive(archive_path.wstring()));
    }

    game_context_result resolve_from_executable(const std::filesystem::path &executable_path)
    {
        return detail::convert_vendor_context(wolf::game::ResolveFromExecutable(executable_path.wstring()));
    }
} // namespace unpackrat::wolfrpg
