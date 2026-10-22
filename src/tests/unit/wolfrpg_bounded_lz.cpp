// Behavioral tests for the shared UberWolf LZ kernel (wolf::archive::boundedlz) that both
// DXArchive::Decode and unpackrat::wolfrpg::codec::lz_reader drive. The encoded byte layouts are
// hand-authored from the pinned DXLib Decode() token grammar (see licenses/uberwolf.txt,
// "3rdParty/DXLib") so the kernel is checked against independent fixtures with known output.

#include "../../core/wolfrpg/lz_reader.h"

#include <uberwolf/Archive/BoundedLz.hpp>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include <catch2/catch_test_macros.hpp>

namespace
{
    namespace boundedlz = wolf::archive::boundedlz;
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

    std::string ascii(const byte_vector &data, const std::size_t count)
    {
        std::string text;
        text.reserve(count);
        for (std::size_t index = 0; index < count; ++index) {
            text.push_back(static_cast<char>(std::to_integer<unsigned char>(data[index])));
        }
        return text;
    }

    // Contiguous output sink mirroring the DXArchive::Decode compatibility path: the produced
    // buffer is its own history.
    struct output_sink
    {
        byte_vector data;
        std::uint64_t position = 0;

        static std::byte read(void *context, const std::uint64_t index) noexcept
        {
            return static_cast<output_sink *>(context)->data[static_cast<std::size_t>(index)];
        }

        static void write(void *context, const std::byte value) noexcept
        {
            auto *self = static_cast<output_sink *>(context);
            self->data[static_cast<std::size_t>(self->position)] = value;
            ++self->position;
        }

        [[nodiscard]] boundedlz::emitter emitter() noexcept
        {
            return {&output_sink::read, &output_sink::write, this};
        }
    };

    // Returns at most max_chunk bytes per call so short positive fills are exercised.
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

    // Claims one byte more than the span it was handed.
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

    // Delivers up to ``limit`` bytes from the stream and then throws the vendor kernel error type
    // with a distinct sentinel message. This is how the tests prove that the lz_reader adapter
    // never mistakes an exception raised by the caller's own source for a kernel format failure,
    // in either the constructor's header read or a later payload pull.
    class kernel_error_source
    {
      public:
        kernel_error_source(byte_vector data, const std::size_t limit, std::string message)
            : data_(std::move(data)), limit_(limit), message_(std::move(message))
        {
        }

        std::size_t operator()(std::span<std::byte> destination)
        {
            if (position_ >= limit_) {
                throw boundedlz::error(message_);
            }
            const std::size_t count = std::min({destination.size(), limit_ - position_, data_.size() - position_});
            std::memcpy(destination.data(), data_.data() + position_, count);
            position_ += count;
            return count;
        }

      private:
        byte_vector data_;
        std::size_t position_ = 0;
        std::size_t limit_ = 0;
        std::string message_;
    };

    // Delivers the 9-byte header and then throws a non-kernel exception.
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
                throw source_probe_error{11};
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

    // Delivers only the 9-byte header on its first call, throws the caller's own exception exactly
    // once on the next call, then delivers the remaining payload on the retry. This reproduces a
    // transient source failure immediately followed by a genuine in-stream format defect, so the
    // adapter must not keep blaming the source once a later pull succeeds.
    class one_shot_fault_source
    {
      public:
        explicit one_shot_fault_source(byte_vector data) : data_(std::move(data))
        {
        }

        std::size_t operator()(std::span<std::byte> destination)
        {
            ++calls_;
            if (calls_ == 1) {
                const std::size_t count = std::min<std::size_t>(destination.size(), 9);
                std::memcpy(destination.data(), data_.data(), count);
                position_ = count;
                return count;
            }
            if (calls_ == 2) {
                // NOLINTNEXTLINE(bugprone-std-exception-baseclass): intentional non-std source fault
                throw source_probe_error{11};
            }
            const std::size_t remaining = data_.size() - position_;
            const std::size_t count = std::min(destination.size(), remaining);
            if (count != 0) {
                std::memcpy(destination.data(), data_.data() + position_, count);
            }
            position_ += count;
            return count;
        }

      private:
        byte_vector data_;
        std::size_t position_ = 0;
        int calls_ = 0;
    };

