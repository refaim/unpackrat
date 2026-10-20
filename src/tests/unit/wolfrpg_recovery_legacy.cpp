// Recovery tests use the public reader contract and frozen upstream-encoded fixtures.
#include "../../core/wolfrpg/legacy_archive.h"
#include "../../core/wolfrpg/legacy_crypto.h"
#include "../../core/wolfrpg/legacy_stream_source.h"
#include "../support/wolfrpg_default_mode_fixtures.h"
#include <algorithm>
#include <array>
#include <catch2/catch_test_macros.hpp>
#include <cstdint>
#include <cstring>
#include <functional>
#include <sstream>
#include <string>
#include <uberwolf/DXLib/DXArchiveVer5.h>
#include <utility>
#include <vector>

namespace
{
    namespace wolf = unpackrat::wolfrpg;
    namespace fixtures = test::support::wolfrpg_default_mode;
    std::istringstream input(const std::vector<std::byte> &bytes)
    {
        return std::istringstream(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()),
                                  std::ios::binary);
    }
} // namespace
TEST_CASE("recovered legacy defaults list and extract independently frozen payloads", "[recovery-legacy]")
{
    for (const auto *fixture : fixtures::all()) {
        if (fixture->container_version > 6)
            continue;
        DYNAMIC_SECTION(fixture->file_name)
        {
            const auto bytes = fixtures::load_bytes(fixture->file_name);
            REQUIRE(fixtures::fingerprint(bytes) == fixture->file_fingerprint);
            auto source = input(bytes);
            auto reader = wolf::legacy_archive::open(source, fixture->file_name);
            REQUIRE(reader->version() == fixture->container_version);
            REQUIRE(reader->entries().size() == fixture->entries.size());
            for (const auto &expected : fixture->entries) {
                const auto entries = reader->entries();
                const auto found = std::find_if(entries.begin(), entries.end(),
                                                [&](const auto &item) { return item.path == expected.path; });
                REQUIRE(found != entries.end());
                REQUIRE(found->unpacked_size == expected.unpacked_size);
                std::ostringstream output;
                std::int64_t progress = 0;
                reader->extract(static_cast<std::size_t>(found - entries.begin()), source, output,
                                [&](std::int64_t delta) { progress += delta; });
                REQUIRE(progress == expected.unpacked_size);
                REQUIRE(output.str() ==
                        std::string(reinterpret_cast<const char *>(expected.payload.data()), expected.payload.size()));
            }
        }
    }
}
TEST_CASE("legacy key derivation and phase are the public vendor primitives", "[recovery-legacy]")
{
    for (const std::string &text : {std::string{}, std::string("secret"), std::string("0123456789abcdef"),
                                    std::string("\0z", 2), std::string("ab\0cd", 5)}) {
        std::array<unsigned char, 12> reference{};
        DXArchive_VER5::KeyCreate(text.empty() || text.front() == '\0' ? nullptr : text.substr(0, 12).c_str(),
                                  reference.data());
        REQUIRE(wolf::legacy::derive_key(text) == reference);
    }
    const auto secret = wolf::legacy::derive_key("phase");
    std::vector<std::byte> bytes(37, std::byte{0x53});
    auto expected = bytes;
    auto reference_key = secret;
    DXArchive_VER5::KeyConv(expected.data(), static_cast<int>(expected.size()), 7, reference_key.data());
    wolf::legacy::apply_key(bytes, 31, secret);
    REQUIRE(bytes == expected);
    wolf::legacy::apply_key(bytes, 31, secret);
    REQUIRE(bytes == std::vector<std::byte>(37, std::byte{0x53}));
    std::vector<std::byte> empty;
    wolf::legacy::apply_key(empty, 0, secret);
    REQUIRE(empty.empty());
    REQUIRE(wolf::legacy::raw_fallback_key() ==
            wolf::legacy::key{0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff});
    REQUIRE(wolf::legacy::default_mode_keys().size() == 3);
}

namespace
{
    struct caller_error
    {
    };
    void ignore_progress(std::int64_t)
    {
    }
    template <class Exception, class Operation> void expect_failure(Operation &&operation)
    {
        bool caught = false;
        try {
            operation();
        } catch (const Exception &) {
            caught = true;
        }
        REQUIRE(caught);
    }
    class fault_source final : public std::stringbuf
    {
      public:
        explicit fault_source(const std::vector<std::byte> &bytes)
            : std::stringbuf(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()), std::ios::in)
        {
        }
        std::function<void(std::ios::seekdir)> on_seek;
        std::function<void()> on_read;
        bool absolute_only = false;
        bool reject_absolute = false;
        bool reject_size = false;
        std::streamsize maximum_read = INT64_MAX;
        bool wrong_offset = false;
        bool wrong_absolute = false;

