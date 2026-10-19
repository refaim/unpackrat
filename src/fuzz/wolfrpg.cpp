#include "../core/wolfrpg/archive.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <exception>
#include <functional>
#include <istream>
#include <memory>
#include <ostream>
#include <sstream>
#include <streambuf>
#include <string>

// Bounded libFuzzer entry point for the classic WolfRPG / DX8 reader. The input is parsed and every
// listed entry is streamed into a discarding sink under a byte and entry budget, so a malformed
// archive can neither write to the real filesystem nor drive unbounded extraction. Only the
// reader's own parse errors are swallowed; allocation failures and sanitizer findings must escape.
namespace
{
    // The harness's own control signal. It is raised by the sink once the byte budget is exhausted
    // and caught by type at the boundary.
    struct extraction_cancelled : std::exception
    {
    };

    constexpr std::uint64_t max_extracted_bytes = std::uint64_t{4} * 1024 * 1024;
    constexpr std::size_t max_entries = 4096;

    // The sole owner of the byte budget. It counts and drops every admitted byte and refuses,
    // before counting anything, every bulk write that would push the running total past the budget,
    // so a single oversized write can never overshoot. A negative count is outside the stream
    // contract; the unsigned comparison fails closed instead of driving the accounting backwards.
    class discarding_sink final : public std::streambuf
    {
      protected:
        std::streamsize xsputn(const char *, const std::streamsize count) override
        {
            const std::uint64_t added = static_cast<std::uint64_t>(count);
            if (added > max_extracted_bytes - bytes_) {
                throw extraction_cancelled{};
            }
            bytes_ += added;
            return count;
        }

        // Routes a single character through the one budget owner, so xsputn stays the only place
        // that admits bytes. The admitted count is derived as a Boolean: the EOF sentinel carries no
        // byte (count 0) while any other character carries one (count 1), so the character path
        // needs no separate branch and cannot diverge from the bulk path's accounting.
        int_type overflow(const int_type character) override
        {
            const char byte = traits_type::to_char_type(character);
            const auto count = static_cast<std::streamsize>(!traits_type::eq_int_type(character, traits_type::eof()));
            xsputn(&byte, count);
            return traits_type::not_eof(character);
        }

      public:
        [[nodiscard]] std::uint64_t bytes() const noexcept
        {
            return bytes_;
        }

      private:
        std::uint64_t bytes_ = 0;
    };

    // Drives one real extraction pass: parses the archive from ``stream`` and streams up to the
    // entry cap into ``output``. It is catch-free, so a parse failure, the sink's own cancel and any
    // unexpected failure keep their identities for the boundary to classify.
    //
    // badbit exceptions are enabled on ``output`` before any extraction so the sink's cancel escapes
    // the ostream instead of being swallowed, which would silently turn the refused writes into
    // no-ops and let the extraction run to completion unbounded by the budget.
    void exercise_archive(std::istream &stream, std::ostream &output)
    {
        output.exceptions(std::ios_base::badbit);

        // A successful open always yields a non-null reader; every failure path throws instead.
        const std::unique_ptr<unpackrat::wolfrpg::archive> reader =
            unpackrat::wolfrpg::archive::open(stream, "fuzz.wolf");
        const std::size_t count = std::min(reader->entries().size(), max_entries);
        const std::function<void(std::int64_t)> progress = [](const std::int64_t) {};
        for (std::size_t index = 0; index < count; ++index) {
            reader->extract(index, stream, output, progress);
        }
    }
} // namespace

extern "C" int LLVMFuzzerTestOneInput(const std::uint8_t *data, const std::size_t size)
{
    const std::string bytes(reinterpret_cast<const char *>(data), size);
    std::istringstream stream(bytes, static_cast<std::ios_base::openmode>(static_cast<unsigned>(std::ios::in) |
                                                                          static_cast<unsigned>(std::ios::binary)));
    discarding_sink sink;
    std::ostream output(&sink);

    // Malformed or truncated input, a keyed archive without a key, and the sink's own byte-budget
    // cancel are all expected outcomes of a fuzz run; each is swallowed by type and reported as a
    // clean zero. Every other failure, including allocation failures and sanitizer findings, escapes
    // unchanged.
    try {
        exercise_archive(stream, output);
    } catch (const unpackrat::wolfrpg::read_error &) {
        return 0;
    } catch (const unpackrat::wolfrpg::key_required &) {
        return 0;
    } catch (const extraction_cancelled &) {
        return 0;
    }

    return 0;
}
