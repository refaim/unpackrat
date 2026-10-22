#ifndef UNPACKRAT_CORE_WOLFRPG_TEXT_ENCODING_H
#define UNPACKRAT_CORE_WOLFRPG_TEXT_ENCODING_H

#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <stdexcept>
#include <string>

// Platform-neutral text decoding for the Wolf RPG / DXLib string components.
//
// Wolf RPG stores every string as a length-prefixed component of raw encoded bytes plus a
// per-archive code page. Those bytes are untrusted archive input: the component may be truncated,
// may contain a NUL, may use a non-shortest UTF-8 form, may encode a surrogate or an out-of-range
// scalar, and may carry arbitrary high bytes. This translation unit is the single owner of the
// pinned DXLib (uberwolf) character-code primitives; the DXLib headers and types never cross into
// this interface, and no call site links the DXLib character library directly.
//
// The conversion here only produces display / path-validation text. Callers must keep the raw
// stored bytes for the DX per-file key and must apply any path-separator policy to the UTF-8
// result of convert(), never to the encoded bytes: an encoded component can legitimately contain a
// 0x5C trail byte inside a multi-byte character (for example the Shift-JIS encoding of U+8868),
// so treating 0x5C as a separator before decoding would corrupt the name.
namespace unpackrat::wolfrpg::text
{
    // Largest encoded component accepted by convert(). DXLib/WolfRPG string components are length
    // prefixed, and 4092 bytes is the widest component the reader is willing to decode before it
    // treats the length as corrupt.
    inline constexpr std::size_t max_component_bytes = 4092;

    // Upper bound on the UTF-8 byte count for an accepted component: one encoded byte can decode to
    // at most one scalar, the widest supported source unit is one byte, and the widest UTF-8 scalar
    // is four bytes.
    inline constexpr std::size_t max_utf8_bytes = 4 * max_component_bytes;

    // Reason a component could not be decoded. Callers may branch on this instead of parsing the
    // message text.
    enum class encoding_error_code : std::uint8_t
    {
        unsupported_codepage, // code page is outside the supported set
        input_too_large,      // component is wider than max_component_bytes
        embedded_nul,         // a zero code unit appears inside the component
        truncated_sequence,   // a lead byte / code unit needs more bytes than remain
        invalid_sequence,     // bad lead byte, bad continuation byte, unpaired surrogate, or a
                              // UTF-32 scalar above U+10FFFF
        overlong_sequence,    // non-shortest UTF-8 encoding of the decoded scalar
        surrogate_code_point, // a decoded scalar falls in U+D800..U+DFFF
        unmappable_character, // the encoded character has no Unicode mapping
    };

    // Raised for a component that cannot be decoded under the requested code page.
    class encoding_error final : public std::runtime_error
    {
      public:
        explicit encoding_error(encoding_error_code code);

        [[nodiscard]] encoding_error_code code() const noexcept;

      private:
        encoding_error_code code_;
    };

    // True when convert() accepts the code page. The set matches the pinned DXLib character formats
    // the adapter has verified: Shift-JIS (932), GB2312 (936), UHC (949), Big5 (950), Windows-1251 (1251),
    // Windows-1252 (1252), ISO-8859-15 (32764), ASCII (32765), UTF-8 (65001), UTF-16LE (1200), UTF-16BE (1201),
    // UTF-32LE (32766) and UTF-32BE (32767).
    [[nodiscard]] bool is_supported_codepage(std::uint32_t codepage) noexcept;

    // Smallest encoded unit for the code page: 1 for single-byte and UTF-8 code pages, 2 for
    // UTF-16, 4 for UTF-32, and 0 for an unsupported code page. Callers use this to treat a bounded
    // component as a sequence of logical UTF-16 / UTF-32 names.
    [[nodiscard]] std::size_t encoding_unit_size(std::uint32_t codepage) noexcept;

    // Byte offset of the first logical NUL terminator in a bounded component, or std::nullopt when
    // the component holds no terminator. Scanning steps by encoding_unit_size() so a lone 0x00 byte
    // inside a UTF-16 / UTF-32 unit is not mistaken for the terminator. Throws encoding_error with
    // code unsupported_codepage for a code page convert() rejects.
    [[nodiscard]] std::optional<std::size_t> find_terminator(std::span<const std::byte> encoded_component,
                                                             std::uint32_t codepage);

    // Decodes a bounded encoded component (terminator excluded) into UTF-8. Throws encoding_error
    // for an unsupported code page, an oversize component, an embedded NUL, or malformed encoded
    // text; never substitutes replacement characters or silently drops input.
    [[nodiscard]] std::string convert(std::span<const std::byte> encoded_component, std::uint32_t codepage);

    // Borrowed host dependency for the Windows-1251 path. For each non-NUL byte it returns a
    // valid nonzero Unicode scalar, or no value when conversion is refused. It must not return a
    // surrogate or a value above U+10FFFF. No callable or state is retained by convert().
    // Declared as a plain typedef, not `using`, so analyzers follow their mature
    // function-type-alias parser path; the borrowed reference contract is unchanged.
    typedef std::optional<std::uint32_t>(single_byte_decoder)(std::uint32_t, unsigned char);

    // Same validation and output contract as convert(encoded,codepage). Only Windows-1251 calls
    // decoder; preflight errors and other code pages do not. Decoder exceptions propagate.
    [[nodiscard]] std::string convert(std::span<const std::byte> encoded_component, std::uint32_t codepage,
                                      single_byte_decoder &decoder);

} // namespace unpackrat::wolfrpg::text
#endif
