#include <catch2/catch_test_macros.hpp>

#include <array>
#include <cstddef>
#include <cstdint>
#include <exception>
#include <functional>
#include <ios>
#include <memory>
#include <span>
#include <sstream>
#include <streambuf>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "../../core/wolfrpg/archive.h"
#include "../../core/wolfrpg/lz_reader.h"
#include "../support/wolfrpg_fixtures.h"

// Unit tests for the classic WolfRPG Observer adapter. This translation unit compiles the real
// adapter (src/modules/wolfrpg/wolfrpg.cpp) against the real platform-neutral reader core, so the
// tests exercise the production mapping instead of a mock.
//
// Defining the token ``extractor`` here renames the whole adapter namespace: the ``namespace
// extractor`` in src/modules/extractor.h, the ``extractor`` class itself and every out-of-line
// member definition in the adapter collapse onto ``distinct_token``. That lets the test call the
// Observer entry points directly without linking the module. The public extractor.h -- and any
// header that pulls it in -- must NOT be included before the rename or its include guard would
// suppress the renamed declarations.
#define extractor distinct_token
#define UNPACKRAT_MODULE_VERSION_MAJOR 1
#define UNPACKRAT_MODULE_VERSION_MINOR 0
#include "../../modules/wolfrpg/wolfrpg.cpp" // NOLINT(bugprone-suspicious-include): unit test includes the module TU to drive its C ABI
#undef UNPACKRAT_MODULE_VERSION_MINOR
#undef UNPACKRAT_MODULE_VERSION_MAJOR
#undef extractor

namespace
{
    using unpackrat::wolfrpg::key_required;
    using unpackrat::wolfrpg::read_error;
    using unpackrat::wolfrpg::codec::lz_error;

    // Distinct what() payloads so a forwarded lz_error can be checked by type *and* payload rather
    // than by pointer identity, which MSVC does not guarantee across separate current_exception
    // captures.
    constexpr const char *callback_lz_sentinel = "wolfrpg-callback-lz-sentinel";
    constexpr const char *output_lz_sentinel = "wolfrpg-output-lz-sentinel";

    struct cancel_signal
    {
    };

    std::istringstream to_stream(const std::vector<std::byte> &bytes)
    {
        return std::istringstream(std::string(reinterpret_cast<const char *>(bytes.data()), bytes.size()),
                                  std::ios::binary);
    }

    // NOLINTNEXTLINE(bugprone-exception-escape): plain aggregate of stream/unique_ptr members
    struct opened_archive
    {
        std::istringstream stream;
        std::unique_ptr<distinct_token::distinct_token> adapter;
        std::vector<std::unique_ptr<distinct_token::file>> files;
    };

    // Drives the real adapter: admit the source through get_archive_info, then list its entries.
    opened_archive open_synthetic(const std::vector<std::byte> &bytes)
    {
        opened_archive opened;
        opened.stream = to_stream(bytes);
        opened.adapter = std::make_unique<distinct_token::distinct_token>();
        static_cast<void>(
            opened.adapter->get_archive_info(opened.stream, std::span<const std::byte>(bytes), "synthetic.wolf"));
        opened.files = opened.adapter->list_files(opened.stream);
        return opened;
    }

    opened_archive open_single(const test::support::synthetic_entry &entry)
    {
        const std::array entries{entry};
        return open_synthetic(test::support::build_plain_archive(entries).bytes);
    }

    test::support::synthetic_entry large_entry(const char fill)
    {
        return test::support::synthetic_entry{"large.bin", std::string(100000, fill), false};
    }

    // An output streambuf that throws whatever the caller asks from the first write and records the
    // exact exception_ptr, so a test can confirm the adapter rethrows the same object.
    class throwing_output_streambuf final : public std::streambuf
    {
      public:
        explicit throwing_output_streambuf(std::function<void()> thrower) : thrower_(std::move(thrower))
        {
        }

        std::exception_ptr thrown;

