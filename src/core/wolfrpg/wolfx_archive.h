#ifndef UNPACKRAT_CORE_WOLFRPG_WOLFX_ARCHIVE_H
#define UNPACKRAT_CORE_WOLFRPG_WOLFX_ARCHIVE_H

#include "archive.h"
#include "wolfx_candidates.h"

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <istream>
#include <memory>
#include <ostream>
#include <span>

// A WolfX container (the "WolfX" single-file encryption used by newer Wolf RPG games):
// one encrypted blob, not a directory archive, so the parsed container exposes exactly one
// entry whose body is the decrypted payload. The reader owns no Observer or Win32
// dependency; the UberWolf/WolfX primitives are confined to the implementation unit and
// every archive byte, offset and candidate is treated as untrusted input.
//
// The backend never buffers the whole file: it reads a 20-byte header, then pulls only the
// five checksum sample bytes through a random-access read callback, and finally streams the
// decrypted payload in bounded chunks. The search is a pure value computation, so two
// archives never share state and a successful open never rejects another key.
namespace unpackrat::wolfrpg
{
    class wolfx_archive final
    {
      public:
        // Construction is owned by open(): the passkey keeps the single-argument constructor
        // out of the public API while still allowing std::make_unique to allocate the object.
        struct passkey final
        {
            passkey(const passkey &) = delete;
            passkey &operator=(const passkey &) = delete;
            passkey(passkey &&) = delete;
            passkey &operator=(passkey &&) = delete;

          private:
            friend class wolfx_archive;
            passkey() = default;
        };

        ~wolfx_archive();
        wolfx_archive(wolfx_archive &&) noexcept;
        wolfx_archive &operator=(wolfx_archive &&) noexcept;
        wolfx_archive(const wolfx_archive &) = delete;
        wolfx_archive &operator=(const wolfx_archive &) = delete;
        // The passkey is taken by const reference: std::make_unique forwards its argument as an
        // rvalue, and the passkey is deliberately neither copyable nor movable, so a by-value
        // parameter would need a move that does not exist. A const reference binds that rvalue
        // without transferring ownership of anything.
        explicit wolfx_archive(const passkey &);

        // Parses and decrypts the header of a WolfX stream. ``path`` only supplies the output
        // entry name (a trailing ``.wolfx`` is stripped and the remainder must be a safe file
        // name). Structural corruption raises read_error; a header that cannot be decrypted
        // with the supplied candidates raises key_required. The stream must be seekable and is
        // left untouched for extract().
        static std::unique_ptr<wolfx_archive> open(std::istream &stream, const std::filesystem::path &path,
                                                   const wolfx_candidates &candidates = {});

        [[nodiscard]] std::span<const entry> entries() const noexcept;
        // The WolfX container revision (constant 1): the format carries no version field, so
        // this identifies the single-file WolfX container the backend understands.
        [[nodiscard]] std::uint32_t version() const noexcept;

        // Streams the single decrypted payload to ``output``. Source-stream failures, whether
        // reported through the stream state or thrown by an exception-enabled stream, are
        // normalized to read_error. A write that fails the output stream's state raises
        // read_error, while any exception thrown by ``output`` or by ``progress`` (cancellation)
        // propagates unchanged.
        void extract(std::size_t index, std::istream &stream, std::ostream &output,
                     const std::function<void(std::int64_t)> &progress) const;

      private:
        struct impl;
        std::unique_ptr<impl> impl_;
    };
} // namespace unpackrat::wolfrpg
#endif
