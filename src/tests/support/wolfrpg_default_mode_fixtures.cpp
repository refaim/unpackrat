#include "wolfrpg_default_mode_fixtures.h"

#include <filesystem>
#include <fstream>
#include <iterator>
#include <stdexcept>

namespace test::support::wolfrpg_default_mode
{
    std::vector<std::byte> short_body()
    {
        static const std::string text = "Owned WolfRPG fixture payload\n"; // 30 bytes
        std::vector<std::byte> out(text.size());
        for (std::size_t i = 0; i < text.size(); ++i) {
            out[i] = static_cast<std::byte>(static_cast<unsigned char>(text[i]));
        }
        return out;
    }

    std::vector<std::byte> repeat_body()
    {
        static const std::string unit = "0123456789abcdef";
        std::vector<std::byte> out;
        out.reserve(8192);
        for (int i = 0; i < 512; ++i) {
            for (const char value : unit) {
                out.push_back(static_cast<std::byte>(static_cast<unsigned char>(value)));
            }
        }
        return out;
    }

    std::vector<std::byte> leaf_body()
    {
        std::vector<std::byte> out(64);
        std::uint32_t state = 0x9E3779B9u;
        for (std::size_t i = 0; i < out.size(); ++i) {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            out[i] = static_cast<std::byte>(static_cast<std::uint8_t>(state >> 24));
        }
        return out;
    }

    std::uint64_t fingerprint(const std::span<const std::byte> data)
    {
        std::uint64_t hash = 14695981039346656037ULL;
        for (const std::byte value : data) {
            hash ^= std::to_integer<std::uint8_t>(value);
            hash *= 1099511628211ULL;
        }
        return hash;
    }

    std::vector<std::byte> load_bytes(const std::string &file_name)
    {
        const std::filesystem::path path =
            std::filesystem::path(__FILE__).parent_path().parent_path() / "fixtures" / "wolfrpg" / file_name;
        std::ifstream input(path, std::ios::binary);
        if (!input.is_open()) {
            throw std::runtime_error("cannot open WolfRPG default-mode fixture: " + path.string());
        }
        const std::vector<char> raw{ std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>() };
        std::vector<std::byte> out(raw.size());
        for (std::size_t i = 0; i < raw.size(); ++i) {
            out[i] = static_cast<std::byte>(static_cast<unsigned char>(raw[i]));
        }
        return out;
    }

    namespace
    {
        expected_entry entry(std::string path, const std::uint64_t payload_fingerprint, std::vector<std::byte> payload)
        {
            expected_entry value;
            value.path = std::move(path);
            value.unpacked_size = static_cast<std::int64_t>(payload.size());
            value.payload_fingerprint = payload_fingerprint;
            value.payload = std::move(payload);
            return value;
        }

        // The input tree every fixture was built from, in the order the oracle wrote it.
        std::vector<expected_entry> expected_tree()
        {
            return {
                entry("hello.txt", 0x45cb0322a9a90780ULL, short_body()),
                entry("repeat.bin", 0x4966fd4996a59325ULL, repeat_body()),
                entry("nested/leaf.bin", 0xcc109ff091a993eeULL, leaf_body()),
            };
        }
    } // namespace

    const std::vector<const fixture *> &all()
    {
        // Order follows WolfDec::DefaultCryptModes(). Modes 0/1 (VER5) are stored-body archives:
        // the unchanged VER5 encoder only terminates with Press=false, so those two fixtures carry
        // the mode's cipher over an uncompressed body. The remaining modes were written with the
        // vendor's default Press=true. Neither choice is re-derived here; the frozen size and
        // fingerprint above are the drift control.
        static const std::vector<fixture> values{
            { "default-mode-0.wolf", 0, "Wolf RPG v2.01", 0x0, "0f53e13e04371217600f53e1", 5, 8676, 0x01258709fa0b5f80ULL,
              expected_tree() },
            { "default-mode-1.wolf", 1, "Wolf RPG v2.10", 0x0, "4cd92ab7289bac073e77ec4c", 5, 8676, 0x4529f8920b0474e9ULL,
              expected_tree() },
            { "default-mode-2.wolf", 2, "Wolf RPG v2.20", 0x0, "38504028724f21703b733538", 6, 668, 0x05e1fd768b14ad06ULL,
              expected_tree() },
            { "default-mode-4.wolf", 4, "Wolf RPG v3.00", 0x12C,
              "0f53e13e8eb54191521655ae34c98f79592f596b95199b1b359a2fdec97c1296c314b50f53e13e8e00", 8, 1365,
              0x7bc9d20afb68ada1ULL, expected_tree() },
            { "default-mode-5.wolf", 5, "Wolf RPG v3.14", 0x13A,
              "31f90136a3e38d3c7bc37d25ad6328191bf78e6cc4e5e27682ea4fed61dae0445bb6463b06d5ceb67858d07c8200", 8, 1365,
              0x8ac079eaf1c2d3b3ULL, expected_tree() },
            { "default-mode-9.wolf", 9, "One Way Heroics", 0x0, "6e47756939282726313d4033236100", 8, 1365,
              0x54c5584b70cf8df0ULL, expected_tree() },
            { "default-mode-10.wolf", 10, "One Way Heroics Plus", 0x0, "50683d58335e5d6f3241282c313d4033236100", 8, 1365,
              0xe0c82536c4367641ULL, expected_tree() },
        };
        static const std::vector<const fixture *> pointers{ &values[0], &values[1], &values[2], &values[3],
                                                            &values[4], &values[5], &values[6] };
        return pointers;
    }
} // namespace test::support::wolfrpg_default_mode
