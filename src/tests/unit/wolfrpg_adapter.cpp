#include "../../core/wolfrpg/archive.h"
#include "../../core/wolfrpg/lz_reader.h"
#include "../support/wolfrpg_fixtures.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <exception>
#include <functional>
#include <ios>
#include <optional>
#include <sstream>
#include <streambuf>
#include <string>
#include <string_view>
#include <vector>

#include <catch2/catch_test_macros.hpp>

// Reader contract tests for the classic WolfRPG / DX8 archive reader. The implementation lives in
// src/core/wolfrpg/archive.cpp (owned by a later phase); until it exists these tests fail to link,
// which is the intended red state for this phase.
namespace
{
    template <class Error> std::optional<Error> observed_exception(const std::function<void()> &operation)
    {
        try {
            operation();
        } catch (const Error &error) {
            return error;
        }
        return {};
    }
    template <class Error> void require_message(const std::function<void()> &operation, const std::string_view message)
    {
        const auto error = observed_exception<Error>(operation);
        REQUIRE(error.has_value());
        if (!error.has_value()) {
            return;
        }
        CHECK(std::string_view(error.value().what()) == message);
    }
    void require_io_failure(const std::function<void()> &operation)
    {
        const auto error = observed_exception<std::ios_base::failure>(operation);
        REQUIRE(error.has_value());
        if (!error.has_value()) {
            return;
        }
        CHECK(error.value().code() == std::make_error_code(std::io_errc::stream));
        CHECK_FALSE(std::string_view(error.value().what()).empty());
    }
    using unpackrat::wolfrpg::archive;
    using unpackrat::wolfrpg::read_error;
    using unpackrat::wolfrpg::codec::lz_error;

    // Distinct what() payloads so a forwarded lz_error can be checked by concrete type *and*
    // payload rather than by std::exception_ptr identity, which MSVC does not guarantee across
    // separate current_exception captures.
    constexpr const char *progress_lz_sentinel = "wolfrpg-progress-lz-sentinel";
    constexpr const char *output_lz_sentinel = "wolfrpg-output-lz-sentinel";

    std::istringstream to_stream(const std::vector<std::byte> &bytes)
    {
        return std::istringstream(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()),
                                  std::ios::binary);
    }

    void ignore_progress(std::int64_t) noexcept
    {
    }

    std::string extract_all(const archive &source, const std::size_t index, std::istream &stream)
    {
        std::ostringstream output;
        source.extract(index, stream, output, ignore_progress);
        return output.str();
    }

    std::string repeat_text()
    {
        std::string text;
        text.reserve(131072);
        for (unsigned i = 0; i < 8192u; ++i) {
            text += "0123456789abcdef";
        }
        return text;
    }

    void write_u64(std::vector<std::byte> &bytes, const std::size_t offset, const std::uint64_t value)
    {
        for (unsigned i = 0; i < 8u; ++i) {
            bytes[offset + static_cast<std::size_t>(i)] =
                static_cast<std::byte>((value >> (8u * static_cast<unsigned>(i))) & 0xffu);
        }
    }

    // An output streambuf that rejects every write, so the stream raises ios_base::failure once the
    // badbit exception mask is armed.
    class failing_output_streambuf final : public std::streambuf
    {
      protected:
        int_type overflow(int_type) override
        {
            return traits_type::eof();
        }
    };

    struct cancel_signal
    {
    };

    // A deterministic, non-0xff byte pattern so it can also feed the literal-only LZ builder.
    std::string byte_pattern(const std::size_t size)
    {
        std::string text;
        text.reserve(size);
        for (std::size_t index = 0; index < size; ++index) {
            text.push_back(static_cast<char>('a' + static_cast<int>(index % 26)));
        }
        return text;
    }

    struct progress_observation
    {
        std::string output;
        std::vector<std::int64_t> steps;
    };

    progress_observation observe_progress(const test::support::synthetic_entry &entry)
    {
        const std::array entries{entry};
        const test::support::built_archive built = test::support::build_plain_archive(entries);
        std::istringstream stream = to_stream(built.bytes);
        const std::unique_ptr<archive> reader = archive::open(stream, "progress.wolf");
        REQUIRE(reader);

        progress_observation observation;
        std::ostringstream output;
        reader->extract(0, stream, output,
                        [&observation](const std::int64_t delta) { observation.steps.push_back(delta); });
        observation.output = output.str();
        return observation;
    }

    // An output streambuf that throws a codec lz_error carrying ``message``. With the stream's
    // badbit exception mask armed, the standard library re-raises this exact object out of
    // ostream::write; without it the failure would be swallowed into badbit. Either way the adapter
    // must not fold it into read_error. The thrown exception_ptr is recorded so the test can assert
    // the concrete type and its distinguishing payload rather than std::exception_ptr identity.
    class lz_error_output_streambuf final : public std::streambuf
    {
      public:
        explicit lz_error_output_streambuf(const char *message) : message_(message)
        {
        }

        std::exception_ptr thrown;

      protected:
        std::streamsize xsputn(const char *, std::streamsize) override
        {
            try {
                throw lz_error(message_);
            } catch (...) {
                thrown = std::current_exception();
                throw;
            }
        }

      private:
        const char *message_;
    };

    // Returns true when rethrowing ``p`` yields a codec lz_error carrying ``message`` as its what().
    // Used instead of CHECK_THROWS_AS so the [[noreturn]] std::rethrow_exception does not leave the
    // Catch2 macro's trailing statement unreachable (C4702 under /W4 /WX). The payload distinguishes
    // the progress failure from the output failure without std::exception_ptr identity.
    bool rethrows_lz_error(const std::exception_ptr &p, const char *message)
    {
        try {
            if (p) {
                std::rethrow_exception(p);
            }
        } catch (const lz_error &error) {
            return std::string_view(error.what()) == message;
        } catch (...) {
            return false;
        }
        return false;
    }
} // namespace

