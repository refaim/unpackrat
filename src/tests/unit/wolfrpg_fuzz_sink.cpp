#include <catch2/catch_test_macros.hpp>

#include <cstddef>
#include <cstdint>
#include <sstream>
#include <streambuf>
#include <string>
#include <vector>

// Include the real fuzz harness translation unit so its anonymous-namespace discarding_sink,
// extraction_cancelled, byte budget and exercise_archive helper are visible to these tests. The
// sink is driven only through the real std::streambuf public interface (sputn/sputc) and the real
// extraction path only through exercise_archive, never through a copied or reimplemented stand-in.
#include "../../fuzz/wolfrpg.cpp" // NOLINT(bugprone-suspicious-include): unit test includes the fuzz TU to drive its sink

#include "../support/wolfrpg_fixtures.h"

namespace
{
    template <class Operation> void expect_cancel(Operation operation)
    {
        bool caught = false;
        try {
            operation();
        } catch (const extraction_cancelled &) {
            caught = true;
        }
        REQUIRE(caught);
    }

    // A buffer of exactly the harness byte budget.
    std::vector<char> budget_buffer()
    {
        return std::vector<char>(static_cast<std::size_t>(max_extracted_bytes), '\0');
    }

    // Feeds raw bytes to the exported fuzz entry point exactly as libFuzzer would.
    int run_entry(const std::vector<std::byte> &input)
    {
        return LLVMFuzzerTestOneInput(reinterpret_cast<const std::uint8_t *>(input.data()), input.size());
    }

    // The classified outcome of one real extraction pass, plus the bytes the actual sink admitted.
    struct outcome
    {
        bool cancelled = false;
        bool read_error = false;
        bool completed = false;
        std::uint64_t bytes = 0;
    };

    // Drives the real extraction path over ``input`` with a real discarding_sink, reusing the
    // harness's own exercise_archive helper and classifying only the boundary exceptions it can
    // raise. The sink is observed through its public bytes() accessor.
    outcome drive(const std::vector<std::byte> &input)
    {
        const std::string data(reinterpret_cast<const char *>(input.data()), input.size());
        std::istringstream stream(data, static_cast<std::ios_base::openmode>(static_cast<unsigned>(std::ios::in) |
                                                                             static_cast<unsigned>(std::ios::binary)));
        discarding_sink sink;
        std::ostream sink_stream(&sink);
        outcome result;
        try {
            exercise_archive(stream, sink_stream);
            result.completed = true;
        } catch (const extraction_cancelled &) {
            result.cancelled = true;
        } catch (const unpackrat::wolfrpg::read_error &) {
            result.read_error = true;
        }
        result.bytes = sink.bytes();
        return result;
    }
} // namespace

TEST_CASE("wolfrpg discarding sink rejects a bulk write beyond the byte budget", "[wolfrpg][fuzz]")
{
    // Existing regression: one oversized bulk write must be rejected whole, before any byte is
    // counted, so a single huge write can never push the running total past the budget.
    discarding_sink sink;
    std::vector<char> buffer(static_cast<std::size_t>(max_extracted_bytes) + 1, '\0');

    expect_cancel([&] { sink.sputn(buffer.data(), static_cast<std::streamsize>(buffer.size())); });
    REQUIRE(sink.bytes() == 0);
}

TEST_CASE("wolfrpg discarding sink accepts a bulk write of exactly the byte budget", "[wolfrpg][fuzz]")
{
    discarding_sink sink;
    std::vector<char> buffer = budget_buffer();

    const std::streamsize requested = static_cast<std::streamsize>(buffer.size());
    REQUIRE(sink.sputn(buffer.data(), requested) == requested);
    REQUIRE(sink.bytes() == max_extracted_bytes);
}

TEST_CASE("wolfrpg discarding sink rejects the next byte after an exact budget without counting it", "[wolfrpg][fuzz]")
{
    discarding_sink sink;
    std::vector<char> buffer = budget_buffer();

    const std::streamsize requested = static_cast<std::streamsize>(buffer.size());
    REQUIRE(sink.sputn(buffer.data(), requested) == requested);
    REQUIRE(sink.bytes() == max_extracted_bytes);

    // The byte after a full budget is refused, and the refusal must not change the count.
    expect_cancel([&] { sink.sputn(buffer.data(), 1); });
    REQUIRE(sink.bytes() == max_extracted_bytes);
}

