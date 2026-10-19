#ifndef UNPACKRAT_MODULES_ZANZARAH_PAK_H
#define UNPACKRAT_MODULES_ZANZARAH_PAK_H

#include "../../core/archive_limits.h"
#include "../../core/io/bounded_stream.h"
#include "../extractor.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <istream>
#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

// The Zanzarah PAK index reader and the whole-archive framing policy live here as a
// platform-neutral core: a plain ``std::istream`` goes in and an ``extractor::file`` list
// comes out, with no Observer or Win32 dependency. Keeping the parser separate lets it be
// unit-tested without loading the module binary; ``zanzarah.cpp`` is the only production
// caller.

namespace zanzarah
{
    namespace detail
    {
        [[nodiscard]] inline std::int32_t read_positive_or_zero_int32(unpackrat::io::bounded_stream &stream)
        {
            const auto value = stream.read_trivial<std::int32_t>();

            if (value < 0) {
                throw extractor::read_error();
            }

            return value;
        }

        [[nodiscard]] inline std::int32_t read_positive_int32(unpackrat::io::bounded_stream &stream)
        {
            const std::int32_t value = read_positive_or_zero_int32(stream);
            if (value == 0) {
                throw extractor::read_error();
            }
            return value;
        }

        // The canonical framing predicate probes a four-byte prefix before every block body and
        // a four-byte suffix at its end. Probing each marker with its own absolute seek costs four
        // file operations per entry, and reading a large read-ahead window at every marker address
        // is worse: for widely spaced or shuffled blocks almost every probe would pull a full
        // window. The caller instead hands the probes over in ascending physical order and this
        // reader serves them from aligned page-sized windows: each refill reads at most 512 bytes
        // and nearby markers can share cached data. Only the probed DWORD is ever interpreted.
        class marker_reader final
        {
          public:
            static constexpr std::size_t window_bytes = 512;

            explicit marker_reader(unpackrat::io::bounded_stream &input) : input_(input)
            {
            }

            // ``position`` must be non-decreasing across one reader's lifetime; the caller sorts
            // the probes so the page window only ever moves forward.
            [[nodiscard]] std::uint32_t read_dword(const std::streamoff position)
            {
                if (position + static_cast<std::streamoff>(sizeof(std::uint32_t)) >
                    base_ + static_cast<std::streamoff>(filled_)) {
                    refill(position);
                }
                std::uint32_t value = 0;
                std::memcpy(&value, buffer_.data() + (position - base_), sizeof(value));
                return value;
            }

          private:
            [[nodiscard]] std::size_t page_length(const std::streamoff base) const
            {
                return static_cast<std::size_t>(
                    std::min<std::streamoff>(static_cast<std::streamoff>(window_bytes), input_.size() - base));
            }

            void refill(const std::streamoff position)
            {
                base_ = position - (position % static_cast<std::streamoff>(window_bytes));
                filled_ = page_length(base_);
                if (position + static_cast<std::streamoff>(sizeof(std::uint32_t)) >
                    base_ + static_cast<std::streamoff>(filled_)) {
                    // The DWORD straddles the page end, so take the short read-ahead window before
                    // the next page rather than fetching a second full page.
                    base_ = position;
                    filled_ = page_length(base_);
                }
                input_.seek_absolute(base_);
                input_.read_exact(buffer_.data(), filled_);
            }

            unpackrat::io::bounded_stream &input_;
            std::array<char, window_bytes> buffer_{};
            std::streamoff base_ = 0;
            std::size_t filled_ = 0;
        };
    } // namespace detail