      protected:
        pos_type seekoff(off_type at, std::ios::seekdir dir, std::ios::openmode mode) override
        {
            if (on_seek)
                on_seek(dir);
            if (dir == std::ios::end && reject_size)
                return pos_type(off_type(-1));
            if (dir == std::ios::beg && wrong_offset)
                return pos_type(at + 1);
            if (dir == std::ios::beg && absolute_only)
                return pos_type(off_type(-1));
            return std::stringbuf::seekoff(at, dir, mode);
        }
        pos_type seekpos(pos_type at, std::ios::openmode mode) override
        {
            if (reject_absolute)
                return pos_type(off_type(-1));
            if (wrong_absolute)
                return pos_type(off_type(at) + 1);
            return std::stringbuf::seekpos(at, mode);
        }
        std::streamsize xsgetn(char *out, std::streamsize size) override
        {
            if (on_read)
                on_read();
            if (size > maximum_read)
                return 0;
            return std::stringbuf::xsgetn(out, size);
        }
    };
} // namespace
TEST_CASE("legacy failures preserve source and caller exception identity", "[recovery-legacy]")
{
    const auto bytes = fixtures::load_bytes("default-mode-0.wolf");
    auto source = input(bytes);
    auto reader = wolf::legacy_archive::open(source, "owned.wolf");
    std::ostringstream output;
    SECTION("wrong password")
    {
        expect_failure<wolf::read_error>([&] { wolf::legacy_archive::open(source, "owned.wolf", "other"); });
    }
    SECTION("no input buffer")
    {
        std::istream absent(nullptr);
        expect_failure<wolf::read_error>([&] { wolf::legacy_archive::open(absent, "owned.wolf"); });
        expect_failure<wolf::read_error>([&] { reader->extract(0, absent, output, ignore_progress); });
    }
    SECTION("cancel")
    {
        expect_failure<caller_error>([&] {
            reader->extract(0, source, output, [](std::int64_t) {
                throw caller_error{}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
            });
        });
    }
    SECTION("short body")
    {
        auto short_source = input(std::vector<std::byte>(bytes.begin(), bytes.begin() + 28));
        expect_failure<wolf::read_error>([&] { reader->extract(0, short_source, output, ignore_progress); });
    }
    SECTION("bad index")
    {
        expect_failure<wolf::read_error>(
            [&] { reader->extract(reader->entries().size(), source, output, ignore_progress); });
    }
    SECTION("absolute-only positioning")
    {
        fault_source b(bytes);
        b.absolute_only = true;
        std::istream in(&b);
        auto value = wolf::legacy_archive::open(in, "owned.wolf");
        REQUIRE(value->entries().size() == 3);
    }
    SECTION("failed absolute positioning")
    {
        fault_source b(bytes);
        b.absolute_only = true;
        b.reject_absolute = true;
        std::istream in(&b);
        expect_failure<wolf::read_error>([&] { wolf::legacy_archive::open(in, "owned.wolf"); });
    }
    SECTION("metadata read refusal")
    {
        fault_source b(bytes);
        b.maximum_read = 32;
        std::istream in(&b);
        expect_failure<wolf::read_error>([&] { wolf::legacy_archive::open(in, "owned.wolf"); });
    }
    SECTION("size refusal")
    {
        fault_source b(bytes);
        b.reject_size = true;
        std::istream in(&b);
        expect_failure<wolf::read_error>([&] { wolf::legacy_archive::open(in, "owned.wolf"); });
    }
    SECTION("a positive wrong position is rejected despite absolute fallback")
    {
        fault_source b(bytes);
        b.wrong_offset = true;
        std::istream in(&b);
        expect_failure<wolf::read_error>([&] { wolf::legacy_archive::open(in, "owned.wolf"); });
    }
    SECTION("wrong absolute position is rejected")
    {
        fault_source b(bytes);
        b.absolute_only = true;
        b.wrong_absolute = true;
        std::istream in(&b);
        expect_failure<wolf::read_error>([&] { wolf::legacy_archive::open(in, "owned.wolf"); });
    }
    SECTION("source exception decision table")
    {
        for (int stage = 0; stage < 3; ++stage)
            for (int fault = 0; fault < 3; ++fault) {
                fault_source b(bytes);
                const auto raise = [fault] {
                    if (fault == 0)
                        throw caller_error{}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std
                                              // signal
                    if (fault == 1)
                        throw std::bad_alloc{};
                    throw std::length_error("owned source");
                };
                if (stage == 2)
                    b.on_read = raise;
                else
                    b.on_seek = [stage, raise](std::ios::seekdir dir) {
                        if ((stage == 0) == (dir == std::ios::end))
                            raise();
                    };
                std::istream in(&b);
                if (fault == 0)
                    expect_failure<wolf::read_error>([&] { wolf::legacy_archive::open(in, "owned.wolf"); });
                else if (fault == 1)
                    expect_failure<std::bad_alloc>([&] { wolf::legacy_archive::open(in, "owned.wolf"); });
                else
                    expect_failure<std::length_error>([&] { wolf::legacy_archive::open(in, "owned.wolf"); });
            }
    }
    SECTION("move assignment")
    {
        wolf::legacy_archive moved = std::move(*reader);
        auto other = wolf::legacy_archive::open(source, "owned.wolf");
        *other = std::move(moved);
        REQUIRE(other->version() == 5);
        REQUIRE(other->entries().size() == 3);
    }
}

