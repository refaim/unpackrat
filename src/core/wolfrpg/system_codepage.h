#ifndef UNPACKRAT_CORE_WOLFRPG_SYSTEM_CODEPAGE_H
#define UNPACKRAT_CORE_WOLFRPG_SYSTEM_CODEPAGE_H

#include <cstdint>
#include <optional>

namespace unpackrat::wolfrpg::text::platform
{
    // Strictly convert one byte through the host code-page engine. A refused or incomplete input
    // returns no scalar. The platform adapter owns all OS types and headers.
    [[nodiscard]] std::optional<std::uint32_t> single_byte_to_scalar(std::uint32_t codepage,
                                                                     unsigned char byte) noexcept;
} // namespace unpackrat::wolfrpg::text::platform
#endif
