#include "legacy_crypto.h"

#include <algorithm>

// The pinned vendor headers are C-style and warn freely under /W4; keep them quiet without
// relaxing warnings on our own translation units. Their macros stay inside this unit.
#pragma warning(push, 0)
#include <uberwolf/DXLib/DXArchiveVer5.h>
#include <uberwolf/WolfDec.h>
#pragma warning(pop)

namespace unpackrat::wolfrpg::legacy
{
    key derive_key(const std::string_view source)
    {
        std::array<char, key_length + 1> buffer{};
        const std::size_t copied = std::min(source.size(), key_length);
        for (std::size_t index = 0; index < copied; ++index) {
            buffer[index] = source[index];
        }

        // The reference KeyCreate loops on the C-string length, so a source whose effective C-string
        // length is zero (an empty view, or one that begins with a NUL byte) would spin it forever.
        // Fold any zero-length effective C string onto the documented empty/default key instead of
        // handing the vendor a non-NULL buffer it would strlen to zero.
        std::size_t effective = 0;
        while (effective < copied && buffer[effective] != '\0') {
            ++effective;
        }

        key secret{};
        void (*create)(const char *, unsigned char *) = &DXArchive_VER5::KeyCreate;
        create(effective == 0 ? nullptr : buffer.data(), secret.data());
        return secret;
    }

    key raw_fallback_key() noexcept
    {
        key secret{};
        secret.fill(0xff);
        return secret;
    }

    void apply_key(std::span<std::byte> data, const std::uint64_t phase, const key &secret)
    {
        if (data.empty()) {
            return;
        }

        std::array<unsigned char, key_length> mutable_key{};
        std::copy(secret.begin(), secret.end(), mutable_key.begin());

        void (*conv)(void *, int, int, unsigned char *) = &DXArchive_VER5::KeyConv;
        const int position = static_cast<int>(phase % key_length);
        conv(data.data(), static_cast<int>(data.size()), position, mutable_key.data());
    }

    std::vector<std::string> default_mode_keys()
    {
        std::vector<std::string> keys;
        const CryptModes &modes = WolfDec::DefaultCryptModes();
        const std::size_t count = std::min<std::size_t>(modes.size(), 3);
        keys.reserve(count);
        for (std::size_t index = 0; index < count; ++index) {
            const auto &raw = modes[index].key;
            std::string text;
            text.reserve(key_length);
            const std::size_t length = std::min<std::size_t>(raw.size(), key_length);
            for (std::size_t byte = 0; byte < length; ++byte) {
                text.push_back(static_cast<char>(static_cast<unsigned char>(raw[byte])));
            }
            keys.push_back(std::move(text));
        }
        return keys;
    }
} // namespace unpackrat::wolfrpg::legacy