      protected:
        std::streamsize xsputn(const char *, std::streamsize) override
        {
            try {
                thrower_();
                return 0;
            } catch (...) {
                thrown = std::current_exception();
                throw;
            }
        }

      private:
        std::function<void()> thrower_;
    };

    // Rejects every write without throwing, so the stream records badbit quietly when the caller has
    // not armed the badbit mask. The adapter must preserve that quiet failure exactly.
    class rejecting_output_streambuf final : public std::streambuf
    {
      protected:
        std::streamsize xsputn(const char *, std::streamsize) override
        {
            return 0;
        }

        int_type overflow(int_type) override
        {
            return traits_type::eof();
        }
    };

    // Returns true when rethrowing ``p`` yields exactly ``Error``. Used instead of CHECK_THROWS_AS
    // so the [[noreturn]] std::rethrow_exception does not leave a trailing statement unreachable
    // (C4702 under /W4 /WX).
    template <typename Error> bool rethrows_is(const std::exception_ptr &p)
    {
        try {
            if (p) {
                std::rethrow_exception(p);
            }
        } catch (const Error &) {
            return true;
        } catch (...) {
            return false;
        }
        return false;
    }

    // Returns true when rethrowing ``p`` yields exactly ``Error`` with ``message`` as its what().
    template <typename Error> bool rethrows_with_message(const std::exception_ptr &p, const char *message)
    {
        try {
            if (p) {
                std::rethrow_exception(p);
            }
        } catch (const Error &error) {
            return std::string_view(error.what()) == message;
        } catch (...) {
            return false;
        }
        return false;
    }

    // The callback raises a reader error type. The adapter must not translate a caller exception, so
    // the object that escapes must still be exactly that reader type, never extractor::read_error.
    template <typename Error> void check_callback_preserves(opened_archive &opened)
    {
        std::ostringstream output;
        std::exception_ptr caught;
        try {
            opened.files[0]->extract(opened.stream, output, [](std::int64_t) { throw Error{}; });
        } catch (...) {
            caught = std::current_exception();
        }
        REQUIRE(caught);
        CHECK(rethrows_is<Error>(caught));
    }

    void check_callback_preserves_lz(opened_archive &opened)
    {
        std::ostringstream output;
        std::exception_ptr caught;
        try {
            opened.files[0]->extract(opened.stream, output, [](std::int64_t) { throw lz_error(callback_lz_sentinel); });
        } catch (...) {
            caught = std::current_exception();
        }
        REQUIRE(caught);
        CHECK(rethrows_with_message<lz_error>(caught, callback_lz_sentinel));
    }

    // The destination stream raises a reader error type; the adapter must forward the same object
    // rather than folding it into its own error translation.
    template <typename Error> void check_output_preserves(opened_archive &opened)
    {
        throwing_output_streambuf buffer([] { throw Error{}; });
        std::ostream output(&buffer);
        output.exceptions(std::ios::badbit);
        std::exception_ptr caught;
        try {
            opened.files[0]->extract(opened.stream, output, [](std::int64_t) {});
        } catch (...) {
            caught = std::current_exception();
        }
        REQUIRE(buffer.thrown);
        REQUIRE(caught);
        CHECK(rethrows_is<Error>(caught));
    }

    void check_output_preserves_lz(opened_archive &opened)
    {
        throwing_output_streambuf buffer([] { throw lz_error(output_lz_sentinel); });
        std::ostream output(&buffer);
        output.exceptions(std::ios::badbit);
        std::exception_ptr caught;
        try {
            opened.files[0]->extract(opened.stream, output, [](std::int64_t) {});
        } catch (...) {
            caught = std::current_exception();
        }
        REQUIRE(buffer.thrown);
        REQUIRE(caught);
        CHECK(rethrows_with_message<lz_error>(caught, output_lz_sentinel));
    }

    // A non-std::exception sentinel carrying a distinct payload. Used to prove the adapter forwards
    // a caller-raised object by concrete type AND payload, not merely that *something* failed.
    struct payload_signal
    {
        const char *payload;
    };

