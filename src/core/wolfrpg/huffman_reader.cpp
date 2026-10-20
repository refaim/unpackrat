#include "huffman_reader.h"

#include <uberwolf/Archive/BoundedHuffman.hpp>

#include <cstddef>
#include <cstdint>
#include <exception>
#include <memory>
#include <span>
#include <utility>

namespace unpackrat::wolfrpg::codec
{
    namespace
    {
        // The reader is only a lifetime and error-translation shell around the one shared vendor
        // context. Every byte-ordering, tree and validation rule now lives in
        // UberWolfLib/Archive/BoundedHuffman.hpp, which the patched DXLib Huffman_Decode also drives,
        // so there is exactly one Huffman implementation in the product.
        using uberwolf::archive::bounded_huffman;
        using uberwolf::archive::huffman_size_mode;
        using uberwolf::archive::huffman_status;

        // One message per huffman_status value, indexed by the value, so the translation is total
        // with no unreachable switch arm to leave uncovered.
        constexpr const char *status_messages[] = {
            "wolfrpg huffman decoder failure",                         // ok (never reported)
            "wolfrpg huffman header truncated",                        // header_truncated
            "wolfrpg huffman original size exceeds bound",             // original_exceeds_bound
            "wolfrpg huffman original size mismatch",                  // original_mismatch
            "wolfrpg huffman weight out of range",                     // weight_out_of_range
            "wolfrpg huffman payload exceeds encoded extent",          // payload_exceeds_extent
            "wolfrpg huffman trailing bytes outside declared payload", // trailing_bytes
            "wolfrpg huffman empty stream carries a payload",          // empty_with_payload
            "wolfrpg huffman payload truncated",                       // payload_truncated
            "wolfrpg huffman payload not fully consumed",              // payload_not_consumed
            "wolfrpg huffman source failed",                           // source_failure
        };

        [[nodiscard]] const char *describe(huffman_status status) noexcept
        {
            return status_messages[static_cast<std::size_t>(status)];
        }
    } // namespace

    class huffman_reader::impl final
    {
      public:
        impl(source pull, std::uint64_t encoded_size, std::uint64_t expected_size, size_mode mode)
            : pull_(std::move(pull)),
              mode_(mode == size_mode::exact ? huffman_size_mode::exact : huffman_size_mode::bounded)
        {
            if (!pull_) {
                throw huffman_error("wolfrpg huffman source is empty");
            }

            // The exact constructor knows the encoded extent and the exact decoded size; the bounded
            // factory knows the extent and a decoded-size ceiling. The vendor context reconstructs
            // the header from a single byte source that never over-reads and enforces the sizes
            // before a weight or payload byte is requested.
            // ``expected_size`` is the exact decoded length for the exact constructor and the ceiling
            // for the bounded factory; the vendor distinguishes them by mode.
            const huffman_status status = decoder_.init(&impl::pull_bytes, this, mode_, encoded_size, expected_size);
            if (status != huffman_status::ok) {
                fail(status);
            }
        }

        std::size_t read(std::span<std::byte> destination)
        {
            std::size_t written = 0;
            const huffman_status status =
                decoder_.read(reinterpret_cast<unsigned char *>(destination.data()), destination.size(), &written);
            if (status != huffman_status::ok) {
                fail(status);
            }
            return written;
        }

        [[nodiscard]] std::uint64_t remaining() const noexcept
        {
            return decoder_.remaining();
        }

      private:
        // Bridges the C++ source callback to the vendor's no-throw C function pointer. The user
        // callback may throw, and it may also report more bytes than the destination can hold; the
        // vendor contract forbids both crossing the boundary. An over-long fill is flagged so the
        // reader can name it, a throw is captured so its exact type and payload can be rethrown.
        static std::size_t pull_bytes(void *context, unsigned char *destination, std::size_t capacity)
        {
            impl &self = *static_cast<impl *>(context);
            try {
                const std::size_t produced =
                    self.pull_(std::span<std::byte>(reinterpret_cast<std::byte *>(destination), capacity));
                if (produced > capacity) {
                    self.overfilled_ = true;
                    return bounded_huffman::source_failed;
                }
                return produced;
            } catch (...) {
                self.exception_ = std::current_exception();
                return bounded_huffman::source_failed;
            }
        }

        // Translates a non-ok vendor status into the reader's contract: a source failure is either
        // the caller's own exception (rethrown unchanged) or a contract violation named as such;
        // everything else is reported as an huffman_error.
        [[noreturn]] void fail(huffman_status status)
        {
            if (status == huffman_status::source_failure) {
                if (exception_) {
                    std::rethrow_exception(exception_);
                }
                if (overfilled_) {
                    throw huffman_error("wolfrpg huffman source overfilled");
                }
            }
            throw huffman_error(describe(status));
        }

        source pull_;
        bounded_huffman decoder_;
        huffman_size_mode mode_;
        std::exception_ptr exception_;
        bool overfilled_ = false;
    };

    huffman_reader::huffman_reader(source pull, std::uint64_t encoded_size, std::uint64_t expected_size)
        : impl_(std::make_unique<impl>(std::move(pull), encoded_size, expected_size, size_mode::exact))
    {
    }

    huffman_reader::huffman_reader(source pull, std::uint64_t encoded_size, std::uint64_t expected_size_or_bound,
                                   size_mode mode)
        : impl_(std::make_unique<impl>(std::move(pull), encoded_size, expected_size_or_bound, mode))
    {
    }

    huffman_reader huffman_reader::bounded(source pull, std::uint64_t encoded_size, std::uint64_t max_decoded_size)
    {
        return huffman_reader(std::move(pull), encoded_size, max_decoded_size, size_mode::bounded);
    }

    huffman_reader::~huffman_reader() = default;
    huffman_reader::huffman_reader(huffman_reader &&) noexcept = default;
    huffman_reader &huffman_reader::operator=(huffman_reader &&) noexcept = default;

    std::size_t huffman_reader::read(std::span<std::byte> destination)
    {
        return impl_ ? impl_->read(destination) : 0;
    }

    std::uint64_t huffman_reader::remaining() const noexcept
    {
        return impl_ ? impl_->remaining() : 0;
    }
} // namespace unpackrat::wolfrpg::codec
