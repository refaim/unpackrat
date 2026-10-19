#include "../../modules/zanzarah/pak.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <format>
#include <ios>
#include <istream>
#include <memory>
#include <streambuf>
#include <string>
#include <string_view>
#include <vector>

#include <catch2/catch_test_macros.hpp>

namespace
{
    constexpr std::uint32_t canonical_prefix = 0x101U;
    constexpr std::uint32_t canonical_suffix = 0x202U;
    constexpr std::uint32_t legacy_marker = 0x12345678U;

    void append_u32(std::string &out, const std::uint32_t value)
    {
        out.push_back(static_cast<char>(value & 0xffU));
        out.push_back(static_cast<char>((value >> 8U) & 0xffU));
        out.push_back(static_cast<char>((value >> 16U) & 0xffU));
        out.push_back(static_cast<char>((value >> 24U) & 0xffU));
    }

    void set_u32(std::string &out, const std::size_t position, const std::uint32_t value)
    {
        out[position] = static_cast<char>(value & 0xffU);
        out[position + 1] = static_cast<char>((value >> 8U) & 0xffU);
        out[position + 2] = static_cast<char>((value >> 16U) & 0xffU);
        out[position + 3] = static_cast<char>((value >> 24U) & 0xffU);
    }

    std::string canonical_block(const std::string_view payload)
    {
        std::string block;
        append_u32(block, canonical_prefix);
        block.append(payload);
        append_u32(block, canonical_suffix);
        return block;
    }

    std::string legacy_block(const std::string_view payload, const std::uint32_t marker = legacy_marker)
    {
        std::string block;
        append_u32(block, marker);
        block.append(payload);
        return block;
    }

    struct toc_row final
    {
        std::string name;
        std::uint32_t offset = 0;
        std::uint32_t size = 0;
    };

    std::string build_pak(const std::vector<toc_row> &rows, const std::string &body)
    {
        std::string archive(4, '\0');
        append_u32(archive, static_cast<std::uint32_t>(rows.size()));
        for (const auto &row : rows) {
            append_u32(archive, static_cast<std::uint32_t>(row.name.size()));
            archive.append(row.name);
            append_u32(archive, row.offset);
            append_u32(archive, row.size);
        }
        archive.append(body);
        return archive;
    }

    std::string build_contiguous_pak(const std::vector<std::string> &names, const std::vector<std::string> &blocks)
    {
        REQUIRE(names.size() == blocks.size());
        std::vector<toc_row> rows;
        std::string body;
        std::uint32_t offset = 0;
        for (std::size_t index = 0; index < blocks.size(); ++index) {
            rows.push_back({names[index], offset, static_cast<std::uint32_t>(blocks[index].size())});
            offset += static_cast<std::uint32_t>(blocks[index].size());
            body.append(blocks[index]);
        }
        return build_pak(rows, body);
    }

    // A read-only stream buffer that records the number of absolute seeks, the number of
    // read calls and the number of delivered bytes. ``tellg`` probes the position with a
    // zero-relative ``seekoff(0, cur)``; that is not an absolute seek and does not count.
    class counting_streambuf final : public std::streambuf
    {
      public:
        explicit counting_streambuf(std::string data) : data_(std::move(data))
        {
        }

        [[nodiscard]] std::uint64_t seeks() const noexcept
        {
            return seeks_;
        }

        [[nodiscard]] std::uint64_t reads() const noexcept
        {
            return reads_;
        }

        [[nodiscard]] std::uint64_t bytes() const noexcept
        {
            return bytes_;
        }

      protected:
        int_type underflow() override
        {
            if (position_ >= data_.size()) {
                return traits_type::eof();
            }
            return traits_type::to_int_type(data_[position_]);
        }

        std::streamsize xsgetn(char_type *destination, const std::streamsize count) override
        {
            const auto available = static_cast<std::streamsize>(data_.size() - position_);
            const auto delivered = count < available ? count : available;
            if (delivered > 0) {
                std::memcpy(destination, data_.data() + position_, static_cast<std::size_t>(delivered));
                position_ += static_cast<std::size_t>(delivered);
                ++reads_;
                bytes_ += static_cast<std::uint64_t>(delivered);
            }
            return delivered;
        }

        pos_type seekoff(const off_type offset, const std::ios_base::seekdir direction,
                         const std::ios_base::openmode mode) override
        {
            if ((static_cast<unsigned>(mode) & static_cast<unsigned>(std::ios_base::in)) == 0U) {
                return pos_type(off_type(-1));
            }
            if (offset != 0 || direction != std::ios_base::cur) {
                ++seeks_;
            }
            std::int64_t base = 0;
            if (direction == std::ios_base::cur) {
                base = static_cast<std::int64_t>(position_);
            } else if (direction == std::ios_base::end) {
                base = static_cast<std::int64_t>(data_.size());
            }
            const std::int64_t target = base + static_cast<std::int64_t>(offset);
            if (target < 0 || target > static_cast<std::int64_t>(data_.size())) {
                return pos_type(off_type(-1));
            }
            position_ = static_cast<std::size_t>(target);
            return pos_type(off_type(position_));
        }

