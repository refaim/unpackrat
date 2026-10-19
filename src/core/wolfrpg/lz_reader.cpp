#include "lz_reader.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <exception>
#include <memory>
#include <span>
#include <utility>
#include <vector>

// The single DXLib/WolfRPG LZ token interpreter lives in the pinned UberWolf library as
// UberWolfLib/Archive/BoundedLz.hpp, the same kernel DXArchive::Decode delegates to. This
// translation unit owns only the streaming history ring and the source/exception bridging that
// turns the shared, resumable kernel into the caller-facing lz_reader pull stream; it contains
// no token parsing of its own.
#include <uberwolf/Archive/BoundedLz.hpp>

namespace unpackrat::wolfrpg::codec
{
    namespace
    {
        using kernel = wolf::archive::boundedlz::context;

        // The widest legal back-reference distance is 24 bits, so a history window of that size
        // covers every reference. The caller's storage never exceeds the decoded extent.
        constexpr std::uint64_t max_history_size = wolf::archive::boundedlz::max_distance;

        // Wraps the caller's source so any exception it raises is remembered before it unwinds
        // through the shared kernel. That lets the adapter tell a genuine kernel format failure
        // (translated to lz_error) from an exception the caller's own source threw, which must be
        // forwarded unchanged with its concrete type and payload preserved.
        class source_bridge
        {
          public:
            explicit source_bridge(lz_reader::source input) : input_(std::move(input))
            {
            }

            std::size_t operator()(std::span<std::byte> destination)
            {
                // Clear any provenance recorded by an earlier pull before this one runs, so a
                // source fault that a retry has since recovered from can never be mistaken for a
                // later kernel format failure (and vice versa).
                captured_ = nullptr;
                try {
                    return input_(destination);
                } catch (...) {
                    captured_ = std::current_exception();
                    throw;
                }
            }

            // Rethrows the exception the caller's source raised, if any; a no-op when the failure
            // originated inside the kernel itself.
            void rethrow_source_failure() const
            {
                if (captured_ != nullptr) {
                    std::rethrow_exception(captured_);
                }
            }

          private:
            lz_reader::source input_;
            std::exception_ptr captured_;
        };
    } // namespace

    struct lz_reader::state
    {
        state(source_bridge &bridge, const std::uint64_t encoded_size, const std::uint64_t expected_size)
            : kernel_([&bridge](std::span<std::byte> destination) { return bridge(destination); }, encoded_size,
                      expected_size),
              history_(history_size(expected_size))
        {
        }

        // The smallest ring that still covers min(decoded extent, window) and is never empty.
        [[nodiscard]] static std::size_t history_size(const std::uint64_t expected_size) noexcept
        {
            const std::uint64_t window = expected_size < max_history_size ? expected_size : max_history_size;
            return static_cast<std::size_t>(window == 0 ? 1 : window);
        }

        // Reads the already produced decoded byte at an absolute position from the history ring.
        static std::byte read(void *context, const std::uint64_t position) noexcept
        {
            auto &self = *static_cast<state *>(context);
            return self.history_[static_cast<std::size_t>(position % self.history_.size())];
        }

        // Appends one produced decoded byte to the caller's chunk and advances the history ring.
        static void write(void *context, const std::byte value) noexcept
        {
            auto &self = *static_cast<state *>(context);
            self.history_[static_cast<std::size_t>(self.position_ % self.history_.size())] = value;
            self.output_[self.output_position_] = value;
            ++self.output_position_;
            ++self.position_;
        }

        [[nodiscard]] wolf::archive::boundedlz::emitter sink() noexcept
        {
            return {&state::read, &state::write, this};
        }

        // Owns the source/provenance wrapper the kernel pulls through; assigned by the reader once
        // construction succeeds so it outlives the kernel it feeds. The captured reference in
        // kernel_ stays valid across the move because the pointed-to bridge never relocates.
        std::unique_ptr<source_bridge> bridge_;
        kernel kernel_;
        std::vector<std::byte> history_;
        std::span<std::byte> output_{};
        std::size_t output_position_ = 0;
        std::uint64_t position_ = 0;
    };

    lz_error::lz_error() : std::runtime_error("wolfrpg lz stream error")
    {
    }

    lz_error::lz_error(const char *message) : std::runtime_error(message)
    {
    }

    lz_reader::lz_reader(source input, const std::uint64_t encoded_size, const std::uint64_t expected_size)
    {
        auto bridge = std::make_unique<source_bridge>(std::move(input));
        try {
            state_ = std::make_unique<state>(*bridge, encoded_size, expected_size);
        } catch (const wolf::archive::boundedlz::error &error) {
            // Only the shared kernel's own format errors become lz_error; if the caller's source
            // raised this same vendor type, forward that exact exception unchanged. Any other
            // source failure, including bad_alloc and length_error, is never caught here.
            bridge->rethrow_source_failure();
            throw lz_error(error.what());
        }
        state_->bridge_ = std::move(bridge);
    }

    lz_reader::~lz_reader() = default;

    lz_reader::lz_reader(lz_reader &&other) noexcept = default;

    lz_reader &lz_reader::operator=(lz_reader &&other) noexcept = default;

    std::size_t lz_reader::read(std::span<std::byte> out)
    {
        if (state_ == nullptr || out.empty() || state_->kernel_.finished()) {
            return 0;
        }
        state_->output_ = out;
        state_->output_position_ = 0;
        try {
            return state_->kernel_.produce(out.size(), state_->sink());
        } catch (const wolf::archive::boundedlz::error &error) {
            state_->bridge_->rethrow_source_failure();
            throw lz_error(error.what());
        }
    }

    std::uint64_t lz_reader::remaining() const noexcept
    {
        if (state_ == nullptr) {
            return 0;
        }
        return state_->kernel_.remaining();
    }
} // namespace unpackrat::wolfrpg::codec