TEST_CASE("a one-shot legacy source fault cannot be hidden by another key candidate", "[recovery-legacy-regression]")
{
    const auto bytes = fixtures::load_bytes("default-mode-0.wolf");
    fault_source buffer(bytes);
    buffer.on_read = [first = true]() mutable {
        if (std::exchange(first, false))
            throw caller_error{}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
    };
    std::istream source(&buffer);
    expect_failure<wolf::read_error>([&] { wolf::legacy_archive::open(source, "owned.wolf"); });
    // A failed open borrows the stream; its one-shot source remains usable by the caller.
    source.seekg(0);
    char first_byte = 0;
    source.read(&first_byte, 1);
    REQUIRE(source.good());
    REQUIRE(static_cast<unsigned char>(first_byte) == std::to_integer<unsigned char>(bytes.front()));
}

namespace
{
    // Public vendor structures freeze the independent DX1..5 layout; no reader internals are used.
    std::vector<std::byte> flat_fixture(unsigned version, const std::string &body, std::uint32_t unpacked,
                                        bool compressed)
    {
        DARC_HEAD_VER5 header{};
        header.Head = 0x5844;
        header.Version = static_cast<u16>(version);
        const std::size_t header_size = version >= 4 ? sizeof(header) : sizeof(DARC_HEAD_VER3);
        const std::size_t file_size = version >= 2 ? sizeof(DARC_FILEHEAD_VER5) : sizeof(DARC_FILEHEAD_VER1);
        header.HeadSize = static_cast<u32>(12 + file_size + sizeof(DARC_DIRECTORY_VER5));
        header.DataStartAddress = static_cast<u32>(header_size);
        header.FileNameTableStartAddress = static_cast<u32>(header_size + body.size());
        header.FileTableStartAddress = 12;
        header.DirectoryTableStartAddress = static_cast<u32>(12 + file_size);
        std::vector<std::byte> bytes(header.FileNameTableStartAddress + header.HeadSize);
        std::memcpy(bytes.data(), &header, header_size);
        std::memcpy(bytes.data() + header_size, body.data(), body.size());
        const auto meta = header.FileNameTableStartAddress;
        bytes[meta] = std::byte{1};
        bytes[meta + 2] = std::byte{'b'};
        bytes[meta + 4] = std::byte{'B'};
        bytes[meta + 8] = std::byte{'b'};
        DARC_FILEHEAD_VER5 file{};
        file.DataSize = unpacked;
        file.PressDataSize = compressed ? static_cast<u32>(body.size()) : UINT32_MAX;
        std::memcpy(bytes.data() + meta + 12, &file, file_size);
        DARC_DIRECTORY_VER5 directory{UINT32_MAX, UINT32_MAX, 1, 0};
        std::memcpy(bytes.data() + meta + 12 + file_size, &directory, sizeof(directory));
        std::array<unsigned char, 12> key{};
        DXArchive_VER5::KeyCreate("secret", key.data());
        DXArchive_VER5::KeyConv(bytes.data(), static_cast<int>(header_size), 0, key.data());
        DXArchive_VER5::KeyConv(bytes.data() + meta, static_cast<int>(header.HeadSize),
                                version >= 5 ? 0 : static_cast<int>(meta % 12), key.data());
        DXArchive_VER5::KeyConv(bytes.data() + header_size, static_cast<int>(body.size()),
                                static_cast<int>((version >= 5 ? unpacked : header_size) % 12), key.data());
        return bytes;
    }
    class throwing_output final : public std::streambuf
    {
        std::streamsize xsputn(const char *, std::streamsize) override
        {
            throw caller_error{}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
        }
    };
} // namespace
TEST_CASE("the public legacy reader covers every DX1..6 geometry and empty body", "[recovery-legacy]")
{
    for (unsigned version = 1; version <= 5; ++version)
        for (const std::string &payload : {std::string{}, std::string("Payload"), std::string(70000, 'q')}) {
            auto stream = input(flat_fixture(version, payload, static_cast<std::uint32_t>(payload.size()), false));
            auto reader = wolf::legacy_archive::open(stream, "owned.wolf", "secret");
            REQUIRE(reader->version() == version);
            REQUIRE(reader->entries().size() == 1);
            REQUIRE(reader->entries()[0].path == "b");
            std::ostringstream out;
            std::int64_t progress = 0;
            reader->extract(0, stream, out, [&](std::int64_t n) { progress += n; });
            REQUIRE(out.str() == payload);
            REQUIRE(progress == static_cast<std::int64_t>(payload.size()));
        }
}
TEST_CASE("compressed legacy corruption and caller failures keep their public verdict", "[recovery-legacy]")
{
    std::string literal(16, '\0');
    literal[0] = 7;
    literal[4] = 16;
    literal[8] = static_cast<char>(0xff);
    literal.replace(9, 7, "Payload");
    for (const bool compressed : {false, true}) {
        const auto bytes = flat_fixture(5, compressed ? literal : "Payload", 7, compressed);
        auto stream = input(bytes);
        auto reader = wolf::legacy_archive::open(stream, "owned.wolf", "secret");
        std::ostringstream out;
        reader->extract(0, stream, out, ignore_progress);
        REQUIRE(out.str() == "Payload");
        throwing_output b;
        std::ostream failed(&b);
        failed.exceptions(std::ios::badbit);
        expect_failure<caller_error>([&] { reader->extract(0, stream, failed, ignore_progress); });
        expect_failure<caller_error>([&] {
            reader->extract(0, stream, out, [](std::int64_t) {
                throw caller_error{}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
            });
        });
        auto truncated = input(std::vector<std::byte>(bytes.begin(), bytes.begin() + 28));
        expect_failure<wolf::read_error>([&] { reader->extract(0, truncated, out, ignore_progress); });
    }
    for (const std::string &invalid : {std::string{}, std::string("x"), std::string(16, 'x')}) {
        auto stream = input(flat_fixture(5, invalid, 7, true));
        auto reader = wolf::legacy_archive::open(stream, "owned.wolf", "secret");
        std::ostringstream out;
        expect_failure<wolf::read_error>([&] { reader->extract(0, stream, out, ignore_progress); });
    }
}

