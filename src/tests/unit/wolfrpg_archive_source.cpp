#include "../../core/wolfrpg/archive.h"
#include "../../core/wolfrpg/text_encoding.h"
#include "../support/wolfrpg_fixtures.h"

#include <uberwolf/DXLib/DXArchive.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <functional>
#include <ios>
#include <memory>
#include <new>
#include <sstream>
#include <streambuf>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include <catch2/catch_test_macros.hpp>

// Focused regressions for the thin DX8 adapter in src/core/wolfrpg/archive.cpp.
//
// The adapter owns no container logic: it hands the caller's stream to the vendor
// DXArchive::Source, runs one WalkEntries pass and streams one ExtractEntry per entry. These tests
// pin the parts of that boundary the vendor cannot provide itself - the stream bridge (seekoff with
// seekpos fallback, an exact landing, negative and short reads, caller-exception passthrough), the
// decoded-name path policy, the entry-size narrowing, and the vendor GetEntryPlan forwarder that a
// separate port patch adds.
namespace
{
    using unpackrat::wolfrpg::archive;
    using unpackrat::wolfrpg::entry;
    using unpackrat::wolfrpg::read_error;

    constexpr std::uint64_t uncompressed_size = 0xffffffffffffffffULL;
    constexpr std::uint32_t flag_no_key = 0x00000001U;
    constexpr std::uint32_t flag_no_head_press = 0x00000002U;
    constexpr std::uint32_t char_code_shift_jis = 932U;
    constexpr std::uint32_t file_attribute_directory = 0x10U;

    // A caller-raised, non-std exception type carrying a distinct payload.
    struct source_payload
    {
        int payload;
    };

