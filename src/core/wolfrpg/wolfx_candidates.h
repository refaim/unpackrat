#ifndef UNPACKRAT_CORE_WOLFRPG_WOLFX_CANDIDATES_H
#define UNPACKRAT_CORE_WOLFRPG_WOLFX_CANDIDATES_H

#include <cstddef>
#include <cstdint>
#include <span>
#include <stdexcept>
#include <string>
#include <vector>

// Caller-supplied decryption candidates for a single WolfX file. A WolfX file is one
// encrypted blob whose key search mixes a password-like key, a magic string and a magic
// integer; this value type carries whatever the caller recovered from the map and
// common-event metadata (a later collector) plus any user password, and it enforces the
// resource budgets that keep a hostile archive from driving an unbounded search.
//
// The empty key is implicit: it is always tried first and is never stored here. Public
// shapes stay free of vendor and Win32 types so the adapter layer owns the boundary.
namespace unpackrat::wolfrpg
{
    struct wolfx_candidate_limits final
    {
        std::size_t max_key_length = 4096;
        std::size_t max_total_key_bytes = 1U << 20u;
        std::size_t max_total_string_bytes = 1U << 22u;
        std::size_t max_string_values = 1U << 16u;
        std::size_t max_int_values = 1U << 20u;
        std::size_t max_attempts = 1U << 24u;
    };

    struct wolfx_string_value final
    {
        std::uint32_t id = 0;
        std::string value;
    };

    struct wolfx_int_value final
    {
        std::uint32_t id = 0;
        std::uint32_t value = 0;
    };

    class wolfx_candidates final
    {
      public:
        wolfx_candidates() = default;
        explicit wolfx_candidates(const wolfx_candidate_limits &limits) : limits_(limits)
        {
        }

        [[nodiscard]] const wolfx_candidate_limits &limits() const noexcept
        {
            return limits_;
        }

        // Each add validates the explicit budgets and throws std::invalid_argument when a
        // limit would be exceeded, so a caller cannot smuggle an unbounded search in. An empty
        // key is rejected because it is already implicit, and would otherwise be an unbounded,
        // cost-free entry.
        void add_key(std::string key);
        void add_string_value(std::uint32_t id, std::string value);
        void add_int_value(std::uint32_t id, std::uint32_t value);

        [[nodiscard]] std::span<const std::string> keys() const noexcept
        {
            return keys_;
        }

        [[nodiscard]] std::span<const wolfx_string_value> string_values() const noexcept
        {
            return strings_;
        }

        [[nodiscard]] std::span<const wolfx_int_value> int_values() const noexcept
        {
            return ints_;
        }

      private:
        wolfx_candidate_limits limits_{};
        std::vector<std::string> keys_;
        std::vector<wolfx_string_value> strings_;
        std::vector<wolfx_int_value> ints_;
        std::size_t total_key_bytes_ = 0;
        std::size_t total_string_bytes_ = 0;
    };
} // namespace unpackrat::wolfrpg
#endif
