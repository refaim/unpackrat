#include "wolfx_game_metadata.h"

#include <algorithm>
#include <array>
#include <fstream>
#include <limits>
#include <uberwolf/WolfRPG/WolfRPGSourceDiscovery.hpp>
#include <utility>
#include <vector>

namespace unpackrat::wolfrpg
{
    namespace
    {
        using discovery_error = wolfRPGSourceDiscovery::ErrorKind;
        // Order follows the pinned discovery enum; external error values remain in this unit.
        constexpr std::array discovery_errors{
            game_metadata_error_code::root_missing,           game_metadata_error_code::root_not_directory,
            game_metadata_error_code::root_io_failure,        game_metadata_error_code::traversal_io_failure,
            game_metadata_error_code::depth_exceeded,         game_metadata_error_code::entry_budget_exceeded,
            game_metadata_error_code::source_budget_exceeded, game_metadata_error_code::path_byte_budget_exceeded,
            game_metadata_error_code::common_event_missing,   game_metadata_error_code::common_event_refused,
        };
        static_assert(static_cast<std::size_t>(discovery_error::CommonEventRefused) + 1 == discovery_errors.size());

        void collect_source(const std::filesystem::path &path, const metadata_kind kind,
                            const game_metadata_limits &limits, metadata_collector &collector, wolfx_candidates &staged,
                            game_metadata_source_opener &opener)
        {
            auto source = opener(path);
            if (source == nullptr || source->rdbuf() == nullptr) {
                throw game_metadata_error(game_metadata_error_code::source_read_failed,
                                          "WolfX metadata source could not be opened");
            }
            auto &buffer = *source->rdbuf();
            const std::streamoff length = buffer.pubseekoff(0, std::ios::end, std::ios::in);
            if (length < 0) {
                throw game_metadata_error(game_metadata_error_code::source_read_failed,
                                          "WolfX metadata source could not be opened");
            }
            const auto size = static_cast<std::uintmax_t>(length);
            const auto max_read_size =
                std::min({static_cast<std::uintmax_t>(limits.max_source_bytes),
                          static_cast<std::uintmax_t>(std::numeric_limits<std::size_t>::max()),
                          static_cast<std::uintmax_t>(std::numeric_limits<std::streamsize>::max())});
            if (size > max_read_size) {
                throw game_metadata_error(game_metadata_error_code::source_too_large,
                                          "WolfX metadata source exceeds source limit");
            }
            if (size > limits.collector.max_input_bytes) {
                throw metadata_error(metadata_error_code::input_too_large,
                                     "WolfX metadata source exceeds collector input limit");
            }
            if (size > limits.collector.max_total_read_bytes - collector.total_read_bytes()) {
                throw metadata_error(metadata_error_code::read_budget_exceeded,
                                     "WolfX metadata sources exceed read budget");
            }
            std::vector<std::byte> image(static_cast<std::size_t>(size));
            if (buffer.pubseekpos(0, std::ios::in) != std::streampos(0)) {
                throw game_metadata_error(game_metadata_error_code::source_read_failed,
                                          "WolfX metadata source could not be rewound");
            }
            const auto requested = static_cast<std::streamsize>(size);
            if (buffer.sgetn(reinterpret_cast<char *>(image.data()), requested) != requested) {
                throw game_metadata_error(game_metadata_error_code::source_read_failed,
                                          "WolfX metadata source could not be read");
            }
            collector.collect(kind, image, staged);
        }
    } // namespace

    void collect_game_metadata(const std::filesystem::path &data_folder, const game_metadata_limits &limits,
                               wolfx_candidates &out, game_metadata_source_opener &opener)
    {
        wolfx_candidates staged = out;
        metadata_collector collector(limits.collector);
        wolfRPGSourceDiscovery::Limits discovery_limits;
        discovery_limits.maxDepth = limits.max_depth;
        discovery_limits.maxVisitedEntries = limits.max_visited_entries;
        discovery_limits.maxSources = limits.max_sources;
        discovery_limits.maxTotalPathBytes = limits.max_total_path_bytes;
        wolfRPGSourceDiscovery::SourceDiscovery discovery(discovery_limits);
        try {
            discovery.VisitAll(
                data_folder,
                [&](const auto &path) { collect_source(path, metadata_kind::map, limits, collector, staged, opener); },
                [&](const auto &path) {
                    collect_source(path, metadata_kind::common_events, limits, collector, staged, opener);
                });
        } catch (const wolfRPGSourceDiscovery::SourceDiscoveryException &error) {
            throw game_metadata_error(discovery_errors.at(static_cast<std::size_t>(error.Kind())), error.what());
        }
        out = std::move(staged);
    }
    namespace
    {
        std::unique_ptr<std::istream> open_metadata_source(const std::filesystem::path &path)
        {
            return std::make_unique<std::ifstream>(path, std::ios::binary | std::ios::ate);
        }
    } // namespace
    void collect_game_metadata(const std::filesystem::path &data_folder, const game_metadata_limits &limits,
                               wolfx_candidates &out)
    {
        collect_game_metadata(data_folder, limits, out, open_metadata_source);
    }
} // namespace unpackrat::wolfrpg