        pos_type seekpos(const pos_type position, const std::ios_base::openmode mode) override
        {
            if ((static_cast<unsigned>(mode) & static_cast<unsigned>(std::ios_base::in)) == 0U) {
                return pos_type(off_type(-1));
            }
            const auto target = static_cast<std::int64_t>(static_cast<std::streamoff>(position));
            if (target < 0 || target > static_cast<std::int64_t>(data_.size())) {
                return pos_type(off_type(-1));
            }
            ++seeks_;
            position_ = static_cast<std::size_t>(target);
            return pos_type(off_type(position_));
        }

      private:
        std::string data_;
        std::size_t position_ = 0;
        std::uint64_t seeks_ = 0;
        std::uint64_t reads_ = 0;
        std::uint64_t bytes_ = 0;
    };

    class counting_stream final : public std::istream
    {
      public:
        explicit counting_stream(std::string data) : std::istream(nullptr), buffer_(std::move(data))
        {
            rdbuf(&buffer_);
            clear();
        }

        [[nodiscard]] std::uint64_t seeks() const noexcept
        {
            return buffer_.seeks();
        }

        [[nodiscard]] std::uint64_t reads() const noexcept
        {
            return buffer_.reads();
        }

        [[nodiscard]] std::uint64_t bytes() const noexcept
        {
            return buffer_.bytes();
        }

      private:
        counting_streambuf buffer_;
    };

    std::vector<std::unique_ptr<extractor::file>> parse(const std::string &bytes)
    {
        counting_stream source(bytes);
        return zanzarah::parse_pak(source);
    }
} // namespace

TEST_CASE("zanzarah parser: opening stays within a bounded seek and read budget", "[unit][zanzarah][budget]")
{
    constexpr std::size_t clustered_entries = 64;
    constexpr std::size_t large_payload_bytes = std::size_t{256} * 1024U;

    std::vector<std::string> names;
    std::vector<std::string> blocks;
    for (std::size_t index = 0; index < clustered_entries; ++index) {
        names.push_back(std::format("file{:03}", index));
        blocks.push_back(canonical_block("payload!"));
    }
    names.emplace_back("large");
    blocks.push_back(canonical_block(std::string(large_payload_bytes, 'Z')));

    const auto archive = build_contiguous_pak(names, blocks);
    REQUIRE(archive.size() > large_payload_bytes);

    counting_stream source(archive);
    const auto files = zanzarah::parse_pak(source);

    REQUIRE(files.size() == names.size());
    for (std::size_t index = 0; index < clustered_entries; ++index) {
        REQUIRE(files[index]->compressed_body_size_in_bytes == 8);
    }
    REQUIRE(files.back()->compressed_body_size_in_bytes == static_cast<std::int64_t>(large_payload_bytes));

    // The framing policy reaches only the marker DWORDs. A per-entry absolute seek would cost
    // two seeks and two reads for every block; the accepted budget is one boundary probe per
    // block plus a small constant, so an implementation that scatters a seek per entry fails.
    const auto entry_total = files.size();
    REQUIRE(source.seeks() <= entry_total + 4);
    REQUIRE(source.reads() <= 4 * entry_total + 16);
    REQUIRE(source.bytes() < archive.size());
}

TEST_CASE("zanzarah parser: a large body is not read while the index is validated", "[unit][zanzarah][budget]")
{
    constexpr std::size_t large_payload_bytes = std::size_t{512} * 1024U;
    const auto archive = build_contiguous_pak({"data.bin"}, {canonical_block(std::string(large_payload_bytes, 'Q'))});

    counting_stream source(archive);
    const auto files = zanzarah::parse_pak(source);

    REQUIRE(files.size() == 1);
    REQUIRE(files.front()->compressed_body_size_in_bytes == static_cast<std::int64_t>(large_payload_bytes));
    REQUIRE(source.bytes() < large_payload_bytes);
}

