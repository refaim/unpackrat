#include "wolfx_archive.h"

#include <uberwolf/WolfX/Stream.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <limits>
#include <stdexcept>
#include <streambuf>
#include <string>
#include <string_view>
#include <vector>

namespace unpackrat::wolfrpg
{
    namespace
    {
        constexpr std::size_t header_size = 20;
        constexpr std::uint32_t wolfx_version = 1;
        constexpr std::size_t extract_chunk = 32U * 1024U;

        constexpr std::array<std::uint8_t, 5> wolfx_magic = {0x57, 0x4F, 0x4C, 0x46, 0x58}; // "WOLFX"

        [[nodiscard]] bool is_unsafe_name_byte(const char value) noexcept
        {
            const auto byte = static_cast<unsigned char>(value);
            return byte < 0x20U || value == ':';
        }

        // Derives the plaintext output name from the input path: the last path component is
        // taken (so directory parts and drive specs never leak into the entry name), the
        // trailing ``.wolfx`` extension (case-insensitive) is stripped, and the remainder must
        // be a bare, safe file name.
        [[nodiscard]] std::string decode_name(const std::filesystem::path &path)
        {
            constexpr std::string_view suffix = ".wolfx";
            const std::string raw = path.string();
            const std::size_t separator = raw.find_last_of("/\\");
            std::string stem = separator == std::string::npos ? raw : raw.substr(separator + 1);

            if (stem.size() >= suffix.size()) {
                bool matches = true;
                for (std::size_t i = 0; i < suffix.size(); ++i) {
                    const char lowered = static_cast<char>(
                        std::tolower(static_cast<unsigned char>(stem[stem.size() - suffix.size() + i])));
                    if (lowered != suffix[i]) {
                        matches = false;
                        break;
                    }
                }
                if (matches)
                    stem.resize(stem.size() - suffix.size());
            }

            if (stem.empty() || stem == "." || stem == "..")
                throw read_error();
            for (const char value : stem) {
                if (is_unsafe_name_byte(value))
                    throw read_error();
            }
            return stem;
        }

        // Source I/O is driven straight at the stream buffer instead of through the formatted
        // istream layer. std::istream operations (clear/seekg/read/tellg) catch every exception a
        // buffer raises and merely latch state: with the exception mask off they swallow the
        // original error - a resource failure such as std::bad_alloc or std::length_error - and
        // only report a short count afterwards. Working one level down keeps a caller-owned
        // exception, including a resource error, exactly as thrown, while a plain buffer failure
        // (a rejected seek or a short read) is still normalized to read_error. The catch is
        // deliberately typed to the I/O failure only.
        std::streambuf &source_buffer(std::istream &stream)
        {
            std::streambuf *buffer = stream.rdbuf();
            if (buffer == nullptr)
                throw read_error();
            return *buffer;
        }

        // Seeks the buffer to an absolute byte offset. seekoff is preferred so a buffer that
        // implements only seekoff is enough on its own; a buffer that rejects it falls back to
        // seekpos. A rejected seek, or a landing position the buffer does not honour, is a source
        // error.
        void seek_input(std::istream &stream, const std::uint64_t position)
        {
            std::streambuf &buffer = source_buffer(stream);
            const auto target = static_cast<std::streamoff>(position);
            std::streamoff landed = -1;
            try {
                landed = static_cast<std::streamoff>(buffer.pubseekoff(target, std::ios_base::beg, std::ios_base::in));
                if (landed < 0)
                    landed = static_cast<std::streamoff>(buffer.pubseekpos(target, std::ios_base::in));
            } catch (const std::ios_base::failure &) {
                throw read_error();
            }
            if (landed != target)
                throw read_error();
        }

        void read_input(std::istream &stream, char *data, const std::streamsize count)
        {
            std::streambuf &buffer = source_buffer(stream);
            std::streamsize got = 0;
            try {
                got = buffer.sgetn(data, count);
            } catch (const std::ios_base::failure &) {
                throw read_error();
            }
            if (got != count)
                throw read_error();
        }

        // Physical size of the source, measured by seeking to its end. The signed result is
        // returned as-is so the caller can validate it before it is widened: a negative end
        // position (a rejected end seek) must never become a huge unsigned offset.
        [[nodiscard]] std::streamoff source_size(std::istream &stream)
        {
            std::streambuf &buffer = source_buffer(stream);
            try {
                return static_cast<std::streamoff>(buffer.pubseekoff(0, std::ios_base::end, std::ios_base::in));
            } catch (const std::ios_base::failure &) {
                throw read_error();
            }
        }
    } // namespace