    std::string as_text(const std::vector<std::byte> &bytes)
    {
        return std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size());
    }

    std::istringstream to_stream(const std::vector<std::byte> &bytes)
    {
        return std::istringstream(as_text(bytes), std::ios::binary);
    }

    std::string extract_all(const archive &reader, const std::size_t index, std::istream &stream)
    {
        std::ostringstream output;
        reader.extract(index, stream, output, [](std::int64_t) {});
        return output.str();
    }

    // Serves an in-memory image through the streambuf layer (pubseekoff / pubseekpos / sgetn), with
    // per-request fault switches. It never catches anything, so a test can raise exactly the
    // exception it wants from the source.
    class scripted_streambuf final : public std::streambuf
    {
      public:
        explicit scripted_streambuf(std::string data) : data_(std::move(data))
        {
        }

        bool reject_end_seek = false; // the physical size query is refused
        bool reject_beg_seek = false; // only seekpos can serve a read position
        bool mislocate_beg = false;   // a read position resolves, but at a different reported position
        bool fail_beg_seek = false;   // a read position raises ios_base::failure
        bool fail_end_seek = false;   // the size query raises ios_base::failure
        std::function<void(std::size_t offset, std::size_t count)> before_read;
        std::function<void()> size_fault; // an arbitrary exception raised at the size query
        std::function<void()> seek_fault; // an arbitrary exception raised at a read-position query
        // < 0u: serve every size query; otherwise the number of size queries served before the query
        // is refused. Lets a stream report its length to the metadata parse and then shrink.
        int end_seek_allowance = -1;

      protected:
        pos_type seekoff(const off_type offset, const std::ios_base::seekdir direction,
                         std::ios_base::openmode) override
        {
            const bool size_query = direction == std::ios_base::end;
            if (size_query) {
                if (size_fault) {
                    size_fault();
                }
                if (end_seek_allowance == 0) {
                    return pos_type(off_type(-1));
                }
                if (end_seek_allowance > 0) {
                    --end_seek_allowance;
                }
            } else if (seek_fault) {
                seek_fault();
            }
            if (size_query && fail_end_seek) {
                throw std::ios_base::failure("scripted end seek");
            }
            if (!size_query && fail_beg_seek) {
                throw std::ios_base::failure("scripted read seek");
            }
            if (size_query && reject_end_seek) {
                return pos_type(off_type(-1));
            }
            if (!size_query && reject_beg_seek) {
                return pos_type(off_type(-1));
            }
            const pos_type landed = land(offset, direction);
            if (!size_query && mislocate_beg && landed != pos_type(off_type(-1))) {
                // Land on the target but report a position one byte past it: the bridge must treat
                // any landing that is not exactly the target as a source failure.
                return pos_type(off_type(static_cast<off_type>(landed) + 1));
            }
            return landed;
        }

        pos_type seekpos(const pos_type position, std::ios_base::openmode) override
        {
            return land(static_cast<off_type>(position), std::ios_base::beg);
        }

        std::streamsize xsgetn(char *destination, const std::streamsize count) override
        {
            if (count <= 0) {
                return 0;
            }
            const std::size_t want = static_cast<std::size_t>(count);
            if (before_read) {
                before_read(position_, want);
            }
            const std::size_t available = position_ < data_.size() ? data_.size() - position_ : 0;
            const std::size_t take = want < available ? want : available;
            if (take > 0u) {
                std::memcpy(destination, data_.data() + position_, take);
                position_ += take;
            }
            return static_cast<std::streamsize>(take);
        }

      private:
        pos_type land(const off_type offset, const std::ios_base::seekdir direction)
        {
            const std::streamoff base =
                direction == std::ios_base::beg
                    ? 0
                    : (direction == std::ios_base::end ? static_cast<std::streamoff>(data_.size())
                                                       : static_cast<std::streamoff>(position_));
            const std::streamoff target = base + offset;
            if (target < 0) {
                return pos_type(off_type(-1));
            }
            position_ = static_cast<std::size_t>(target);
            return pos_type(off_type(target));
        }

        std::string data_;
        std::size_t position_ = 0;
    };

    void put_u16(std::vector<std::byte> &out, const std::size_t offset, const std::uint16_t value)
    {
        out[offset] = static_cast<std::byte>(value & 0xffu);
        out[offset + 1] = static_cast<std::byte>((static_cast<std::uint32_t>(value) >> 8u) & 0xffu);
    }

    void put_u32(std::vector<std::byte> &out, const std::size_t offset, const std::uint32_t value)
    {
        for (unsigned index = 0; index < 4u; ++index) {
            out[offset + static_cast<std::size_t>(index)] =
                static_cast<std::byte>((value >> (8u * static_cast<unsigned>(index))) & 0xffu);
        }
    }

    void put_u64(std::vector<std::byte> &out, const std::size_t offset, const std::uint64_t value)
    {
        for (unsigned index = 0; index < 8u; ++index) {
            out[offset + static_cast<std::size_t>(index)] =
                static_cast<std::byte>((value >> (8u * static_cast<unsigned>(index))) & 0xffu);
        }
    }

    void append_text(std::vector<std::byte> &out, const std::string_view text)
    {
        for (const char value : text) {
            out.push_back(static_cast<std::byte>(static_cast<unsigned char>(value)));
        }
    }

    // A name-table record exactly as DXLib writes it: 2-byte block count, 2-byte parity, the
    // upper-cased copy and the original-case copy, both zero padded to blocks * 4 bytes.
    std::vector<std::byte> encode_name(const std::string_view name)
    {
        const std::size_t blocks = name.size() / 4 + 1;
        std::vector<std::byte> record(4 + 2 * blocks * 4, std::byte{0});
        put_u16(record, 0, static_cast<std::uint16_t>(blocks));
        for (std::size_t index = 0; index < name.size(); ++index) {
            const auto value = static_cast<unsigned char>(name[index]);
            record[4 + index] = static_cast<std::byte>(value);
            record[4 + blocks * 4 + index] = static_cast<std::byte>(value);
        }
        return record;
    }

    // A keyless DX8 container holding one stored file at the root and one stored file inside a
    // single subdirectory: the walker must yield "sub/inner.txt" (depth 1) before "top.txt".
    //
    // File table slots: 0 = the root's own DIRECTORY record, 1 = the "sub" directory record,
    // 2 = "top.txt", 3 = "inner.txt". The root frame spans slots 1..2 (FileHeadNum 2); the sub
    // directory's own DARC_DIRECTORY points its single child at slot 3.
    std::vector<std::byte> build_nested_archive()
    {
        constexpr std::string_view inner_text = "inner body";
        constexpr std::string_view top_text = "top body";

        std::vector<std::byte> names(4, std::byte{0});
        const auto add_name = [&names](const std::string_view name) -> std::size_t {
            const std::size_t address = names.size();
            const std::vector<std::byte> record = encode_name(name);
            names.insert(names.end(), record.begin(), record.end());
            return address;
        };
        const std::size_t sub_name = add_name("sub");
        const std::size_t inner_name = add_name("inner.txt");
        const std::size_t top_name = add_name("top.txt");

        std::vector<std::byte> bodies;
        const std::size_t inner_data = bodies.size();
        append_text(bodies, inner_text);
        const std::size_t top_data = bodies.size();
        append_text(bodies, top_text);

        const std::size_t file_table_base = names.size();
        std::vector<std::byte> file_table(4 * sizeof(DARC_FILEHEAD), std::byte{0});
        const auto set_record = [&file_table](const std::size_t slot, const std::uint64_t name_address,
                                              const std::uint64_t attributes, const std::uint64_t data_address,
                                              const std::uint64_t data_size) {
            const std::size_t base = slot * sizeof(DARC_FILEHEAD);
            put_u64(file_table, base + offsetof(DARC_FILEHEAD, NameAddress), name_address);
            put_u64(file_table, base + offsetof(DARC_FILEHEAD, Attributes), attributes);
            put_u64(file_table, base + offsetof(DARC_FILEHEAD, DataAddress), data_address);
            put_u64(file_table, base + offsetof(DARC_FILEHEAD, DataSize), data_size);
            put_u64(file_table, base + offsetof(DARC_FILEHEAD, PressDataSize), uncompressed_size);
            put_u64(file_table, base + offsetof(DARC_FILEHEAD, HuffPressDataSize), uncompressed_size);
        };
        set_record(0, 0, file_attribute_directory, 0, 0);
        set_record(1, sub_name, file_attribute_directory, sizeof(DARC_DIRECTORY), 0);
        set_record(2, top_name, 0, top_data, top_text.size());
        set_record(3, inner_name, 0, inner_data, inner_text.size());

        const std::size_t directory_table_offset = file_table_base + file_table.size();
        std::vector<std::byte> directory_table(2 * sizeof(DARC_DIRECTORY), std::byte{0});
        const auto set_directory = [&directory_table](const std::size_t slot, const std::uint64_t own,
                                                      const std::uint64_t parent, const std::uint64_t head_count,
                                                      const std::uint64_t head_address) {
            const std::size_t base = slot * sizeof(DARC_DIRECTORY);
            put_u64(directory_table, base + offsetof(DARC_DIRECTORY, DirectoryAddress), own);
            put_u64(directory_table, base + offsetof(DARC_DIRECTORY, ParentDirectoryAddress), parent);
            put_u64(directory_table, base + offsetof(DARC_DIRECTORY, FileHeadNum), head_count);
            put_u64(directory_table, base + offsetof(DARC_DIRECTORY, FileHeadAddress), head_address);
        };
        set_directory(0, 0, uncompressed_size, 2, std::uint64_t{1} * sizeof(DARC_FILEHEAD));
        set_directory(1, std::uint64_t{1} * sizeof(DARC_FILEHEAD), 0, 1, std::uint64_t{3} * sizeof(DARC_FILEHEAD));

        std::vector<std::byte> header = names;
        header.insert(header.end(), file_table.begin(), file_table.end());
        header.insert(header.end(), directory_table.begin(), directory_table.end());

        std::vector<std::byte> bytes(sizeof(DARC_HEAD), std::byte{0});
        bytes[offsetof(DARC_HEAD, Head)] = std::byte{'D'};
        bytes[offsetof(DARC_HEAD, Head) + 1] = std::byte{'X'};
        put_u16(bytes, offsetof(DARC_HEAD, Version), 8);
        put_u32(bytes, offsetof(DARC_HEAD, HeadSize), static_cast<std::uint32_t>(header.size()));
        put_u64(bytes, offsetof(DARC_HEAD, DataStartAddress), sizeof(DARC_HEAD));
        put_u64(bytes, offsetof(DARC_HEAD, FileNameTableStartAddress), sizeof(DARC_HEAD) + bodies.size());
        put_u64(bytes, offsetof(DARC_HEAD, FileTableStartAddress), file_table_base);
        put_u64(bytes, offsetof(DARC_HEAD, DirectoryTableStartAddress), directory_table_offset);
        put_u32(bytes, offsetof(DARC_HEAD, CharCodeFormat), char_code_shift_jis);
        put_u32(bytes, offsetof(DARC_HEAD, Flags), flag_no_key | flag_no_head_press);
        bytes[offsetof(DARC_HEAD, HuffmanEncodeKB)] = std::byte{0xff};
        bytes.insert(bytes.end(), bodies.begin(), bodies.end());
        bytes.insert(bytes.end(), header.begin(), header.end());
        return bytes;
    }

    // A vendor byte source over an in-memory image, used to drive GetEntryPlan directly.
    DXArchive::Source source_over(const std::string &bytes)
    {
        DXArchive::Source source;
        source.physical_size = [&bytes]() -> s64 { return static_cast<s64>(bytes.size()); };
        source.read_at = [&bytes](const s64 offset, void *destination, const size_t size) -> bool {
            if (offset < 0 || static_cast<std::uint64_t>(offset) + size > bytes.size()) {
                return false;
            }
            std::memcpy(destination, bytes.data() + static_cast<std::size_t>(offset), size);
            return true;
        };
        return source;
    }
} // namespace

