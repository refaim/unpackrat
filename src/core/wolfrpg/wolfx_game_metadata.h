#ifndef UNPACKRAT_CORE_WOLFRPG_WOLFX_GAME_METADATA_H
#define UNPACKRAT_CORE_WOLFRPG_WOLFX_GAME_METADATA_H

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <istream>
#include <memory>
#include <stdexcept>
#include <string>

#include "wolfx_candidates.h"
#include "wolfx_metadata.h"

// Read-only game-folder metadata driver. It is the missing link between a known WolfRPG data folder
// and the existing per-image metadata_collector: it discovers the same sources the original
// WolfRPG(dataFolder, true) constructor selects (every exact lowercase *.mps regular file under the
// folder, recursively, then BasicData/CommonEvent.dat), reads one bounded image at a time and merges
// every recovered decryption candidate into the caller's wolfx_candidates value.
//
// This layer is deliberately thin and read-only. It never writes to the game folder, never runs the
// whole WolfRPG constructor, and never searches upward for a game root: the caller supplies a data
// folder it already resolved (for example through resolve_from_archive). The vendor's discovery
// types and filesystem code stay inside the implementation translation unit; callers see only
// std::filesystem paths, this project's error enum and the existing candidate/collector values.
namespace unpackrat::wolfrpg
{
    // Bounds the discovery step and one image read before any allocation or collector work. The
    // nested collector limits keep the reviewed byte/unit/expanded budgets authoritative; the other
    // fields bound the folder walk and the per-source file read this adapter performs.
    struct game_metadata_limits final
    {
        // Maximum directory depth of a visited entry (0 visits only the data folder's direct
        // children).
        std::size_t max_depth = 64;
        // Maximum number of filesystem entries the discovery may visit, relevant or not.
        std::size_t max_visited_entries = 1000000;
        // Maximum number of selected sources (maps plus the one common-event image).
        std::size_t max_sources = 100000;
        // Maximum total bytes of the source paths handed to the reader.
        std::size_t max_total_path_bytes = std::size_t{64} * 1024 * 1024;
        // Maximum size of one source file; a larger file is rejected before it is read.
        std::size_t max_source_bytes = std::size_t{64} * 1024 * 1024;
        // The reviewed collector budgets, enforced unchanged by the existing metadata_collector.
        metadata_limits collector{};
    };

    // Why a folder could not be turned into candidates. Only the discovery failures and this
    // adapter's own bounded-read failures are translated into this type; a genuine absence stays
    // distinct from a probe IO failure, a recognized malformed image still reports the collector's
    // metadata_error, and std::bad_alloc / std::length_error are never masked.
    enum class game_metadata_error_code : std::uint8_t
    {
        // The data folder does not exist.
        root_missing,
        // The data folder exists but is not a directory.
        root_not_directory,
        // The data folder could not be probed at all (an invalid path or an access failure), which
        // is distinct from a genuine absence.
        root_io_failure,
        // The data folder could not be enumerated.
        traversal_io_failure,
        // The folder walk exceeded game_metadata_limits::max_depth.
        depth_exceeded,
        // The folder walk exceeded game_metadata_limits::max_visited_entries.
        entry_budget_exceeded,
        // More sources were selected than game_metadata_limits::max_sources allows.
        source_budget_exceeded,
        // The selected source paths exceeded game_metadata_limits::max_total_path_bytes.
        path_byte_budget_exceeded,
        // BasicData/CommonEvent.dat is absent.
        common_event_missing,
        // BasicData or BasicData/CommonEvent.dat exists but could not be confirmed as an unlinked
        // real directory / real regular file, so the read is refused instead of following a link.
        common_event_refused,
        // One source file exceeds game_metadata_limits::max_source_bytes.
        source_too_large,
        // One source file could not be read.
        source_read_failed,
    };

    class game_metadata_error final : public std::runtime_error
    {
      public:
        game_metadata_error(game_metadata_error_code code, const std::string &message)
            : std::runtime_error(message), code_(code)
        {
        }

        [[nodiscard]] game_metadata_error_code code() const noexcept
        {
            return code_;
        }

      private:
        game_metadata_error_code code_;
    };

    // Discovers the map and common-event sources under ``data_folder`` and merges every recovered
    // SetString / SetVariable / ProFeature decryption candidate into ``candidates``.
    //
    // The merge is one transaction for the whole folder: candidates are staged and only committed
    // once every source has been read and parsed, so any failure - a discovery failure, an oversized
    // or unreadable source, or a malformed image - leaves ``candidates`` exactly as it was and never
    // publishes a partial result.
    // The opener lends its callable and transfers ownership of each input stream. A null stream
    // is a source read failure. Resource exceptions propagate without translation.
    using game_metadata_source_opener = std::unique_ptr<std::istream>(const std::filesystem::path &);
    void collect_game_metadata(const std::filesystem::path &data_folder, const game_metadata_limits &limits,
                               wolfx_candidates &out, game_metadata_source_opener &opener);

    void collect_game_metadata(const std::filesystem::path &data_folder, const game_metadata_limits &limits,
                               wolfx_candidates &out);
} // namespace unpackrat::wolfrpg
#endif