    struct wolfx_archive::impl
    {
        entry item;
        std::uint64_t payload_offset = 0;
        wolfx::DecryptBlob blob{};
    };

    wolfx_archive::wolfx_archive(const passkey &)
    {
    }
    wolfx_archive::wolfx_archive(wolfx_archive &&) noexcept = default;
    wolfx_archive &wolfx_archive::operator=(wolfx_archive &&) noexcept = default;
    wolfx_archive::~wolfx_archive() = default;

    std::unique_ptr<wolfx_archive> wolfx_archive::open(std::istream &stream, const std::filesystem::path &path,
                                                       const wolfx_candidates &candidates)
    {
        const std::string name = decode_name(path);

        std::array<std::uint8_t, header_size> header{};
        seek_input(stream, 0);
        read_input(stream, reinterpret_cast<char *>(header.data()), static_cast<std::streamsize>(header.size()));

        const std::streamoff end = source_size(stream);
        if (end < static_cast<std::streamoff>(header_size))
            throw read_error();
        const auto physical = static_cast<std::uint64_t>(end);

        if (!std::equal(wolfx_magic.begin(), wolfx_magic.end(), header.begin()))
            throw read_error();

        wolfx::WolfXDecryptCollection collection;
        collection.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", ""});
        for (const std::string &key : candidates.keys())
            collection.decryptKeys.push_back(wolfx::WolfXDecryptKey{"/", key});
        for (const wolfx_string_value &value : candidates.string_values())
            collection.stringValues[value.id].insert(value.value);
        for (const wolfx_int_value &value : candidates.int_values())
            collection.intValues[value.id].insert(value.value);

        const auto read_sample = [&stream](const std::uint64_t position) -> std::uint8_t {
            char value = 0;
            seek_input(stream, position);
            read_input(stream, &value, 1);
            return static_cast<std::uint8_t>(static_cast<unsigned char>(value));
        };

        const wolfx::stream::Plan plan =
            wolfx::stream::prepare(header, physical, collection, read_sample, candidates.limits().max_attempts);
        if (!plan.found)
            throw key_required();

        const auto stored_size = static_cast<std::int64_t>(physical - plan.payloadOffset);

        auto result = std::make_unique<wolfx_archive>(wolfx_archive::passkey{});
        result->impl_ = std::make_unique<impl>();
        result->impl_->item = entry{name, stored_size, stored_size};
        result->impl_->payload_offset = plan.payloadOffset;
        result->impl_->blob = plan.blob;
        return result;
    }

    std::span<const entry> wolfx_archive::entries() const noexcept
    {
        if (!impl_)
            return {};
        return std::span<const entry>(&impl_->item, 1);
    }

    std::uint32_t wolfx_archive::version() const noexcept
    {
        return wolfx_version;
    }

    void wolfx_archive::extract(std::size_t index, std::istream &stream, std::ostream &output,
                                const std::function<void(std::int64_t)> &progress) const
    {
        if (!impl_ || index >= 1)
            throw std::out_of_range("WolfX contains a single entry");

        const std::uint64_t total = static_cast<std::uint64_t>(impl_->item.stored_size);
        std::vector<std::uint8_t> input(extract_chunk);
        std::vector<std::uint8_t> plaintext(extract_chunk);

        std::uint64_t offset = impl_->payload_offset;
        std::uint64_t remaining = total;
        while (remaining > 0) {
            const auto chunk = static_cast<std::size_t>(std::min<std::uint64_t>(remaining, extract_chunk));
            seek_input(stream, offset);
            read_input(stream, reinterpret_cast<char *>(input.data()), static_cast<std::streamsize>(chunk));

            wolfx::stream::xorRange(input.data(), chunk, offset, impl_->blob, plaintext.data());
            output.write(reinterpret_cast<const char *>(plaintext.data()), static_cast<std::streamsize>(chunk));
            if (!output)
                throw read_error();

            if (progress)
                progress(static_cast<std::int64_t>(chunk));

            offset += chunk;
            remaining -= chunk;
        }
    }
} // namespace unpackrat::wolfrpg