    // The canonical framing predicate probes a four-byte prefix before the block body and a
    // four-byte suffix at its end. When every block carries both markers the framing is
    // canonical and the trailing suffix is removed from the exposed extents; archives which
    // do not match fall back to their prefix-only (zanzapak) extents. The format notes
    // describe the coincidental suffix ambiguity that this fallback accepts.
    [[nodiscard]] inline std::vector<std::unique_ptr<extractor::file>> parse_pak(std::istream &stream)
    {
        constexpr std::int64_t signature_size = 4;
        unpackrat::io::bounded_stream input(stream);
        input.seek_absolute(static_cast<std::streamoff>(signature_size));
        const auto archive_size = input.size();
        const auto file_count = static_cast<std::size_t>(detail::read_positive_int32(input));
        constexpr std::size_t minimum_entry_metadata_bytes = sizeof(std::int32_t) * 3 + 1;
        const auto remaining_bytes = static_cast<std::uint64_t>(input.remaining());
        if (file_count > unpackrat::archive_limits::max_entry_count ||
            file_count > remaining_bytes / minimum_entry_metadata_bytes) {
            throw extractor::read_error();
        }

        auto files = std::vector<std::unique_ptr<extractor::file>>();
        files.reserve(file_count);

        std::string path;
        for (std::size_t i = 0; i < file_count; ++i) {
            const auto path_len = static_cast<std::size_t>(detail::read_positive_int32(input));
            if (path_len > unpackrat::archive_limits::max_path_bytes ||
                path_len > static_cast<std::uint64_t>(input.remaining())) {
                throw extractor::read_error();
            }
            path.resize(path_len);
            input.read_exact(path.data(), path.size());

            const auto block_offset = detail::read_positive_or_zero_int32(input);
            const auto block_size = detail::read_positive_int32(input);

            constexpr int32_t attr_size = 4;
            if (block_size < attr_size) {
                throw extractor::read_error();
            }

            auto new_file = std::make_unique<extractor::file>();
            new_file->path = path;
            new_file->offset = static_cast<std::int64_t>(block_offset) + attr_size;
            new_file->compressed_body_size_in_bytes = static_cast<std::int64_t>(block_size) - attr_size;
            new_file->uncompressed_body_size_in_bytes = new_file->compressed_body_size_in_bytes;

            files.push_back(std::move(new_file));
        }

        const auto body_position = input.position();
        const auto body_bytes = static_cast<std::int64_t>(archive_size - body_position);
        for (const auto &file : files) {
            if (file->offset > body_bytes || file->compressed_body_size_in_bytes > body_bytes - file->offset) {
                throw extractor::read_error();
            }
            file->offset += body_position;
            if (constexpr std::string_view relative_prefix = "..\\"; file->path.starts_with(relative_prefix)) {
                file->path.erase(0, relative_prefix.size());
            }
        }

        constexpr std::int64_t framing_marker_size = 4;
        constexpr std::uint32_t framing_prefix_marker = 0x101U;
        constexpr std::uint32_t framing_suffix_marker = 0x202U;

        // Canonical framing requires every block to carry both markers. Each probe is recorded
        // and then checked in ascending physical order so the page-sized reads stay sequential,
        // while the whole-archive predicate still inspects every entry's prefix and suffix.
        std::vector<std::pair<std::int64_t, std::uint32_t>> probes;
        probes.reserve(files.size() * 2);
        bool canonical_framing = true;
        for (const auto &entry : files) {
            if (entry->compressed_body_size_in_bytes < framing_marker_size) {
                canonical_framing = false;
                break;
            }
            probes.emplace_back(entry->offset - framing_marker_size, framing_prefix_marker);
            probes.emplace_back(entry->offset + entry->compressed_body_size_in_bytes - framing_marker_size,
                                framing_suffix_marker);
        }

        if (canonical_framing) {
            std::ranges::sort(probes);
            detail::marker_reader markers(input);
            for (const auto &[position, expected] : probes) {
                if (markers.read_dword(position) != expected) {
                    canonical_framing = false;
                    break;
                }
            }
        }

        if (canonical_framing) {
            for (const auto &entry : files) {
                entry->compressed_body_size_in_bytes -= framing_marker_size;
                entry->uncompressed_body_size_in_bytes -= framing_marker_size;
            }
        }

        return files;
    }
} // namespace zanzarah
#endif
