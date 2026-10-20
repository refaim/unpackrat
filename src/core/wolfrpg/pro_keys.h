#ifndef UNPACKRAT_CORE_WOLFRPG_PRO_KEYS_H
#define UNPACKRAT_CORE_WOLFRPG_PRO_KEYS_H

#include <cstddef>
#include <cstdint>
#include <functional>
#include <span>
#include <variant>
#include <vector>

// WolfRPG Pro companion key recovery.
//
// "Wolf RPG Pro" sells protected games whose "DxArc" companion file (a .wolf/.data/... file next
// to the game) carries the key that opens the game's DX archive. The pinned UberWolf sources derive
// that key three ways, selected by the companion's first byte:
//
//   * variant::v1  (first byte 0xA0): the companion length must reach UberWolf's 0x5CB8 minimum;
//                  byte[4] seeds the MSVC LCG, byte[19] is the key length, and the key bytes are
//                  the XOR-decoded companion bytes at STEP (0x58B1 / keyLen) spacing from offset 30.
//   * variant::v2  (any other first byte): the key is derived from an AES-CTR stream over the
//                  companion from offset 30 plus a small dynamic tail.
//   * variant::cc2 (first byte 0xF1): the key is a pure function of the 32-bit companion length.
//
// This module is platform-neutral: it depends only on the standard library and never on Observer,
// Win32, or the vendor headers themselves.
namespace unpackrat::wolfrpg::pro
{
    enum class variant : std::uint8_t
    {
        v1,
        v2,
        cc2,
    };

    // Bounded random-access reader over the companion file. A call must fill exactly
    // ``destination.size()`` bytes starting at ``offset`` or throw; the implementation signals any
    // failure by throwing, and the thrown exception's concrete type and payload are left untouched.
    // The reader is invoked only during recover_key and is never retained afterwards.
    using source = std::function<void(std::uint64_t offset, std::span<std::byte> destination)>;

    struct recovered_key final
    {
        variant archive_variant = variant::v1;
        // The upstream crypt version that goes with the variant: 1000 for v1, 1010 for v2, 0xC8 for
        // cc2. Archive decryption needs it alongside the key bytes.
        std::uint16_t crypt_version = 0;
        // The recovered key exactly as the public WolfPro::GetDxArcKey() accessor yields it:
        //   * v1  -- the keyLen selected bytes from the XOR-decoded companion, followed by a trailing
        //            0x00 appended only when the final selected byte is nonzero. A key that already
        //            ends in 0x00 is left untouched; that is the whole of the archive-layer
        //            normalization upstream applies to v1.
        //   * v2  -- the keyLen selected bytes, the 0x00 separator and the four dynamic key bytes,
        //            unmodified: GetDxArcKey never touches the v2 binary tail.
        //   * cc2 -- the 64-byte key derived solely from the companion length.
        std::vector<std::byte> key;
    };

    enum class key_error_code : std::uint8_t
    {
        // The companion is smaller than the variant's fixed header: v1 below its 0x5CB8 minimum, or a
        // v2/cc2 companion below the 31-byte window those variants start from.
        truncated,
        // A v1 companion declares a zero key length, which upstream would divide by.
        invalid_key_length,
        // A v2 companion is exactly the 31-byte header, so its derived data size is zero and the
        // key-position modulo upstream performs would divide by zero. (A 31-byte cc2 companion is
        // valid: its length-derived arithmetic never divides by the data size.)
        invalid_data_size,
        // A selected key byte lies outside the companion.
        key_out_of_range,
        // The companion's size is outside the pinned algorithm's supported domain: data size at or
        // beyond 2^32 (the format's file-size field cannot address it) or a v2 data size beyond the
        // signed 32-bit range the upstream remainder search accepts. Recovery is refused rather than
        // approximated with a fabricated key.
        unsupported_size,
    };

    struct key_error final
    {
        key_error_code code = key_error_code::truncated;
    };

    // Either a recovered key or a diagnosed failure. Failures raised by ``read_at`` are not
    // converted into key_error; they propagate to the caller unchanged.
    using key_result = std::variant<recovered_key, key_error>;

    // Recovers the DxArc key from a companion of ``physical_size`` bytes using random-access reads.
    // Reads are bounded and sparse: the whole companion is never materialized or scanned. A v2
    // companion is recovered only when its derived data size (physical_size - 31) sits inside the
    // signed 32-bit range the upstream remainder search uses; a larger v2 companion, or any
    // companion at or above 2^32, is refused with key_error_code::unsupported_size instead of a
    // fabricated key.
    [[nodiscard]] key_result recover_key(std::uint64_t physical_size, const source &read_at);
} // namespace unpackrat::wolfrpg::pro
#endif