TEST_CASE("zanzarah parser: a sparse shuffled index stays within the marker page budget", "[unit][zanzarah][budget]")
{
    constexpr std::size_t entries = 24;
    constexpr std::size_t payload_bytes = 8192;
    constexpr std::uint64_t marker_page_bytes = 512;

    std::string body;
    std::vector<toc_row> rows;
    for (std::size_t index = 0; index < entries; ++index) {
        const auto block = canonical_block(std::string(payload_bytes, static_cast<char>('A' + (index % 26))));
        toc_row row;
        row.name = std::format("f{:02}", index);
        row.offset = static_cast<std::uint32_t>(body.size());
        row.size = static_cast<std::uint32_t>(block.size());
        rows.push_back(row);
        body += block;
    }
    // Reverse the table so the index order scatters the probes backwards across the body.
    std::reverse(rows.begin(), rows.end());
    const auto archive = build_pak(rows, body);
    const auto index_bytes = archive.size() - body.size();

    counting_stream source(archive);
    const auto files = zanzarah::parse_pak(source);

    REQUIRE(files.size() == entries);
    for (const auto &file : files) {
        REQUIRE(file->compressed_body_size_in_bytes == static_cast<std::int64_t>(payload_bytes));
    }
    REQUIRE(files.front()->path == std::format("f{:02}", entries - 1));

    // Only the metadata and one page per marker probe may be read: sparse blocks cannot share
    // a window, so an implementation that pulls a read-ahead buffer per probe is far over.
    REQUIRE(source.bytes() <=
            static_cast<std::uint64_t>(index_bytes) +
                2ULL * static_cast<std::uint64_t>(entries) * static_cast<std::uint64_t>(marker_page_bytes) +
                static_cast<std::uint64_t>(marker_page_bytes));
    REQUIRE(source.bytes() < archive.size() / 2);
}

TEST_CASE("zanzarah parser: a marker that straddles a page boundary is still read", "[unit][zanzarah]")
{
    const std::string name(491, 'n');
    const std::string block = canonical_block("x");
    const std::vector<toc_row> rows{{name, 0, static_cast<std::uint32_t>(block.size())}};
    const auto files = parse(build_pak(rows, block));

    REQUIRE(files.size() == 1);
    REQUIRE(files.front()->path == name);
    REQUIRE(files.front()->compressed_body_size_in_bytes == 1);
}

TEST_CASE("zanzarah parser: the whole archive falls back when a block lacks the markers", "[unit][zanzarah]")
{
    SECTION("every block canonical trims the suffix")
    {
        const auto archive = build_contiguous_pak(
            {"a", "b", "c"}, {canonical_block("alpha"), canonical_block("bravo!!"), canonical_block("")});
        const auto files = parse(archive);
        REQUIRE(files.size() == 3);
        REQUIRE(files[0]->path == "a");
        REQUIRE(files[0]->compressed_body_size_in_bytes == 5);
        REQUIRE(files[1]->compressed_body_size_in_bytes == 7);
        REQUIRE(files[2]->compressed_body_size_in_bytes == 0);
        REQUIRE(files[0]->uncompressed_body_size_in_bytes == 5);
    }

    SECTION("a legacy block keeps the prefix-only extent")
    {
        const auto archive = build_contiguous_pak({"a"}, {legacy_block("legacy")});
        const auto files = parse(archive);
        REQUIRE(files.size() == 1);
        REQUIRE(files[0]->compressed_body_size_in_bytes == 6);
    }

    SECTION("a mixed archive falls back for every entry")
    {
        const auto archive = build_contiguous_pak({"a", "b"}, {canonical_block("alpha"), legacy_block("bravo")});
        const auto files = parse(archive);
        REQUIRE(files.size() == 2);
        REQUIRE(files[0]->compressed_body_size_in_bytes == 9);
        REQUIRE(files[1]->compressed_body_size_in_bytes == 5);
    }

    SECTION("a canonical prefix with a wrong suffix falls back")
    {
        std::string block;
        append_u32(block, canonical_prefix);
        block.append("bravo");
        append_u32(block, 0xffffffffU);
        const auto files = parse(build_contiguous_pak({"a"}, {block}));
        REQUIRE(files.size() == 1);
        REQUIRE(files[0]->compressed_body_size_in_bytes == 9);
    }

    SECTION("a block shorter than the framing floor is not canonical")
    {
        for (std::uint32_t short_size = 4; short_size <= 7; ++short_size) {
            CAPTURE(short_size);
            std::string block{static_cast<char>(canonical_prefix & 0xffU),
                              static_cast<char>((canonical_prefix >> 8U) & 0xffU)};
            while (block.size() < short_size) {
                block.push_back('x');
            }
            const auto files = parse(build_contiguous_pak({"a"}, {block}));
            REQUIRE(files.size() == 1);
            REQUIRE(files[0]->compressed_body_size_in_bytes == static_cast<std::int64_t>(short_size) - 4);
        }
    }
}

