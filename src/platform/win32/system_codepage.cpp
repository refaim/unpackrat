#include "../../core/wolfrpg/system_codepage.h"

#if !defined(_WIN32)
#error "the unpackrat WolfRPG system code page backend is Windows-only"
#endif

#include <windows.h>

// The project-owned platform adapter behind the neutral core contract in
// src/core/wolfrpg/system_codepage.h. The mature Windows code page engine is used directly - no code
// page table is copied - and every Windows header, type and error code stays inside this translation
// unit. MB_ERR_INVALID_CHARS keeps the conversion strict. The served legacy page maps every byte, so
// the engine returns exactly one UTF-16 unit; a zero return means the engine refused the conversion,
// and the caller's scalar would otherwise stay U+0000. That refusal is reported as an empty neutral
// result rather than a silent NUL.
namespace unpackrat::wolfrpg::text::platform
{
    std::optional<std::uint32_t> single_byte_to_scalar(const std::uint32_t codepage, const unsigned char byte) noexcept
    {
        const char encoded = static_cast<char>(byte);
        wchar_t wide = 0;
        const int converted =
            MultiByteToWideChar(static_cast<UINT>(codepage), MB_ERR_INVALID_CHARS, &encoded, 1, &wide, 1);
        if (converted != 1) {
            return std::nullopt;
        }
        return static_cast<std::uint32_t>(static_cast<unsigned short>(wide));
    }
} // namespace unpackrat::wolfrpg::text::platform