TEST_CASE("wolfrpg discarding sink refuses a partial-capacity overrun without counting and admits the rest",
          "[wolfrpg][fuzz]")
{
    // Three bytes of room: a four-byte write is refused whole (the count is untouched), while a
    // three-byte write still fits and is admitted.
    discarding_sink sink;
    std::vector<char> buffer = budget_buffer();
    const std::streamsize nearly_full = static_cast<std::streamsize>(max_extracted_bytes) - 3;

    REQUIRE(sink.sputn(buffer.data(), nearly_full) == nearly_full);
    REQUIRE(sink.bytes() == max_extracted_bytes - 3);

    expect_cancel([&] { sink.sputn(buffer.data(), 4); });
    REQUIRE(sink.bytes() == max_extracted_bytes - 3);

    REQUIRE(sink.sputn(buffer.data(), 3) == 3);
    REQUIRE(sink.bytes() == max_extracted_bytes);
}

TEST_CASE("wolfrpg discarding sink accepts an empty write before and at the cap", "[wolfrpg][fuzz]")
{
    discarding_sink sink;
    char byte = 'x';

    REQUIRE(sink.sputn(&byte, 0) == 0);
    REQUIRE(sink.bytes() == 0);

    std::vector<char> buffer = budget_buffer();
    const std::streamsize requested = static_cast<std::streamsize>(buffer.size());
    REQUIRE(sink.sputn(buffer.data(), requested) == requested);
    REQUIRE(sink.bytes() == max_extracted_bytes);

    // A zero-length write at the cap is still a no-op success, not an overflow.
    REQUIRE(sink.sputn(&byte, 0) == 0);
    REQUIRE(sink.bytes() == max_extracted_bytes);
}

TEST_CASE("wolfrpg discarding sink rejects a cumulative write that crosses the budget", "[wolfrpg][fuzz]")
{
    discarding_sink sink;
    std::vector<char> buffer(1024, '\0');
    const std::streamsize chunk = static_cast<std::streamsize>(buffer.size());

    std::uint64_t written = 0;
    while (written + buffer.size() <= max_extracted_bytes) {
        REQUIRE(sink.sputn(buffer.data(), chunk) == chunk);
        written += buffer.size();
        REQUIRE(sink.bytes() == written);
    }
    REQUIRE(written == max_extracted_bytes);

    // A further chunk that would exceed the budget is refused whole without changing the count.
    expect_cancel([&] { sink.sputn(buffer.data(), chunk); });
    REQUIRE(sink.bytes() == max_extracted_bytes);
}

TEST_CASE("wolfrpg discarding sink counts in-budget single bytes and refuses the overflow byte", "[wolfrpg][fuzz]")
{
    using traits_type = std::streambuf::traits_type;

    discarding_sink sink;
    REQUIRE(sink.sputc('a') == traits_type::not_eof(traits_type::to_int_type('a')));
    REQUIRE(sink.bytes() == 1);

    // Fill the rest of the budget in one bulk write; the sink already holds one byte.
    std::vector<char> buffer = budget_buffer();
    const std::streamsize rest = static_cast<std::streamsize>(buffer.size()) - 1;
    REQUIRE(sink.sputn(buffer.data(), rest) == rest);
    REQUIRE(sink.bytes() == max_extracted_bytes);

    // A further single byte overflows the budget and must be refused without counting.
    expect_cancel([&] { sink.sputc('b'); });
    REQUIRE(sink.bytes() == max_extracted_bytes);
}

TEST_CASE("wolfrpg discarding sink fails closed on a negative bulk count", "[wolfrpg][fuzz]")
{
    // A negative count is outside the stream contract; the sink must fail closed rather than let the
    // unsigned accounting run backwards. Nothing may be counted.
    discarding_sink sink;

    expect_cancel([&] { sink.sputn("x", -1); });
    REQUIRE(sink.bytes() == 0);
}

TEST_CASE("the real entry extracts a bounded stored archive to completion", "[wolfrpg][fuzz]")
{
    const std::vector<test::support::synthetic_entry> entries{
        {"hello.txt", "Owned WolfRPG fixture payload\n", false},
        {"numbers.bin", "0123456789", false},
    };
    const std::vector<std::byte> bytes = test::support::build_plain_archive(entries).bytes;

    // The exported entry returns 0, and the same input drives the real sink to completion with the
    // exact plaintext byte count, never reaching the budget.
    REQUIRE(run_entry(bytes) == 0);
    const outcome result = drive(bytes);
    REQUIRE(result.completed);
    REQUIRE_FALSE(result.cancelled);
    REQUIRE_FALSE(result.read_error);
    REQUIRE(result.bytes == 30 + 10);
}

TEST_CASE("an empty archive drives no extraction", "[wolfrpg][fuzz]")
{
    // A keyless archive with no root files lists zero entries, so the extraction loop is never
    // entered and the sink stays empty.
    const std::vector<std::byte> bytes = test::support::build_plain_archive({}).bytes;

    REQUIRE(run_entry(bytes) == 0);
    const outcome result = drive(bytes);
    REQUIRE(result.completed);
    REQUIRE(result.bytes == 0);
}