TEST_CASE("classic DX8 archive lists and extracts the golden entries", "[wolfrpg][adapter]")
{
    const std::vector<std::byte> bytes = test::support::classic_dx8_payload();
    std::istringstream stream = to_stream(bytes);
    const std::unique_ptr<archive> reader = archive::open(stream, "classic.wolf");

    REQUIRE(reader);
    CHECK(reader->version() == 8);
    const std::span<const unpackrat::wolfrpg::entry> entries = reader->entries();
    REQUIRE(entries.size() == 2);
    CHECK(entries[0].path == "hello.txt");
    // Frozen file records carry HuffPressDataSize 252/251, independently checked by the recovery
    // raw-table test. DataSize 30/131072 is the expanded size, not the physical stored extent.
    CHECK(entries[0].stored_size == 252);
    CHECK(entries[0].unpacked_size == 30);
    CHECK(entries[1].path == "repeat.bin");
    CHECK(entries[1].stored_size == 251);
    CHECK(entries[1].unpacked_size == 131072);

    CHECK(extract_all(*reader, 0, stream) == "Owned WolfRPG fixture payload\n");
    CHECK(extract_all(*reader, 1, stream) == repeat_text());
}

TEST_CASE("a stored synthetic entry round-trips", "[wolfrpg][adapter]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);
    std::istringstream stream = to_stream(built.bytes);
    const std::unique_ptr<archive> reader = archive::open(stream, "raw.wolf");

    REQUIRE(reader);
    REQUIRE(reader->entries().size() == 1);
    CHECK(reader->entries()[0].path == "note.txt");
    CHECK(reader->entries()[0].stored_size == 17);
    CHECK(reader->entries()[0].unpacked_size == 17);
    CHECK(extract_all(*reader, 0, stream) == "raw payload bytes");
}

TEST_CASE("a literal plus back-reference LZ entry expands", "[wolfrpg][adapter]")
{
    const std::vector<std::byte> bytes = test::support::build_lz_backref_archive();
    std::istringstream stream = to_stream(bytes);
    const std::unique_ptr<archive> reader = archive::open(stream, "backref.wolf");

    REQUIRE(reader);
    REQUIRE(reader->entries().size() == 1);
    CHECK(reader->entries()[0].path == "backref.bin");
    CHECK(reader->entries()[0].unpacked_size == 6);
    CHECK(extract_all(*reader, 0, stream) == "ababab");
}