TEST_CASE("the adapter lists a nested archive depth-first and extracts every decoded entry", "[wolfrpg][source]")
{
    const std::vector<std::byte> bytes = build_nested_archive();
    std::istringstream stream = to_stream(bytes);
    const std::unique_ptr<archive> reader = archive::open(stream, "nested.wolf");

    REQUIRE(reader);
    CHECK(reader->version() == 8);

    const std::span<const entry> entries = reader->entries();
    REQUIRE(entries.size() == 2);
    CHECK(entries[0].path == "sub/inner.txt");
    CHECK(entries[0].stored_size == 10);
    CHECK(entries[0].unpacked_size == 10);
    CHECK(entries[1].path == "top.txt");
    CHECK(entries[1].stored_size == 8);
    CHECK(entries[1].unpacked_size == 8);

    CHECK(extract_all(*reader, 0, stream) == "inner body");
    CHECK(extract_all(*reader, 1, stream) == "top body");
}

TEST_CASE("the adapter never splits an encoded name at a 0x5c trail byte", "[wolfrpg][source]")
{
    // U+8868 is encoded in Shift-JIS as 0x95 0x5C: a legitimate 0x5C trail byte inside one
    // character. Applying the separator policy to the raw encoded bytes would corrupt the name, so
    // the decoded component must survive intact.
    const std::array entries{
        test::support::synthetic_entry{"\x95\x5C"
                                       ".dat",
                                       "payload", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);
    std::istringstream stream = to_stream(built.bytes);
    const std::unique_ptr<archive> reader = archive::open(stream, "sfx.wolf");

    REQUIRE(reader);
    REQUIRE(reader->entries().size() == 1);
    CHECK(reader->entries()[0].path == "\xE8\xA1\xA8.dat"); // U+8868 in UTF-8
    CHECK(extract_all(*reader, 0, stream) == "payload");
}

TEST_CASE("the adapter rejects decoded names that are not safe relative components", "[wolfrpg][source]")
{
    const std::array<std::string, 6> unsafe_names{
        "..",  // parent traversal
        ".",   // current directory
        "a:b", // alternate data stream separator
        "a/b", // forward slash
        "a\x5C"
        "b", // backslash
        "a\x01"
        "b", // control byte
    };

    for (const std::string &name : unsafe_names) {
        CAPTURE(name);
        const std::array entries{
            test::support::synthetic_entry{name, "payload", false},
        };
        const test::support::built_archive built = test::support::build_plain_archive(entries);
        std::istringstream stream = to_stream(built.bytes);
        CHECK_THROWS_AS(archive::open(stream, "unsafe.wolf"), read_error);
    }
}

TEST_CASE("the adapter turns a table the walker rejects into read_error", "[wolfrpg][source]")
{
    // A keyless container whose root DARC_DIRECTORY names itself as something other than offset 0 is
    // accepted by the metadata parse and rejected only by the shared walk kernel.
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    test::support::built_archive built = test::support::build_plain_archive(entries);
    put_u64(built.bytes, built.header_offset + built.root_directory_offset, 5);
    std::istringstream stream = to_stream(built.bytes);
    CHECK_THROWS_AS(archive::open(stream, "badroot.wolf"), read_error);
}

TEST_CASE("the adapter rejects a vendor body size that does not fit a signed entry size", "[wolfrpg][source]")
{
    // A literal-LZ body keeps the physical extent small, so only the declared uncompressed size can
    // be absurd. It must be rejected rather than narrowed into a negative entry size.
    const std::array entries{
        test::support::synthetic_entry{"big.bin", "payload", true},
    };
    test::support::built_archive built = test::support::build_plain_archive(entries);
    test::support::set_file_unpacked_size(built, built.bytes, 0, 1ULL << 63u);
    std::istringstream stream = to_stream(built.bytes);
    CHECK_THROWS_AS(archive::open(stream, "huge.wolf"), read_error);
}

TEST_CASE("the adapter reports a source that has no buffer as a read error", "[wolfrpg][source]")
{
    std::istream stream(nullptr);
    CHECK_THROWS_AS(archive::open(stream, "null.wolf"), read_error);
}

TEST_CASE("a source whose size query fails is a read error", "[wolfrpg][source]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);

    SECTION("a refused end seek is a negative size, never a huge unsigned length")
    {
        scripted_streambuf buffer(as_text(built.bytes));
        buffer.reject_end_seek = true;
        std::istream stream(&buffer);
        CHECK_THROWS_AS(archive::open(stream, "nosize.wolf"), read_error);
    }

    SECTION("a size query raising an ios_base::failure is translated to read_error")
    {
        scripted_streambuf buffer(as_text(built.bytes));
        buffer.fail_end_seek = true;
        std::istream stream(&buffer);
        CHECK_THROWS_AS(archive::open(stream, "badseek.wolf"), read_error);
    }
}

TEST_CASE("the source bridge falls back to seekpos when seekoff refuses a read position", "[wolfrpg][source]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);

    scripted_streambuf buffer(as_text(built.bytes));
    buffer.reject_beg_seek = true;
    std::istream stream(&buffer);
    const std::unique_ptr<archive> reader = archive::open(stream, "seekpos.wolf");

    REQUIRE(reader);
    REQUIRE(reader->entries().size() == 1);
    CHECK(reader->entries()[0].path == "note.txt");
    CHECK(extract_all(*reader, 0, stream) == "raw payload bytes");
}

TEST_CASE("a read position the source cannot land exactly is a read error", "[wolfrpg][source]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);

    scripted_streambuf buffer(as_text(built.bytes));
    buffer.mislocate_beg = true;
    std::istream stream(&buffer);
    CHECK_THROWS_AS(archive::open(stream, "misland.wolf"), read_error);
}

TEST_CASE("a read position that raises an ios_base::failure is a read error", "[wolfrpg][source]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);

    scripted_streambuf buffer(as_text(built.bytes));
    buffer.fail_beg_seek = true;
    std::istream stream(&buffer);
    CHECK_THROWS_AS(archive::open(stream, "badpos.wolf"), read_error);
}

// The public archive.h contract reports every *source* fault as read_error. Only the two caller
// resource exceptions std::bad_alloc and std::length_error keep their own identity; every other
// thrown type - a stream failure, a codec exception, a caller payload - is a source fault and is
// normalized. These three regressions pin that policy at each of the three source operations the
// bridge drives (the physical-size query, the read-position query and the body read).
TEST_CASE("a custom fault raised at the size query is normalized to read_error", "[wolfrpg][source]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);

    scripted_streambuf buffer(as_text(built.bytes));
    buffer.size_fault = [] {
        throw source_payload{71}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
    };
    std::istream stream(&buffer);
    CHECK_THROWS_AS(archive::open(stream, "sizefault.wolf"), read_error);
}

TEST_CASE("a custom fault raised at a read position is normalized to read_error", "[wolfrpg][source]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);

    scripted_streambuf buffer(as_text(built.bytes));
    buffer.seek_fault = [] {
        throw source_payload{72}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
    };
    std::istream stream(&buffer);
    CHECK_THROWS_AS(archive::open(stream, "seekfault.wolf"), read_error);
}

TEST_CASE("a size query raising a resource exception keeps its own type", "[wolfrpg][source]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);

    SECTION("std::bad_alloc is preserved")
    {
        scripted_streambuf buffer(as_text(built.bytes));
        buffer.size_fault = [] { throw std::bad_alloc{}; };
        std::istream stream(&buffer);
        CHECK_THROWS_AS(archive::open(stream, "sizealloc.wolf"), std::bad_alloc);
    }

    SECTION("std::length_error keeps its message")
    {
        scripted_streambuf buffer(as_text(built.bytes));
        buffer.size_fault = [] { throw std::length_error("source length"); };
        std::istream stream(&buffer);
        try {
            static_cast<void>(archive::open(stream, "lenfault.wolf"));
            FAIL("a size query raising std::length_error must propagate unchanged");
        } catch (const std::length_error &error) {
            CHECK(std::string(error.what()) == "source length");
        }
    }
}

TEST_CASE("a read position raising a resource exception keeps its own type", "[wolfrpg][source]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);

    SECTION("std::bad_alloc is preserved")
    {
        scripted_streambuf buffer(as_text(built.bytes));
        buffer.seek_fault = [] { throw std::bad_alloc{}; };
        std::istream stream(&buffer);
        CHECK_THROWS_AS(archive::open(stream, "seekalloc.wolf"), std::bad_alloc);
    }

    SECTION("std::length_error keeps its message")
    {
        scripted_streambuf buffer(as_text(built.bytes));
        buffer.seek_fault = [] { throw std::length_error("seek length"); };
        std::istream stream(&buffer);
        try {
            static_cast<void>(archive::open(stream, "seeklen.wolf"));
            FAIL("a read position raising std::length_error must propagate unchanged");
        } catch (const std::length_error &error) {
            CHECK(std::string(error.what()) == "seek length");
        }
    }
}

TEST_CASE("a truncated body read is a read error and a body read failure keeps its own type", "[wolfrpg][source]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);
    const std::size_t body_offset = built.header_offset - 17U; // DataStartAddress + first DataAddress

    SECTION("a body read raising an ios_base::failure is translated to read_error")
    {
        scripted_streambuf buffer(as_text(built.bytes));
        buffer.before_read = [body_offset](const std::size_t offset, const std::size_t) {
            if (offset == body_offset) {
                throw std::ios_base::failure("scripted body read");
            }
        };
        std::istream stream(&buffer);
        const std::unique_ptr<archive> reader = archive::open(stream, "shortbody.wolf");
        REQUIRE(reader);
        std::ostringstream output;
        CHECK_THROWS_AS(reader->extract(0, stream, output, [](std::int64_t) {}), read_error);
    }

    SECTION("a quiet destination keeps a source resource failure exactly as thrown")
    {
        scripted_streambuf buffer(as_text(built.bytes));
        buffer.before_read = [body_offset](const std::size_t offset, const std::size_t) {
            if (offset == body_offset) {
                throw std::bad_alloc{};
            }
        };
        std::istream stream(&buffer);
        const std::unique_ptr<archive> reader = archive::open(stream, "resource.wolf");
        REQUIRE(reader);
        std::ostringstream output;
        CHECK_THROWS_AS(reader->extract(0, stream, output, [](std::int64_t) {}), std::bad_alloc);
    }

    SECTION("a body read raising a length_error keeps its own type")
    {
        scripted_streambuf buffer(as_text(built.bytes));
        buffer.before_read = [body_offset](const std::size_t offset, const std::size_t) {
            if (offset == body_offset) {
                throw std::length_error("body length");
            }
        };
        std::istream stream(&buffer);
        const std::unique_ptr<archive> reader = archive::open(stream, "bodylen.wolf");
        REQUIRE(reader);
        std::ostringstream output;
        CHECK_THROWS_AS(reader->extract(0, stream, output, [](std::int64_t) {}), std::length_error);
    }

    SECTION("a caller payload at the body read is a source fault and is normalized")
    {
        // The body read is a source operation, so a payload of an arbitrary caller type is not one
        // of the two preserved resource exceptions and becomes read_error. The armed destination
        // only proves the output stream's own exception mask does not change that.
        scripted_streambuf buffer(as_text(built.bytes));
        buffer.before_read = [body_offset](const std::size_t offset, const std::size_t) {
            if (offset == body_offset) {
                throw source_payload{42}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
            }
        };
        std::istream stream(&buffer);
        const std::unique_ptr<archive> reader = archive::open(stream, "payload.wolf");
        REQUIRE(reader);
        std::ostringstream output;
        output.exceptions(std::ios::badbit);
        CHECK_THROWS_AS(reader->extract(0, stream, output, [](std::int64_t) {}), read_error);
    }
}

TEST_CASE("a source that refuses its later body-plan size query is a read error", "[wolfrpg][source]")
{
    // OpenArchiveSource records the physical length once and WalkEntries reuses that cached value,
    // but the per-entry GetEntryPlan re-derives the body geometry through ResolveEntry, which
    // queries the source's physical length again. A stream that serves the metadata parse's size
    // query and then refuses the plan query therefore reaches the adapter's GetEntryPlan result
    // check; the archive must fail there instead of publishing a plan from a stale geometry.
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);

    scripted_streambuf buffer(as_text(built.bytes));
    buffer.end_seek_allowance = 1; // the metadata parse sees the length, the plan query does not
    std::istream stream(&buffer);
    CHECK_THROWS_AS(archive::open(stream, "shrink.wolf"), read_error);
}

