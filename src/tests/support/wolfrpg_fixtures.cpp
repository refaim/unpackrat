#include "wolfrpg_fixtures.h"

#include <array>
#include <cctype>
#include <cstring>
#include <stdexcept>

// Test-only: the pinned upstream DXLib archive layout. The synthetic builders serialise their
// records against these real structures, so a layout change in the pinned dependency fails here
// instead of silently producing an invalid fixture. This header never reaches production code.
#include <uberwolf/DXLib/DXArchive.h>

namespace test::support
{
    namespace
    {
        constexpr std::uint64_t uncompressed = 0xffffffffffffffffULL;
        constexpr std::uint32_t flag_no_key = 0x1;
        constexpr std::uint32_t flag_no_head_press = 0x2;
        constexpr std::uint32_t char_code_shift_jis = 932;
        constexpr std::uint64_t file_attribute_directory = 0x10; // FILE_ATTRIBUTE_DIRECTORY
        constexpr std::size_t head_size = sizeof(DARC_HEAD);
        constexpr std::size_t file_head_size = sizeof(DARC_FILEHEAD);
        constexpr std::size_t directory_size = sizeof(DARC_DIRECTORY);

        static_assert(head_size == 64);
        static_assert(file_head_size == 72);
        static_assert(directory_size == 32);

        void put_u16(std::vector<std::byte> &out, std::size_t offset, std::uint16_t value)
        {
            out[offset + 0] = static_cast<std::byte>(value & 0xff);
            out[offset + 1] = static_cast<std::byte>((value >> 8) & 0xff);
        }

        void put_u32(std::vector<std::byte> &out, std::size_t offset, std::uint32_t value)
        {
            for (int i = 0; i < 4; ++i) {
                out[offset + static_cast<std::size_t>(i)] = static_cast<std::byte>((value >> (8 * i)) & 0xff);
            }
        }

        void put_u64(std::vector<std::byte> &out, std::size_t offset, std::uint64_t value)
        {
            for (int i = 0; i < 8; ++i) {
                out[offset + static_cast<std::size_t>(i)] = static_cast<std::byte>((value >> (8 * i)) & 0xff);
            }
        }

        void append_text(std::vector<std::byte> &out, const std::string &text)
        {
            for (const char ch : text) {
                out.push_back(static_cast<std::byte>(static_cast<unsigned char>(ch)));
            }
        }

        // Name-table entry exactly as DXLib writes it in DXArchive::AddFileNameData: 2-byte pack
        // count, 2-byte parity over the upper-cased name, the upper-case copy, then the original-case
        // copy. Both copies are zero padded to ``blocks * 4`` bytes and the record reserves
        // 4 + 2 * blocks * 4 bytes in total (blocks = byte length / 4 + 1, which is upstream's
        // ceil((length + 1) / 4) including the terminating block).
        std::vector<std::byte> encode_name(const std::string &name)
        {
            if (name.empty()) {
                throw std::invalid_argument("synthetic name must be non-empty");
            }
            const std::size_t blocks = name.size() / 4 + 1;
            std::string upper = name;
            std::uint16_t parity = 0;
            for (char &ch : upper) {
                ch = static_cast<char>(std::toupper(static_cast<unsigned char>(ch)));
                parity = static_cast<std::uint16_t>(parity + static_cast<unsigned char>(ch));
            }
            std::vector<std::byte> entry(4 + 2 * blocks * 4, std::byte{0});
            put_u16(entry, 0, static_cast<std::uint16_t>(blocks));
            put_u16(entry, 2, parity);
            std::memcpy(entry.data() + 4, upper.data(), upper.size());
            std::memcpy(entry.data() + 4 + blocks * 4, name.data(), name.size());
            return entry;
        }