TEST_CASE("malformed headers, counts and paths are rejected", "[wolfrpg][adapter]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };

    SECTION("a broken signature is rejected")
    {
        test::support::built_archive built = test::support::build_plain_archive(entries);
        test::support::corrupt_signature(built.bytes);
        std::istringstream stream = to_stream(built.bytes);
        require_message<read_error>([&] { (void)(archive::open(stream, "bad.wolf")); }, "WolfRPG archive read failed");
    }

    SECTION("an unsupported header version is rejected")
    {
        test::support::built_archive built = test::support::build_plain_archive(entries);
        test::support::set_header_version(built.bytes, 7);
        std::istringstream stream = to_stream(built.bytes);
        require_message<read_error>([&] { (void)(archive::open(stream, "old.wolf")); }, "WolfRPG archive read failed");
    }

    SECTION("a truncated header is rejected")
    {
        test::support::built_archive built = test::support::build_plain_archive(entries);
        test::support::truncate_bytes(built.bytes, 40);
        std::istringstream stream = to_stream(built.bytes);
        require_message<read_error>([&] { (void)(archive::open(stream, "short.wolf")); },
                                    "WolfRPG archive read failed");
    }

    SECTION("an absurd root count beyond the metadata budget is rejected")
    {
        test::support::built_archive built = test::support::build_plain_archive(entries);
        test::support::set_root_file_count(built, built.bytes, 1ULL << 32u);
        std::istringstream stream = to_stream(built.bytes);
        require_message<read_error>([&] { (void)(archive::open(stream, "many.wolf")); }, "WolfRPG archive read failed");
    }

    SECTION("a name address outside the name table is rejected")
    {
        test::support::built_archive built = test::support::build_plain_archive(entries);
        write_u64(built.bytes, built.header_offset + built.file_table_offset, 0xffffffffULL);
        std::istringstream stream = to_stream(built.bytes);
        require_message<read_error>([&] { (void)(archive::open(stream, "badname.wolf")); },
                                    "WolfRPG archive read failed");
    }
}

TEST_CASE("reader failures and cancellations keep their identities", "[wolfrpg][adapter]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);
    std::istringstream stream = to_stream(built.bytes);
    const std::unique_ptr<archive> reader = archive::open(stream, "raw.wolf");
    REQUIRE(reader);

    SECTION("a source stream that ends before the body raises read_error")
    {
        std::vector<std::byte> head(built.bytes.begin(), built.bytes.begin() + 64);
        std::istringstream truncated = to_stream(head);
        std::ostringstream output;
        require_message<read_error>([&] { (void)(reader->extract(0, truncated, output, ignore_progress)); },
                                    "WolfRPG archive read failed");
    }

    SECTION("an output stream failure propagates unchanged")
    {
        failing_output_streambuf buffer;
        std::ostream output(&buffer);
        output.exceptions(std::ios::badbit);
        require_io_failure([&] { (void)(reader->extract(0, stream, output, ignore_progress)); });
    }

    SECTION("a progress exception propagates unchanged")
    {
        const std::string large(1U << 16u, 'p');
        const std::array big_entries{
            test::support::synthetic_entry{"large.bin", large, false},
        };
        const test::support::built_archive big = test::support::build_plain_archive(big_entries);
        std::istringstream big_stream = to_stream(big.bytes);
        const std::unique_ptr<archive> big_reader = archive::open(big_stream, "large.wolf");
        REQUIRE(big_reader);
        std::ostringstream output;
        REQUIRE(observed_exception<cancel_signal>([&] {
                    (void)(big_reader->extract(0, big_stream, output, [](std::int64_t) {
                        throw cancel_signal{}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
                    }));
                }).has_value());
    }

    SECTION("an out-of-range entry index is rejected")
    {
        std::ostringstream output;
        require_message<std::out_of_range>([&] { reader->extract(9, stream, output, ignore_progress); },
                                           "wolfrpg entry index out of range");
    }
}

TEST_CASE("independent readers can be interleaved without shared state", "[wolfrpg][adapter]")
{
    const std::vector<std::byte> golden_bytes = test::support::classic_dx8_payload();
    std::istringstream golden_stream = to_stream(golden_bytes);
    const std::unique_ptr<archive> golden = archive::open(golden_stream, "classic.wolf");
    REQUIRE(golden);

    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);
    std::istringstream synthetic_stream = to_stream(built.bytes);
    const std::unique_ptr<archive> synthetic = archive::open(synthetic_stream, "raw.wolf");
    REQUIRE(synthetic);

    CHECK(extract_all(*synthetic, 0, synthetic_stream) == "raw payload bytes");
    CHECK(extract_all(*golden, 0, golden_stream) == "Owned WolfRPG fixture payload\n");
    CHECK(extract_all(*synthetic, 0, synthetic_stream) == "raw payload bytes");
    CHECK(extract_all(*golden, 1, golden_stream) == repeat_text());
    CHECK(extract_all(*golden, 0, golden_stream) == "Owned WolfRPG fixture payload\n");
}

