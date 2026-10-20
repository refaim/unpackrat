// Behavioral tests for unpackrat::wolfrpg::codec::lz_reader, the bounded DXLib/WolfRPG LZ pull
// stream. The encoded byte layouts below are hand-authored from the DXLib Decode() token format
// (see licenses/uberwolf.txt, "3rdParty/DXLib") rather than emitted by a matching encoder, so the
// reader is checked against independent fixtures with known expected output.

#include "../../core/wolfrpg/lz_reader.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <span>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

namespace
{
    using unpackrat::wolfrpg::codec::lz_error;
    using unpackrat::wolfrpg::codec::lz_reader;

    using byte_vector = std::vector<std::byte>;

    byte_vector bytes(std::initializer_list<unsigned> values)
    {
        byte_vector out;
        out.reserve(values.size());
        for (const unsigned value : values) {
            out.push_back(static_cast<std::byte>(value));
        }
        return out;
    }

    // Deterministic sequential source that never over-delivers and can be limited to short
    // positive reads so the reader's refill path is exercised.
    class slice_source
    {
      public:
        explicit slice_source(byte_vector data, const std::size_t max_chunk = std::numeric_limits<std::size_t>::max())
            : data_(std::move(data)), max_chunk_(max_chunk)
        {
        }

        std::size_t operator()(std::span<std::byte> destination)
        {
            const std::size_t remaining = data_.size() - position_;
            const std::size_t count = std::min({destination.size(), remaining, max_chunk_});
            if (count != 0) {
                std::memcpy(destination.data(), data_.data() + position_, count);
            }
            position_ += count;
            return count;
        }

      private:
        byte_vector data_;
        std::size_t position_ = 0;
        std::size_t max_chunk_ = 0;
    };

    // Source that also asserts the reader never asks for more than the remaining encoded size.
    class guarded_source
    {
      public:
        guarded_source(byte_vector data, const std::uint64_t encoded_size)
            : data_(std::move(data)), encoded_size_(encoded_size)
        {
        }

        std::size_t operator()(std::span<std::byte> destination)
        {
            CHECK(destination.size() <= encoded_size_ - fetched_);
            const std::size_t count = std::min(destination.size(), data_.size() - position_);
            std::memcpy(destination.data(), data_.data() + position_, count);
            position_ += count;
            fetched_ += count;
            return count;
        }

      private:
        byte_vector data_;
        std::uint64_t encoded_size_ = 0;
        std::uint64_t fetched_ = 0;
        std::size_t position_ = 0;
    };

    // Misbehaving source that claims one byte more than the span it was handed.
    class overfill_source
    {
      public:
        explicit overfill_source(byte_vector data) : data_(std::move(data))
        {
        }

        std::size_t operator()(std::span<std::byte> destination)
        {
            const std::size_t count = std::min(destination.size(), data_.size() - position_);
            std::memcpy(destination.data(), data_.data() + position_, count);
            position_ += count;
            return destination.size() + 1;
        }

      private:
        byte_vector data_;
        std::size_t position_ = 0;
    };

    struct source_probe_error
    {
        int tag;
    };

    // Source that delivers the 9-byte header and then throws a non-standard exception.
    class throwing_source
    {
      public:
        explicit throwing_source(byte_vector data) : data_(std::move(data))
        {
        }

        std::size_t operator()(std::span<std::byte> destination)
        {
            if (position_ >= limit_) {
                // NOLINTNEXTLINE(bugprone-std-exception-baseclass): intentional non-std source fault
                throw source_probe_error{7};
            }
            const std::size_t count = std::min({destination.size(), limit_ - position_, data_.size() - position_});
            std::memcpy(destination.data(), data_.data() + position_, count);
            position_ += count;
            return count;
        }

      private:
        byte_vector data_;
        std::size_t position_ = 0;
        std::size_t limit_ = 9;
    };

    byte_vector read_exact(lz_reader &reader, const std::size_t total)
    {
        byte_vector out(total);
        std::size_t position = 0;
        while (position < total) {
            const std::size_t produced = reader.read(std::span<std::byte>(out).subspan(position));
            if (produced == 0) {
                break;
            }
            position += produced;
        }
        out.resize(position);
        return out;
    }

    std::string ascii(const byte_vector &data)
    {
        std::string text;
        text.reserve(data.size());
        for (const std::byte value : data) {
            text.push_back(static_cast<char>(std::to_integer<unsigned char>(value)));
        }
        return text;
    }

