#include "wolfx_candidates.h"

#include <utility>

namespace unpackrat::wolfrpg
{
    void wolfx_candidates::add_key(std::string key)
    {
        // The empty key is implicit (the backend always tries it first), so accepting it here
        // would only add a zero-cost entry that the byte budget cannot bound. Reject it before
        // any state changes; every stored key then costs at least one byte.
        if (key.empty())
            throw std::invalid_argument("WolfX candidate key must not be empty");
        if (key.size() > limits_.max_key_length)
            throw std::invalid_argument("WolfX candidate key exceeds max_key_length");
        if (total_key_bytes_ + key.size() > limits_.max_total_key_bytes)
            throw std::invalid_argument("WolfX candidate keys exceed max_total_key_bytes");

        total_key_bytes_ += key.size();
        keys_.push_back(std::move(key));
    }

    void wolfx_candidates::add_string_value(std::uint32_t id, std::string value)
    {
        if (strings_.size() >= limits_.max_string_values)
            throw std::invalid_argument("WolfX candidate strings exceed max_string_values");
        if (total_string_bytes_ + value.size() > limits_.max_total_string_bytes)
            throw std::invalid_argument("WolfX candidate strings exceed max_total_string_bytes");

        total_string_bytes_ += value.size();
        strings_.push_back(wolfx_string_value{id, std::move(value)});
    }

    void wolfx_candidates::add_int_value(std::uint32_t id, std::uint32_t value)
    {
        if (ints_.size() >= limits_.max_int_values)
            throw std::invalid_argument("WolfX candidate integers exceed max_int_values");

        ints_.push_back(wolfx_int_value{id, value});
    }
} // namespace unpackrat::wolfrpg