TEST_CASE("the vendor GetEntryPlan forwarder reports the shared body geometry", "[wolfrpg][vendor]")
{
    constexpr std::string_view body = "raw payload bytes";
    const std::array entries{
        test::support::synthetic_entry{"note.txt", std::string(body), false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);
    const std::string bytes = as_text(built.bytes);
    const DXArchive::Source source = source_over(bytes);

    DXArchive archive;
    REQUIRE(archive.OpenArchiveSource(source, nullptr) == DXArchive::SOURCE_RESULT_OK);

    DXArchive::EntryRecord captured{};
    bool visited = false;
    const int walked = archive.WalkEntries([&captured, &visited](const DXArchive::EntryRecord &record) -> int {
        captured = record;
        visited = true;
        return 0;
    });
    REQUIRE(walked == DXArchive::SOURCE_RESULT_OK);
    REQUIRE(visited);

    DXArchive::BodyPlan plan{};
    CHECK(archive.GetEntryPlan(captured, source, plan) == DXArchive::SOURCE_RESULT_OK);
    // The stored body is exactly the declared byte count, both on disk and after decoding.
    CHECK(plan.OutputSize == static_cast<u64>(body.size()));
    CHECK(plan.Physical == plan.OutputSize);
    CHECK_FALSE(plan.Huff);
    CHECK_FALSE(plan.Lz);

    SECTION("a record with no validated handle is rejected")
    {
        DXArchive::EntryRecord invalid{};
        DXArchive::BodyPlan rejected{};
        CHECK(archive.GetEntryPlan(invalid, source, rejected) == DXArchive::SOURCE_RESULT_FORMAT);
    }

    SECTION("a record borrowed from another archive is rejected")
    {
        DXArchive archive2;
        REQUIRE(archive2.OpenArchiveSource(source, nullptr) == DXArchive::SOURCE_RESULT_OK);
        DXArchive::EntryRecord foreign{};
        REQUIRE(archive2.WalkEntries([&foreign](const DXArchive::EntryRecord &record) -> int {
            foreign = record;
            return 0;
        }) == DXArchive::SOURCE_RESULT_OK);

        DXArchive::BodyPlan rejected{};
        CHECK(archive.GetEntryPlan(foreign, source, rejected) == DXArchive::SOURCE_RESULT_FORMAT);
    }
}

TEST_CASE("a supplied password is forwarded to the vendor for a keyless archive", "[wolfrpg][source]")
{
    // The keyless container carries its own DXA_FLAG_NO_KEY, so a caller password neither breaks the
    // open nor is echoed back; it is copied into the library's bounded key buffer and handed over.
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);
    std::istringstream stream = to_stream(built.bytes);
    const std::unique_ptr<archive> reader = archive::open(stream, "keyless.wolf", "not-the-default-key");

    REQUIRE(reader);
    REQUIRE(reader->entries().size() == 1);
    CHECK(extract_all(*reader, 0, stream) == "raw payload bytes");
}

