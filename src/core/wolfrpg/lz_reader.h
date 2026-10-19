#ifndef UNPACKRAT_CORE_WOLFRPG_LZ_READER_H
#define UNPACKRAT_CORE_WOLFRPG_LZ_READER_H

#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <stdexcept>

namespace unpackrat::wolfrpg::codec
{
    // Raised for a structurally invalid DXLib LZ stream, for a short input source, or for an
    // encoded region that does not decode to exactly the declared output size.
    class lz_error final : public std::runtime_error
    {
      public:
        lz_error();
        explicit lz_error(const char *message);
    };

    // Bounded pull decoder for the DXLib/WolfRPG LZ block format. This is a thin streaming adapter
    // over the shared UberWolf kernel (UberWolfLib/Archive/BoundedLz.hpp), the same resumable
    // interpreter DXArchive::Decode delegates to (see licenses/uberwolf.txt, section
    // "3rdParty/DXLib"); unlike the original void*/void* routine it enforces the encoded and
    // decoded bounds supplied at construction and never reads past the caller-declared encoded
    // size.
    class lz_reader final
    {
      public:
        // Sequential encoded-byte source. Writes at most destination.size() bytes, returns the
        // number written (0 meaning end of input), and may return short positive counts. The
        // reader never requests more than the remaining encoded size, and any exception the source
        // throws propagates unchanged.
        using source = std::function<std::size_t(std::span<std::byte> destination)>;

        // Reads and validates the 9-byte header. Throws lz_error when the source ends before the
        // header, when the declared sizes disagree with the arguments, when encoded_size is smaller
        // than the header, or when a zero-output block carries bytes beyond its 9-byte header. A
        // valid zero-output block is finished at construction, so remaining() is zero immediately.
        lz_reader(source input, std::uint64_t encoded_size, std::uint64_t expected_size);

        ~lz_reader();

        // Moving transfers the decode state to the destination and leaves the source as a safe
        // empty reader: remaining() is zero and read() returns zero instead of dereferencing a null
        // state. A moved-from reader may be reassigned a fresh reader.
        lz_reader(lz_reader &&other) noexcept;
        lz_reader &operator=(lz_reader &&other) noexcept;
        lz_reader(const lz_reader &) = delete;
        lz_reader &operator=(const lz_reader &) = delete;

        // Decodes up to out.size() bytes. Returns 0 for an empty output span without consuming
        // encoded input, returns 0 once the whole declared output has been produced, and returns 0
        // on a moved-from reader.
        std::size_t read(std::span<std::byte> out);

        // Bytes still required to reach the declared output size; zero on a moved-from reader.
        [[nodiscard]] std::uint64_t remaining() const noexcept;

      private:
        struct state;
        std::unique_ptr<state> state_;
    };
} // namespace unpackrat::wolfrpg::codec
#endif