    constexpr const char *tie_flush_sentinel = "wolfrpg-tie-flush-sentinel";
    constexpr const char *mask_change_sentinel = "wolfrpg-mask-change-sentinel";

    bool rethrows_payload(const std::exception_ptr &p, const char *expected)
    {
        try {
            if (p) {
                std::rethrow_exception(p);
            }
        } catch (const payload_signal &signal) {
            return std::string_view(signal.payload) == expected;
        } catch (...) {
            return false;
        }
        return false;
    }

    // A tie stream whose flush raises a payload sentinel. ostream::write flushes its tie inside the
    // sentry, before the buffer try block, so this exception escapes the destination's own exception
    // mask entirely: the caller's quiet stream still genuinely throws out of write.
    class flushing_sentinel_streambuf final : public std::streambuf
    {
      protected:
        int sync() override
        {
            // NOLINTNEXTLINE(bugprone-std-exception-baseclass): intentional non-std signal
            throw payload_signal{tie_flush_sentinel};
        }
    };

    // Accepts writes until ``armed`` is set, after which it raises a payload sentinel. Lets a test
    // arm a failure from the progress callback, i.e. after the destination's mask was observed once.
    class mask_change_streambuf final : public std::streambuf
    {
      public:
        bool armed = false;

      protected:
        std::streamsize xsputn(const char *data, const std::streamsize count) override
        {
            if (armed) {
                // NOLINTNEXTLINE(bugprone-std-exception-baseclass): intentional non-std signal
                throw payload_signal{mask_change_sentinel};
            }
            sink_.append(data, static_cast<std::size_t>(count));
            return count;
        }

      private:
        std::string sink_;
    };

    // Rejects the first write quietly (returning 0) until the caller releases it, then accepts
    // every later write. Models a destination the caller can recover by clearing it mid-stream.
    class gated_output_streambuf final : public std::streambuf
    {
      public:
        bool reject = true;
        std::string accepted;

      protected:
        std::streamsize xsputn(const char *data, const std::streamsize count) override
        {
            if (reject) {
                return 0;
            }
            accepted.append(data, static_cast<std::size_t>(count));
            return count;
        }
    };
} // namespace

TEST_CASE("the adapter reports its module identity and empty signature", "[wolfrpg][module]")
{
    const distinct_token::version_info info = distinct_token::get_version_info();
    CHECK(info.id.Data1 == 0x709c4932U);
    CHECK(info.id.Data2 == 0xb3b2U);
    CHECK(info.id.Data3 == 0x47d3U);
    const std::array<std::uint8_t, 8> expected{0xb0, 0x35, 0xb5, 0xaf, 0xe7, 0xab, 0x7a, 0x34};
    for (std::size_t index = 0; index < expected.size(); ++index) {
        CHECK(info.id.Data4[index] == expected[index]);
    }
    CHECK(info.major_version == 1U);
    CHECK(info.minor_version == 0U);
    CHECK(distinct_token::distinct_token::get_signature().empty());
}

TEST_CASE("the adapter accepts settings and resets without disturbing later opens", "[wolfrpg][module]")
{
    distinct_token::configure(nullptr);
    distinct_token::configure(L"unused,settings");
    distinct_token::reset_settings();

    const opened_archive opened = open_single(test::support::synthetic_entry{"note.txt", "raw payload bytes", false});
    REQUIRE(opened.files.size() == 1);
    CHECK(opened.files[0]->path == "note.txt");
}

TEST_CASE("the adapter describes and lists a raw DX8 archive", "[wolfrpg][module]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);
    std::istringstream stream = to_stream(built.bytes);
    distinct_token::distinct_token adapter;

    const distinct_token::archive_info info =
        adapter.get_archive_info(stream, std::span<const std::byte>(built.bytes), "synthetic.wolf");
    CHECK(info.format == L"WolfRPG");
    CHECK(info.compression == L"DX8");
    CHECK(info.comment == L"v8");

    const std::vector<std::unique_ptr<distinct_token::file>> files = adapter.list_files(stream);
    REQUIRE(files.size() == 1);
    CHECK(files[0]->path == "note.txt");
    CHECK(files[0]->compressed_body_size_in_bytes == 17);
    CHECK(files[0]->uncompressed_body_size_in_bytes == 17);
}