    lz_reader make_reader(const byte_vector &data, const std::uint64_t encoded_size, const std::uint64_t expected_size,
                          const std::size_t max_chunk = std::numeric_limits<std::size_t>::max())
    {
        return lz_reader(slice_source(data, max_chunk), encoded_size, expected_size);
    }

    void decode_truncated_stream(byte_vector prefix, const std::uint64_t encoded_size)
    {
        lz_reader reader(slice_source(std::move(prefix)), encoded_size, 10);
        byte_vector out(10);
        while (reader.read(std::span<std::byte>(out)) != 0) {
        }
    }

    TEST_CASE("lz_reader decodes literal bytes verbatim")
    {
        // decoded 5, encoded 14, key 0xFF, five plain literals
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        lz_reader reader = make_reader(stream, 14, 5);
        REQUIRE(ascii(read_exact(reader, 5)) == "Hello");
        REQUIRE(reader.remaining() == 0);

        std::array<std::byte, 1> extra{};
        REQUIRE(reader.read(std::span<std::byte>(extra)) == 0);
    }

    TEST_CASE("lz_reader decodes an escaped key byte")
    {
        // decoded 3, encoded 13, key 0xFF: literal 'A', escaped key, literal 'B'
        const byte_vector stream =
            bytes({0x03, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0xFF, 0x42});
        lz_reader reader = make_reader(stream, 13, 3);
        const byte_vector out = read_exact(reader, 3);
        REQUIRE(out == bytes({0x41, 0xFF, 0x42}));
        REQUIRE(reader.remaining() == 0);
    }

    TEST_CASE("lz_reader decodes a distance-one run")
    {
        // decoded 10, encoded 13, key 0xFF: 'A' then width-0 backref length 9 distance 1
        const byte_vector stream =
            bytes({0x0A, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x28, 0x00});
        lz_reader reader = make_reader(stream, 13, 10);
        REQUIRE(ascii(read_exact(reader, 10)) == "AAAAAAAAAA");
    }

    TEST_CASE("lz_reader decodes a two-byte distance")
    {
        // decoded 8, encoded 17, key 0xFF: "ABCD" then width-1 backref length 4 distance 4
        const byte_vector stream = bytes(
            {0x08, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0xFF, 0x41, 0x42, 0x43, 0x44, 0xFF, 0x01, 0x03, 0x00});
        lz_reader reader = make_reader(stream, 17, 8);
        REQUIRE(ascii(read_exact(reader, 8)) == "ABCDABCD");
    }

