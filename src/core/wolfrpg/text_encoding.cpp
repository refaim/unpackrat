#include "text_encoding.h"
#include "system_codepage.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <string>

#include "uberwolf/DXLib/CharCode.h"

// The single translation unit that owns the pinned DXLib character-code primitives. Only this
// implementation sees <uberwolf/DXLib/CharCode.h>; the rest of the program uses the neutral
// interface in text_encoding.h. The vendor tables are primed once through a call_once barrier so a
// cold, concurrent first use cannot race the lazy per-format table setup.
namespace unpackrat::wolfrpg::text
{
    namespace
    {
        // Formats whose conversion table is filled lazily by the first ConvCharCode() that
        // references them. On Windows InitCharCode() only raises its own flag, so each table must be
        // primed explicitly before convert() is called from several threads at once.
        constexpr std::array<int, 6> vendor_table_codepages{
            CHARCODEFORMAT_SHIFTJIS, CHARCODEFORMAT_GB2312,       CHARCODEFORMAT_UHC,
            CHARCODEFORMAT_BIG5,     CHARCODEFORMAT_WINDOWS_1252, CHARCODEFORMAT_ISO_IEC_8859_15};

        // GetCharCode() may inspect up to six bytes past a caller-supplied lead. The scratch copy
        // pads the bounded component with zeroes so every vendor read stays inside the buffer.
        constexpr std::size_t scratch_slack = 8;
        constexpr std::uint32_t windows_cyrillic_codepage = 1251;

        // Smallest scalar a canonical UTF-8 sequence of each width may encode; a shorter subsequence
        // is a non-shortest (overlong) form.
        constexpr std::array<std::uint32_t, 5> utf8_min_scalar{0, 0, 0x80, 0x800, 0x10000};

        // Payload bits carried by the lead byte of a one- to four-byte sequence.
        constexpr std::array<std::uint32_t, 5> utf8_lead_mask{0, 0x7F, 0x1F, 0x0F, 0x07};

        void prime_vendor_character_tables()
        {
            InitCharCode();
            for (const int codepage : vendor_table_codepages) {
                (void)ConvCharCode('A', codepage, CHARCODEFORMAT_UTF16LE);
            }
        }

        void ensure_vendor_character_tables()
        {
            static std::once_flag primed;
            std::call_once(primed, prime_vendor_character_tables);
        }

        unsigned char byte_at(const char *data, std::size_t index)
        {
            return static_cast<unsigned char>(data[index]);
        }

        std::uint32_t read_u16(const char *data, std::size_t offset, bool big_endian)
        {
            const std::uint32_t first = byte_at(data, offset);
            const std::uint32_t second = byte_at(data, offset + 1);
            return big_endian ? (first << 8u) | second : (second << 8u) | first;
        }

        std::uint32_t read_u32(const char *data, std::size_t offset, bool big_endian)
        {
            const std::uint32_t b0 = byte_at(data, offset);
            const std::uint32_t b1 = byte_at(data, offset + 1);
            const std::uint32_t b2 = byte_at(data, offset + 2);
            const std::uint32_t b3 = byte_at(data, offset + 3);
            return big_endian ? (b0 << 24u) | (b1 << 16u) | (b2 << 8u) | b3
                              : (b3 << 24u) | (b2 << 16u) | (b1 << 8u) | b0;
        }

        // ASCII is validated here rather than delegated: the vendor returns the raw byte unchanged
        // for values above 0x7F, which would smuggle high bytes into the decoded text.
        std::uint32_t decode_ascii(const char *data, std::size_t &offset)
        {
            const std::uint32_t value = byte_at(data, offset);
            if (value > 0x7Fu) {
                throw encoding_error(encoding_error_code::invalid_sequence);
            }
            offset += 1;
            return value;
        }