        // An LZ stream that emits ``contents`` literally (keycode 0xff, so no byte may be 0xff).
        std::vector<std::byte> literal_lz(const std::string &contents)
        {
            if (contents.find('\xff') != std::string::npos) {
                throw std::invalid_argument("literal-only LZ stream cannot carry the 0xff keycode byte");
            }
            std::vector<std::byte> body(9, std::byte{0});
            put_u32(body, 0, static_cast<std::uint32_t>(contents.size()));
            put_u32(body, 4, static_cast<std::uint32_t>(contents.size() + 9));
            body[8] = std::byte{0xff};
            append_text(body, contents);
            return body;
        }

        struct file_body
        {
            std::string name;
            std::vector<std::byte> body;
            std::uint64_t unpacked_size = 0;
            bool compressed = false;
        };

        // An LZ body that expands ``unpacked_size`` bytes of 'A' from a handful of encoded bytes:
        // one literal seed then maximum-length distance-1 copies. Each copy token is four bytes
        // (key, code, length high byte, distance) and reproduces 8195 bytes. Requires a size of at
        // least 5 so the seed plus one minimum-length copy always fits.
        std::vector<std::byte> expansion_lz(const std::uint64_t unpacked_size)
        {
            constexpr std::uint64_t max_copy_length = 8195; // 4 + 31 + 255 * 32
            std::vector<std::byte> tokens;
            append_text(tokens, "A"); // literal seed byte (never the 0xff key)

            const auto emit_copy = [&tokens](const std::uint64_t length) {
                const std::uint64_t extra = length - 4;
                const auto low = static_cast<std::uint8_t>(extra & 0x1f);
                const auto high = static_cast<std::uint8_t>((extra >> 5) & 0xff);
                tokens.push_back(std::byte{0xff});
                tokens.push_back(std::byte{static_cast<std::uint8_t>(low << 3 | (high != 0 ? 0x04 : 0x00))});
                if (high != 0) {
                    tokens.push_back(std::byte{high});
                }
                tokens.push_back(std::byte{0x00}); // distance - 1 == 0, i.e. a distance of one
            };

            std::uint64_t remaining = unpacked_size - 1;
            while (remaining >= max_copy_length) {
                emit_copy(max_copy_length);
                remaining -= max_copy_length;
            }
            if (remaining >= 4) {
                emit_copy(remaining);
                remaining = 0;
            }
            for (std::uint64_t index = 0; index < remaining; ++index) {
                tokens.push_back(std::byte{'A'}); // fewer than four trailing literal bytes
            }

            std::vector<std::byte> body(9, std::byte{0});
            put_u32(body, 0, static_cast<std::uint32_t>(unpacked_size));
            put_u32(body, 4, static_cast<std::uint32_t>(9 + tokens.size()));
            body[8] = std::byte{0xff};
            body.insert(body.end(), tokens.begin(), tokens.end());
            return body;
        }

