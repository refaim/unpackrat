#ifndef UNPACKRAT_CORE_WOLFRPG_GAME_CONTEXT_H
#define UNPACKRAT_CORE_WOLFRPG_GAME_CONTEXT_H

#include <filesystem>

// Game/data/companion localization for a Wolf RPG game, as an operation with no side effects.
//
// This is a thin adapter over the pinned UberWolf ``wolf::game`` resolution API, which owns the
// original discovery policy: the archive's parent folder (stepping up one level when that folder
// is named exactly "Data") for Game.exe then GamePro.exe, the first data.<extension> beside the
// executable before an existing "Data" directory for the data root, and the first existing special
// file in that root for the DxArc companion. The vendor header, its tString paths and its status
// enum stay confined to the implementation translation unit; callers see only std::filesystem
// paths, this project's status enum and an owned result value. Nothing here extracts, writes a
// file or configuration, starts a process or changes the working directory.
namespace unpackrat::wolfrpg
{
    // Why a Wolf RPG context was or was not resolved.
    enum class game_context_status
    {
        resolved,          // the executable, its data root and the companion were all located
        missing_context,   // no game executable, or no data root beside it
        missing_companion, // the data root was located but holds no pinned companion file
        io_failure,        // a filesystem probe failed instead of reporting a plain absence
    };

    // Owned localization result. For any status before resolved the located paths are still the
    // best answer the probe produced: missing_companion keeps the executable, game folder and data
    // root, and every field before the failing step is filled in.
    struct game_context
    {
        std::filesystem::path executable;  // Game.exe or GamePro.exe
        std::filesystem::path game_folder; // the executable's parent folder
        std::filesystem::path data_folder; // the data root (the game folder or its "Data" child)
        std::filesystem::path companion;   // the first existing pinned companion file
        bool data_in_base_folder = false;  // true when the data root is the game folder itself
    };

    struct game_context_result
    {
        game_context_status status = game_context_status::missing_context;
        game_context context;
    };

    // Resolves the context of the game that owns the archive at ``archive_path``.
    [[nodiscard]] game_context_result resolve_from_archive(const std::filesystem::path &archive_path);

    // Resolves the context of a known game executable path. A path without a parent folder is made
    // absolute against the current directory, matching the vendor's original behaviour.
    [[nodiscard]] game_context_result resolve_from_executable(const std::filesystem::path &executable_path);
} // namespace unpackrat::wolfrpg
#endif