TEST_CASE("the adapter keeps its decrypt transform as the identity", "[wolfrpg][module]")
{
    const distinct_token::distinct_token adapter;
    std::vector<char> data{'a', 'b', 'c'};
    CHECK(adapter.decrypt(0x12345678U, data) == 0x12345678U);
    CHECK(data == std::vector<char>({'a', 'b', 'c'}));
}

TEST_CASE("the adapter maps a malformed open to the extractor read error", "[wolfrpg][module]")
{
    distinct_token::distinct_token adapter;
    std::istringstream stream("this is not a DX8 archive at all", std::ios::binary);
    CHECK_THROWS_AS(adapter.get_archive_info(stream, {}, "malformed.wolf"), distinct_token::read_error);
}

TEST_CASE("the adapter translates a genuine core read error during extraction", "[wolfrpg][module]")
{
    const std::array entries{
        test::support::synthetic_entry{"note.txt", "raw payload bytes", false},
    };
    const test::support::built_archive built = test::support::build_plain_archive(entries);
    const opened_archive opened = open_synthetic(built.bytes);
    REQUIRE(opened.files.size() == 1);

    // Only the 64-byte header survives, so streaming the body runs past the end of the source and
    // the core raises its own read error, which the adapter must translate.
    const std::vector<std::byte> head(built.bytes.begin(), built.bytes.begin() + 64);
    std::istringstream truncated = to_stream(head);
    std::ostringstream output;
    CHECK_THROWS_AS(opened.files[0]->extract(truncated, output, [](std::int64_t) {}), distinct_token::read_error);
}

TEST_CASE("the adapter extracts a stored body and reports per-write deltas", "[wolfrpg][module]")
{
    const std::string content(100000, 'q');
    opened_archive opened = open_single(test::support::synthetic_entry{"large.bin", content, false});
    REQUIRE(opened.files.size() == 1);

    std::ostringstream output;
    std::vector<std::int64_t> steps;
    opened.files[0]->extract(opened.stream, output, [&steps](const std::int64_t delta) { steps.push_back(delta); });
    CHECK(output.str() == content);
    REQUIRE(steps.size() > 1u);
    std::int64_t total = 0;
    for (const std::int64_t delta : steps) {
        CHECK(delta > 0u);
        total += delta;
    }
    CHECK(total == static_cast<std::int64_t>(content.size()));
}

TEST_CASE("the adapter streams an LZ-pressed body through the real decoder", "[wolfrpg][module]")
{
    opened_archive opened = open_synthetic(test::support::build_lz_backref_archive());
    REQUIRE(opened.files.size() == 1);
    CHECK(opened.files[0]->path == "backref.bin");
    CHECK(opened.files[0]->uncompressed_body_size_in_bytes == 6);

    std::ostringstream output;
    opened.files[0]->extract(opened.stream, output, [](std::int64_t) {});
    CHECK(output.str() == "ababab");
}

TEST_CASE("the adapter passes through a cancellation exception unchanged", "[wolfrpg][module]")
{
    opened_archive opened = open_single(large_entry('c'));
    REQUIRE(opened.files.size() == 1);

    std::ostringstream output;
    std::exception_ptr caught;
    try {
        opened.files[0]->extract(opened.stream, output, [](std::int64_t) {
            throw cancel_signal{}; // NOLINT(bugprone-std-exception-baseclass): intentional non-std signal
        });
    } catch (...) {
        caught = std::current_exception();
    }
    REQUIRE(caught);
    CHECK(rethrows_is<cancel_signal>(caught));
}

