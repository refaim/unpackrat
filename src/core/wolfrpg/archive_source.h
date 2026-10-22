#ifndef UNPACKRAT_CORE_WOLFRPG_ARCHIVE_SOURCE_H
#define UNPACKRAT_CORE_WOLFRPG_ARCHIVE_SOURCE_H

#include <cstdint>
#include <istream>
#include <span>

// Private translation-unit interface for the two byte-source operations the WolfRPG DX8 adapter
// performs from inside a vendor DXArchive::Source callback.
//
// The operations are ordinary functions rather than native-only helpers so their request policy -
// an empty or negative request that must be answered before the source is consulted, and a logical
// position that overflows - is directly testable; a validated vendor geometry never produces those
// requests itself. The definitions stay in archive.cpp beside the stream helpers they share. This
// header exposes only own and standard types and is not part of the public archive API.
namespace unpackrat::wolfrpg
{
    class modern_crypto;

    namespace detail
    {
        // Serves ``destination`` from the byte source at ``offset``. An empty destination is
        // satisfied before any stream access and succeeds; a negative offset is refused before any
        // stream access and reports false with the destination untouched. Otherwise exactly
        // ``destination.size()`` bytes are read (a short read is a source fault), the first 64 bytes
        // of a modern container are served from its decoded header and the remaining bytes are
        // outer-decoded. Caller-owned resource exceptions keep their identity; every other failure
        // is read_error.
        [[nodiscard]] bool source_read_at(std::istream &stream, std::int64_t offset,
                                          std::span<std::uint8_t> destination, const modern_crypto *modern);

        // Applies the borrowed modern inner transform to ``data`` at logical position
        // ``start + offset``. A position that overflows is a source fault (read_error), raised
        // before any byte of the range is transformed.
        void transform_modern_at(const modern_crypto &modern, std::span<std::uint8_t> data, std::uint64_t start,
                                 std::uint64_t offset);
    } // namespace detail
} // namespace unpackrat::wolfrpg
#endif
