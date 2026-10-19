#include "../../core/wolfrpg/archive.h"
#include "../extractor.h"

#include <cstddef>
#include <cstdint>
#include <exception>
#include <filesystem>
#include <functional>
#include <istream>
#include <memory>
#include <ostream>
#include <span>
#include <streambuf>
#include <string>
#include <string_view>
#include <vector>

// The classic Wolf RPG / DXLib ("DX8") Observer adapter. The platform-neutral reader lives in
// src/core/wolfrpg and owns all DXLib/UberWolf primitives behind its own interface; this adapter
// only maps that interface onto the Observer extractor contract.
//
// build/unpackrat-module-version.props injects the version macros from src/modules/wolfrpg/VERSION,
// the single source of truth. A project that does not import that file fails to compile here.

namespace extractor
{
    namespace
    {
        namespace wolfrpg = unpackrat::wolfrpg;

        // Per-storage lifetime state. The opened reader (and therefore every byte range it
        // validates) is owned here for as long as the archive wrapper keeps this extractor alive.
        // Entry callbacks borrow a pointer to this state plus their entry index because the file
        // list is destroyed before the wrapper releases the extractor.
        class wolfrpg_session final : public storage_state
        {
          public:
            explicit wolfrpg_session(std::unique_ptr<wolfrpg::archive> opened) : archive_(std::move(opened))
            {
            }

            wolfrpg::archive &archive() noexcept
            {
                return *archive_;
            }

          private:
            std::unique_ptr<wolfrpg::archive> archive_;
        };

        // Adapter-private control signal. A caller-supplied exception (raised by the destination
        // stream or the progress callback) is captured once in an exception_ptr and this signal is
        // thrown in its place, so the backend error translation below can never rewrite a caller
        // exception whose concrete type happens to equal one of the reader's own error types.
        struct forwarded_exception
        {
        };

        // Forwards every write to the caller's destination stream so its state, exception mask and
        // quiet-failure behaviour are preserved exactly. Each write delegates to the destination,
        // whose own state and mask stay authoritative, and always reports the full requested count:
        // the wrapper therefore never accumulates a failure state of its own, so a destination that
        // quietly drops a write behaves exactly like writing to it directly and a later write still
        // goes through once the caller clears it. Any exception the destination raises is captured
        // once (the same exception_ptr is rethrown later, never re-created) and replaced by
        // forwarded_exception so extract_entry can unwind past the backend translation untouched.
        class forwarding_streambuf final : public std::streambuf
        {
          public:
            forwarding_streambuf(std::ostream &destination, std::exception_ptr &forwarded) noexcept
                : destination_(&destination), forwarded_(&forwarded)
            {
            }

          protected:
            std::streamsize xsputn(const char *data, const std::streamsize count) override
            {
                try {
                    destination_->write(data, count);
                } catch (...) {
                    *forwarded_ = std::current_exception();
                    throw forwarded_exception{};
                }
                return count;
            }

          private:
            std::ostream *destination_;
            std::exception_ptr *forwarded_;
        };

        // A destination stream whose buffer forwards to the caller's stream. Its badbit is always in
        // the exception mask so the standard stream machinery re-raises a captured destination
        // exception instead of folding it into badbit, whatever the caller's initial mask. A quiet
        // destination failure never sets badbit here because the buffer always reports the full
        // count, so a quiet caller keeps seeing quiet behaviour.
        class forwarding_ostream final : public std::ostream
        {
          public:
            forwarding_ostream(std::ostream &destination, std::exception_ptr &forwarded)
                : std::ostream(nullptr), buffer_(destination, forwarded)
            {
                rdbuf(&buffer_);
                exceptions(std::ios::badbit);
            }

          private:
            forwarding_streambuf buffer_;
        };

