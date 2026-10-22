#ifndef UNPACKRAT_CORE_WOLFRPG_WOLFX_METADATA_H
#define UNPACKRAT_CORE_WOLFRPG_WOLFX_METADATA_H

#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <stdexcept>
#include <string>

#include "wolfx_candidates.h"

// WolfX decryption-metadata collector. A WolfX archive can only be cracked with key material the
// game itself carries inside its map and common-event scripts (SetString / SetVariable values and
// ProFeature ".wolfx key" commands). This interface recovers those candidates from one metadata
// image at a time and merges them into a caller-owned wolfx_candidates value.
//
// The byte images come from an outer integration layer (plain *.mps / BasicData / CommonEvent.dat
// files, or entries extracted from a DX archive); this layer never touches the filesystem, never
// runs the whole WolfRPG constructor, and never writes to a game folder. The public shapes stay
// free of vendor and Win32 types: the vendor parser is reached only behind a small adapter that a
// later phase supplies, so this header exposes only our own enums, limits and errors.
namespace unpackrat::wolfrpg
{
    // Which WolfX metadata image a byte buffer represents. `map` is one *.mps map image and
    // `common_events` is a CommonEvent.dat image.
    enum class metadata_kind : std::uint8_t
    {
        map,
        common_events,
    };

    // Cumulative budgets a single collector session enforces across every metadata image it parses.
    // The defaults keep a hostile archive from driving an unbounded read or unit loop; focused tests
    // set tiny override values. `max_input_bytes` bounds one image, and the two total fields bound
    // the whole session.
    //
    // Byte accounting (`max_input_bytes`, `max_total_read_bytes`). Every accepted image is charged
    // its full `input.size()` once, before its parse begins, and only after both the per-image
    // `max_input_bytes` check and the cumulative `max_total_read_bytes` check have passed. A
    // preflight rejection charges nothing because no parse work happened. Once a parse begins its
    // byte charge is retained even if that parse later fails, so a caller cannot refund work by
    // retrying a hostile image.
    //
    // Unit accounting (`max_total_units`). A unit is one untrusted collection element the parser
    // has to visit while collecting, not one per image or per command. The collector charges one
    // unit for each map event, each event page, each page movement route, each common event, each
    // command, each command integer argument, and each command string argument it traverses. A
    // fixed-layout block that is read as a single bounded raw copy charges no units: the map tile
    // plane, the page condition and movement byte runs, the common-event 0x1D byte run, and the
    // fixed 100-slot common-event string block are all raw copies. Bytes of variable-length strings
    // are governed by the byte budget above, not by this unit budget. The limit guard runs before
    // each increment, so the charged total never exceeds `max_total_units`; a partially traversed
    // image keeps the units it already charged when it fails.
    //
    // Expanded-byte accounting (`max_image_expanded_bytes`, `max_total_expanded_bytes`). A compressed
    // Map (0x65 / 0x67) or CommonEvents (0x93 / 0xCC) image decodes its LZ4 block into a fresh buffer
    // whose declared length is attacker-controlled, so the decoded size is charged before that buffer
    // is allocated: `max_image_expanded_bytes` bounds one decode and `max_total_expanded_bytes` bounds
    // the cumulative decoded bytes of the whole session. Both charges run before the allocation, so a
    // preflight rejection charges nothing, and once a decode is charged the bytes are retained even if
    // that decode later fails, so a caller cannot refund decode work by retrying a hostile image. This
    // counter is separate from `max_input_bytes` / `max_total_read_bytes`, which still account the
    // physical input byte-for-byte exactly once.
    struct metadata_limits final
    {
        std::size_t max_input_bytes = std::size_t{64} * 1024 * 1024;
        std::size_t max_total_read_bytes = std::size_t{256} * 1024 * 1024;
        std::size_t max_total_units = 100000U;
        std::size_t max_image_expanded_bytes = std::size_t{128} * 1024 * 1024;
        std::size_t max_total_expanded_bytes = std::size_t{512} * 1024 * 1024;
    };

