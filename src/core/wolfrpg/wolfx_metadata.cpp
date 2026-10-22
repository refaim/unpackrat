#include "wolfx_metadata.h"

// The pinned UberWolf parsers are the only thing that understands the map / common-event byte
// layout, so this adapter reaches them through their angle-include headers (treated as external,
// keeping the vendored warning level) and never re-implements the format. The bounded load
// overload added by build/vcpkg/ports/uberwolf/metadata-read-context.patch carries a per-image
// WolfReadContext, so this path neither reads nor writes the process-wide format statics.
#include <uberwolf/WolfRPG/CommonEvents.hpp>
#include <uberwolf/WolfRPG/Map.hpp>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <exception>
#include <filesystem>
#include <iterator>
#include <memory>
#include <span>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace unpackrat::wolfrpg
{
    namespace
    {
        [[nodiscard]] Bytes to_vendor_bytes(const std::span<const std::byte> input)
        {
            Bytes bytes(input.size());
            for (std::size_t i = 0; i < input.size(); ++i) {
                bytes[i] = std::to_integer<std::uint8_t>(input[i]);
            }
            return bytes;
        }

        // Recover one command's decryption candidates exactly as the pinned WolfXWrapper does:
        // SetString 122 -> normalized string id + UTF-8 value, SetVariable 121 -> normalized
        // variable id + stored value, ProFeature 1000 subtype 7 -> the non-empty .wolfx key. The
        // vendored accessors (GetID / GetString / GetValue / GetWolfxKey) own the normalization, so
        // no formula is duplicated here.
        void collect_command(const Command::CommandShPtr::Command &command, wolfx_candidates &staged)
        {
            const Command::CommandType type = command->GetType();
            if (type == Command::CommandType::SetString) {
                // The command id alone does not establish the dynamic type: OriginalLoad's
                // Command::Init builds a Move for every terminator-0x01 command regardless of its
                // cid, so a Move-tail image can carry a SetString id on a Move object. A checked
                // cast reports that mismatch instead of invoking undefined behaviour.
                const auto typed = std::dynamic_pointer_cast<Command::CommandSpecialClasses::SetString>(command);
                if (typed == nullptr) {
                    throw metadata_error(metadata_error_code::malformed,
                                         "WolfX SetString command has the wrong dynamic type");
                }
                staged.add_string_value(typed->GetID(), typed->GetString());
            } else if (type == Command::CommandType::SetVariable) {
                const auto typed = std::dynamic_pointer_cast<Command::CommandSpecialClasses::SetVariable>(command);
                if (typed == nullptr) {
                    throw metadata_error(metadata_error_code::malformed,
                                         "WolfX SetVariable command has the wrong dynamic type");
                }
                staged.add_int_value(typed->GetID(), typed->GetValue());
            } else if (type == Command::CommandType::ProFeature) {
                const auto typed = std::dynamic_pointer_cast<Command::CommandSpecialClasses::ProFeature>(command);
                if (typed == nullptr) {
                    throw metadata_error(metadata_error_code::malformed,
                                         "WolfX ProFeature command has the wrong dynamic type");
                }
                if (typed->GetProFeatureType() == Command::CommandSpecialClasses::ProFeature::Type::SetWolfxKey) {
                    std::string key = ToUTF8(typed->GetWolfxKey());
                    // The empty key is implicit and is never stored.
                    if (!key.empty()) {
                        staged.add_key(std::move(key));
                    }
                }
            }
        }
    } // namespace

    struct metadata_collector::impl
    {
        metadata_limits limits{};
        std::size_t read_bytes = 0;
        std::size_t units = 0;
        std::size_t expanded_bytes = 0;
    };

    metadata_collector::metadata_collector(const metadata_limits &limits) : impl_(std::make_unique<impl>())
    {
        impl_->limits = limits;
    }

    metadata_collector::~metadata_collector() = default;
    metadata_collector::metadata_collector(metadata_collector &&) noexcept = default;
    metadata_collector &metadata_collector::operator=(metadata_collector &&) noexcept = default;

    void metadata_collector::collect(metadata_kind kind, std::span<const std::byte> input, wolfx_candidates &out)
    {
        const std::size_t size = input.size();

        // Preflight budgets. A rejected image performed no parse work, so nothing is charged; the
        // read budget is the only cumulative one, and its subtraction is safe because read_bytes
        // never exceeds max_total_read_bytes.
        if (size > impl_->limits.max_input_bytes) {
            throw metadata_error(metadata_error_code::input_too_large, "WolfX metadata image exceeds max_input_bytes");
        }
        if (size > impl_->limits.max_total_read_bytes - impl_->read_bytes) {
            throw metadata_error(metadata_error_code::read_budget_exceeded,
                                 "WolfX metadata session exceeds max_total_read_bytes");
        }

        // Charge the full image once, before the parse begins, and keep it even if the parse later
        // fails, so a caller cannot refund work by retrying a hostile image.
        impl_->read_bytes += size;

        const Bytes bytes = to_vendor_bytes(input);

        WolfReadContext context(impl_->units, impl_->limits.max_total_units);
        context.ConfigureExpanded(impl_->expanded_bytes, impl_->limits.max_image_expanded_bytes,
                                  impl_->limits.max_total_expanded_bytes);
        context.ResetImage();

        std::vector<Command::CommandShPtr::Command> commands;
        try {
            if (kind == metadata_kind::map) {
                Map map(std::filesystem::path(), false);
                // The bounded Load() returns true or throws; a quiet false is not part of its
                // contract. An image with no events is a valid empty map, so it is accepted with no
                // candidates charged, exactly as the original loader treats it.
                map.Load(bytes, context);
                for (const Event &event : map.GetEvents()) {
                    for (const Page &page : event.GetPages()) {
                        const auto &page_commands = page.GetCommands();
                        std::copy(page_commands.begin(), page_commands.end(), std::back_inserter(commands));
                    }
                }
            } else {
                CommonEvents events(std::filesystem::path(), false);
                events.Load(bytes, context);
                for (const CommonEvent &event : events.GetEvents()) {
                    const auto &event_commands = event.GetCommands();
                    std::copy(event_commands.begin(), event_commands.end(), std::back_inserter(commands));
                }
            }
        } catch (const WolfReadUnitBudgetExceeded &) {
            throw metadata_error(metadata_error_code::unit_budget_exceeded,
                                 "WolfX metadata session exceeds max_total_units");
        } catch (const WolfRPGExpandedBudgetExceeded &) {
            throw metadata_error(metadata_error_code::expanded_budget_exceeded,
                                 "WolfX metadata session exceeds max_total_expanded_bytes");
        } catch (const WolfRPGUnsupportedException &) {
            // The vendor dispatch refused a protected / compressed revision this phase cannot decode.
            throw metadata_error(metadata_error_code::unsupported,
                                 "WolfX metadata image uses a format this phase does not decode");
        } catch (const WolfRPGException &) {
            throw metadata_error(metadata_error_code::malformed, "WolfX metadata image is malformed");
        } catch (const FileWalkerException &) {
            throw metadata_error(metadata_error_code::malformed, "WolfX metadata image is truncated");
        }

        // Stage into a copy of the caller's value so a candidate-budget failure leaves ``out``
        // exactly as it was, then commit by move once the whole walk succeeded.
        wolfx_candidates staged = out;
        try {
            for (const Command::CommandShPtr::Command &command : commands) {
                collect_command(command, staged);
            }
        } catch (const std::invalid_argument &) {
            throw metadata_error(metadata_error_code::candidate_budget_exceeded,
                                 "WolfX candidates exceed their budget");
        }

        out = std::move(staged);
    }

    const metadata_limits &metadata_collector::limits() const noexcept
    {
        return impl_->limits;
    }

    std::size_t metadata_collector::total_read_bytes() const noexcept
    {
        return impl_->read_bytes;
    }

    std::size_t metadata_collector::total_units() const noexcept
    {
        return impl_->units;
    }

    std::size_t metadata_collector::total_expanded_bytes() const noexcept
    {
        return impl_->expanded_bytes;
    }
} // namespace unpackrat::wolfrpg
