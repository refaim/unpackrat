#ifndef UNPACKRAT_CORE_WOLFRPG_HUFFMAN_READER_H
#define UNPACKRAT_CORE_WOLFRPG_HUFFMAN_READER_H

#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <stdexcept>

namespace unpackrat::wolfrpg::codec
{
    // WolfRPG (DXLib/uberwolf) stores each compressed stream as an MSB-first bit-packed header
    // (original size, pressed size and 256 code weights) followed by an LSB-first bit-packed payload
    // that is walked over a Huffman tree rebuilt deterministically from those weights.
    //
    // huffman_reader pulls encoded bytes lazily through a caller-supplied source, so it never needs
    // the whole stream resident and never asks for more than the declared encoded extent:
    //
    //   std::size_t source(std::span<std::byte> destination);
    //
    // The source fills up to destination.size() bytes and returns how many it produced. A return of
    // zero signals end of stream, partial fills are accepted, and any exception it throws (including
    // during the constructor's header probe) propagates unchanged. The header is validated against
    // encoded_size and expected_size before any payload byte is decoded.
    class huffman_error final : public std::runtime_error
    {
      public:
        using std::runtime_error::runtime_error;
    };

    class huffman_reader final
    {
      public:
        using source = std::function<std::size_t(std::span<std::byte>)>;

        huffman_reader(source pull, std::uint64_t encoded_size, std::uint64_t expected_size);
        ~huffman_reader();

        huffman_reader(const huffman_reader &) = delete;
        huffman_reader &operator=(const huffman_reader &) = delete;
        huffman_reader(huffman_reader &&) noexcept;
        huffman_reader &operator=(huffman_reader &&) noexcept;

        // Builds a reader for a caller that does not yet know the decoded size. The original size
        // carried by the encoded header becomes the expected size, so remaining() reports the real
        // decoded length before the first read. The header's original size is validated against
        // max_decoded_size immediately after it is decoded and before any weight or payload byte is
        // requested: an original that exceeds the bound raises huffman_error without reading
        // further. All malformed-extent, weight and payload errors and source-exception semantics
        // are identical to the exact constructor.
        [[nodiscard]] static huffman_reader bounded(source pull, std::uint64_t encoded_size,
                                                    std::uint64_t max_decoded_size);

        // Decodes up to destination.size() bytes and returns how many were written; a short return
        // means the stream is exhausted. Throws huffman_error on malformed data and lets any source
        // exception escape unchanged.
        std::size_t read(std::span<std::byte> destination);

        // Bytes still to be produced. Zero for an exhausted or moved-from reader.
        [[nodiscard]] std::uint64_t remaining() const noexcept;

      private:
        enum class size_mode : std::uint8_t
        {
            exact,
            bounded,
        };

        huffman_reader(source pull, std::uint64_t encoded_size, std::uint64_t expected_size_or_bound, size_mode mode);

        class impl;
        std::unique_ptr<impl> impl_;
    };
} // namespace unpackrat::wolfrpg::codec
#endif