TEST_CASE("the adapter preserves a callback core error's concrete type", "[wolfrpg][module]")
{
    opened_archive opened = open_single(large_entry('r'));
    REQUIRE(opened.files.size() == 1);

    SECTION("a core read_error")
    {
        check_callback_preserves<read_error>(opened);
    }
    SECTION("a core key_required")
    {
        check_callback_preserves<key_required>(opened);
    }
    SECTION("a codec lz_error keeps its payload")
    {
        check_callback_preserves_lz(opened);
    }
}

TEST_CASE("the adapter preserves an output core error's concrete type", "[wolfrpg][module]")
{
    opened_archive opened = open_single(large_entry('s'));
    REQUIRE(opened.files.size() == 1);

    SECTION("a core read_error")
    {
        check_output_preserves<read_error>(opened);
    }
    SECTION("a core key_required")
    {
        check_output_preserves<key_required>(opened);
    }
    SECTION("a codec lz_error keeps its payload")
    {
        check_output_preserves_lz(opened);
    }
}

TEST_CASE("the adapter preserves a quiet output failure without inventing an error", "[wolfrpg][module]")
{
    opened_archive opened = open_single(large_entry('t'));
    REQUIRE(opened.files.size() == 1);

    rejecting_output_streambuf buffer;
    std::ostream output(&buffer);
    CHECK_NOTHROW(opened.files[0]->extract(opened.stream, output, [](std::int64_t) {}));
    CHECK_FALSE(output.good());
}

TEST_CASE("the adapter forwards a caller exception that escapes a quiet destination", "[wolfrpg][module]")
{
    opened_archive opened = open_single(large_entry('u'));
    REQUIRE(opened.files.size() == 1);

    SECTION("a tie-stream flush failure escapes a quiet destination mask by type and payload")
    {
        // The destination stays quiet: its own mask would swallow a buffer failure into badbit. The
        // tie stream is the one that raises, from inside the sentry before the buffer try block, so
        // the exception genuinely reaches the adapter and must survive by type and payload.
        flushing_sentinel_streambuf tie_buffer;
        std::ostream tie(&tie_buffer);
        tie.exceptions(std::ios::badbit);
        std::ostringstream sink;
        std::ostream output(sink.rdbuf());
        output.tie(&tie);
        REQUIRE(output.exceptions() == std::ios::goodbit);

        std::exception_ptr caught;
        try {
            opened.files[0]->extract(opened.stream, output, [](std::int64_t) {});
        } catch (...) {
            caught = std::current_exception();
        }
        REQUIRE(caught);
        CHECK(rethrows_payload(caught, tie_flush_sentinel));
    }

    SECTION("an exception escaping after the destination mask changes between chunks propagates")
    {
        mask_change_streambuf buffer;
        std::ostream output(&buffer);
        REQUIRE(output.exceptions() == std::ios::goodbit);
        bool changed = false;
        std::exception_ptr caught;
        try {
            opened.files[0]->extract(opened.stream, output, [&](std::int64_t) {
                if (!changed) {
                    changed = true;
                    buffer.armed = true;
                    output.exceptions(std::ios::badbit);
                }
            });
        } catch (...) {
            caught = std::current_exception();
        }
        CHECK(changed);
        REQUIRE(caught);
        CHECK(rethrows_payload(caught, mask_change_sentinel));
    }
}

TEST_CASE("the adapter lets a quiet destination recover between chunks", "[wolfrpg][module]")
{
    opened_archive opened = open_single(large_entry('v'));
    REQUIRE(opened.files.size() == 1);

    // The first chunk is rejected quietly so the destination goes bad. The callback then clears the
    // destination and releases the gate; every later chunk must still be delivered, exactly as it
    // would if the body were written straight to the caller's stream.
    gated_output_streambuf buffer;
    std::ostream output(&buffer);
    bool recovered = false;
    opened.files[0]->extract(opened.stream, output, [&](std::int64_t) {
        if (!recovered) {
            recovered = true;
            buffer.reject = false;
            output.clear();
        }
    });

    CHECK(recovered);
    CHECK(output.good());
    // 100000 bytes in 32768-byte chunks: the first chunk is dropped, the remaining 67232 are not.
    CHECK(buffer.accepted == std::string(67232, 'v'));
}
