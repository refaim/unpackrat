#ifndef UNPACKRAT_MODULES_RENPY_RPA1_INDEX_H
#define UNPACKRAT_MODULES_RENPY_RPA1_INDEX_H

#include "../../core/archive_limits.h"

#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <vector>

namespace unpackrat::renpy
{
    struct rpa1_fragment final
    {
        std::int64_t offset;
        std::int64_t size;
    };

    struct rpa1_entry final
    {
        std::string path;
        std::vector<rpa1_fragment> fragments;
        std::int64_t size = 0;
    };

    struct rpa1_limits final
    {
        std::size_t max_compressed_index_bytes = std::size_t{64} * 1024 * 1024;
        std::size_t max_decompressed_index_bytes = std::size_t{64} * 1024 * 1024;
        std::size_t max_entries = archive_limits::max_entry_count;
        std::size_t max_fragments = 1'000'000;
        std::size_t max_path_bytes = archive_limits::max_path_bytes;
    };

    // RPA1 stores its zlib-compressed Pickle index separately from the headerless data file.
    // Each path maps to an ordered, nonempty list of (offset, size) fragments. The caller
    // supplies the data file's authoritative size; all returned extents fit that file.
    [[nodiscard]] std::vector<rpa1_entry> parse_rpa1_index(std::span<const std::byte> compressed_index,
                                                           std::int64_t archive_size, const rpa1_limits &limits = {});
} // namespace unpackrat::renpy

#endif