TEST_CASE("the real entry cancels a stored entry that overruns the byte budget", "[wolfrpg][fuzz]")
{
    const std::vector<test::support::synthetic_entry> entries{
        {"big.bin", std::string(std::size_t{5} * 1024 * 1024, 'Z'), false},
    };
    const std::vector<std::byte> bytes = test::support::build_plain_archive(entries).bytes;

    REQUIRE(run_entry(bytes) == 0);

    // The same input, driven through the real extraction path with the actual sink, raises the
    // harness's own cancel and never admits more than the byte budget.
    const std::string data(reinterpret_cast<const char *>(bytes.data()), bytes.size());
    std::istringstream stream(data, static_cast<std::ios_base::openmode>(static_cast<unsigned>(std::ios::in) |
                                                                         static_cast<unsigned>(std::ios::binary)));
    discarding_sink sink;
    std::ostream sink_stream(&sink);
    expect_cancel([&] { exercise_archive(stream, sink_stream); });
    REQUIRE(sink.bytes() <= max_extracted_bytes);
    REQUIRE(sink.bytes() == max_extracted_bytes);
}

TEST_CASE("the real entry cancels a small LZ body that expands past the byte budget", "[wolfrpg][fuzz]")
{
    // A compressed body of a few kilobytes decodes to more than the budget; the sink must stop it.
    const std::vector<std::byte> bytes = test::support::build_lz_expansion_archive(std::uint64_t{5} * 1024 * 1024);
    REQUIRE(bytes.size() < max_extracted_bytes);

    REQUIRE(run_entry(bytes) == 0);
    const outcome result = drive(bytes);
    REQUIRE(result.cancelled);
    REQUIRE(result.bytes <= max_extracted_bytes);
    REQUIRE(result.bytes == max_extracted_bytes);
}

TEST_CASE("the byte budget is cumulative across several entries", "[wolfrpg][fuzz]")
{
    // Three two-megabyte entries: the first two fit, the third crosses the running budget.
    const std::string two_megabytes(std::size_t{2} * 1024 * 1024, 'Q');
    const std::vector<test::support::synthetic_entry> entries{
        {"a.bin", two_megabytes, false},
        {"b.bin", two_megabytes, false},
        {"c.bin", two_megabytes, false},
    };
    const std::vector<std::byte> bytes = test::support::build_plain_archive(entries).bytes;

    REQUIRE(run_entry(bytes) == 0);
    const outcome result = drive(bytes);
    REQUIRE(result.cancelled);
    REQUIRE(result.bytes == max_extracted_bytes);
}

TEST_CASE("the entry cap bounds a pathologically wide archive", "[wolfrpg][fuzz]")
{
    // More entries than the harness will extract: the cap, not the archive, decides how far the
    // extraction goes. Every entry holds one byte, so the sink's total proves the cap was applied.
    constexpr std::size_t total = max_entries + 64;
    std::vector<test::support::synthetic_entry> entries;
    entries.reserve(total);
    for (std::size_t index = 0; index < total; ++index) {
        entries.push_back({"f" + std::to_string(index), "x", false});
    }
    const std::vector<std::byte> bytes = test::support::build_plain_archive(entries).bytes;

    REQUIRE(run_entry(bytes) == 0);
    const outcome result = drive(bytes);
    REQUIRE(result.completed);
    REQUIRE(result.bytes == max_entries);
}

TEST_CASE("the real entry classifies malformed input as a read error", "[wolfrpg][fuzz]")
{
    std::vector<std::byte> bytes = test::support::build_lz_backref_archive();
    test::support::corrupt_signature(bytes);

    REQUIRE(run_entry(bytes) == 0);
    const outcome result = drive(bytes);
    REQUIRE(result.read_error);
    REQUIRE(result.bytes == 0);
}

TEST_CASE("an input shorter than the fixed header is classified as a read error", "[wolfrpg][fuzz]")
{
    const std::vector<std::byte> bytes{std::byte{'D'}, std::byte{'X'}, std::byte{0x08}};

    REQUIRE(run_entry(bytes) == 0);
    const outcome result = drive(bytes);
    REQUIRE(result.read_error);
    REQUIRE(result.bytes == 0);
}

TEST_CASE("the known keyed classic archive completes within the extraction budget", "[wolfrpg][fuzz]")
{
    // The restored reader supports this frozen default-key classic archive, including its
    // compressed header and bodies. Count expanded plaintext: hello.txt is 30 bytes and repeat.bin
    // is the 16-byte golden pattern repeated 8192 times; stored extents 252/251 are not output sizes.
    const std::vector<std::byte> bytes = test::support::classic_dx8_payload();

    REQUIRE(run_entry(bytes) == 0);
    const outcome result = drive(bytes);
    REQUIRE(result.completed);
    REQUIRE_FALSE(result.read_error);
    REQUIRE_FALSE(result.cancelled);
    REQUIRE(result.bytes == 30 + 16 * 8192);
}