        // Writes a keyless classic DX8 container: DARC_HEAD, the file bodies, then the name, file
        // head and directory tables. Everything is little-endian and uncompressed. The layout follows
        // DXArchive::EncodeArchive: the name table opens with the empty root name, the file table
        // opens with the root's own DIRECTORY DARC_FILEHEAD, and the root DARC_DIRECTORY points its
        // children at file-relative 72.
        built_archive assemble(std::span<const file_body> files)
        {
            // 4-byte empty root name entry, then one packed entry per file.
            std::vector<std::byte> names(4, std::byte{0});
            std::vector<std::uint64_t> name_address(files.size());
            for (std::size_t i = 0; i < files.size(); ++i) {
                name_address[i] = names.size();
                const std::vector<std::byte> entry = encode_name(files[i].name);
                names.insert(names.end(), entry.begin(), entry.end());
            }

            std::vector<std::byte> bodies;
            std::vector<std::uint64_t> data_address(files.size());
            std::vector<std::uint64_t> press_size(files.size());
            for (std::size_t i = 0; i < files.size(); ++i) {
                data_address[i] = bodies.size();
                press_size[i] = files[i].compressed ? files[i].body.size() : uncompressed;
                bodies.insert(bodies.end(), files[i].body.begin(), files[i].body.end());
            }

            // File table: the root's own record first, then one record per file.
            const std::size_t file_table_base = names.size();
            std::vector<std::byte> file_table(file_head_size + files.size() * file_head_size, std::byte{0});
            put_u64(file_table, offsetof(DARC_FILEHEAD, NameAddress), 0);
            put_u64(file_table, offsetof(DARC_FILEHEAD, Attributes), file_attribute_directory);
            put_u64(file_table, offsetof(DARC_FILEHEAD, DataAddress), 0); // directory-table relative 0
            put_u64(file_table, offsetof(DARC_FILEHEAD, DataSize), 0);
            put_u64(file_table, offsetof(DARC_FILEHEAD, PressDataSize), uncompressed);
            // The root HuffPressDataSize, Time and the remaining fields stay zero, as upstream leaves
            // them after memset.
            for (std::size_t i = 0; i < files.size(); ++i) {
                const std::size_t base = file_head_size + i * file_head_size;
                put_u64(file_table, base + offsetof(DARC_FILEHEAD, NameAddress), name_address[i]);
                put_u64(file_table, base + offsetof(DARC_FILEHEAD, Attributes), 0);
                // The Time member sits between Attributes and DataAddress and stays zero.
                put_u64(file_table, base + offsetof(DARC_FILEHEAD, DataAddress), data_address[i]);
                put_u64(file_table, base + offsetof(DARC_FILEHEAD, DataSize), files[i].unpacked_size);
                put_u64(file_table, base + offsetof(DARC_FILEHEAD, PressDataSize), press_size[i]);
                put_u64(file_table, base + offsetof(DARC_FILEHEAD, HuffPressDataSize), uncompressed);
            }

            const std::size_t directory_table_offset = file_table_base + file_table.size();
            std::vector<std::byte> directory_table(directory_size, std::byte{0});
            put_u64(directory_table, offsetof(DARC_DIRECTORY, DirectoryAddress), 0);
            put_u64(directory_table, offsetof(DARC_DIRECTORY, ParentDirectoryAddress), uncompressed);
            put_u64(directory_table, offsetof(DARC_DIRECTORY, FileHeadNum), files.size());
            put_u64(directory_table, offsetof(DARC_DIRECTORY, FileHeadAddress), file_head_size);

            std::vector<std::byte> header = names;
            header.insert(header.end(), file_table.begin(), file_table.end());
            header.insert(header.end(), directory_table.begin(), directory_table.end());

            std::vector<std::byte> bytes(head_size, std::byte{0});
            bytes[offsetof(DARC_HEAD, Head)] = std::byte{'D'};
            bytes[offsetof(DARC_HEAD, Head) + 1] = std::byte{'X'};
            put_u16(bytes, offsetof(DARC_HEAD, Version), 8);
            put_u32(bytes, offsetof(DARC_HEAD, HeadSize), static_cast<std::uint32_t>(header.size()));
            put_u64(bytes, offsetof(DARC_HEAD, DataStartAddress), head_size);
            put_u64(bytes, offsetof(DARC_HEAD, FileNameTableStartAddress), head_size + bodies.size());
            put_u64(bytes, offsetof(DARC_HEAD, FileTableStartAddress), file_table_base);
            put_u64(bytes, offsetof(DARC_HEAD, DirectoryTableStartAddress), directory_table_offset);
            put_u32(bytes, offsetof(DARC_HEAD, CharCodeFormat), char_code_shift_jis);
            put_u32(bytes, offsetof(DARC_HEAD, Flags), flag_no_key | flag_no_head_press);
            bytes[offsetof(DARC_HEAD, HuffmanEncodeKB)] = std::byte{0xff};
            bytes.insert(bytes.end(), bodies.begin(), bodies.end());
            bytes.insert(bytes.end(), header.begin(), header.end());

            built_archive result;
            result.bytes = std::move(bytes);
            result.header_offset = head_size + bodies.size();
            // The root record occupies the first file-table slot; the files start one record later.
            result.file_table_offset = file_table_base + file_head_size;
            result.root_directory_offset = directory_table_offset;
            return result;
        }
    } // namespace

