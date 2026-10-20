#include "../extractor.h"
#include "pak.h"

#include <cstddef>

// build/unpackrat-module-version.props injects the version macros from src/modules/zanzarah/VERSION,
// the single source of truth. A project that does not import that file fails to compile here.

namespace extractor
{
    void configure([[maybe_unused]] const wchar_t *settings) noexcept
    {
    }
    void reset_settings() noexcept
    {
    }

    version_info get_version_info() noexcept
    {
        return {
            {0x86e7e4c3, 0xbc44, 0x4e8e, {0x90, 0xaf, 0xbd, 0xbd, 0x1c, 0xb6, 0x1a, 0x83}},
            UNPACKRAT_MODULE_VERSION_MAJOR,
            UNPACKRAT_MODULE_VERSION_MINOR,
        };
    }

    std::vector<std::byte> extractor::get_signature()
    {
        return {std::byte{0}, std::byte{0}, std::byte{0}, std::byte{0}};
    }

    archive_info extractor::get_archive_info(
        [[maybe_unused]] std::istream &stream, const std::span<const std::byte> &data,
        [[maybe_unused]] const std::filesystem::path &path,
        [[maybe_unused]] const std::string_view password) // NOLINT(*-convert-member-functions-to-static)
    {
        static_cast<void>(data);
        return archive_info{L"Zanzarah", L"", L""};
    }

    std::vector<std::unique_ptr<file>> extractor::list_files(
        std::istream &stream) // NOLINT(*-convert-member-functions-to-static)
    {
        // The index reader and the whole-archive framing policy live in the platform-neutral
        // ``pak.h`` core so they can be unit-tested without the Observer module binary.
        return zanzarah::parse_pak(stream);
    }

    uint32_t extractor::decrypt(uint32_t magic, std::vector<char> &data) const
    {
        static_cast<void>(data);
        return magic;
    }
} // namespace extractor