        std::uint32_t decode_utf8(const char *data, std::size_t size, std::size_t &offset)
        {
            const std::uint32_t lead = byte_at(data, offset);
            std::size_t width = 0;
            if (lead < 0x80u) {
                width = 1;
            } else if (lead >= 0xC0 && lead < 0xE0u) {
                width = 2;
            } else if (lead >= 0xE0 && lead < 0xF0u) {
                width = 3;
            } else if (lead >= 0xF0 && lead < 0xF5u) {
                width = 4;
            }

            // 0x80..0xBF is a stray continuation byte and 0xF5..0xFF is beyond Unicode: each is an
            // invalid lead byte, refused exactly as the original chain did with no width recorded.
            if (width == 0) {
                throw encoding_error(encoding_error_code::invalid_sequence);
            }

            if (size - offset < width) {
                throw encoding_error(encoding_error_code::truncated_sequence);
            }

            std::uint32_t scalar = lead & utf8_lead_mask[width];
            for (std::size_t index = 1; index < width; ++index) {
                const std::uint32_t continuation = byte_at(data, offset + index);
                if ((continuation & 0xC0u) != 0x80u) {
                    throw encoding_error(encoding_error_code::invalid_sequence);
                }
                scalar = (scalar << 6u) | (continuation & 0x3Fu);
            }

            if (scalar >= 0xD800 && scalar <= 0xDFFF) {
                throw encoding_error(encoding_error_code::surrogate_code_point);
            }
            if (scalar > 0x10FFFFu) {
                throw encoding_error(encoding_error_code::invalid_sequence);
            }
            if (scalar < utf8_min_scalar[width]) {
                throw encoding_error(encoding_error_code::overlong_sequence);
            }

            offset += width;
            return scalar;
        }

        std::uint32_t decode_utf16(const char *data, std::size_t size, std::size_t &offset, bool big_endian)
        {
            if (size - offset < 2u) {
                throw encoding_error(encoding_error_code::truncated_sequence);
            }
            const std::uint32_t lead = read_u16(data, offset, big_endian);
            offset += 2;

            if (lead >= 0xD800 && lead <= 0xDBFF) {
                if (size - offset < 2u) {
                    throw encoding_error(encoding_error_code::truncated_sequence);
                }
                const std::uint32_t trail = read_u16(data, offset, big_endian);
                if (trail < 0xDC00u || trail > 0xDFFFu) {
                    throw encoding_error(encoding_error_code::invalid_sequence);
                }
                offset += 2;
                return 0x10000 + ((lead - 0xD800) << 10u) + (trail - 0xDC00);
            }
            if (lead >= 0xDC00 && lead <= 0xDFFF) {
                throw encoding_error(encoding_error_code::surrogate_code_point);
            }
            return lead;
        }

        std::uint32_t decode_utf32(const char *data, std::size_t size, std::size_t &offset, bool big_endian)
        {
            if (size - offset < 4u) {
                throw encoding_error(encoding_error_code::truncated_sequence);
            }
            const std::uint32_t scalar = read_u32(data, offset, big_endian);
            offset += 4;

            if (scalar >= 0xD800 && scalar <= 0xDFFF) {
                throw encoding_error(encoding_error_code::surrogate_code_point);
            }
            if (scalar > 0x10FFFFu) {
                throw encoding_error(encoding_error_code::invalid_sequence);
            }
            return scalar;
        }

        std::uint32_t decode_multibyte(std::uint32_t codepage, const char *data, std::size_t size, std::size_t &offset)
        {
            int encoded_bytes = 0;
            const u32 encoded = GetCharCode(data + offset, static_cast<int>(codepage), &encoded_bytes);

            // The vendor pads a missing trail byte with zero and still reports a two-byte unit, so a
            // component that ends mid-character must be rejected here rather than trusted.
            if (size - offset < static_cast<std::size_t>(encoded_bytes)) {
                throw encoding_error(encoding_error_code::truncated_sequence);
            }

            // A zero scalar means the byte(s) have no Unicode mapping. GetCharCode cannot return zero
            // for a non-empty unit, so this only fires for a genuinely unmappable character.
            const u32 scalar = ConvCharCode(encoded, static_cast<int>(codepage), CHARCODEFORMAT_UTF32LE);
            if (scalar == 0) {
                throw encoding_error(encoding_error_code::unmappable_character);
            }

            offset += static_cast<std::size_t>(encoded_bytes);
            return scalar;
        }

        std::uint32_t decode_scalar(std::uint32_t codepage, const char *data, std::size_t size, std::size_t &offset,
                                    single_byte_decoder &decoder)
        {
            switch (codepage) {
            case windows_cyrillic_codepage: {
                const auto scalar = decoder(codepage, byte_at(data, offset++));
                if (!scalar) {
                    throw encoding_error(encoding_error_code::unmappable_character);
                }
                return *scalar;
            }
            case CHARCODEFORMAT_ASCII:
                return decode_ascii(data, offset);
            case CHARCODEFORMAT_UTF8:
                return decode_utf8(data, size, offset);
            case CHARCODEFORMAT_UTF16LE:
                return decode_utf16(data, size, offset, false);
            case CHARCODEFORMAT_UTF16BE:
                return decode_utf16(data, size, offset, true);
            case CHARCODEFORMAT_UTF32LE:
                return decode_utf32(data, size, offset, false);
            case CHARCODEFORMAT_UTF32BE:
                return decode_utf32(data, size, offset, true);
            default:
                // convert() has already rejected every codepage outside the supported set, so the
                // only remaining formats are the six table-backed multi-byte/single-byte pages.
                return decode_multibyte(codepage, data, size, offset);
            }
        }