TEST_CASE("large declared entries are not artificially capped", "[wolfrpg][adapter]")
{
    constexpr std::uint64_t six_hundred_mib = 600ULL * 1024 * 1024;
    const std::array entries{
        test::support::synthetic_entry{"big.bin", "ab", true},
    };
    test::support::built_archive built = test::support::build_plain_archive(entries);
    test::support::set_file_unpacked_size(built, built.bytes, 0, six_hundred_mib);
    std::istringstream stream = to_stream(built.bytes);

    const std::unique_ptr<archive> reader = archive::open(stream, "big.wolf");
    REQUIRE(reader);
    REQUIRE(reader->entries().size() == 1);
    CHECK(reader->entries()[0].unpacked_size == static_cast<std::int64_t>(six_hundred_mib));
}

TEST_CASE("extract progress reports per-write byte deltas, not a running total", "[wolfrpg][adapter]")
{
    const std::string content = byte_pattern(100000);
    REQUIRE(content.size() > 32768u);

    const auto check_deltas = [](const progress_observation &observation, const std::string &expected) {
        CHECK(observation.output == expected);
        CHECK(observation.steps.size() > 1u);
        std::int64_t total = 0;
        for (const std::int64_t delta : observation.steps) {
            CHECK(delta > 0u);
            CHECK(delta <= 32768);
            total += delta;
        }
        CHECK(total == static_cast<std::int64_t>(expected.size()));
    };

    SECTION("a stored entry spanning several chunks")
    {
        check_deltas(observe_progress(test::support::synthetic_entry{"stored.bin", content, false}), content);
    }

    SECTION("a literal-LZ entry spanning several chunks")
    {
        check_deltas(observe_progress(test::support::synthetic_entry{"lz.bin", content, true}), content);
    }
}

TEST_CASE("extract keeps the identity of callback and output failures for a real LZ payload", "[wolfrpg][adapter]")
{
    const std::vector<std::byte> bytes = test::support::build_lz_backref_archive();
    std::istringstream stream = to_stream(bytes);
    const std::unique_ptr<archive> reader = archive::open(stream, "backref.wolf");
    REQUIRE(reader);
    REQUIRE(reader->entries().size() == 1);

    SECTION("a progress callback throwing a codec lz_error is not translated")
    {
        std::ostringstream output;
        std::exception_ptr caught;
        try {
            reader->extract(0, stream, output, [](std::int64_t) { throw lz_error(progress_lz_sentinel); });
        } catch (...) {
            caught = std::current_exception();
        }
        REQUIRE(caught);
        CHECK(rethrows_lz_error(caught, progress_lz_sentinel));
    }

    SECTION("an output stream failure throwing a codec lz_error is not translated")
    {
        lz_error_output_streambuf buffer(output_lz_sentinel);
        std::ostream output(&buffer);
        output.exceptions(std::ios::badbit);
        std::exception_ptr caught;
        try {
            reader->extract(0, stream, output, ignore_progress);
        } catch (...) {
            caught = std::current_exception();
        }
        REQUIRE(buffer.thrown);
        REQUIRE(caught);
        CHECK(rethrows_lz_error(caught, output_lz_sentinel));
    }
}

TEST_CASE("a malformed LZ body is translated to read_error", "[wolfrpg][adapter]")
{
    const std::array entries{
        test::support::synthetic_entry{"broken.bin", "malformed lz payload", true},
    };
    test::support::built_archive built = test::support::build_plain_archive(entries);
    test::support::set_file_unpacked_size(built, built.bytes, 0, 4096);
    std::istringstream stream = to_stream(built.bytes);
    const std::unique_ptr<archive> reader = archive::open(stream, "broken.wolf");
    REQUIRE(reader);

    std::ostringstream output;
    require_message<read_error>([&] { (void)(reader->extract(0, stream, output, ignore_progress)); },
                                "WolfRPG archive read failed");
}

TEST_CASE("adapter exception predicate rejects missing wrong type and wrong payload",
          "[wolfrpg][adapter][source-helper]")
{
    CHECK_FALSE(rethrows_lz_error({}, "expected"));
    CHECK_FALSE(rethrows_lz_error(std::make_exception_ptr(std::runtime_error("expected")), "expected"));
    CHECK_FALSE(rethrows_lz_error(std::make_exception_ptr(lz_error("different")), "expected"));
    CHECK(rethrows_lz_error(std::make_exception_ptr(lz_error("expected")), "expected"));
    const auto entries = std::array{test::support::synthetic_entry{"owned.txt", "owned", false}};
    const auto built = test::support::build_plain_archive(entries);
    auto stream = to_stream(built.bytes);
    CHECK_FALSE(observed_exception<read_error>([&] {
                    const auto source = archive::open(stream, "owned.wolf");
                    REQUIRE(source);
                    CHECK(extract_all(*source, 0, stream) == "owned");
                }).has_value());
}