TEST_CASE("zanzarah parser: the table order may differ from the physical block order", "[unit][zanzarah]")
{
    const std::string first = "alpha";
    const std::string second = "bravo!";
    std::string body = canonical_block(first);
    body += canonical_block(second);

    const auto first_size = static_cast<std::uint32_t>(canonical_block(first).size());
    const auto second_size = static_cast<std::uint32_t>(canonical_block(second).size());
    const std::vector<toc_row> rows{
        {"b", first_size, second_size},
        {"a", 0, first_size},
    };
    const auto archive = build_pak(rows, body);
    const auto body_position = archive.size() - body.size();

    const auto files = parse(archive);
    REQUIRE(files.size() == 2);
    REQUIRE(files[0]->path == "b");
    REQUIRE(files[0]->offset == static_cast<std::int64_t>(body_position + first_size) + 4);
    REQUIRE(files[0]->compressed_body_size_in_bytes == static_cast<std::int64_t>(second.size()));
    REQUIRE(files[1]->path == "a");
    REQUIRE(files[1]->offset == static_cast<std::int64_t>(body_position) + 4);
    REQUIRE(files[1]->compressed_body_size_in_bytes == static_cast<std::int64_t>(first.size()));
}

TEST_CASE("zanzarah parser: leading relative paths are normalized", "[unit][zanzarah]")
{
    const auto archive =
        build_contiguous_pak({"..\\data\\a.txt", "data\\b.txt"}, {canonical_block("one"), canonical_block("two")});
    const auto files = parse(archive);
    REQUIRE(files.size() == 2);
    REQUIRE(files[0]->path == "data\\a.txt");
    REQUIRE(files[1]->path == "data\\b.txt");
}

TEST_CASE("zanzarah parser: malformed indexes are rejected", "[unit][zanzarah]")
{
    SECTION("a zero file count is rejected")
    {
        REQUIRE_THROWS_AS(parse(std::string{0, 0, 0, 0, 0, 0, 0, 0}), extractor::read_error);
    }

    SECTION("a file count beyond the metadata ceiling is rejected")
    {
        const char high = static_cast<char>(0xff);
        REQUIRE_THROWS_AS(parse(std::string{0, 0, 0, 0, high, high, high, 0x7f}), extractor::read_error);
    }

    SECTION("a file count larger than the remaining bytes is rejected")
    {
        REQUIRE_THROWS_AS(parse(std::string{0, 0, 0, 0, 2, 0, 0, 0}), extractor::read_error);
    }

    SECTION("a zero path length is rejected")
    {
        auto archive = build_contiguous_pak({"a"}, {canonical_block("alpha")});
        set_u32(archive, 8, 0);
        REQUIRE_THROWS_AS(parse(archive), extractor::read_error);
    }

    SECTION("a path length beyond the resource budget is rejected")
    {
        auto archive = build_contiguous_pak({"a"}, {canonical_block("alpha")});
        set_u32(archive, 8, 0x7fffffffU);
        REQUIRE_THROWS_AS(parse(archive), extractor::read_error);
    }

    SECTION("a path length beyond the remaining bytes is rejected")
    {
        auto archive = build_contiguous_pak({"a"}, {canonical_block("alpha")});
        set_u32(archive, 8, 4092U);
        REQUIRE_THROWS_AS(parse(archive), extractor::read_error);
    }

    SECTION("a negative block offset is rejected")
    {
        auto archive = build_contiguous_pak({"a"}, {canonical_block("alpha")});
        set_u32(archive, 13, 0xffffffffU);
        REQUIRE_THROWS_AS(parse(archive), extractor::read_error);
    }

    SECTION("a zero block size is rejected")
    {
        auto archive = build_contiguous_pak({"a"}, {canonical_block("alpha")});
        set_u32(archive, 17, 0);
        REQUIRE_THROWS_AS(parse(archive), extractor::read_error);
    }

    SECTION("a block size below the prefix floor is rejected")
    {
        for (std::uint32_t short_size = 1; short_size < 4; ++short_size) {
            CAPTURE(short_size);
            auto archive = build_contiguous_pak({"a"}, {canonical_block("alpha")});
            set_u32(archive, 17, short_size);
            REQUIRE_THROWS_AS(parse(archive), extractor::read_error);
        }
    }

    SECTION("a block offset outside the body is rejected")
    {
        auto archive = build_contiguous_pak({"a"}, {canonical_block("alpha")});
        set_u32(archive, 13, 0x7fffffffU);
        REQUIRE_THROWS_AS(parse(archive), extractor::read_error);
    }

    SECTION("a block size beyond the body is rejected")
    {
        const std::vector<toc_row> rows{{"a", 0, 100}};
        REQUIRE_THROWS_AS(parse(build_pak(rows, std::string(8, 'x'))), extractor::read_error);
    }

    SECTION("a truncated stream is a bounded read error")
    {
        REQUIRE_THROWS_AS(parse(std::string{0, 0, 0, 0, 1, 0}), unpackrat::io::read_error);
    }
}