TEST_CASE("a decrypted zero-block legacy name is rejected before publishing entries", "[recovery-legacy]")
{
    auto bytes = flat_fixture(5, "Payload", 7, false);
    bytes[sizeof(DARC_HEAD_VER5) + 7] ^= std::byte{1};
    auto stream = input(bytes);
    expect_failure<wolf::read_error>([&] { wolf::legacy_archive::open(stream, "owned.wolf", "secret"); });
}

namespace
{
    class counted_legacy_source final : public std::stringbuf
    {
      public:
        counted_legacy_source() : std::stringbuf("ab", std::ios::in)
        {
        }
        std::size_t seeks = 0;
        std::size_t reads = 0;

      protected:
        pos_type seekoff(off_type offset, std::ios::seekdir direction, std::ios::openmode mode) override
        {
            ++seeks;
            return std::stringbuf::seekoff(offset, direction, mode);
        }
        std::streamsize xsgetn(char *destination, std::streamsize size) override
        {
            ++reads;
            return std::stringbuf::xsgetn(destination, size);
        }
    };
} // namespace
TEST_CASE("legacy source requests validate numeric bounds before touching borrowed storage", "[recovery-legacy]")
{
    struct request
    {
        std::int64_t offset;
        int size;
        int result;
        std::size_t seeks;
        std::size_t reads;
        bool failed;
        std::array<char, 2> destination;
    };
    const std::array requests{request{-1, -1, -1, 0, 0, false, {'!', '!'}}, request{-1, 0, -1, 0, 0, false, {'!', '!'}},
                              request{0, -1, -1, 0, 0, false, {'!', '!'}},  request{0, 0, 0, 1, 1, false, {'!', '!'}},
                              request{0, 1, 0, 1, 1, false, {'a', '!'}},    request{1, 1, 0, 1, 1, false, {'b', '!'}}};
    for (const auto &request : requests) {
        counted_legacy_source storage;
        wolf::detail::legacy_stream_source source{storage};
        std::array<char, 2> destination{'!', '!'};
        REQUIRE(wolf::detail::read_legacy_source(source, request.offset, destination.data(), request.size) ==
                request.result);
        REQUIRE(source.source_failed == request.failed);
        REQUIRE(storage.seeks == request.seeks);
        REQUIRE(storage.reads == request.reads);
        REQUIRE(destination == request.destination);
        REQUIRE(storage.str() == "ab");
    }
}
