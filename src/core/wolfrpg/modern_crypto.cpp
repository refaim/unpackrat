#include "modern_crypto.h"

#include "modern_context.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <span>
#include <string>
#include <string_view>

// This translation unit is pure range orchestration over the Wolf RPG "new crypt" container:
// it parses the 64-byte DARC_HEAD, validates the requested ranges and applies the outer/inner
// transforms to caller-supplied byte ranges. The stateful derivations (address scramble, Wolf
// XOR key and v3.50 body size) live in modern_context.h against an operation-local
// LCG/xorshift, and the nonstandard AES key schedule / cipher is reused from the pinned
// header-only UberWolf primitive (wolf::crypt::aes). No part of UberWolf is linked or reachable
// beyond those header-only primitives; DecodeArchive (whole-file, temp-file, shared global
// state) is not used.
#include <uberwolf/WolfCrypt/WolfCrypt.hpp>

namespace unpackrat::wolfrpg
{
    namespace
    {
        // DARC_HEAD (DXLib packed layout) offsets.
        constexpr std::size_t kFileNameTableStartOffset = 16;
        constexpr std::size_t kFlagsOffset = 44;
        constexpr std::size_t kReserveOffset = 49;
        constexpr std::size_t kHeaderSize = 64;
        constexpr std::size_t kWolfKeySize = wolf::crypt::modern::KEY_SIZE;
        constexpr std::size_t kAesKeyExpansion = wolf::crypt::aes::KEY_EXP_SIZE;
        constexpr std::size_t kAesRoundKeySize = wolf::crypt::aes::ROUND_KEY_SIZE;

        constexpr std::uint32_t kFlagNoKey = 0x00000001;
        constexpr std::uint32_t kFirstAesRegion = 0x400; // non-V35 fixed body size
        constexpr std::uint32_t kMinAesRegion = 0x400;   // minimum physical region outside v3.31
        // The dispatcher's modern range is 331..999 and 1010..65535; 330 and the classic Pro
        // controls 1000..1009 are handled outside this context and must be rejected here.
        constexpr std::uint16_t kVersionMin = 331;
        constexpr std::uint16_t kVersionProControl = 1000;
        constexpr std::uint16_t kVersionPro = 1010;
        constexpr std::uint16_t kVersionLiteralSalt = 0x15E; // 350: outer key salts from "958"
        constexpr std::size_t kProKeyPrefix = 63;            // DXA_KEY_STRING_LENGTH
        constexpr std::size_t kProKeySize = 4;               // the vendor reads pK2[0..3]

        [[nodiscard]] std::uint32_t read_le32(std::span<const std::uint8_t> bytes, std::size_t offset) noexcept
        {
            std::uint32_t value = 0;
            for (std::size_t i = 0; i < 4u; ++i) {
                value |= static_cast<std::uint32_t>(bytes[offset + i]) << (8 * i);
            }
            return value;
        }

        [[nodiscard]] std::uint64_t read_le64(std::span<const std::uint8_t> bytes, std::size_t offset) noexcept
        {
            std::uint64_t value = 0;
            for (std::size_t i = 0; i < 8u; ++i) {
                value |= static_cast<std::uint64_t>(bytes[offset + i]) << (8 * i);
            }
            return value;
        }

        [[nodiscard]] bool add_within(std::uint64_t offset, std::size_t size, std::uint64_t limit) noexcept
        {
            return size <= limit && offset <= limit - size;
        }

        // The dispatcher's "new crypt" range: 331..999 and 1010..65535. 1000..1009 (classic Pro)
        // and everything outside are not modern archives.
        [[nodiscard]] constexpr bool is_modern_version(const std::uint16_t version) noexcept
        {
            return (version >= kVersionMin && version < kVersionProControl) || version >= kVersionPro;
        }
    } // namespace

    struct modern_crypto::impl
    {
        modern_header header{};
        std::uint16_t version = 0;
        std::uint64_t physical_size = 0;
        bool no_key = false;
        std::array<std::uint8_t, kWolfKeySize> outer_key{};
        std::array<std::uint8_t, kWolfKeySize> inner_key{};
        std::array<std::uint8_t, kAesKeyExpansion> aes_schedule{};
        std::array<std::uint8_t, 16> aes_iv{};
        std::array<std::uint8_t, 16> aes_iv_region2{};
        std::uint32_t body_size = 0;
        std::uint64_t name_table_offset = 0;
    };

    modern_crypto::modern_crypto() : impl_(std::make_unique<impl>())
    {
    }