        // Streams one entry body. Only the reader's genuine errors are translated: a source-stream
        // fault becomes our read_error and a required key becomes our password_required. A caller
        // exception (destination-stream failure, cancellation or any other failure from the output
        // stream or the progress callback) is rethrown as the very object that was raised, so its
        // concrete type and payload survive even when they equal a reader error type.
        void extract_entry(wolfrpg_session &session, const std::size_t index, std::istream &in, std::ostream &out,
                           const std::function<void(int64_t)> &report_progress)
        {
            std::exception_ptr forwarded;
            const std::function<void(int64_t)> guarded_progress = [&forwarded, &report_progress](const int64_t delta) {
                try {
                    report_progress(delta);
                } catch (...) {
                    forwarded = std::current_exception();
                    throw forwarded_exception{};
                }
            };
            forwarding_ostream guarded_output(out, forwarded);

            try {
                session.archive().extract(index, in, guarded_output, guarded_progress);
            } catch (const forwarded_exception &) {
                std::rethrow_exception(forwarded);
            } catch (const wolfrpg::read_error &) {
                throw read_error();
            } catch (const wolfrpg::key_required &) {
                throw password_required();
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
            {0x709c4932, 0xb3b2, 0x47d3, {0xb0, 0x35, 0xb5, 0xaf, 0xe7, 0xab, 0x7a, 0x34}},
            UNPACKRAT_MODULE_VERSION_MAJOR,
            UNPACKRAT_MODULE_VERSION_MINOR,
        };
    }

    // Encrypted legacy archives do not share a fixed plaintext prefix, so the adapter offers no
    // signature and lets every candidate reach the reader rather than pre-filtering valid headers.
    std::vector<std::byte> extractor::get_signature()
    {
        return {};
    }

    archive_info extractor::get_archive_info(std::istream &stream, const std::span<const std::byte> &data,
                                             const std::filesystem::path &path, const std::string_view password)
    {
        // The Observer preview span may be empty; validation always runs against the real stream so
        // an unsupported or truncated header still fails the open instead of surfacing as a listing
        // error later. A rejected open propagates, letting the API layer report SOR_INVALID_FILE.
        static_cast<void>(data);
        std::unique_ptr<wolfrpg::archive> opened;
        try {
            opened = wolfrpg::archive::open(stream, path, password);
        } catch (const wolfrpg::key_required &) {
            // A keyed header without a matching password is a recoverable open failure the API layer
            // reports as SOR_PASSWORD_REQUIRED rather than a generic invalid file.
            throw password_required();
        } catch (const wolfrpg::read_error &) {
            // A malformed or truncated header is an open failure of its own kind; translate the
            // reader's error so the API layer reports SOR_INVALID_FILE instead of an unknown error.
            throw read_error();
        }
        const std::uint32_t version = opened->version();
        state_ = std::make_unique<wolfrpg_session>(std::move(opened));
        return archive_info{L"WolfRPG", L"DX8", L"v" + std::to_wstring(version)};
    }

    std::vector<std::unique_ptr<file>> extractor::list_files(std::istream &stream)
    {
        static_cast<void>(stream);
        // The archive layer calls list_files only after get_archive_info admitted this source, so
        // the owned session is always present here and its entry metadata mirrors the reader.
        auto &session = static_cast<wolfrpg_session &>(*state_);
        const std::span<const wolfrpg::entry> entries = session.archive().entries();

        std::vector<std::unique_ptr<file>> files;
        files.reserve(entries.size());
        for (std::size_t index = 0; index < entries.size(); ++index) {
            const wolfrpg::entry &source = entries[index];
            auto item = std::make_unique<file>();
            item->path = source.path;
            item->compressed_body_size_in_bytes = source.stored_size;
            item->uncompressed_body_size_in_bytes = source.unpacked_size;
            item->extract = [session = &session, index](std::istream &in, std::ostream &out,
                                                        const std::function<void(int64_t)> &report_progress) {
                extract_entry(*session, index, in, out, report_progress);
            };
            files.push_back(std::move(item));
        }
        return files;
    }

    // Custom extraction owns every entry body, so the default header/body copy path with its
    // rolling decrypt is never used and the identity transform keeps the contract explicit.
    uint32_t extractor::decrypt(uint32_t magic, std::vector<char> &data) const
    {
        static_cast<void>(data);
        return magic;
    }
} // namespace extractor
