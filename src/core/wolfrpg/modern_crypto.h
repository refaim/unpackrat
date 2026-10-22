#ifndef UNPACKRAT_CORE_WOLFRPG_MODERN_CRYPTO_H
#define UNPACKRAT_CORE_WOLFRPG_MODERN_CRYPTO_H

#include <array>
#include <cstdint>
#include <memory>
#include <span>
#include <stdexcept>
#include <string_view>

// Seekable decoder for the "new crypt" Wolf RPG archive container. It covers the dispatcher's
// full modern crypt-version range, 331..999 and 1010..65535: the fixed v3.31-style logical prefix for
// versions below 350 and 1020, and the derived v3.50 body for 350..999 and 1020 and above, with
// the Pro password-rekey (pK2) derivations for 1010 and up.
//
// This is the platform-neutral core: it owns only plain values and never exposes a UberWolf or
// Win32 type. The DXLib/UberWolf crypto primitives live entirely inside the implementation
// translation unit. ``create`` derives an immutable context from the archive header; the two
// decode entry points then transform a caller-supplied byte range at any physical or logical
// position. No whole-archive buffer, no stream and no mutable cursor are retained, so calls may
// arrive out of order, interleaved, or from several threads on the same context.
namespace unpackrat::wolfrpg
{
    // Raised when a modern archive header is unsupported or inconsistent with its declared size,
    // when a decode range lies outside the archive, or when an empty password is supplied for a
    // version that derives its salt from the password.
    class modern_crypto_error final : public std::runtime_error
    {
      public:
        modern_crypto_error() : std::runtime_error("WolfRPG modern archive crypto failed")
        {
        }
    };

    // The decoded 64-byte DARC_HEAD (DXLib packed layout) with its scrambled addresses restored.
    using modern_header = std::array<std::uint8_t, 64>;

    class modern_crypto final
    {
      public:
        ~modern_crypto();
        modern_crypto(modern_crypto &&) noexcept;
        modern_crypto &operator=(modern_crypto &&) noexcept;
        modern_crypto(const modern_crypto &) = delete;
        modern_crypto &operator=(const modern_crypto &) = delete;

        // ``header`` is the raw 64-byte header stored at the start of the archive, ``physical_size``
        // is the total archive size in bytes, and ``password`` is the archive password as a raw
        // byte image (its first NUL ends the salt prefix, and Pro versions read their four-byte
        // rekey blob starting one byte past the 63-clamped prefix). Raises modern_crypto_error for
        // a crypt version outside 331..999 and 1010..65535, physical size below 64 bytes for
        // v3.31 or 1088 bytes for other versions, a name table outside [64, physical_size], a
        // derived body size that would run past the file, an empty salt prefix for a version
        // that salts from the password, or a Pro archive too short to hold its four-byte rekey
        // blob. The archive reader separately validates all metadata and member geometry.
        [[nodiscard]] static modern_crypto create(std::span<const std::uint8_t, 64> header, std::uint64_t physical_size,
                                                  std::string_view password);

        // The decoded header: the 64 stored bytes with the DXLib address scramble undone.
        [[nodiscard]] const modern_header &header() const noexcept;

        // Crypt version read from the header flags (any modern version: 331..999 or 1010..65535).
        [[nodiscard]] std::uint16_t crypt_version() const noexcept;

        // Whether the archive carries an inner key (DXA_FLAG_NO_KEY is clear).
        [[nodiscard]] bool has_inner_key() const noexcept;

        // Absolute physical offset of the name table, i.e. the start of the second AES region.
        [[nodiscard]] std::uint64_t name_table_offset() const noexcept;

        // Logical length of the first AES region, beginning at physical offset 64. For v3.31
        // this remains 1024 even when its physical intersection ends earlier at the file end.
        [[nodiscard]] std::uint32_t aes_body_size() const noexcept;

        // Applies every outer transform that overlaps physical [physical_offset,
        // physical_offset + data.size()): the Wolf XOR over [64, physical_size - 64) and both AES
        // CTR regions, each at its own counter position. The logical first region is intersected
        // with physical bytes; the metadata IV advances by ceil(actual first-region bytes / 16).
        // Ranges may be split, unaligned, out of order or overlapping. The header region [0, 64)
        // is never covered here (see header()).
        // Raises modern_crypto_error when the range leaves [64, physical_size).
        void decode_outer_at(std::span<std::uint8_t> data, std::uint64_t physical_offset) const;

        // Applies the inner Wolf key XOR to a logical range of the archive's encoded member bytes
        // (the transform sits before Huffman/LZ decoding) at the given logical position. It is a
        // no-op when the archive declares no key.
        void apply_inner(std::span<std::uint8_t> data, std::uint64_t logical_position) const;

      private:
        modern_crypto();
        struct impl;
        std::unique_ptr<impl> impl_;
    };
} // namespace unpackrat::wolfrpg
#endif