    TEST_CASE("lz_reader decodes a three-byte distance")
    {
        // decoded 5, encoded 15, key 0xFF: 'X' then width-2 backref length 4 distance 1
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0xFF, 0x58, 0xFF, 0x02, 0x00, 0x00, 0x00});
        lz_reader reader = make_reader(stream, 15, 5);
        REQUIRE(ascii(read_exact(reader, 5)) == "XXXXX");
    }

    TEST_CASE("lz_reader decodes an extended length")
    {
        // decoded 41, encoded 14, key 0xFF: 'A' then width-0 backref length 40 distance 1
        const byte_vector stream =
            bytes({0x29, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x24, 0x01, 0x00});
        lz_reader reader = make_reader(stream, 14, 41);
        const byte_vector out = read_exact(reader, 41);
        REQUIRE(out.size() == 41);
        REQUIRE(std::all_of(out.begin(), out.end(), [](const std::byte value) { return value == std::byte{0x41}; }));
    }

    TEST_CASE("lz_reader handles the stored-code collision above the key")
    {
        // decoded 41, encoded 14, key 0x05: the code byte 0x24 was stored as 0x25 because it is
        // greater than the key, and must be decremented back to 0x24 before decoding.
        const byte_vector stream =
            bytes({0x29, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x05, 0x41, 0x05, 0x25, 0x01, 0x00});
        lz_reader reader = make_reader(stream, 14, 41);
        const byte_vector out = read_exact(reader, 41);
        REQUIRE(out.size() == 41);
        REQUIRE(std::all_of(out.begin(), out.end(), [](const std::byte value) { return value == std::byte{0x41}; }));
    }

    TEST_CASE("lz_reader decodes an overlapping distance-two copy")
    {
        // decoded 10, encoded 17, key 0xFF: "ABCD" then width-1 backref length 6 distance 2
        const byte_vector stream = bytes(
            {0x0A, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0xFF, 0x41, 0x42, 0x43, 0x44, 0xFF, 0x11, 0x01, 0x00});
        lz_reader reader = make_reader(stream, 17, 10);
        REQUIRE(ascii(read_exact(reader, 10)) == "ABCDCDCDCD");
    }

    TEST_CASE("lz_reader accepts a zero-output stream")
    {
        // decoded 0, encoded 9, key 0x00: header only
        const byte_vector stream = bytes({0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00});
        lz_reader reader = make_reader(stream, 9, 0);
        REQUIRE(reader.remaining() == 0);
        std::array<std::byte, 1> out{};
        REQUIRE(reader.read(std::span<std::byte>(out)) == 0);
        REQUIRE(reader.read(std::span<std::byte>(out)) == 0);
    }

    TEST_CASE("lz_reader keeps state across arbitrary output chunks")
    {
        const byte_vector stream =
            bytes({0x0A, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x28, 0x00});
        lz_reader reader = make_reader(stream, 13, 10);
        std::string text;
        std::array<std::byte, 3> chunk{};
        while (true) {
            const std::size_t produced = reader.read(std::span<std::byte>(chunk));
            if (produced == 0) {
                break;
            }
            for (std::size_t index = 0; index < produced; ++index) {
                text.push_back(static_cast<char>(std::to_integer<unsigned char>(chunk[index])));
            }
        }
        REQUIRE(text == "AAAAAAAAAA");
        REQUIRE(reader.remaining() == 0);
    }

    TEST_CASE("lz_reader continues a pending back-reference across read calls")
    {
        const byte_vector stream =
            bytes({0x0A, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x28, 0x00});
        lz_reader reader = make_reader(stream, 13, 10);
        std::string text;
        std::array<std::byte, 1> chunk{};
        while (reader.read(std::span<std::byte>(chunk)) == 1) {
            text.push_back(static_cast<char>(std::to_integer<unsigned char>(chunk[0])));
        }
        REQUIRE(text == "AAAAAAAAAA");
    }

    TEST_CASE("lz_reader rejects a reserved distance width")
    {
        // key 0xFF, code byte 0x03 selects the reserved width 3
        const byte_vector stream = bytes({0x04, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00});
        lz_reader reader = make_reader(stream, 12, 4);
        REQUIRE_THROWS_AS(read_exact(reader, 4), lz_error);
    }

    TEST_CASE("lz_reader rejects a back-reference before the output start")
    {
        // decoded 5, encoded 13, key 0xFF: 'A' then backref distance 2 while only 1 byte exists
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x00, 0x01});
        lz_reader reader = make_reader(stream, 13, 5);
        REQUIRE_THROWS_AS(read_exact(reader, 5), lz_error);
    }

    TEST_CASE("lz_reader rejects a back-reference overrunning the declared output")
    {
        // decoded 3, encoded 13, key 0xFF: 'A' then backref length 4 with only 2 bytes remaining
        const byte_vector stream =
            bytes({0x03, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x00, 0x00});
        lz_reader reader = make_reader(stream, 13, 3);
        REQUIRE_THROWS_AS(read_exact(reader, 3), lz_error);
    }

    TEST_CASE("lz_reader rejects a stream that ends before the declared output")
    {
        // decoded 5, encoded 11, key 0xFF: only "AB" is available
        const byte_vector stream = bytes({0x05, 0x00, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0xFF, 0x41, 0x42});
        lz_reader reader = make_reader(stream, 11, 5);
        REQUIRE_THROWS_AS(read_exact(reader, 5), lz_error);
    }

    TEST_CASE("lz_reader rejects trailing encoded bytes")
    {
        // decoded 3 but four literal bytes are present
        const byte_vector stream =
            bytes({0x03, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0x42, 0x43, 0x44});
        lz_reader reader = make_reader(stream, 13, 3);
        REQUIRE_THROWS_AS(read_exact(reader, 3), lz_error);
    }

    TEST_CASE("lz_reader rejects a declared output size mismatch")
    {
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        REQUIRE_THROWS_AS(make_reader(stream, 14, 6), lz_error);
    }

    TEST_CASE("lz_reader rejects a declared encoded size mismatch")
    {
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        REQUIRE_THROWS_AS(make_reader(stream, 13, 5), lz_error);
    }

    TEST_CASE("lz_reader rejects an output size above the 32-bit format limit")
    {
        const byte_vector stream = bytes({0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00});
        REQUIRE_THROWS_AS(make_reader(stream, 9, 0x1'0000'0000ull), lz_error);
    }

    TEST_CASE("lz_reader rejects an encoded size below the header size")
    {
        REQUIRE_THROWS_AS(make_reader(bytes({}), 8, 0), lz_error);
    }

    TEST_CASE("lz_reader rejects every truncated header and token boundary")
    {
        // decoded 10, encoded 13, key 0xFF: 'A' then a width-0 backref
        const byte_vector stream =
            bytes({0x0A, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x28, 0x00});
        for (std::size_t length = 0; length < stream.size(); ++length) {
            byte_vector prefix(stream.begin(), stream.begin() + static_cast<std::ptrdiff_t>(length));
            REQUIRE_THROWS_AS(decode_truncated_stream(std::move(prefix), stream.size()), lz_error);
        }
    }

    TEST_CASE("lz_reader tolerates short positive reads from the source")
    {
        const byte_vector literal =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        lz_reader literal_reader = make_reader(literal, 14, 5, 1);
        REQUIRE(ascii(read_exact(literal_reader, 5)) == "Hello");

        const byte_vector run = bytes(
            {0x08, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0xFF, 0x41, 0x42, 0x43, 0x44, 0xFF, 0x01, 0x03, 0x00});
        lz_reader run_reader = make_reader(run, 17, 8, 1);
        REQUIRE(ascii(read_exact(run_reader, 8)) == "ABCDABCD");
    }

    TEST_CASE("lz_reader propagates a source exception unchanged")
    {
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        try {
            lz_reader reader(throwing_source(stream), 14, 5);
            std::array<std::byte, 5> out{};
            (void)reader.read(std::span<std::byte>(out));
            FAIL("expected the source exception to propagate");
        } catch (const source_probe_error &error) {
            REQUIRE(error.tag == 7);
        } catch (...) {
            FAIL("unexpected exception type escaped the reader");
        }
    }

    TEST_CASE("lz_reader rejects a source that returns more than requested")
    {
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        REQUIRE_THROWS_AS(lz_reader(overfill_source(stream), 14, 5), lz_error);
    }

    TEST_CASE("lz_reader never requests more than the remaining encoded size")
    {
        // decoded 70, encoded 79, key 0xFF, seventy plain literals so the source is refilled twice
        byte_vector stream = bytes({0x46, 0x00, 0x00, 0x00, 0x4F, 0x00, 0x00, 0x00, 0xFF});
        for (unsigned index = 0; index < 70u; ++index) {
            stream.push_back(std::byte{0x41});
        }
        lz_reader reader(guarded_source(stream, 79), 79, 70);
        REQUIRE(ascii(read_exact(reader, 70)) == std::string(70, 'A'));
        REQUIRE(reader.remaining() == 0);
    }

    TEST_CASE("lz_reader returns zero for an empty output span without consuming input")
    {
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        lz_reader reader = make_reader(stream, 14, 5);
        REQUIRE(reader.remaining() == 5);
        REQUIRE(reader.read(std::span<std::byte>()) == 0);
        REQUIRE(reader.remaining() == 5);
        REQUIRE(ascii(read_exact(reader, 5)) == "Hello");
        REQUIRE(reader.read(std::span<std::byte>()) == 0);
    }

    TEST_CASE("lz_reader keeps independent readers interleaved")
    {
        const byte_vector literal =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        const byte_vector run = bytes({0x0A, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x28, 0x00});
        lz_reader first = make_reader(literal, 14, 5);
        lz_reader second = make_reader(run, 13, 10);

        std::string first_text;
        std::string second_text;
        std::array<std::byte, 1> chunk{};
        for (unsigned index = 0; index < 5u; ++index) {
            REQUIRE(first.read(std::span<std::byte>(chunk)) == 1);
            first_text.push_back(static_cast<char>(std::to_integer<unsigned char>(chunk[0])));
            REQUIRE(second.read(std::span<std::byte>(chunk)) == 1);
            second_text.push_back(static_cast<char>(std::to_integer<unsigned char>(chunk[0])));
        }
        REQUIRE(first.read(std::span<std::byte>(chunk)) == 0);
        REQUIRE(second.read(std::span<std::byte>(chunk)) == 1);

        REQUIRE(first_text == "Hello");
        REQUIRE(second_text == "AAAAA");
    }

    TEST_CASE("lz_reader is movable mid-stream")
    {
        const byte_vector run = bytes({0x0A, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x28, 0x00});
        lz_reader original = make_reader(run, 13, 10);
        std::array<std::byte, 2> head{};
        REQUIRE(original.read(std::span<std::byte>(head)) == 2);
        REQUIRE(ascii(byte_vector(head.begin(), head.end())) == "AA");

        lz_reader moved(std::move(original));
        REQUIRE(ascii(read_exact(moved, 4)) == "AAAA");
        REQUIRE(moved.remaining() == 4);

        const byte_vector literal =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        lz_reader target = make_reader(literal, 14, 5);
        target = std::move(moved);
        REQUIRE(ascii(read_exact(target, 4)) == "AAAA");
        REQUIRE(target.remaining() == 0);
    }

    TEST_CASE("lz_reader decodes a large wrapped stream with a maximum-distance copy")
    {
        constexpr std::uint64_t distance = 16ull * 1024ull * 1024ull; // 16777216
        constexpr std::uint64_t produced_before_copy = distance + 4;  // 16777220
        constexpr std::uint64_t decoded_size = produced_before_copy + 4;
        constexpr unsigned key = 0xFF;
        constexpr std::array<unsigned, 3> pattern{0x41, 0x42, 0x43};

        // Produce the period-3 pattern "ABCABC..." with width-0 backrefs of distance 3, then a
        // final width-2 backref whose distance is the maximum the 24-bit field can express.
        byte_vector tokens = bytes({0x41, 0x42, 0x43});
        std::uint64_t produced = 3;
        while (produced < produced_before_copy) {
            const std::uint64_t length = std::min<std::uint64_t>(produced_before_copy - produced, 0x1FFF + 4);
            const std::uint32_t maxconbo = static_cast<std::uint32_t>(length - 4);
            const bool extended = maxconbo >= 0x20;
            const std::uint32_t code = ((maxconbo & 0x1Fu) << 3u) | (extended ? 0x4u : 0u);
            tokens.push_back(std::byte{key});
            tokens.push_back(static_cast<std::byte>(code));
            if (extended) {
                tokens.push_back(static_cast<std::byte>((maxconbo >> 5u) & 0xFFu));
            }
            tokens.push_back(static_cast<std::byte>(0x02)); // distance 3 -> raw 2
            produced += length;
        }
        REQUIRE(produced == produced_before_copy);

        tokens.push_back(std::byte{key});
        tokens.push_back(static_cast<std::byte>(0x02)); // length 4, width 2
        tokens.push_back(static_cast<std::byte>(0xFF));
        tokens.push_back(static_cast<std::byte>(0xFF));
        tokens.push_back(static_cast<std::byte>(0xFF)); // raw 0xFFFFFF -> distance 16 MiB

        byte_vector stream(9);
        const std::uint32_t encoded_size = static_cast<std::uint32_t>(9 + tokens.size());
        stream[0] = static_cast<std::byte>(decoded_size & 0xFFu);
        stream[1] = static_cast<std::byte>((decoded_size >> 8u) & 0xFFu);
        stream[2] = static_cast<std::byte>((decoded_size >> 16u) & 0xFFu);
        stream[3] = static_cast<std::byte>((decoded_size >> 24u) & 0xFFu);
        stream[4] = static_cast<std::byte>(encoded_size & 0xFFu);
        stream[5] = static_cast<std::byte>((encoded_size >> 8u) & 0xFFu);
        stream[6] = static_cast<std::byte>((encoded_size >> 16u) & 0xFFu);
        stream[7] = static_cast<std::byte>((encoded_size >> 24u) & 0xFFu);
        stream[8] = std::byte{key};
        stream.insert(stream.end(), tokens.begin(), tokens.end());

        lz_reader reader = make_reader(stream, encoded_size, decoded_size);
        std::vector<std::byte> chunk(std::size_t{64} * 1024);
        std::uint64_t position = 0;
        std::uint64_t mismatches = 0;
        while (position < decoded_size) {
            const std::size_t want =
                static_cast<std::size_t>(std::min<std::uint64_t>(chunk.size(), decoded_size - position));
            const std::size_t got = reader.read(std::span<std::byte>(chunk).first(want));
            REQUIRE(got != 0);
            for (std::size_t index = 0; index < got; ++index) {
                const std::uint64_t index_position = position + index;
                const std::uint64_t source_position =
                    index_position < produced_before_copy ? index_position : index_position - distance;
                const unsigned expected = pattern[source_position % 3];
                if (std::to_integer<unsigned>(chunk[index]) != expected) {
                    ++mismatches;
                }
            }
            position += got;
        }
        REQUIRE(mismatches == 0);
        REQUIRE(reader.remaining() == 0);
    }

    TEST_CASE("lz_reader accepts a zero-output stream of exactly the header")
    {
        // decoded 0, encoded 9, key 0x00: header only. The block is validated and finished at
        // construction, so the natural while (remaining()) consumer observes a complete stream.
        const byte_vector stream = bytes({0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00});
        lz_reader reader = make_reader(stream, 9, 0);
        REQUIRE(reader.remaining() == 0);
    }

    TEST_CASE("lz_reader rejects a zero-output stream with trailing encoded bytes at construction")
    {
        // decoded 0 but encoded 10: the header plus one stray byte that a pull consumer never reads
        // because remaining() is already zero, so construction itself must reject the malformation.
        const byte_vector stream = bytes({0x00, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00});
        REQUIRE_THROWS_AS(make_reader(stream, 10, 0), lz_error);
    }

    TEST_CASE("lz_reader move construction leaves the source as a safe empty reader")
    {
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        lz_reader source = make_reader(stream, 14, 5);
        lz_reader moved(std::move(source));

        // The moved-from source must behave like an exhausted reader instead of dereferencing a
        // null state: remaining() is zero and reads return zero for empty and non-empty spans.
        // This intentionally exercises the documented moved-from empty-reader contract.
#pragma warning(suppress : 26800)
        REQUIRE(source.remaining() == 0); // NOLINT(bugprone-use-after-move, clang-analyzer-cplusplus.Move)
        std::array<std::byte, 1> probe{};
        REQUIRE(source.read(std::span<std::byte>(probe)) == 0);
        REQUIRE(source.read(std::span<std::byte>()) == 0);

        // The receiving reader keeps its normal decoding behavior.
        REQUIRE(ascii(read_exact(moved, 5)) == "Hello");

        // The moved-from source can be reassigned a fresh reader and decode again.
        source = make_reader(stream, 14, 5);
        REQUIRE(ascii(read_exact(source, 5)) == "Hello");
    }

    TEST_CASE("lz_reader move assignment leaves the source as a safe empty reader")
    {
        const byte_vector run = bytes({0x0A, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x28, 0x00});
        const byte_vector literal =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        lz_reader source = make_reader(run, 13, 10);
        lz_reader target = make_reader(literal, 14, 5);

        target = std::move(source);

        // Assignment must transfer the state and leave the source as a safe empty reader.
        // This intentionally exercises the documented moved-from empty-reader contract.
#pragma warning(suppress : 26800)
        REQUIRE(source.remaining() == 0); // NOLINT(bugprone-use-after-move, clang-analyzer-cplusplus.Move)
        std::array<std::byte, 1> probe{};
        REQUIRE(source.read(std::span<std::byte>(probe)) == 0);
        REQUIRE(source.read(std::span<std::byte>()) == 0);

        // The assignment target keeps its normal decoding behavior.
        REQUIRE(ascii(read_exact(target, 10)) == "AAAAAAAAAA");

        // The moved-from source can be reassigned a fresh reader and decode again.
        source = make_reader(literal, 14, 5);
        REQUIRE(ascii(read_exact(source, 5)) == "Hello");
    }

    TEST_CASE("lz_error default construction carries the documented diagnostic through both standard bases")
    {
        // The default constructor stands in for any lz_error raised without an explicit message, so
        // its text must be the stable public diagnostic and the type must stay catchable as both
        // std::runtime_error and std::exception.
        REQUIRE(std::string(lz_error().what()) == "wolfrpg lz stream error");

        bool caught_as_runtime_error = false;
        try {
            throw lz_error();
        } catch (const std::runtime_error &error) {
            caught_as_runtime_error = true;
            REQUIRE(std::string(error.what()) == "wolfrpg lz stream error");
            REQUIRE(dynamic_cast<const lz_error *>(&error) != nullptr);
        } catch (...) {
            FAIL("a default lz_error must be catchable as std::runtime_error");
        }
        REQUIRE(caught_as_runtime_error);

        bool caught_as_exception = false;
        try {
            throw lz_error();
        } catch (const std::exception &error) {
            caught_as_exception = true;
            REQUIRE(std::string(error.what()) == "wolfrpg lz stream error");
            REQUIRE(dynamic_cast<const lz_error *>(&error) != nullptr);
        } catch (...) {
            FAIL("a default lz_error must be catchable as std::exception");
        }
        REQUIRE(caught_as_exception);
    }
} // namespace
