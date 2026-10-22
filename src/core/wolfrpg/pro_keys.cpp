#include "pro_keys.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <exception>
#include <limits>
#include <span>
#include <vector>

// The WolfRPG Pro DxArc variants are the pinned UberWolf derivations, reached through the shared
// header-only kernels in the vendor WolfDxArcKey headers (Archive/WolfDxArcKey.hpp for variants 1
// and 2, WolfCrypt/WolfDxArcKey.hpp for variant cc2). This translation unit owns only the
// sparse companion access the archive layer needs: it reads the few header and selected-key bytes
// each variant touches (never the whole companion), validates the physical size against the pinned
// algorithm's domain, and translates the recovered bytes or an algorithm-domain refusal into the
// public key_result. No LCG stream, cipher, counter or key-selection arithmetic is implemented
// here; the v1 kernel, the v2 remainder search and selector, and the cc2 derivation are the pinned
// vendor bodies, shared with the whole-buffer vendor entry points.
//
// A selected key byte that lands outside the companion is signalled by the internal
// selected_out_of_range type and translated to key_error_code::key_out_of_range. A failure raised
// by the caller's source reader is a different exception type that propagates to the caller with
// its concrete type and payload untouched.
#include <uberwolf/WolfCrypt/WolfDxArcKey.hpp>

namespace unpackrat::wolfrpg::pro
{
    namespace
    {
        namespace dxarckey = wolf::crypt::dxarckey;

        // The companion size is a 32-bit format field; a size at or beyond 2^32u cannot be addressed.
        constexpr std::uint64_t size_limit = std::uint64_t{1} << 32u;

        constexpr std::uint8_t v1_first_byte = 0xA0;
        constexpr std::uint8_t cc2_first_byte = 0xF1;

        // Every variant's header fields live below this offset: v1 reads bytes 4 and 19, v2 reads
        // bytes 3..7 and 19, and cc2 reads only the first byte.
        constexpr std::size_t header_bytes = 20;

        // A selected key byte the variant's selection lands outside the companion. Never escapes
        // recover_key: it is this unit's own signal, distinct from any source failure.
        struct selected_out_of_range : std::exception
        {
        };

        [[nodiscard]] std::uint8_t to_value(const std::byte value) noexcept
        {
            return std::to_integer<std::uint8_t>(value);
        }

        [[nodiscard]] std::vector<std::byte> to_bytes(const std::vector<std::uint8_t> &values)
        {
            std::vector<std::byte> bytes(values.size());
            for (std::size_t i = 0; i < values.size(); ++i)
                bytes[i] = static_cast<std::byte>(values[i]);
            return bytes;
        }

        // Reads the shared 20-byte header window. Kept to a stack buffer and one caller read, so no
        // whole-companion buffer is ever materialized.
        [[nodiscard]] std::array<std::byte, header_bytes> read_header(const source &read_at)
        {
            std::array<std::byte, header_bytes> header{};
            read_at(0, header);
            return header;
        }

        [[nodiscard]] std::uint8_t read_one(const source &read_at, const std::uint32_t offset)
        {
            std::byte value{};
            read_at(offset, std::span<std::byte>(&value, 1));
            return to_value(value);
        }

        [[nodiscard]] key_result recover_v1(const std::uint64_t physical_size, const source &read_at)
        {
            if (physical_size < dxarckey::v1::MIN_FILESIZE)
                return key_error{key_error_code::truncated};

            const std::array<std::byte, header_bytes> header = read_header(read_at);
            const std::uint8_t key_len = to_value(header[dxarckey::v1::KEY_LEN_OFFSET]);

            // Upstream would divide the step by a zero key length.
            if (key_len == 0)
                return key_error{key_error_code::invalid_key_length};

            const auto read_raw = [&read_at](const std::uint32_t offset) -> std::uint8_t {
                return read_one(read_at, offset);
            };

            recovered_key recovered;
            recovered.archive_variant = variant::v1;
            recovered.crypt_version = 1000;
            recovered.key =
                to_bytes(dxarckey::v1::calcKey(to_value(header[dxarckey::v1::SEED_OFFSET]), key_len, read_raw));

            // The public accessor leaves a key already ending in 0x00 untouched and appends exactly
            // one 0x00 otherwise; that is the whole of the archive-layer normalization for v1.
            if (recovered.key.back() != std::byte{0x00})
                recovered.key.push_back(std::byte{0x00});

            return recovered;
        }

