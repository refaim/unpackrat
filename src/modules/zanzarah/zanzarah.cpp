#include "../extractor.h"
#include "pak.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <istream>
#include <memory>
#include <ostream>
#include <span>
#include <utility>
#include <vector>

// build/unpackrat-module-version.props injects the version macros from src/modules/zanzarah/VERSION,
// the single source of truth. A project that does not import that file fails to compile here.

namespace extractor
{
    namespace
    {
        // One currently open archive owns the immutable block extents and the deferred
        // framing decision. Entry callbacks borrow this state; archive destroys its file list
        // before it releases the extractor, so those non-owning pointers stay valid.
        class zanzarah_session final : public storage_state
        {
          public:
            zanzarah_session(std::vector<zanzarah::block_extent> blocks, const std::int64_t archive_size)
                : blocks_(std::move(blocks)), archive_size_(archive_size)
            {
            }

            [[nodiscard]] const zanzarah::block_extent &block(const std::size_t index) const noexcept
            {
                return blocks_[index];
            }

            [[nodiscard]] std::int64_t archive_size() const noexcept
            {
                return archive_size_;
            }

            // The first successful classification is retained for this open lifetime. A source
            // failure leaves the decision unknown so a retry classifies from scratch, while a
            // later body or output failure keeps the already successful decision.
            [[nodiscard]] zanzarah::framing ensure_framing(std::istream &stream,
                                                           const std::function<void(int64_t)> &report_progress)
            {
                if (framing_ == zanzarah::framing::unknown) {
                    try {
                        framing_ = zanzarah::classify_framing(stream, blocks_, archive_size_, report_progress);
                    } catch (const unpackrat::io::read_error &) {
                        throw read_error();
                    }
                }
                return framing_;
            }

          private:
            std::vector<zanzarah::block_extent> blocks_;
            std::int64_t archive_size_ = 0;
            zanzarah::framing framing_ = zanzarah::framing::unknown;
        };

        // Copies exactly the bytes the whole-archive decision admits. The marker reads happen
        // only inside ensure_framing, before the first body byte is copied.
        void extract_entry(zanzarah_session &session, const std::size_t index, std::istream &source,
                           std::ostream &destination, const std::function<void(int64_t)> &report_progress)
        {
            // A failed read leaves failbit/badbit on the archive-owned source stream. The retry
            // is a fresh operation, so clear that state before probing or seeking again; the
            // successful framing decision, if any, stays in the session.
            source.clear();
            // An empty body still offers one cancellation opportunity before any marker or
            // payload byte is read. The zero delta never changes the extracted-byte count.
            report_progress(0);

            const zanzarah::block_extent block = session.block(index);
            const zanzarah::framing classification = session.ensure_framing(source, report_progress);
            int64_t bytes_left = zanzarah::framed_body_size(block.provisional_size, classification);

            constexpr std::int64_t buffer_size = 128LL * 1024;
            std::vector<char> buffer(static_cast<std::size_t>(buffer_size));
            unpackrat::io::bounded_stream input(source, session.archive_size());
            try {
                input.seek_absolute(block.offset);
                while (bytes_left > 0) {
                    const auto chunk_size = static_cast<std::size_t>(std::min(bytes_left, buffer_size));
                    input.read_exact(buffer.data(), chunk_size);
                    // Output failures and cancellation escape unchanged so archive applies its
                    // documented write_error and partial-output cleanup handling.
                    destination.write(buffer.data(), static_cast<std::streamsize>(chunk_size));
                    bytes_left -= static_cast<std::int64_t>(chunk_size);
                    report_progress(static_cast<std::int64_t>(chunk_size));
                }
            } catch (const unpackrat::io::read_error &) {
                throw read_error();
            }
        }
    } // namespace

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
        // ``pak.h`` core so they can be unit-tested without the Observer module binary. Listing
        // publishes the provisional upper bound and performs no body access.
        auto index = zanzarah::read_pak_index(stream);
        auto session = std::make_unique<zanzarah_session>(std::move(index.blocks), index.archive_size);
        auto *borrowed = session.get();
        state_ = std::move(session);

        auto files = std::move(index.files);
        for (std::size_t position = 0; position < files.size(); ++position) {
            files[position]->extract = [borrowed, position](std::istream &in, std::ostream &out,
                                                            const std::function<void(int64_t)> &report_progress) {
                extract_entry(*borrowed, position, in, out, report_progress);
            };
        }
        return files;
    }

    uint32_t extractor::decrypt(uint32_t magic, std::vector<char> &data) const
    {
        static_cast<void>(data);
        return magic;
    }
} // namespace extractor
