#ifndef UNPACKRAT_CORE_IO_BOUNDED_STREAM_H
#define UNPACKRAT_CORE_IO_BOUNDED_STREAM_H

#include <cstddef>
#include <cstdint>
#include <istream>
#include <stdexcept>
#include <type_traits>

namespace unpackrat::io
{
    class read_error final : public std::runtime_error
    {
      public:
        read_error() : std::runtime_error("bounded stream read failed")
        {
        }
    };

    class bounded_stream final
    {
      public:
        explicit bounded_stream(std::istream &stream);
        // Authoritative length supplied by the caller (for example a file length already obtained
        // from the open handle). No end-of-stream scan is performed, so a caller that probes many
        // windows of one stream does not pay a seek-to-end per window. The caller owns the value.
        // A negative length is not a length at all, so it is rejected at this public boundary with
        // io::read_error; a zero length is a valid empty window.
        bounded_stream(std::istream &stream, std::streamoff known_size);

        [[nodiscard]] std::streamoff size() const noexcept;
        [[nodiscard]] std::streamoff position();
        [[nodiscard]] std::streamoff remaining();
        void seek_absolute(std::streamoff offset);
        void read_exact(char *destination, std::size_t byte_count);

        template <typename Value>
            requires std::is_trivially_copyable_v<Value>
        [[nodiscard]] Value read_trivial()
        {
            Value value{};
            read_exact(reinterpret_cast<char *>(&value), sizeof(value));
            return value;
        }

      private:
        std::istream &stream_;
        std::streamoff size_ = 0;
    };
} // namespace unpackrat::io
#endif
