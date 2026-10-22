#include "rpa1_index.h"
#include "../../core/compression/zlib_codec.h"
#include "pickle.h"

#include <limits>
#include <stdexcept>
#include <string_view>
#include <utility>

namespace unpackrat::renpy
{
    namespace
    {
        pickle::value_ptr load_index(const std::span<const std::byte> data)
        {
            try {
                return pickle::loads(data);
            } catch (const std::invalid_argument &) {
                throw std::runtime_error("RPA1 index contains an invalid integer");
            } catch (const std::out_of_range &) {
                throw std::runtime_error("RPA1 index contains an integer outside int64");
            }
        }

        void validate_path(const std::string_view path, const std::size_t max_path_bytes)
        {
            if (path.empty()) {
                throw std::runtime_error("RPA1 entry path is empty");
            }
            if (path.size() > max_path_bytes) {
                throw std::runtime_error("RPA1 entry path exceeds the path budget");
            }
            if (path.find_first_of(std::string_view("\0:", 2)) != std::string_view::npos) {
                throw std::runtime_error("RPA1 entry path contains a forbidden character");
            }
            if (path.find_first_of("/\\") == 0) {
                throw std::runtime_error("RPA1 entry path is absolute");
            }

            std::size_t begin = 0;
            for (;;) {
                const auto end = path.find_first_of("/\\", begin);
                const auto segment = path.substr(begin, end == std::string_view::npos ? end : end - begin);
                if (segment == "..") {
                    throw std::runtime_error("RPA1 entry path contains parent traversal");
                }
                if (end == std::string_view::npos) {
                    return;
                }
                begin = end + 1;
            }
        }
    } // namespace

    std::vector<rpa1_entry> parse_rpa1_index(const std::span<const std::byte> compressed_index,
                                             const std::int64_t archive_size, const rpa1_limits &limits)
    {
        if (archive_size < 0) {
            throw std::runtime_error("RPA1 data file size is negative");
        }
        if (compressed_index.size() > limits.max_compressed_index_bytes) {
            throw std::runtime_error("RPA1 compressed index exceeds the metadata budget");
        }
        const auto decompressed = compression::decompress_zlib(compressed_index, limits.max_decompressed_index_bytes);
        const auto root = load_index(decompressed);
        const auto &dict = root->as_dict();
        if (dict.size() > limits.max_entries) {
            throw std::runtime_error("RPA1 index exceeds the entry budget");
        }

        std::vector<rpa1_entry> result;
        result.reserve(dict.size());
        std::size_t fragment_count = 0;
        for (const auto &[path, value] : dict) {
            validate_path(path, limits.max_path_bytes);
            const auto &fragments = value->as_list();
            if (fragments.empty()) {
                throw std::runtime_error("RPA1 entry has no fragments");
            }
            if (fragments.size() > limits.max_fragments - fragment_count) {
                throw std::runtime_error("RPA1 index exceeds the fragment budget");
            }
            fragment_count += fragments.size();

            rpa1_entry entry{path, {}, 0};
            entry.fragments.reserve(fragments.size());
            for (const auto &fragment : fragments) {
                const auto &tuple = fragment->as_tuple();
                if (tuple.size() != 2) {
                    throw std::runtime_error("RPA1 fragment must contain exactly two integers");
                }
                const auto offset = tuple[0]->as_int64();
                const auto size = tuple[1]->as_int64();
                if (offset < 0 || size < 0) {
                    throw std::runtime_error("RPA1 fragment has a negative extent");
                }
                if (offset > archive_size || size > archive_size - offset) {
                    throw std::runtime_error("RPA1 fragment exceeds the data file");
                }
                if (size > std::numeric_limits<std::int64_t>::max() - entry.size) {
                    throw std::runtime_error("RPA1 entry size overflows int64");
                }
                entry.size += size;
                entry.fragments.push_back({offset, size});
            }
            result.push_back(std::move(entry));
        }
        return result;
    }
} // namespace unpackrat::renpy