    byte_vector decode(const byte_vector &stream, const std::uint64_t encoded, const std::uint64_t decoded,
                       const std::size_t chunk = std::numeric_limits<std::size_t>::max())
    {
        output_sink sink;
        sink.data.resize(decoded == 0 ? 1 : static_cast<std::size_t>(decoded));
        boundedlz::context ctx(slice_source(stream), encoded, decoded);
        while (ctx.produce(chunk, sink.emitter()) != 0) {
        }
        sink.data.resize(static_cast<std::size_t>(sink.position));
        return sink.data;
    }

    TEST_CASE("boundedlz kernel decodes literal bytes verbatim")
    {
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        REQUIRE(ascii(decode(stream, 14, 5), 5) == "Hello");
    }

    TEST_CASE("boundedlz kernel decodes an escaped key byte")
    {
        const byte_vector stream =
            bytes({0x03, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0xFF, 0x42});
        REQUIRE(decode(stream, 13, 3) == bytes({0x41, 0xFF, 0x42}));
    }

    TEST_CASE("boundedlz kernel decodes a distance-one run")
    {
        const byte_vector stream =
            bytes({0x0A, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x28, 0x00});
        REQUIRE(ascii(decode(stream, 13, 10), 10) == "AAAAAAAAAA");
    }

    TEST_CASE("boundedlz kernel decodes an overlapping distance-two copy")
    {
        const byte_vector stream = bytes(
            {0x0A, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0xFF, 0x41, 0x42, 0x43, 0x44, 0xFF, 0x11, 0x01, 0x00});
        REQUIRE(ascii(decode(stream, 17, 10), 10) == "ABCDCDCDCD");
    }