    std::vector<std::byte> classic_dx8_payload()
    {
        return {
            std::byte{0x44}, std::byte{0x58}, std::byte{0x08}, std::byte{0x00}, std::byte{0x34}, std::byte{0x01}, std::byte{0x00}, std::byte{0x00}, std::byte{0x40}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
            std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x38}, std::byte{0x02}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
            std::byte{0x3c}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x14}, std::byte{0x01}, std::byte{0x00}, std::byte{0x00},
            std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0xa4}, std::byte{0x03}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
            std::byte{0x0c}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
            std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0xd9}, std::byte{0xe5}, std::byte{0x91}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca},
            std::byte{0x27}, std::byte{0x33}, std::byte{0x01}, std::byte{0xe0}, std::byte{0xe1}, std::byte{0xe1}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6},
            std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x3f}, std::byte{0xff}, std::byte{0xda},
            std::byte{0x42}, std::byte{0xc4}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27},
            std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0},
            std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf3}, std::byte{0xb4}, std::byte{0x8f}, std::byte{0xe3}, std::byte{0x73}, std::byte{0x14}, std::byte{0xf1},
            std::byte{0xf6}, std::byte{0xf0}, std::byte{0x9e}, std::byte{0x63}, std::byte{0x32}, std::byte{0x65}, std::byte{0xe0}, std::byte{0xa7}, std::byte{0xe1}, std::byte{0xbb}, std::byte{0x37}, std::byte{0x33},
            std::byte{0x14}, std::byte{0xa5}, std::byte{0xb2}, std::byte{0xac}, std::byte{0x8e}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xa8},
            std::byte{0x05}, std::byte{0x09}, std::byte{0x9c}, std::byte{0x79}, std::byte{0xee}, std::byte{0x78}, std::byte{0x42}, std::byte{0x27}, std::byte{0x59}, std::byte{0x36}, std::byte{0xd1}, std::byte{0xa2},
            std::byte{0xb4}, std::byte{0x96}, std::byte{0x63}, std::byte{0x32}, std::byte{0x9c}, std::byte{0x79}, std::byte{0x1c}, std::byte{0xd2}, std::byte{0xff}, std::byte{0x36}, std::byte{0x26}, std::byte{0x05},
            std::byte{0xc6}, std::byte{0xe7}, std::byte{0xc7}, std::byte{0xdb}, std::byte{0x32}, std::byte{0x22}, std::byte{0x03}, std::byte{0xe0}, std::byte{0xe3}, std::byte{0xe1}, std::byte{0xca}, std::byte{0x7b},
            std::byte{0x77}, std::byte{0x40}, std::byte{0xb5}, std::byte{0xf6}, std::byte{0xf5}, std::byte{0x0e}, std::byte{0x67}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0},
            std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1},
            std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33},
            std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca},
            std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6},
            std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14},
            std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27},
            std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0},
            std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xf1}, std::byte{0xf6}, std::byte{0xf0}, std::byte{0xca}, std::byte{0x27}, std::byte{0x33}, std::byte{0x14}, std::byte{0xc9},
            std::byte{0x6c}, std::byte{0x99}, std::byte{0x60}, std::byte{0x98}, std::byte{0xa6}, std::byte{0x16}, std::byte{0x80}, std::byte{0x83}, std::byte{0x02}, std::byte{0x0b}, std::byte{0x2a}, std::byte{0x5e},
            std::byte{0xf1}, std::byte{0x0a}, std::byte{0x9c}, std::byte{0xf8}, std::byte{0xd5}, std::byte{0x83}, std::byte{0xd4}, std::byte{0x6c}, std::byte{0x53}, std::byte{0x30}, std::byte{0xee}, std::byte{0xc5},
            std::byte{0x65}, std::byte{0x14}, std::byte{0xe5}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x11},
            std::byte{0x13}, std::byte{0xd8}, std::byte{0x25}, std::byte{0x18}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d},
            std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b},
            std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x3f}, std::byte{0x4b}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xa9},
            std::byte{0xa1}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47},
            std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d},
            std::byte{0xd6}, std::byte{0xc5}, std::byte{0x73}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0xcc}, std::byte{0x9d}, std::byte{0xab}, std::byte{0xcf},
            std::byte{0x4b}, std::byte{0x14}, std::byte{0xe5}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d},
            std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d},
            std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b},
            std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab},
            std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47},
            std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d},
            std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf},
            std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d},
            std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d}, std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x5d},
            std::byte{0x47}, std::byte{0x1d}, std::byte{0xab}, std::byte{0xcf}, std::byte{0x4b}, std::byte{0x1d}, std::byte{0x6f}, std::byte{0xa7}, std::byte{0x40}, std::byte{0x07}, std::byte{0x59}, std::byte{0x25},
            std::byte{0x13}, std::byte{0x01}, std::byte{0x4c}, std::byte{0x84}, std::byte{0x6e}, std::byte{0x23}, std::byte{0x2f}, std::byte{0xbb}, std::byte{0x0f}, std::byte{0xfe}, std::byte{0x4f}, std::byte{0x06},
            std::byte{0x1a}, std::byte{0x50}, std::byte{0xd0}, std::byte{0x0b}, std::byte{0xfc}, std::byte{0xc6}, std::byte{0x40}, std::byte{0xc8}, std::byte{0x2a}, std::byte{0xf9}, std::byte{0xbb}, std::byte{0xa1},
            std::byte{0xfb}, std::byte{0x4d}, std::byte{0x29}, std::byte{0xad}, std::byte{0xfb}, std::byte{0xbb}, std::byte{0xa1}, std::byte{0xfb}, std::byte{0x4d}, std::byte{0x29}, std::byte{0xad}, std::byte{0xfb},
            std::byte{0xbb}, std::byte{0xb6}, std::byte{0x1d}, std::byte{0xab}, std::byte{0x48}, std::byte{0xa0}, std::byte{0x1a}, std::byte{0x8b}, std::byte{0x18}, std::byte{0xec}, std::byte{0x6c}, std::byte{0xce},
            std::byte{0xf2}, std::byte{0x1b}, std::byte{0xc7}, std::byte{0x87}, std::byte{0x59}, std::byte{0xe4}, std::byte{0xcd}, std::byte{0x5e}, std::byte{0x1b}, std::byte{0xce}, std::byte{0x5a}, std::byte{0xae},
            std::byte{0xb4}, std::byte{0xc0}, std::byte{0x1a}, std::byte{0x77}, std::byte{0x07}, std::byte{0xa2}, std::byte{0x75}, std::byte{0x88}, std::byte{0xbc}, std::byte{0xc7}, std::byte{0x07}, std::byte{0xf4},
            std::byte{0x87}, std::byte{0xcf}, std::byte{0x0e}, std::byte{0x56}, std::byte{0xd1}, std::byte{0xd6}, std::byte{0x01}, std::byte{0x6b}, std::byte{0x77}, std::byte{0xa8}, std::byte{0x54}, std::byte{0xeb},
            std::byte{0xc5}, std::byte{0xc9}, std::byte{0x07}, std::byte{0x49}, std::byte{0xe4}, std::byte{0x0d}, std::byte{0x5a}, std::byte{0x02}, std::byte{0x1c}, std::byte{0xe9}, std::byte{0x7f}, std::byte{0x0e},
            std::byte{0x54}, std::byte{0xeb}, std::byte{0xc5}, std::byte{0xc9}, std::byte{0x07}, std::byte{0x49}, std::byte{0xe4}, std::byte{0x0d}, std::byte{0x50}, std::byte{0x6c}, std::byte{0xa7}, std::byte{0x8d},
            std::byte{0xa1}, std::byte{0xb7}, std::byte{0x8b}, std::byte{0x55}, std::byte{0xd7}, std::byte{0x31}, std::byte{0x6f}, std::byte{0xcf}, std::byte{0x2f}, std::byte{0xec}, std::byte{0x66}, std::byte{0x84},
            std::byte{0xa7}, std::byte{0x87}, std::byte{0x49}, std::byte{0xe4}, std::byte{0x0d}, std::byte{0x50}, std::byte{0x6c}, std::byte{0xa7}, std::byte{0x8f}, std::byte{0xa1}, std::byte{0xab}, std::byte{0xec},
            std::byte{0x76}, std::byte{0x84}, std::byte{0xae}, std::byte{0xe9}, std::byte{0x4f}, std::byte{0xab}, std::byte{0x66}, std::byte{0x45}, std::byte{0x5c}, std::byte{0x2a}, std::byte{0x53}, std::byte{0xc6},
            std::byte{0x60}, std::byte{0xb3}, std::byte{0x4a}, std::byte{0x6e}, std::byte{0x90}, std::byte{0xde}, std::byte{0x12}, std::byte{0x6f}, std::byte{0xec}, std::byte{0xf2}, std::byte{0x77}, std::byte{0x1e},
            std::byte{0x5a}, std::byte{0xae}, std::byte{0xb4}, std::byte{0xc0}, std::byte{0x1a}, std::byte{0xf8}, std::byte{0x65}, std::byte{0x53}, std::byte{0x0f}, std::byte{0x0e}, std::byte{0x7f}, std::byte{0xbc},
            std::byte{0x4b}, std::byte{0xd1}, std::byte{0x11}, std::byte{0x27}, std::byte{0x06}, std::byte{0x38}, std::byte{0x55}, std::byte{0xd6}, std::byte{0xa5}, std::byte{0x9c}, std::byte{0x6f}, std::byte{0x0e},
            std::byte{0x54}, std::byte{0xab}, std::byte{0xcc}, std::byte{0x8e}, std::byte{0xcb}, std::byte{0xe2}, std::byte{0x42}, std::byte{0x56}, std::byte{0x76}, std::byte{0x84}, std::byte{0xae}, std::byte{0xe9},
            std::byte{0x4f}, std::byte{0x83}, std::byte{0x80}, std::byte{0x87}, std::byte{0x34}, std::byte{0xc9}, std::byte{0x07}, std::byte{0xcd}, std::byte{0x55}, std::byte{0xfe}, std::byte{0x9e}, std::byte{0xc9},
            std::byte{0x1d}, std::byte{0x22}, std::byte{0xff}, std::byte{0xbb}, std::byte{0x66}, std::byte{0xf0}, std::byte{0x64}, std::byte{0xc9}, std::byte{0x22}, std::byte{0x75}, std::byte{0x0e}, std::byte{0x56},
            std::byte{0xf0}, std::byte{0x6c}, std::byte{0xa7}, std::byte{0x87}, std::byte{0xcf}, std::byte{0x06}, std::byte{0x38}, std::byte{0x55}, std::byte{0xd6}, std::byte{0xa7}, std::byte{0x87}, std::byte{0xcf},
            std::byte{0x0c}, std::byte{0x4d}, std::byte{0x59}, std::byte{0x02}, std::byte{0x27}, std::byte{0x87}, std::byte{0xcf}, std::byte{0x0e}, std::byte{0x56}, std::byte{0xf8}, std::byte{0x02}, std::byte{0x02},
            std::byte{0x3d}, std::byte{0xcf}, std::byte{0x0e}, std::byte{0x56}, std::byte{0xf0}, std::byte{0x6c}, std::byte{0xa7}, std::byte{0x01}, std::byte{0x25}, std::byte{0x55}, std::byte{0xf6}, std::byte{0xf0},
            std::byte{0xea}, std::byte{0x4d}, std::byte{0xdc}, std::byte{0x6f}, std::byte{0x0e}, std::byte{0x56}, std::byte{0xf0}, std::byte{0x4d}, std::byte{0x1d}, std::byte{0x11}, std::byte{0x27}, std::byte{0x0e},
            std::byte{0x56}, std::byte{0xf0}, std::byte{0x6c}, std::byte{0xa7}, std::byte{0x85}, std::byte{0xd4}, std::byte{0xa7}, std::byte{0x38}, std::byte{0x70}, std::byte{0x6c}, std::byte{0xa7}, std::byte{0x87},
            std::byte{0xcf}, std::byte{0x0e}, std::byte{0x56}, std::byte{0xf0}, std::byte{0x6c}, std::byte{0xa7}, std::byte{0x87}, std::byte{0xcf}, std::byte{0x0e}, std::byte{0x56}, std::byte{0xf8}, std::byte{0x02},
            std::byte{0x02}, std::byte{0x3d}, std::byte{0xcf}, std::byte{0x0e}, std::byte{0x56}, std::byte{0xf0}, std::byte{0x6c}, std::byte{0xa7}, std::byte{0x87}, std::byte{0xcf}, std::byte{0x0e}, std::byte{0x56},
            std::byte{0xf0}, std::byte{0x6c}, std::byte{0xa7}, std::byte{0x87}, std::byte{0xee}, std::byte{0xb4}, std::byte{0xc0}, std::byte{0x18}, std::byte{0x64}, std::byte{0xc9}, std::byte{0x22}, std::byte{0x75},
            std::byte{0x0e}, std::byte{0x56}, std::byte{0xf0}, std::byte{0x6c}, std::byte{0xa7}, std::byte{0x87}, std::byte{0xcf}, std::byte{0x0e}, std::byte{0x56}, std::byte{0xf0}, std::byte{0x6c}, std::byte{0xa7},
            std::byte{0x87}, std::byte{0xcf}, std::byte{0x0e}, std::byte{0x56}, std::byte{0xf0}, std::byte{0x6c}, std::byte{0xa5}, std::byte{0x9c}, std::byte{0x6f}, std::byte{0x2b}, std::byte{0xec}, std::byte{0x39},
            std::byte{0xef}, std::byte{0xd1}, std::byte{0xc2}, std::byte{0xff}, std::byte{0x8a}, std::byte{0xaa}, std::byte{0xa7}, std::byte{0x90}, std::byte{0x48}, std::byte{0x78}, std::byte{0x54}, std::byte{0x51},
            std::byte{0x63}, std::byte{0x49}, std::byte{0xe7}, std::byte{0x40}, std::byte{0x56}, std::byte{0x1c}, std::byte{0xb4}, std::byte{0xfb}, std::byte{0x0f}, std::byte{0x95}, std::byte{0xdc}, std::byte{0x11},
            std::byte{0x46}, std::byte{0x1c}, std::byte{0xc6}, std::byte{0x72}, std::byte{0x36}, std::byte{0xcf}, std::byte{0x6a}, std::byte{0x0b}, std::byte{0x56}, std::byte{0xaa}, std::byte{0x75}, std::byte{0xd0},
            std::byte{0x81}, std::byte{0x8d}, std::byte{0x5a}, std::byte{0xf8}, std::byte{0x1d}, std::byte{0x86}, std::byte{0x9d}, std::byte{0x69}, std::byte{0x82}, std::byte{0x46}, std::byte{0x29}, std::byte{0x46},
            std::byte{0x06}, std::byte{0xa8}, std::byte{0x69}, std::byte{0x18}, std::byte{0x44}, std::byte{0x89}, std::byte{0x43}, std::byte{0x6e}, std::byte{0xb3}, std::byte{0x73}, std::byte{0x76}, std::byte{0x89},
            std::byte{0x2a}, std::byte{0xf2}, std::byte{0xd3}, std::byte{0xa8}, std::byte{0x29}, std::byte{0xa7}, std::byte{0x9c}, std::byte{0x64}, std::byte{0xb2}, std::byte{0x9a}, std::byte{0xfa}, std::byte{0x89},
            std::byte{0xa8}, std::byte{0xe8}, std::byte{0x3a}, std::byte{0x8c}, std::byte{0x68}, std::byte{0x02}, std::byte{0x29}, std::byte{0x36}, std::byte{0xd4}, std::byte{0xa7}, std::byte{0x2c}, std::byte{0x57},
            std::byte{0xbd}, std::byte{0x59}, std::byte{0x20}, std::byte{0x95}, std::byte{0x5e}, std::byte{0x30}, std::byte{0xf7}, std::byte{0xae}, std::byte{0xf7}, std::byte{0x9b}, std::byte{0x65}, std::byte{0x30},
            std::byte{0xed}, std::byte{0xc3}, std::byte{0x9a}, std::byte{0xd0}, std::byte{0xbc}, std::byte{0xcd},
        };
    }

    built_archive build_plain_archive(const std::span<const synthetic_entry> entries)
    {
        std::vector<file_body> files;
        files.reserve(entries.size());
        for (const synthetic_entry &entry : entries) {
            file_body file;
            file.name = entry.name;
            file.unpacked_size = entry.contents.size();
            if (entry.lz_compress) {
                file.body = literal_lz(entry.contents);
                file.compressed = true;
            } else {
                append_text(file.body, entry.contents);
            }
            files.push_back(std::move(file));
        }
        return assemble(files);
    }

    std::vector<std::byte> build_lz_backref_archive()
    {
        std::vector<std::byte> body(9, std::byte{0});
        put_u32(body, 0, 6);  // unpacked size
        put_u32(body, 4, 14); // stored size (9-byte header + 2 literals + 3-byte copy token)
        body[8] = std::byte{0xff};
        append_text(body, "ab");
        // Copy token after the two literals: 0xff keycode, 0x00 code (length 4, one-byte distance),
        // 0x01 distance index (stored as distance - 1, i.e. a distance of 2).
        body.push_back(std::byte{0xff});
        body.push_back(std::byte{0x00});
        body.push_back(std::byte{0x01});
        const file_body file{"backref.bin", std::move(body), 6, true};
        const std::span<const file_body> files{&file, 1};
        return assemble(files).bytes;
    }

    std::vector<std::byte> build_lz_expansion_archive(const std::uint64_t unpacked_size)
    {
        if (unpacked_size < 5 || unpacked_size > 0xffffffffULL) {
            throw std::invalid_argument("expansion size must be at least 5 and fit in 32 bits");
        }
        file_body file;
        file.name = "expansion.bin";
        file.body = expansion_lz(unpacked_size);
        file.unpacked_size = unpacked_size;
        file.compressed = true;
        const std::span<const file_body> files{&file, 1};
        return assemble(files).bytes;
    }

    void corrupt_signature(std::vector<std::byte> &bytes)
    {
        if (bytes.size() >= 2) {
            bytes[0] = std::byte{'X'};
        }
    }

    void set_header_version(std::vector<std::byte> &bytes, const std::uint16_t version)
    {
        put_u16(bytes, 2, version);
    }

    void truncate_bytes(std::vector<std::byte> &bytes, const std::size_t new_size)
    {
        if (new_size < bytes.size()) {
            bytes.resize(new_size);
        }
    }

    void set_root_file_count(const built_archive &archive, std::vector<std::byte> &bytes, const std::uint64_t count)
    {
        put_u64(bytes, archive.header_offset + archive.root_directory_offset + 16, count);
    }

    void set_file_unpacked_size(const built_archive &archive, std::vector<std::byte> &bytes, const std::size_t index,
                                const std::uint64_t unpacked_size)
    {
        const std::size_t base = archive.header_offset + archive.file_table_offset + index * file_head_size;
        put_u64(bytes, base + offsetof(DARC_FILEHEAD, DataSize), unpacked_size);
    }
} // namespace test::support