    modern_crypto::~modern_crypto() = default;
    modern_crypto::modern_crypto(modern_crypto &&) noexcept = default;
    modern_crypto &modern_crypto::operator=(modern_crypto &&) noexcept = default;

    modern_crypto modern_crypto::create(std::span<const std::uint8_t, 64> header, std::uint64_t physical_size,
                                        std::string_view password)
    {
        modern_crypto context;
        impl &state = *context.impl_;

        const std::uint32_t flags = read_le32(header, kFlagsOffset);
        state.version = static_cast<std::uint16_t>(flags >> 16u);
        if (!is_modern_version(state.version)) {
            throw modern_crypto_error{};
        }

        // Official Editor 3.396 emits valid v3.31 images below 1088 bytes (the owned
        // 866-byte fixture contains a known 256-byte payload). Keep the logical AES prefix
        // at 1024 bytes; decode_outer_at intersects it with validated physical requests.
        // Other versions retain the existing minimum, and name-table bounds still apply.
        const std::uint64_t minimum_size = state.version == kVersionMin ? kHeaderSize : kHeaderSize + kMinAesRegion;
        if (physical_size < minimum_size) {
            throw modern_crypto_error{};
        }

        // The shared Wolf XOR primitives take signed int64 offsets. An archive larger than the
        // signed range cannot be addressed: the decode overlaps would wrap to negative values and
        // index outside the key. Reject it here, before any context is derived.
        if (physical_size > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max())) {
            throw modern_crypto_error{};
        }

        const bool v35 = wolf::crypt::utils::isV35(state.version);
        const bool pro = state.version >= kVersionPro;

        // The password is a raw byte image. Its first NUL is the full salt prefix (the upstream
        // strlen, with no 63 clamp), so an empty prefix only fails when calcSalt is actually used;
        // v3.50 salts from the literal "958" and ignores the blob entirely.
        const std::size_t nul = password.find('\0');
        const std::size_t prefix_length = nul == std::string_view::npos ? password.size() : nul;
        if (state.version != kVersionLiteralSalt && prefix_length == 0) {
            throw modern_crypto_error{};
        }

        // Pro key derivation reads pK2[0..3] starting one byte past the 63-clamped prefix, which
        // can point inside a longer prefix. The four bytes are validated before the vendor read
        // (never taken from the tail of the blob).
        std::array<std::uint8_t, kProKeySize> pro_key{};
        std::uint8_t *const pk2 = pro ? pro_key.data() : nullptr;
        if (pro) {
            const std::size_t clamped = prefix_length < kProKeyPrefix ? prefix_length : kProKeyPrefix;
            const std::size_t pk2_offset = clamped + 1;
            if (!add_within(pk2_offset, kProKeySize, password.size())) {
                throw modern_crypto_error{};
            }
            std::transform(password.begin() + static_cast<std::ptrdiff_t>(pk2_offset),
                           password.begin() + static_cast<std::ptrdiff_t>(pk2_offset + kProKeySize), pro_key.begin(),
                           [](const char value) { return static_cast<std::uint8_t>(value); });
        }

        std::copy(header.begin(), header.end(), state.header.begin());
        state.physical_size = physical_size;
        state.no_key = (flags & kFlagNoKey) != 0;

        const std::uint8_t *const reserve = state.header.data() + kReserveOffset;

        wolf::crypt::modern::cryptAddresses(state.header.data(), reserve, state.version);

        const std::uint64_t name_table = read_le64(state.header, kFileNameTableStartOffset);
        if (name_table < kHeaderSize || name_table > physical_size) {
            throw modern_crypto_error{};
        }
        state.name_table_offset = name_table;

        // Adapter-owned NUL-terminated password for the vendor strlen-based salt. The copy keeps
        // embedded NUL bytes; calcSalt stops at the first one, exactly like the upstream strlen.
        const std::string password_z(password);

        wolf::crypt::modern::initWolfCrypt(reserve, true, state.version, password_z.c_str(), state.outer_key.data());
        wolf::crypt::modern::initWolfCrypt(reserve, false, state.version, "", state.inner_key.data(), pk2);

        std::array<std::uint8_t, kAesRoundKeySize> round_key{};
        wolf::crypt::aes::initAES128(round_key.data(), state.header.data() + kReserveOffset, pk2, state.version);
        std::memcpy(state.aes_schedule.data(), round_key.data(), kAesKeyExpansion);
        std::memcpy(state.aes_iv.data(), round_key.data() + kAesKeyExpansion, state.aes_iv.size());