        void append_utf8(std::string &out, std::uint32_t scalar)
        {
            // Every scalar reaching here is a validated Unicode scalar (<= U+10FFFF), so PutCharCode
            // writes one to four bytes into the eight-byte buffer and never fails.
            std::array<char, 8> encoded{};
            const int written = PutCharCode(scalar, CHARCODEFORMAT_UTF8, encoded.data());
            out.append(encoded.data(), static_cast<std::size_t>(written));
        }
    } // namespace

    encoding_error::encoding_error(encoding_error_code code) : std::runtime_error("text_encoding failure"), code_(code)
    {
    }

    encoding_error_code encoding_error::code() const noexcept
    {
        return code_;
    }

    bool is_supported_codepage(std::uint32_t codepage) noexcept
    {
        switch (codepage) {
        case windows_cyrillic_codepage:
        case CHARCODEFORMAT_SHIFTJIS:
        case CHARCODEFORMAT_GB2312:
        case CHARCODEFORMAT_UHC:
        case CHARCODEFORMAT_BIG5:
        case CHARCODEFORMAT_WINDOWS_1252:
        case CHARCODEFORMAT_ISO_IEC_8859_15:
        case CHARCODEFORMAT_ASCII:
        case CHARCODEFORMAT_UTF8:
        case CHARCODEFORMAT_UTF16LE:
        case CHARCODEFORMAT_UTF16BE:
        case CHARCODEFORMAT_UTF32LE:
        case CHARCODEFORMAT_UTF32BE:
            return true;
        default:
            return false;
        }
    }

    std::size_t encoding_unit_size(std::uint32_t codepage) noexcept
    {
        if (codepage == windows_cyrillic_codepage) {
            return 1;
        }
        if (!is_supported_codepage(codepage)) {
            return 0;
        }
        return static_cast<std::size_t>(GetCharCodeFormatUnitSize(static_cast<int>(codepage)));
    }

    std::optional<std::size_t> find_terminator(std::span<const std::byte> encoded_component, std::uint32_t codepage)
    {
        if (!is_supported_codepage(codepage)) {
            throw encoding_error(encoding_error_code::unsupported_codepage);
        }

        const std::size_t unit = encoding_unit_size(codepage);
        std::size_t offset = 0;
        while (encoded_component.size() - offset >= unit) {
            bool all_zero = true;
            for (std::size_t index = 0; index < unit; ++index) {
                if (encoded_component[offset + index] != std::byte{0}) {
                    all_zero = false;
                    break;
                }
            }
            if (all_zero) {
                return offset;
            }
            offset += unit;
        }
        return std::nullopt;
    }

    std::string convert(std::span<const std::byte> encoded_component, std::uint32_t codepage)
    {
        return convert(encoded_component, codepage, platform::single_byte_to_scalar);
    }

    std::string convert(std::span<const std::byte> encoded_component, std::uint32_t codepage,
                        single_byte_decoder &decoder)
    {
        if (!is_supported_codepage(codepage)) {
            throw encoding_error(encoding_error_code::unsupported_codepage);
        }
        if (encoded_component.size() > max_component_bytes) {
            throw encoding_error(encoding_error_code::input_too_large);
        }
        // A component is terminator-free by contract, so any full NUL unit is embedded corruption.
        if (find_terminator(encoded_component, codepage).has_value()) {
            throw encoding_error(encoding_error_code::embedded_nul);
        }

        ensure_vendor_character_tables();

        std::array<char, max_component_bytes + scratch_slack> scratch{};
        for (std::size_t index = 0; index < encoded_component.size(); ++index) {
            scratch[index] = static_cast<char>(std::to_integer<unsigned char>(encoded_component[index]));
        }

        std::string out;
        out.reserve(encoded_component.size());

        std::size_t offset = 0;
        while (offset < encoded_component.size()) {
            const std::uint32_t scalar =
                decode_scalar(codepage, scratch.data(), encoded_component.size(), offset, decoder);
            append_utf8(out, scalar);
        }
        return out;
    }
} // namespace unpackrat::wolfrpg::text
