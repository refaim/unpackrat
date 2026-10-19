#include "key_recovery.h"
#include "../codecs/x86_decoder.h"
#include "../io/bounded_stream.h"
#include <algorithm>
#include <array>
#include <exception>
#include <limits>
#include <optional>
#include <span>
#include <utility>

namespace unpackrat::godot
{
    namespace
    {
        // Recognition only: no machine state, instruction execution, branch evaluation or imports.
        // Candidates are untrusted until the archive session validates the complete plaintext MD5
        // and index. Only executable PE sections are searched; embedded game data is never scanned.
        struct allowance_exhausted final : std::exception
        {
        };
        bool charge(std::uint64_t &used, std::uint64_t amount, std::uint64_t limit)
        {
            if (used > limit || amount > limit - used)
                return false;
            used += amount;
            return true;
        }
        // The longest single x86 instruction is 15 bytes and the anchor look-back is 14 bytes.
        // One 64 KiB window therefore holds the look-back, any decoded instruction and the whole
        // default pattern scan (max_pattern_instructions * 15 bytes) while keeping the retained
        // executable-byte footprint independent of section size.
        constexpr std::uint64_t cache_bytes = 64ULL * 1024ULL;
        constexpr std::uint64_t instruction_bytes = 15;
        constexpr std::uint64_t look_back_bytes = 14;
        struct section final
        {
            std::uint64_t address = 0, virtual_size = 0, raw_offset = 0, raw_size = 0;
            bool executable = false;
        };
        struct secret final
        {
            codecs::key256 bytes{};
            ~secret()
            {
                codecs::erase_key(bytes);
            }
        };
        class image final
        {
          public:
            image(std::istream &input, key_recovery_budget &budget, const key_recovery_limits &limits)
                : reader_(input), budget_(budget), limits_(limits), cache_(static_cast<std::size_t>(cache_bytes))
            {
            }
            std::vector<std::byte> read(std::uint64_t offset, std::size_t count)
            {
                authorize_read(offset, count);
                std::vector<std::byte> bytes(count);
                read_bytes(offset, bytes);
                return bytes;
            }
            static std::uint64_t integer(std::span<const std::byte> bytes, std::size_t offset, std::size_t width)
            {
                std::uint64_t value = 0;
                for (std::size_t i = 0; i < width; ++i)
                    value |= std::to_integer<std::uint64_t>(bytes[offset + i]) << (8 * i);
                return value;
            }
            bool parse()
            {
                if (reader_.size() < 64)
                    return false;
                const auto dos = read(0, 64);
                if (integer(dos, 0, 2) != 0x5a4d)
                    return false;
                const auto pe = read(integer(dos, 0x3c, 4), 24);
                if (integer(pe, 0, 4) != 0x4550)
                    throw key_recovery_error("invalid PE signature");
                const auto machine = integer(pe, 4, 2), count = integer(pe, 6, 2), optional_size = integer(pe, 20, 2);
                if ((machine != 0x14c && machine != 0x8664) || count == 0 || count > 96 || optional_size < 64 ||
                    optional_size > 4096)
                    throw key_recovery_error("unsupported PE header");
                wide_ = machine == 0x8664;
                const auto optional_offset = integer(dos, 0x3c, 4) + 24;
                const auto optional = read(optional_offset, static_cast<std::size_t>(optional_size));
                if (integer(optional, 0, 2) != (wide_ ? 0x20b : 0x10b))
                    throw key_recovery_error("inconsistent PE mode");
                base_ = integer(optional, wide_ ? 24 : 28, wide_ ? 8 : 4);
                const auto table = read(optional_offset + optional_size, static_cast<std::size_t>(count) * 40);
                for (std::size_t i = 0; i < count; ++i) {
                    const auto row = std::span(table).subspan(i * 40, 40);
                    section entry;
                    const auto rva = integer(row, 12, 4);
                    entry.raw_offset = integer(row, 20, 4);
                    entry.raw_size = integer(row, 16, 4);
                    entry.virtual_size = std::max(integer(row, 8, 4), entry.raw_size);
                    entry.executable = (integer(row, 36, 4) & 0x20000000U) != 0;
                    if (rva > std::numeric_limits<std::uint64_t>::max() - base_ ||
                        entry.virtual_size > std::numeric_limits<std::uint64_t>::max() - base_ - rva)
                        throw key_recovery_error("PE virtual range overflow");
                    entry.address = base_ + rva;
                    if (entry.raw_offset > static_cast<std::uint64_t>(reader_.size()) ||
                        entry.raw_size > static_cast<std::uint64_t>(reader_.size()) - entry.raw_offset)
                        throw key_recovery_error("PE section outside source");
                    if (std::any_of(sections_.begin(), sections_.end(), [&entry](const section &previous) {
                            return entry.virtual_size != 0 && previous.virtual_size != 0 &&
                                   entry.address < previous.address + previous.virtual_size &&
                                   previous.address < entry.address + entry.virtual_size;
                        }))
                        throw key_recovery_error("overlapping PE virtual sections");
                    // Charge the whole section as the scan allowance without retaining its bytes;
                    // the windowed reads below reuse this exact authorization as before.
                    if (entry.executable)
                        authorize_read(entry.raw_offset, static_cast<std::size_t>(entry.raw_size));
                    sections_.push_back(entry);
                }
                return true;
            }
            // A bounded forward window into one executable section. The returned span is only
            // valid until the next fetch; callers never hold it across another read or decode.
            // Callers bound ``offset`` by the section's raw size, so the subtraction is safe.
            std::span<const std::byte> fetch(const section &entry, std::uint64_t offset, std::uint64_t length)
            {
                if (cached_section_ == &entry && offset >= cache_start_ && offset + length <= cache_start_ + cache_len_)
                    return std::span(cache_).subspan(static_cast<std::size_t>(offset - cache_start_),
                                                     static_cast<std::size_t>(cache_len_ - (offset - cache_start_)));
                const auto want = std::min<std::uint64_t>(cache_bytes, entry.raw_size - offset);
                reader_.seek_absolute(static_cast<std::streamoff>(entry.raw_offset + offset));
                reader_.read_exact(reinterpret_cast<char *>(cache_.data()), static_cast<std::size_t>(want));
                cached_section_ = &entry;
                cache_start_ = offset;
                cache_len_ = want;
                return std::span(cache_).first(static_cast<std::size_t>(want));
            }
            const section *mapped(std::uint64_t address, std::size_t count, bool code, bool backed) const
            {
                for (const auto &entry : sections_) {
                    if (entry.executable != code || address < entry.address)
                        continue;
                    const auto offset = address - entry.address, size = backed ? entry.raw_size : entry.virtual_size;
                    if (offset <= size && count <= size - offset)
                        return &entry;
                }
                return nullptr;
            }
            bool data(std::uint64_t address, std::span<std::byte> bytes)
            {
                const auto *entry = mapped(address, bytes.size(), false, true);
                if (entry == nullptr)
                    return false;
                const auto offset = entry->raw_offset + address - entry->address;
                authorize_read(offset, bytes.size());
                read_bytes(offset, bytes);
                return true;
            }
            std::uint64_t base() const
            {
                return base_;
            }
            bool wide() const
            {
                return wide_;
            }
            const std::vector<section> &sections() const
            {
                return sections_;
            }