        std::uint32_t body_size = kFirstAesRegion;
        if (v35) {
            body_size = wolf::crypt::modern::bodySize(reserve, physical_size, state.version, pk2);
        }

        // Derived v3.50 regions must fit physically. The fixed regions already meet the
        // minimum above, except v3.31's logical prefix, which is bounded at every read.
        if (v35 && body_size > physical_size - kHeaderSize) {
            throw modern_crypto_error{};
        }
        state.body_size = body_size;

        // The sequential primitive advances its IV once per actual 16-byte block consumed,
        // including a partial final block. A short v3.31 image must not advance the metadata
        // counter for the absent tail of its logical prefix.
        const std::uint64_t consumed_body_size = std::min<std::uint64_t>(body_size, physical_size - kHeaderSize);
        state.aes_iv_region2 = state.aes_iv;
        wolf::crypt::modern::advanceCounter(std::span<std::uint8_t, 16>(state.aes_iv_region2),
                                            (consumed_body_size + 15) / 16);

        return context;
    }

    const modern_header &modern_crypto::header() const noexcept
    {
        return impl_->header;
    }

    std::uint16_t modern_crypto::crypt_version() const noexcept
    {
        return impl_->version;
    }

    bool modern_crypto::has_inner_key() const noexcept
    {
        return !impl_->no_key;
    }

    std::uint64_t modern_crypto::name_table_offset() const noexcept
    {
        return impl_->name_table_offset;
    }

    std::uint32_t modern_crypto::aes_body_size() const noexcept
    {
        return impl_->body_size;
    }

    void modern_crypto::decode_outer_at(std::span<std::uint8_t> data, std::uint64_t physical_offset) const
    {
        const impl &state = *impl_;

        if (physical_offset < kHeaderSize || !add_within(physical_offset, data.size(), state.physical_size)) {
            throw modern_crypto_error{};
        }

        const std::uint64_t end = physical_offset + data.size();

        // Every region starts at or after the header, and decode_outer_at has already rejected any
        // range below it, so only the upper edge of each region ever needs clamping: the lower
        // clamp would be a constant-true comparison.
        const std::uint64_t wolf_end = state.physical_size - kHeaderSize;
        if (physical_offset < wolf_end) {
            const std::uint64_t overlap_end = end < wolf_end ? end : wolf_end;
            auto sub = data.subspan(0, static_cast<std::size_t>(overlap_end - physical_offset));
            wolf::crypt::wolfCrypt(state.outer_key.data(), sub.data(), static_cast<std::int64_t>(physical_offset),
                                   static_cast<std::int64_t>(overlap_end), false, state.version);
        }

        const std::uint64_t aes1_end = kHeaderSize + state.body_size;
        if (physical_offset < aes1_end) {
            const std::uint64_t overlap_end = end < aes1_end ? end : aes1_end;
            auto sub = data.subspan(0, static_cast<std::size_t>(overlap_end - physical_offset));
            wolf::crypt::modern::aesCtrXor(sub, std::span<const std::uint8_t, kAesKeyExpansion>(state.aes_schedule),
                                           std::span<const std::uint8_t, 16>(state.aes_iv),
                                           physical_offset - kHeaderSize);
        }

        const std::uint64_t aes2_begin = state.name_table_offset;
        if (end > aes2_begin) {
            const std::uint64_t overlap_begin = physical_offset > aes2_begin ? physical_offset : aes2_begin;
            auto sub = data.subspan(static_cast<std::size_t>(overlap_begin - physical_offset),
                                    static_cast<std::size_t>(end - overlap_begin));
            wolf::crypt::modern::aesCtrXor(sub, std::span<const std::uint8_t, kAesKeyExpansion>(state.aes_schedule),
                                           std::span<const std::uint8_t, 16>(state.aes_iv_region2),
                                           overlap_begin - aes2_begin);
        }
    }

    void modern_crypto::apply_inner(std::span<std::uint8_t> data, std::uint64_t logical_position) const
    {
        const impl &state = *impl_;
        if (state.no_key || data.empty()) {
            return;
        }
        if (!add_within(logical_position, data.size(),
                        static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()))) {
            throw modern_crypto_error{};
        }
        wolf::crypt::wolfCrypt(state.inner_key.data(), data.data(), static_cast<std::int64_t>(logical_position),
                               static_cast<std::int64_t>(logical_position + data.size()), false, state.version);
    }
} // namespace unpackrat::wolfrpg