TEST_CASE("a declared code page the text codec rejects is a read error", "[wolfrpg][source]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    test::support::built_archive built = test::support::build_plain_archive(entries);
    put_u32(built.bytes, offsetof(DARC_HEAD, CharCodeFormat), 12345);
    std::istringstream stream = to_stream(built.bytes);
    CHECK_THROWS_AS(archive::open(stream, "codepage.wolf"), read_error);
}

TEST_CASE("moving an archive transfers its entries and leaves the source empty", "[wolfrpg][source]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);
    std::istringstream stream = to_stream(built.bytes);
    const std::unique_ptr<archive> source = archive::open(stream, "move.wolf");
    REQUIRE(source);
    REQUIRE(source->entries().size() == 1);

    archive moved = std::move(*source);
    CHECK(moved.entries().size() == 1);
    CHECK(moved.entries()[0].path == "note.txt");
    CHECK(moved.version() == 8);

    // A moved-from reader owns no state: it lists nothing, reports no version, and refuses an
    // extraction instead of reading through a dangling archive.
    CHECK(source->entries().empty());
    CHECK(source->version() == 0);
    std::ostringstream output;
    CHECK_THROWS_AS(source->extract(0, stream, output, [](std::int64_t) {}), std::out_of_range);

    // Move assignment replaces an existing reader's whole state.
    archive target = std::move(moved);
    CHECK(target.entries().size() == 1);
    const std::unique_ptr<archive> replacement = archive::open(stream, "replace.wolf");
    REQUIRE(replacement);
    target = std::move(*replacement);
    CHECK(target.entries().size() == 1);
    CHECK(target.entries()[0].path == "note.txt");
}
