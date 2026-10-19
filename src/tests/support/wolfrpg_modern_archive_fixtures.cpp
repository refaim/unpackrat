#include "wolfrpg_modern_archive_fixtures.h"

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace test::support
{
    namespace
    {
        constexpr std::string_view kHello = "Owned WolfRPG fixture payload\n";
        constexpr std::size_t kBlockBytes = 2048;
        constexpr std::size_t kBlockRepeats = 4;
        constexpr std::uint32_t kBlockSeed = 0x51ED2701u;

        // Mirrors the generator: the same xorshift32 stream produces the 2048-byte owned block.
        std::vector<std::byte> block_bytes()
        {
            std::vector<std::byte> base(kBlockBytes);
            std::uint32_t state = kBlockSeed;
            for (std::size_t i = 0; i < kBlockBytes; ++i) {
                state ^= state << 13u;
                state ^= state >> 17u;
                state ^= state << 5u;
                base[i] = static_cast<std::byte>(state >> 24u);
            }
            return base;
        }

        std::vector<std::byte> text_bytes(const std::string &text)
        {
            std::vector<std::byte> bytes;
            bytes.reserve(text.size());
            for (const char ch : text) {
                bytes.push_back(static_cast<std::byte>(static_cast<unsigned char>(ch)));
            }
            return bytes;
        }

        std::vector<std::byte> from_hex(const std::string &hex)
        {
            const auto nibble = [](const char ch) -> unsigned {
                if (ch >= '0' && ch <= '9') {
                    return static_cast<unsigned>(ch - '0');
                }
                if (ch >= 'a' && ch <= 'f') {
                    return static_cast<unsigned>(ch - 'a') + 10U;
                }
                if (ch >= 'A' && ch <= 'F') {
                    return static_cast<unsigned>(ch - 'A') + 10U;
                }
                throw std::runtime_error("invalid hex digit in fixture key");
            };

            std::vector<std::byte> bytes;
            bytes.reserve(hex.size() / 2);
            for (std::size_t i = 0; i + 1 < hex.size(); i += 2) {
                bytes.push_back(static_cast<std::byte>((nibble(hex[i]) << 4u) | nibble(hex[i + 1])));
            }
            return bytes;
        }

        // Pinned WolfDec::DefaultCryptModes() key blobs (the trailing 0x00 is part of the table data
        // for the vector<unsigned char> entries).
        constexpr const char *kKey6 = "ca084c5d170ddaa1d727c8415438823254b7f9468e136bcad05c9595e2dc0353609b4a3817f36959"
                                      "a4c79a4363e654afdbbb435800";
        constexpr const char *kKey7 = "d284ce28ce8882e42a182e4c06b4ea8406b8c6885aa09e7c5640ba3452ccc67c2e141268fe5c7694"
                                      "86788e4cbe88669c1ee08e6c00";
        constexpr const char *kKey8 =
            "c982f8b42c939e830ebcbc92688d59a14a9e7fb0acaf1d8f8eb83b9ee889d9adffbc2dab9d8b0fb4bb9a698500";
    } // namespace

    std::vector<std::byte> modern_hello_payload()
    {
        return text_bytes(kHello);
    }

    std::vector<std::byte> modern_repeat_payload()
    {
        const std::vector<std::byte> base = block_bytes();
        std::vector<std::byte> out(kBlockBytes * kBlockRepeats);
        for (std::size_t i = 0; i < out.size(); ++i) {
            out[i] = base[i % kBlockBytes];
        }
        return out;
    }

    const std::vector<modern_archive_fixture> &modern_archive_fixtures()
    {
        static const std::vector<modern_archive_fixture> fixtures = [] {
            const std::string enc = "DXArchive::EncodeArchiveOneDirectoryWolf";
            const std::string dec = "DXArchive::DecodeArchive";

            modern_archive_fixture six;
            six.file_name = "default-mode-6.wolf";
            six.mode_index = 6;
            six.mode_name = "Wolf RPG v3.31";
            six.crypt_version = 0x14b;
            six.key = from_hex(kKey6);
            six.enc_symbol = enc;
            six.dec_symbol = dec;
            six.archive_sha256 = "067fb87a6eb3de22fb42118ed289263fc5b586f0605442b1b3789e53a930d768";
            six.archive_size = 3137;
            six.archive_fingerprint = 0x51d5a52b1f7c54f7ULL;

            modern_archive_fixture seven;
            seven.file_name = "default-mode-7.wolf";
            seven.mode_index = 7;
            seven.mode_name = "Wolf RPG v3.50";
            seven.crypt_version = 0x15e;
            seven.key = from_hex(kKey7);
            seven.enc_symbol = enc;
            seven.dec_symbol = dec;
            seven.archive_sha256 = "1f26e2e1a84d83c60c54c745da124b72f49cfd66f83ffb7961ecc3605980d98a";
            seven.archive_size = 3137;
            seven.archive_fingerprint = 0x2b8ae1e580bc5d3eULL;

            modern_archive_fixture eight;
            eight.file_name = "default-mode-8.wolf";
            eight.mode_index = 8;
            eight.mode_name = "Wolf RPG ChaCha2 v1";
            eight.crypt_version = 0x64;
            eight.key = from_hex(kKey8);
            eight.enc_symbol = enc;
            eight.dec_symbol = dec;
            eight.archive_sha256 = "d2c1ac9c7eb38868928c66f3ce214c5c00b2cea8ccfe5d127b94a823f6bb9f3a";
            eight.archive_size = 3137;
            eight.archive_fingerprint = 0x0cadb317ca29ffe4ULL;

            const std::vector<modern_payload> entries = {
                // encoded_size is the frozen HuffPressDataSize read from the exact container, i.e.
                // the physical stored extent the reader reports, not the plaintext length.
                modern_payload{"hello.txt", modern_hello_payload(), 252},
                modern_payload{"sub/repeat.bin", modern_repeat_payload(), 2402},
            };
            six.entries = entries;
            seven.entries = entries;
            eight.entries = entries;

            return std::vector<modern_archive_fixture>{six, seven, eight};
        }();
        return fixtures;
    }

    std::vector<std::byte> read_modern_fixture(const std::filesystem::path &path)
    {
        std::ifstream stream(path, std::ios::binary);
        if (!stream.is_open()) {
            throw std::runtime_error("cannot open owned WolfRPG fixture: " + path.string());
        }
        const std::vector<char> raw{std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>()};
        std::vector<std::byte> bytes(raw.size());
        if (!raw.empty()) {
            std::memcpy(bytes.data(), raw.data(), raw.size());
        }
        return bytes;
    }

    std::uint64_t fnv1a_64(const std::vector<std::byte> &bytes)
    {
        std::uint64_t hash = 14695981039346656037ULL;
        for (const std::byte value : bytes) {
            hash ^= static_cast<std::uint64_t>(static_cast<unsigned char>(value));
            hash *= 1099511628211ULL;
        }
        return hash;
    }
} // namespace test::support