        [[nodiscard]] key_result recover_cc2(const std::uint64_t physical_size)
        {
            if (physical_size < dxarckey::v2::HEADER_SIZE)
                return key_error{key_error_code::truncated};

            recovered_key recovered;
            recovered.archive_variant = variant::cc2;
            recovered.crypt_version = 0xC8;
            recovered.key = to_bytes(dxarckey::cc2::calcKey(static_cast<std::uint32_t>(physical_size)));
            return recovered;
        }

        [[nodiscard]] key_result recover_v2(const std::uint64_t physical_size, const source &read_at)
        {
            if (physical_size < dxarckey::v2::HEADER_SIZE)
                return key_error{key_error_code::truncated};

            const std::uint64_t data_size = physical_size - dxarckey::v2::HEADER_SIZE;

            if (data_size == 0)
                return key_error{key_error_code::invalid_data_size};

            // The pinned remainder search runs over signed 32-bit values; a larger data size is out
            // of domain and is refused before it can be narrowed to a negative int32.
            if (data_size > static_cast<std::uint64_t>(std::numeric_limits<std::int32_t>::max()))
                return key_error{key_error_code::unsupported_size};

            const std::array<std::byte, header_bytes> header = read_header(read_at);

            std::array<std::uint8_t, header_bytes> header8{};
            for (std::size_t i = 0; i < header8.size(); ++i)
                header8[i] = to_value(header[i]);

            const dxarckey::v2::derived derived =
                dxarckey::v2::initCrypt(static_cast<std::uint32_t>(data_size), header8);
            const wolf::crypt::aes::AesRoundKey schedule = dxarckey::v2::aesSchedule(derived);

            std::array<std::uint8_t, wolf::crypt::aes::IV_SIZE> iv{};
            for (std::size_t i = 0; i < iv.size(); ++i)
                iv[i] = schedule[wolf::crypt::aes::KEY_EXP_SIZE + i];

            const auto read_selected = [&](const std::uint32_t offset) -> std::uint8_t {
                if (offset >= physical_size)
                    throw selected_out_of_range{};

                std::uint8_t value = read_one(read_at, offset);

                // The pinned path CTR-decoded [KEY_START_OFFSET, size - 1) in place; a selected byte
                // outside that half-open region (in particular the final physical byte) is raw.
                if (dxarckey::v2::KEY_START_OFFSET <= offset && offset < physical_size - 1) {
                    std::array<std::uint8_t, wolf::crypt::aes::IV_SIZE> counter = iv;
                    wolf::crypt::aes::aesCtrXCryptRange(&value, schedule.data(), counter.data(),
                                                        offset - dxarckey::v2::KEY_START_OFFSET, 1);
                }

                return value;
            };

            recovered_key recovered;
            recovered.archive_variant = variant::v2;
            recovered.crypt_version = 1010;
            recovered.key = to_bytes(dxarckey::v2::calcKey(derived, header8, read_selected));
            return recovered;
        }
    } // namespace

    key_result recover_key(const std::uint64_t physical_size, const source &read_at)
    {
        if (physical_size == 0)
            return key_error{key_error_code::truncated};

        if (physical_size >= size_limit)
            return key_error{key_error_code::unsupported_size};

        const std::uint8_t first = read_one(read_at, 0);

        try {
            switch (first) {
            case v1_first_byte:
                return recover_v1(physical_size, read_at);
            case cc2_first_byte:
                return recover_cc2(physical_size);
            default:
                return recover_v2(physical_size, read_at);
            }
        } catch (const selected_out_of_range &) {
            return key_error{key_error_code::key_out_of_range};
        }
    }
} // namespace unpackrat::wolfrpg::pro