    TEST_CASE("boundedlz kernel decodes each distance width and an extended length")
    {
        const byte_vector two_byte = bytes(
            {0x08, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0xFF, 0x41, 0x42, 0x43, 0x44, 0xFF, 0x01, 0x03, 0x00});
        REQUIRE(ascii(decode(two_byte, 17, 8), 8) == "ABCDABCD");

        const byte_vector three_byte =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0xFF, 0x58, 0xFF, 0x02, 0x00, 0x00, 0x00});
        REQUIRE(ascii(decode(three_byte, 15, 5), 5) == "XXXXX");

        const byte_vector extended =
            bytes({0x29, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x24, 0x01, 0x00});
        const byte_vector out = decode(extended, 14, 41);
        REQUIRE(out.size() == 41);
        REQUIRE(std::all_of(out.begin(), out.end(), [](const std::byte value) { return value == std::byte{0x41}; }));
    }

    TEST_CASE("boundedlz kernel handles the stored-code collision above the key")
    {
        const byte_vector stream =
            bytes({0x29, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x05, 0x41, 0x05, 0x25, 0x01, 0x00});
        const byte_vector out = decode(stream, 14, 41);
        REQUIRE(out.size() == 41);
        REQUIRE(std::all_of(out.begin(), out.end(), [](const std::byte value) { return value == std::byte{0x41}; }));
    }

    TEST_CASE("boundedlz kernel keeps pending state across arbitrary chunk sizes")
    {
        const byte_vector stream =
            bytes({0x0A, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x28, 0x00});
        REQUIRE(ascii(decode(stream, 13, 10, 1), 10) == "AAAAAAAAAA");
        REQUIRE(ascii(decode(stream, 13, 10, 3), 10) == "AAAAAAAAAA");
    }

    TEST_CASE("boundedlz kernel exposes its header and supports empty and completed pulls")
    {
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        output_sink sink;
        sink.data.resize(5);
        boundedlz::context ctx(slice_source(stream), 14, 5);

        REQUIRE(ctx.encoded_extent() == 14);
        REQUIRE(ctx.decoded_extent() == 5);
        REQUIRE(ctx.key() == 0xFF);
        REQUIRE_FALSE(ctx.finished());

        // A zero-length pull makes no progress and does not complete.
        REQUIRE(ctx.produce(0, sink.emitter()) == 0);
        REQUIRE(ctx.produced() == 0);
        REQUIRE(ctx.remaining() == 5);

        REQUIRE(ctx.produce(5, sink.emitter()) == 5);
        REQUIRE(ctx.finished());

        // A completed pull still makes no progress and re-validates the consumed extent.
        REQUIRE(ctx.produce(4, sink.emitter()) == 0);
        REQUIRE(ctx.remaining() == 0);
    }

    TEST_CASE("boundedlz kernel accepts a zero-output stream of exactly the header")
    {
        const byte_vector stream = bytes({0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00});
        output_sink sink;
        sink.data.resize(1);
        boundedlz::context ctx(slice_source(stream), 9, 0);
        REQUIRE(ctx.decoded_extent() == 0);
        REQUIRE(ctx.finished());
        REQUIRE(ctx.produce(4, sink.emitter()) == 0);
    }

    TEST_CASE("boundedlz kernel rejects every structural violation with a kernel error")
    {
        // header min-9 and declared-size agreement
        REQUIRE_THROWS_AS(boundedlz::context(slice_source(bytes({})), 8, 0), boundedlz::error);
        REQUIRE_THROWS_AS(
            [&] {
                const byte_vector s =
                    bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
                boundedlz::context ctx(slice_source(s), 14, 6);
            }(),
            boundedlz::error);
        REQUIRE_THROWS_AS(
            [&] {
                const byte_vector s =
                    bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
                boundedlz::context ctx(slice_source(s), 13, 5);
            }(),
            boundedlz::error);

        // a zero-output block with trailing encoded bytes
        REQUIRE_THROWS_AS(
            [&] {
                const byte_vector s = bytes({0x00, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00});
                boundedlz::context ctx(slice_source(s), 10, 0);
            }(),
            boundedlz::error);

        // a source that over-delivers
        REQUIRE_THROWS_AS(
            [&] {
                const byte_vector s =
                    bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
                boundedlz::context ctx(overfill_source(s), 14, 5);
            }(),
            boundedlz::error);

        // a source that ends before the declared extent
        REQUIRE_THROWS_AS(
            [&] {
                const byte_vector s = bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C});
                output_sink sink;
                sink.data.resize(5);
                boundedlz::context ctx(slice_source(s), 14, 5);
                while (ctx.produce(5, sink.emitter()) != 0) {
                }
            }(),
            boundedlz::error);
    }

    TEST_CASE("boundedlz kernel rejects a reserved width and every out-of-range back-reference")
    {
        // reserved distance width 3
        REQUIRE_THROWS_AS(
            [&] {
                const byte_vector s = bytes({0x04, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00});
                output_sink sink;
                sink.data.resize(4);
                boundedlz::context ctx(slice_source(s), 12, 4);
                while (ctx.produce(4, sink.emitter()) != 0) {
                }
            }(),
            boundedlz::error);

        // back-reference before the output start
        REQUIRE_THROWS_AS(
            [&] {
                const byte_vector s =
                    bytes({0x05, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x00, 0x01});
                output_sink sink;
                sink.data.resize(5);
                boundedlz::context ctx(slice_source(s), 13, 5);
                while (ctx.produce(5, sink.emitter()) != 0) {
                }
            }(),
            boundedlz::error);

        // back-reference overrunning the declared output
        REQUIRE_THROWS_AS(
            [&] {
                const byte_vector s =
                    bytes({0x03, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x00, 0x00});
                output_sink sink;
                sink.data.resize(3);
                boundedlz::context ctx(slice_source(s), 13, 3);
                while (ctx.produce(3, sink.emitter()) != 0) {
                }
            }(),
            boundedlz::error);
    }

    TEST_CASE("boundedlz kernel rejects trailing encoded and truncated token bytes")
    {
        // trailing encoded bytes after the output is complete
        REQUIRE_THROWS_AS(
            [&] {
                const byte_vector s =
                    bytes({0x03, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0x42, 0x43, 0x44});
                output_sink sink;
                sink.data.resize(3);
                boundedlz::context ctx(slice_source(s), 13, 3);
                while (ctx.produce(3, sink.emitter()) != 0) {
                }
            }(),
            boundedlz::error);

        // a stream whose tokens end before the declared output
        REQUIRE_THROWS_AS(
            [&] {
                const byte_vector s = bytes({0x05, 0x00, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0xFF, 0x41, 0x42});
                output_sink sink;
                sink.data.resize(5);
                boundedlz::context ctx(slice_source(s), 11, 5);
                while (ctx.produce(5, sink.emitter()) != 0) {
                }
            }(),
            boundedlz::error);
    }

    TEST_CASE("boundedlz kernel lets a source exception propagate unchanged")
    {
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        try {
            output_sink sink;
            sink.data.resize(5);
            boundedlz::context ctx(throwing_source(stream), 14, 5);
            (void)ctx.produce(5, sink.emitter());
            FAIL("expected the source exception to propagate");
        } catch (const source_probe_error &error) {
            REQUIRE(error.tag == 11);
        } catch (...) {
            FAIL("an unexpected exception type escaped the kernel");
        }
    }

    TEST_CASE("lz_reader preserves a source thrown kernel error from the header read")
    {
        using unpackrat::wolfrpg::codec::lz_error;
        using unpackrat::wolfrpg::codec::lz_reader;

        // The source throws the vendor kernel error type before delivering the 9-byte header, so
        // construction fails; the adapter must surface that exact type and payload rather than
        // reclassifying it as its own lz_error.
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        try {
            const lz_reader reader(kernel_error_source(stream, 0, "source header sentinel"), 14, 5);
            (void)reader;
            FAIL("expected the source's kernel error to propagate from construction");
        } catch (const boundedlz::error &error) {
            REQUIRE(std::string(error.what()) == "source header sentinel");
            REQUIRE(dynamic_cast<const lz_error *>(&error) == nullptr);
        } catch (...) {
            FAIL("the adapter translated the source's kernel error to another type");
        }
    }

    TEST_CASE("lz_reader preserves a source thrown kernel error from a payload pull")
    {
        using unpackrat::wolfrpg::codec::lz_error;
        using unpackrat::wolfrpg::codec::lz_reader;

        // The source delivers the header and then throws the vendor kernel error type once the
        // first payload token is requested; the adapter must preserve that exact type and payload
        // through the pull path too.
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F});
        lz_reader reader(kernel_error_source(stream, 9, "source payload sentinel"), 14, 5);
        byte_vector out(5);
        try {
            (void)reader.read(std::span<std::byte>(out));
            FAIL("expected the source's kernel error to propagate from a pull");
        } catch (const boundedlz::error &error) {
            REQUIRE(std::string(error.what()) == "source payload sentinel");
            REQUIRE(dynamic_cast<const lz_error *>(&error) == nullptr);
        } catch (...) {
            FAIL("the adapter translated the source's kernel error to another type");
        }
    }

    TEST_CASE("lz_reader clears a transient source fault before a later kernel error")
    {
        using unpackrat::wolfrpg::codec::lz_error;
        using unpackrat::wolfrpg::codec::lz_reader;

        // Header: decoded 5, encoded 13, key 0xFF. The payload 41 FF 00 01 decodes a literal 'A'
        // and then a back-reference that points before the start of the output, a genuine kernel
        // format error. The source delivers the header, faults once, then delivers the payload.
        const byte_vector stream =
            bytes({0x05, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0xFF, 0x41, 0xFF, 0x00, 0x01});
        lz_reader reader(one_shot_fault_source(stream), 13, 5);
        byte_vector out(5);

        // First pull faults inside the caller's source; that exact type and payload must surface
        // rather than being reclassified as a format error.
        try {
            (void)reader.read(std::span<std::byte>(out));
            FAIL("expected the source's one-shot fault to propagate from the first pull");
        } catch (const source_probe_error &error) {
            REQUIRE(error.tag == 11);
        } catch (...) {
            FAIL("the adapter translated the source's one-shot fault to another type");
        }

        // The retry reaches the malformed token. The adapter must raise its own lz_error now and
        // must not resurrect the stale source fault recorded during the previous pull.
        try {
            (void)reader.read(std::span<std::byte>(out));
            FAIL("expected the malformed token to raise the adapter's lz_error");
        } catch (const lz_error &) {
            REQUIRE(out[0] == std::byte{0x41});
        } catch (const source_probe_error &) {
            FAIL("the adapter resurrected the stale source fault from the earlier pull");
        } catch (...) {
            FAIL("the adapter translated the kernel error to an unexpected type");
        }
    }
} // namespace