          private:
            void authorize_read(std::uint64_t offset, std::size_t count)
            {
                const auto size = static_cast<std::uint64_t>(reader_.size());
                if (offset > size || count > size - offset)
                    throw key_recovery_error("PE range outside source");
                if (!charge(budget_.scanned_bytes, count, limits_.max_scanned_bytes))
                    throw allowance_exhausted{};
            }
            void read_bytes(std::uint64_t offset, std::span<std::byte> bytes)
            {
                reader_.seek_absolute(static_cast<std::streamoff>(offset));
                reader_.read_exact(reinterpret_cast<char *>(bytes.data()), bytes.size());
            }
            io::bounded_stream reader_;
            key_recovery_budget &budget_;
            const key_recovery_limits &limits_;
            std::vector<section> sections_;
            std::uint64_t base_ = 0;
            bool wide_ = false;
            std::vector<std::byte> cache_;
            const section *cached_section_ = nullptr;
            std::uint64_t cache_start_ = 0;
            std::uint64_t cache_len_ = 0;
        };
        struct pattern final
        {
            std::vector<std::uint64_t> references, displacements, calls;
            std::size_t xors = 0;
            bool width32 = false, image_base = false;
        };
        void unique_address(std::vector<std::uint64_t> &values, std::uint64_t value)
        {
            if (std::ranges::find(values, value) == values.end())
                values.push_back(value);
        }
        class recognizer final
        {
          public:
            recognizer(image &source, key_recovery_budget &budget, const key_recovery_limits &limits)
                : source_(source), budget_(budget), limits_(limits)
            {
            }
            std::vector<codecs::key256> run()
            {
                try {
                    if (limits_.max_candidates == 0 || limits_.max_pattern_instructions == 0 || !source_.parse())
                        return {};
                    for (const auto &entry : source_.sections()) {
                        if (!entry.executable)
                            continue;
                        constexpr std::array magic{std::byte{0x47}, std::byte{0x44}, std::byte{0x50}, std::byte{0x43}};
                        for (std::uint64_t position = 0; position + magic.size() <= entry.raw_size; ++position) {
                            const auto bytes = source_.fetch(entry, position, magic.size());
                            if (!std::equal(magic.begin(), magic.end(), bytes.begin()))
                                continue;
                            // The literal must belong to a decoded instruction, not just appear in code bytes.
                            const auto begin = position > look_back_bytes ? position - look_back_bytes : 0;
                            for (std::uint64_t start = begin; start <= position; ++start) {
                                const auto decoded = decode(entry, start);
                                if (decoded && decoded->pack_magic &&
                                    start + decoded->length >= position + magic.size()) {
                                    const auto found = window(entry, start);
                                    consume(found);
                                    for (const auto call : found.calls) {
                                        const auto *target = source_.mapped(call, 1, true, true);
                                        if (target != nullptr)
                                            consume(window(*target, call - target->address));
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    // One bounded pass over syntactically plausible address references finds initializers
                    // of the small set of unbacked globals mentioned by a key consumer. No call execution.
                    if (!globals_.empty()) {
                        for (const auto &entry : source_.sections()) {
                            if (!entry.executable)
                                continue;
                            if (!charge(budget_.scanned_bytes, entry.raw_size, limits_.max_scanned_bytes))
                                throw allowance_exhausted{};
                            for (std::uint64_t start = 0; start < entry.raw_size; ++start) {
                                const auto head = source_.fetch(entry, start, 1);
                                const auto first = std::to_integer<unsigned>(head[0]);
                                if (!((first >= 0x48 && first <= 0x4f) || (first >= 0xb8 && first <= 0xbf) ||
                                      first == 0x8d || first == 0x8b))
                                    continue;
                                const auto instruction = decode(entry, start);
                                if (!instruction)
                                    continue;
                                for (std::size_t i = 0; i < instruction->address_count; ++i) {
                                    if (std::ranges::find(globals_, instruction->addresses[i]) != globals_.end()) {
                                        consume(window(entry, start));
                                        break;
                                    }
                                }
                            }
                        }
                    }
                } catch (const allowance_exhausted &) {
                    // Exhaustion stops optional discovery, preserving already collected candidates.
                    return collected();
                }
                return collected();
            }
            ~recognizer()
            {
                for (auto &key : candidates_)
                    codecs::erase_key(key);
            }

          private:
            std::vector<codecs::key256> collected() const
            {
                return {candidates_.begin(), candidates_.begin() + static_cast<std::ptrdiff_t>(candidate_count_)};
            }
            std::optional<codecs::x86_instruction> decode(const section &entry, std::uint64_t start)
            {
                if (!charge(budget_.instructions, 1, limits_.max_instructions))
                    throw allowance_exhausted{};
                const auto bytes = source_.fetch(entry, start, instruction_bytes);
                return codecs::decode_x86(bytes, entry.address + start,
                                          source_.wide() ? codecs::x86_mode::x64 : codecs::x86_mode::x86);
            }
            pattern window(const section &entry, std::uint64_t start)
            {
                pattern result;
                for (std::uint64_t count = 0; count < limits_.max_pattern_instructions && start < entry.raw_size;
                     ++count) {
                    const auto instruction = decode(entry, start);
                    if (!instruction)
                        break;
                    result.width32 = result.width32 || instruction->size32;
                    result.xors += instruction->bit_xor ? 1 : 0;
                    for (std::size_t i = 0; i < instruction->address_count; ++i) {
                        const auto address = instruction->addresses[i];
                        result.image_base = result.image_base || address == source_.base();
                        if (source_.mapped(address, 32, false, false) != nullptr)
                            unique_address(result.references, address);
                    }
                    if (instruction->indexed_displacement)
                        unique_address(result.displacements, *instruction->indexed_displacement);
                    if (instruction->call_target)
                        unique_address(result.calls, *instruction->call_target);
                    start += instruction->length;
                    if (instruction->returns)
                        break;
                }
                return result;
            }
            void admit(const codecs::key256 &key)
            {
                const auto end = candidates_.begin() + static_cast<std::ptrdiff_t>(candidate_count_);
                if (std::find(candidates_.begin(), end, key) != end)
                    return;
                if (!charge(budget_.candidates, 1, std::min<std::uint64_t>(limits_.max_candidates, max_recovered_keys)))
                    throw allowance_exhausted{};
                candidates_[candidate_count_++] = key;
            }
            void constant(std::uint64_t address)
            {
                secret key;
                if (source_.data(address, key.bytes))
                    admit(key.bytes);
            }
            void consume(const pattern &value)
            {
                if (!value.width32)
                    return;
                // The observed obfuscators use exactly three image-relative constant arrays and two
                // XOR operations in a fixed 32-byte initializer. This computes only that formula.
                if (value.image_base && value.xors >= 2) {
                    std::vector<std::uint64_t> arrays;
                    for (const auto displacement : value.displacements) {
                        if (displacement <= std::numeric_limits<std::uint64_t>::max() - source_.base()) {
                            const auto address = source_.base() + displacement;
                            if (source_.mapped(address, 32, false, true) != nullptr)
                                unique_address(arrays, address);
                        }
                    }
                    if (arrays.size() == 3) {
                        secret key;
                        for (const auto address : arrays) {
                            secret bytes;
                            static_cast<void>(source_.data(address, bytes.bytes));
                            for (std::size_t i = 0; i < key.bytes.size(); ++i)
                                key.bytes[i] ^= bytes.bytes[i];
                        }
                        admit(key.bytes);
                    }
                }
                for (const auto address : value.references) {
                    if (source_.mapped(address, 32, false, true) == nullptr) {
                        // Explicit limit bounds retained global targets independently of EXE size.
                        if (globals_.size() < 32)
                            unique_address(globals_, address);
                        continue;
                    }
                    secret pointer;
                    const std::size_t width = source_.wide() ? 8 : 4;
                    static_cast<void>(source_.data(address, std::span(pointer.bytes).first(width)));
                    constant(image::integer(pointer.bytes, 0, width));
                    constant(address);
                }
            }
            image &source_;
            key_recovery_budget &budget_;
            const key_recovery_limits &limits_;
            std::vector<std::uint64_t> globals_;
            std::array<codecs::key256, max_recovered_keys> candidates_{};
            std::size_t candidate_count_ = 0;
        };
    } // namespace
    std::vector<codecs::key256> recover_key_candidates(std::istream &source, key_recovery_budget &budget,
                                                       const key_recovery_limits &limits)
    {
        try {
            image pe(source, budget, limits);
            recognizer scan(pe, budget, limits);
            return scan.run();
        } catch (const io::read_error &) {
            throw key_recovery_error("PE source read failed");
        }
    }
} // namespace unpackrat::godot