    // Typed failure codes the collector reports. Only recognized format/vendor parse failures, the
    // budget failures and candidate-budget failures below are translated into this type. Exceptions
    // the collector cannot classify as a format/vendor parse failure, in particular std::bad_alloc
    // and std::length_error raised by allocation, propagate unchanged and are never converted to a
    // metadata_error. This is a deliberately narrow contract: memory-exhaustion conditions stay
    // visible to the caller instead of being reported as a malformed image.
    enum class metadata_error_code : std::uint8_t
    {
        // input.size() exceeds metadata_limits::max_input_bytes.
        input_too_large,
        // The cumulative bytes read would exceed metadata_limits::max_total_read_bytes.
        read_budget_exceeded,
        // The cumulative unit count would exceed metadata_limits::max_total_units.
        unit_budget_exceeded,
        // The decoded size of one compressed image would exceed metadata_limits::max_image_expanded_bytes,
        // or the cumulative decoded size would exceed metadata_limits::max_total_expanded_bytes.
        expanded_budget_exceeded,
        // The image is structurally invalid (truncation, bad terminator, a count that cannot fit in
        // the remaining bytes, or a size product that overflows before allocation).
        malformed,
        // The image uses a format this foundation does not decode (protected dispatch, unknown
        // version).
        unsupported,
        // Merging a recovered candidate would exceed the wolfx_candidates budgets.
        candidate_budget_exceeded,
    };

    class metadata_error final : public std::runtime_error
    {
      public:
        metadata_error(metadata_error_code code, const std::string &message) : std::runtime_error(message), code_(code)
        {
        }

        [[nodiscard]] metadata_error_code code() const noexcept
        {
            return code_;
        }

      private:
        metadata_error_code code_;
    };

    // Session-scoped collector. One instance may parse any number of images of either kind; the
    // read and unit budgets accumulate across those calls, while every per-image format flag is
    // reset before each parse so a v3.5 image cannot leak its flags into a later classic image.
    //
    // collect() is transactional: when it throws, `out` is left exactly as it was, so a malformed
    // image or a candidate-budget failure can never partially merge candidates.
    class metadata_collector final
    {
      public:
        explicit metadata_collector(const metadata_limits &limits = {});
        ~metadata_collector();

        metadata_collector(metadata_collector &&) noexcept;
        metadata_collector &operator=(metadata_collector &&) noexcept;
        metadata_collector(const metadata_collector &) = delete;
        metadata_collector &operator=(const metadata_collector &) = delete;

        // Parse one metadata image and merge its recovered decryption candidates into `out`:
        //  * SetString (command 122) -> add_string_value(normalized id, UTF-8 string value);
        //  * SetVariable (command 121) -> add_int_value(normalized id, stored args[1]);
        //  * ProFeature (command 1000, subtype 7) -> add_key(the SetWolfxKey key string).
        // The implicit empty key is never stored. Recovered commands are read from every map event
        // page and every common-event command.
        //
        // The candidate merge is transactional: recovered candidates are staged and only committed
        // to `out` after a full parse and a successful candidate-budget check, so a parse failure or
        // a candidate-budget failure leaves `out` exactly as it was. Work counters are the opposite:
        // the byte charge taken before the parse and the units traversed up to a failure are retained
        // and never rolled back.
        //
        // Throws metadata_error for the recognized failure classes only. std::bad_alloc and
        // std::length_error propagate unchanged (see metadata_error_code).
        void collect(metadata_kind kind, std::span<const std::byte> input, wolfx_candidates &out);

        [[nodiscard]] const metadata_limits &limits() const noexcept;

        // Number of input bytes charged so far: every accepted image adds its full input.size()
        // once, before its parse, and the charge is retained even if that parse later fails.
        [[nodiscard]] std::size_t total_read_bytes() const noexcept;

        // Number of traversal units charged so far (see the unit rule on metadata_limits). Once a
        // parse starts, its units are retained even if the parse later fails.
        [[nodiscard]] std::size_t total_units() const noexcept;

        // Number of decoded (expanded) bytes charged so far (see the expanded-byte rule on
        // metadata_limits). Charged before each decode and retained even if that decode later fails.
        [[nodiscard]] std::size_t total_expanded_bytes() const noexcept;

      private:
        struct impl;
        std::unique_ptr<impl> impl_;
    };
} // namespace unpackrat::wolfrpg
#endif
