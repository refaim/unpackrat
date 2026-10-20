#include "../../core/wolfrpg/huffman_reader.h"

#include <uberwolf/Archive/BoundedHuffman.hpp>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <type_traits>
#include <utility>
#include <vector>

#include <catch2/catch_test_macros.hpp>

// The original DXLib C ABI entry point. The reuse refactor rewrote its body to delegate to the one
// shared bounded_huffman context but kept the void*/void* signature, so the durable suite drives
// the real shipped symbol rather than a private re-implementation.
#include <uberwolf/DXLib/Huffman.h>

namespace
{
    using unpackrat::wolfrpg::codec::huffman_error;
    using unpackrat::wolfrpg::codec::huffman_reader;
    using bytes = std::vector<std::byte>;

    // Frozen golden vectors produced by the authoritative DXLib/uberwolf Huffman_Encode. They pin
    // the decoder to the upstream byte layout without ever invoking the runtime encoder.
    // ---- phrase ----
    // encode 43 bytes -> need=245 written=245
    inline const std::array<std::byte, 43> phrase_plain{{
        std::byte{0x75}, std::byte{0x6e}, std::byte{0x70}, std::byte{0x61}, std::byte{0x63}, std::byte{0x6b},
        std::byte{0x72}, std::byte{0x61}, std::byte{0x74}, std::byte{0x20}, std::byte{0x77}, std::byte{0x6f},
        std::byte{0x6c}, std::byte{0x66}, std::byte{0x72}, std::byte{0x70}, std::byte{0x67}, std::byte{0x20},
        std::byte{0x68}, std::byte{0x75}, std::byte{0x66}, std::byte{0x66}, std::byte{0x6d}, std::byte{0x61},
        std::byte{0x6e}, std::byte{0x20}, std::byte{0x73}, std::byte{0x74}, std::byte{0x72}, std::byte{0x65},
        std::byte{0x61}, std::byte{0x6d}, std::byte{0x69}, std::byte{0x6e}, std::byte{0x67}, std::byte{0x20},
        std::byte{0x64}, std::byte{0x65}, std::byte{0x63}, std::byte{0x6f}, std::byte{0x64}, std::byte{0x65},
        std::byte{0x72},
    }};

    inline const std::array<std::byte, 245> phrase_encoded{{
        std::byte{0x16}, std::byte{0xb1}, std::byte{0x57}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0xc5}, std::byte{0xf4}, std::byte{0x35},
        std::byte{0x7d}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x03}, std::byte{0x17}, std::byte{0xd0}, std::byte{0xd5}, std::byte{0xf4}, std::byte{0x2a},
        std::byte{0xfa}, std::byte{0x00}, std::byte{0xa5}, std::byte{0xf4}, std::byte{0x02}, std::byte{0xd7},
        std::byte{0xd2}, std::byte{0xd7}, std::byte{0xd0}, std::byte{0x0b}, std::byte{0x5f}, std::byte{0x4a},
        std::byte{0x5f}, std::byte{0x40}, std::byte{0x29}, std::byte{0x7d}, std::byte{0x29}, std::byte{0x7d},
        std::byte{0x2d}, std::byte{0x7d}, std::byte{0x00}, std::byte{0xbb}, std::byte{0xe8}, std::byte{0xc5},
        std::byte{0xf4}, std::byte{0x35}, std::byte{0x1d}, std::byte{0xca}, std::byte{0x5f}, std::byte{0x40},
        std::byte{0x2e}, std::byte{0xfa}, std::byte{0x29}, std::byte{0x7d}, std::byte{0x2d}, std::byte{0x7d},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0xda}, std::byte{0xbc}, std::byte{0xcf}, std::byte{0xde}, std::byte{0xca}, std::byte{0x04},
        std::byte{0x72}, std::byte{0x75}, std::byte{0x0c}, std::byte{0x13}, std::byte{0x6b}, std::byte{0x15},
        std::byte{0x6e}, std::byte{0x8f}, std::byte{0x97}, std::byte{0xcb}, std::byte{0x45}, std::byte{0xd3},
        std::byte{0x30}, std::byte{0x3f}, std::byte{0x1f}, std::byte{0xfd}, std::byte{0x3c},
    }};

    // ---- allsymbols ----
    // encode 256 bytes -> need=453 written=453
    inline const std::array<std::byte, 256> allsymbols_plain{{
        std::byte{0x00}, std::byte{0x01}, std::byte{0x02}, std::byte{0x03}, std::byte{0x04}, std::byte{0x05},
        std::byte{0x06}, std::byte{0x07}, std::byte{0x08}, std::byte{0x09}, std::byte{0x0a}, std::byte{0x0b},
        std::byte{0x0c}, std::byte{0x0d}, std::byte{0x0e}, std::byte{0x0f}, std::byte{0x10}, std::byte{0x11},
        std::byte{0x12}, std::byte{0x13}, std::byte{0x14}, std::byte{0x15}, std::byte{0x16}, std::byte{0x17},
        std::byte{0x18}, std::byte{0x19}, std::byte{0x1a}, std::byte{0x1b}, std::byte{0x1c}, std::byte{0x1d},
        std::byte{0x1e}, std::byte{0x1f}, std::byte{0x20}, std::byte{0x21}, std::byte{0x22}, std::byte{0x23},
        std::byte{0x24}, std::byte{0x25}, std::byte{0x26}, std::byte{0x27}, std::byte{0x28}, std::byte{0x29},
        std::byte{0x2a}, std::byte{0x2b}, std::byte{0x2c}, std::byte{0x2d}, std::byte{0x2e}, std::byte{0x2f},
        std::byte{0x30}, std::byte{0x31}, std::byte{0x32}, std::byte{0x33}, std::byte{0x34}, std::byte{0x35},
        std::byte{0x36}, std::byte{0x37}, std::byte{0x38}, std::byte{0x39}, std::byte{0x3a}, std::byte{0x3b},
        std::byte{0x3c}, std::byte{0x3d}, std::byte{0x3e}, std::byte{0x3f}, std::byte{0x40}, std::byte{0x41},
        std::byte{0x42}, std::byte{0x43}, std::byte{0x44}, std::byte{0x45}, std::byte{0x46}, std::byte{0x47},
        std::byte{0x48}, std::byte{0x49}, std::byte{0x4a}, std::byte{0x4b}, std::byte{0x4c}, std::byte{0x4d},
        std::byte{0x4e}, std::byte{0x4f}, std::byte{0x50}, std::byte{0x51}, std::byte{0x52}, std::byte{0x53},
        std::byte{0x54}, std::byte{0x55}, std::byte{0x56}, std::byte{0x57}, std::byte{0x58}, std::byte{0x59},
        std::byte{0x5a}, std::byte{0x5b}, std::byte{0x5c}, std::byte{0x5d}, std::byte{0x5e}, std::byte{0x5f},
        std::byte{0x60}, std::byte{0x61}, std::byte{0x62}, std::byte{0x63}, std::byte{0x64}, std::byte{0x65},
        std::byte{0x66}, std::byte{0x67}, std::byte{0x68}, std::byte{0x69}, std::byte{0x6a}, std::byte{0x6b},
        std::byte{0x6c}, std::byte{0x6d}, std::byte{0x6e}, std::byte{0x6f}, std::byte{0x70}, std::byte{0x71},
        std::byte{0x72}, std::byte{0x73}, std::byte{0x74}, std::byte{0x75}, std::byte{0x76}, std::byte{0x77},
        std::byte{0x78}, std::byte{0x79}, std::byte{0x7a}, std::byte{0x7b}, std::byte{0x7c}, std::byte{0x7d},
        std::byte{0x7e}, std::byte{0x7f}, std::byte{0x80}, std::byte{0x81}, std::byte{0x82}, std::byte{0x83},
        std::byte{0x84}, std::byte{0x85}, std::byte{0x86}, std::byte{0x87}, std::byte{0x88}, std::byte{0x89},
        std::byte{0x8a}, std::byte{0x8b}, std::byte{0x8c}, std::byte{0x8d}, std::byte{0x8e}, std::byte{0x8f},
        std::byte{0x90}, std::byte{0x91}, std::byte{0x92}, std::byte{0x93}, std::byte{0x94}, std::byte{0x95},
        std::byte{0x96}, std::byte{0x97}, std::byte{0x98}, std::byte{0x99}, std::byte{0x9a}, std::byte{0x9b},
        std::byte{0x9c}, std::byte{0x9d}, std::byte{0x9e}, std::byte{0x9f}, std::byte{0xa0}, std::byte{0xa1},
        std::byte{0xa2}, std::byte{0xa3}, std::byte{0xa4}, std::byte{0xa5}, std::byte{0xa6}, std::byte{0xa7},
        std::byte{0xa8}, std::byte{0xa9}, std::byte{0xaa}, std::byte{0xab}, std::byte{0xac}, std::byte{0xad},
        std::byte{0xae}, std::byte{0xaf}, std::byte{0xb0}, std::byte{0xb1}, std::byte{0xb2}, std::byte{0xb3},
        std::byte{0xb4}, std::byte{0xb5}, std::byte{0xb6}, std::byte{0xb7}, std::byte{0xb8}, std::byte{0xb9},
        std::byte{0xba}, std::byte{0xbb}, std::byte{0xbc}, std::byte{0xbd}, std::byte{0xbe}, std::byte{0xbf},
        std::byte{0xc0}, std::byte{0xc1}, std::byte{0xc2}, std::byte{0xc3}, std::byte{0xc4}, std::byte{0xc5},
        std::byte{0xc6}, std::byte{0xc7}, std::byte{0xc8}, std::byte{0xc9}, std::byte{0xca}, std::byte{0xcb},
        std::byte{0xcc}, std::byte{0xcd}, std::byte{0xce}, std::byte{0xcf}, std::byte{0xd0}, std::byte{0xd1},
        std::byte{0xd2}, std::byte{0xd3}, std::byte{0xd4}, std::byte{0xd5}, std::byte{0xd6}, std::byte{0xd7},
        std::byte{0xd8}, std::byte{0xd9}, std::byte{0xda}, std::byte{0xdb}, std::byte{0xdc}, std::byte{0xdd},
        std::byte{0xde}, std::byte{0xdf}, std::byte{0xe0}, std::byte{0xe1}, std::byte{0xe2}, std::byte{0xe3},
        std::byte{0xe4}, std::byte{0xe5}, std::byte{0xe6}, std::byte{0xe7}, std::byte{0xe8}, std::byte{0xe9},
        std::byte{0xea}, std::byte{0xeb}, std::byte{0xec}, std::byte{0xed}, std::byte{0xee}, std::byte{0xef},
        std::byte{0xf0}, std::byte{0xf1}, std::byte{0xf2}, std::byte{0xf3}, std::byte{0xf4}, std::byte{0xf5},
        std::byte{0xf6}, std::byte{0xf7}, std::byte{0xf8}, std::byte{0xf9}, std::byte{0xfa}, std::byte{0xfb},
        std::byte{0xfc}, std::byte{0xfd}, std::byte{0xfe}, std::byte{0xff},
    }};

    inline const std::array<std::byte, 453> allsymbols_encoded{{
        std::byte{0x22}, std::byte{0x00}, std::byte{0x4a}, std::byte{0x00}, std::byte{0xdf}, std::byte{0xe0},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x80}, std::byte{0x40}, std::byte{0xc0}, std::byte{0x20}, std::byte{0xa0}, std::byte{0x60},
        std::byte{0xe0}, std::byte{0x10}, std::byte{0x90}, std::byte{0x50}, std::byte{0xd0}, std::byte{0x30},
        std::byte{0xb0}, std::byte{0x70}, std::byte{0xf0}, std::byte{0x08}, std::byte{0x88}, std::byte{0x48},
        std::byte{0xc8}, std::byte{0x28}, std::byte{0xa8}, std::byte{0x68}, std::byte{0xe8}, std::byte{0x18},
        std::byte{0x98}, std::byte{0x58}, std::byte{0xd8}, std::byte{0x38}, std::byte{0xb8}, std::byte{0x78},
        std::byte{0xf8}, std::byte{0x04}, std::byte{0x84}, std::byte{0x44}, std::byte{0xc4}, std::byte{0x24},
        std::byte{0xa4}, std::byte{0x64}, std::byte{0xe4}, std::byte{0x14}, std::byte{0x94}, std::byte{0x54},
        std::byte{0xd4}, std::byte{0x34}, std::byte{0xb4}, std::byte{0x74}, std::byte{0xf4}, std::byte{0x0c},
        std::byte{0x8c}, std::byte{0x4c}, std::byte{0xcc}, std::byte{0x2c}, std::byte{0xac}, std::byte{0x6c},
        std::byte{0xec}, std::byte{0x1c}, std::byte{0x9c}, std::byte{0x5c}, std::byte{0xdc}, std::byte{0x3c},
        std::byte{0xbc}, std::byte{0x7c}, std::byte{0xfc}, std::byte{0x02}, std::byte{0x82}, std::byte{0x42},
        std::byte{0xc2}, std::byte{0x22}, std::byte{0xa2}, std::byte{0x62}, std::byte{0xe2}, std::byte{0x12},
        std::byte{0x92}, std::byte{0x52}, std::byte{0xd2}, std::byte{0x32}, std::byte{0xb2}, std::byte{0x72},
        std::byte{0xf2}, std::byte{0x0a}, std::byte{0x8a}, std::byte{0x4a}, std::byte{0xca}, std::byte{0x2a},
        std::byte{0xaa}, std::byte{0x6a}, std::byte{0xea}, std::byte{0x1a}, std::byte{0x9a}, std::byte{0x5a},
        std::byte{0xda}, std::byte{0x3a}, std::byte{0xba}, std::byte{0x7a}, std::byte{0xfa}, std::byte{0x06},
        std::byte{0x86}, std::byte{0x46}, std::byte{0xc6}, std::byte{0x26}, std::byte{0xa6}, std::byte{0x66},
        std::byte{0xe6}, std::byte{0x16}, std::byte{0x96}, std::byte{0x56}, std::byte{0xd6}, std::byte{0x36},
        std::byte{0xb6}, std::byte{0x76}, std::byte{0xf6}, std::byte{0x0e}, std::byte{0x8e}, std::byte{0x4e},
        std::byte{0xce}, std::byte{0x2e}, std::byte{0xae}, std::byte{0x6e}, std::byte{0xee}, std::byte{0x1e},
        std::byte{0x9e}, std::byte{0x5e}, std::byte{0xde}, std::byte{0x3e}, std::byte{0xbe}, std::byte{0x7e},
        std::byte{0xfe}, std::byte{0x01}, std::byte{0x81}, std::byte{0x41}, std::byte{0xc1}, std::byte{0x21},
        std::byte{0xa1}, std::byte{0x61}, std::byte{0xe1}, std::byte{0x11}, std::byte{0x91}, std::byte{0x51},
        std::byte{0xd1}, std::byte{0x31}, std::byte{0xb1}, std::byte{0x71}, std::byte{0xf1}, std::byte{0x09},
        std::byte{0x89}, std::byte{0x49}, std::byte{0xc9}, std::byte{0x29}, std::byte{0xa9}, std::byte{0x69},
        std::byte{0xe9}, std::byte{0x19}, std::byte{0x99}, std::byte{0x59}, std::byte{0xd9}, std::byte{0x39},
        std::byte{0xb9}, std::byte{0x79}, std::byte{0xf9}, std::byte{0x05}, std::byte{0x85}, std::byte{0x45},
        std::byte{0xc5}, std::byte{0x25}, std::byte{0xa5}, std::byte{0x65}, std::byte{0xe5}, std::byte{0x15},
        std::byte{0x95}, std::byte{0x55}, std::byte{0xd5}, std::byte{0x35}, std::byte{0xb5}, std::byte{0x75},
        std::byte{0xf5}, std::byte{0x0d}, std::byte{0x8d}, std::byte{0x4d}, std::byte{0xcd}, std::byte{0x2d},
        std::byte{0xad}, std::byte{0x6d}, std::byte{0xed}, std::byte{0x1d}, std::byte{0x9d}, std::byte{0x5d},
        std::byte{0xdd}, std::byte{0x3d}, std::byte{0xbd}, std::byte{0x7d}, std::byte{0xfd}, std::byte{0x03},
        std::byte{0x83}, std::byte{0x43}, std::byte{0xc3}, std::byte{0x23}, std::byte{0xa3}, std::byte{0x63},
        std::byte{0xe3}, std::byte{0x13}, std::byte{0x93}, std::byte{0x53}, std::byte{0xd3}, std::byte{0x33},
        std::byte{0xb3}, std::byte{0x73}, std::byte{0xf3}, std::byte{0x0b}, std::byte{0x8b}, std::byte{0x4b},
        std::byte{0xcb}, std::byte{0x2b}, std::byte{0xab}, std::byte{0x6b}, std::byte{0xeb}, std::byte{0x1b},
        std::byte{0x9b}, std::byte{0x5b}, std::byte{0xdb}, std::byte{0x3b}, std::byte{0xbb}, std::byte{0x7b},
        std::byte{0xfb}, std::byte{0x07}, std::byte{0x87}, std::byte{0x47}, std::byte{0xc7}, std::byte{0x27},
        std::byte{0xa7}, std::byte{0x67}, std::byte{0xe7}, std::byte{0x17}, std::byte{0x97}, std::byte{0x57},
        std::byte{0xd7}, std::byte{0x37}, std::byte{0xb7}, std::byte{0x77}, std::byte{0xf7}, std::byte{0x0f},
        std::byte{0x8f}, std::byte{0x4f}, std::byte{0xcf}, std::byte{0x2f}, std::byte{0xaf}, std::byte{0x6f},
        std::byte{0xef}, std::byte{0x1f}, std::byte{0x9f}, std::byte{0x5f}, std::byte{0xdf}, std::byte{0x3f},
        std::byte{0xbf}, std::byte{0x7f}, std::byte{0xff},
    }};

    // ---- ramp ----
    // encode 288 bytes -> need=311 written=311
    inline const std::array<std::byte, 288> ramp_plain{{
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01}, std::byte{0x01},
        std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02},
        std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02},
        std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02},
        std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02},
        std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02},
        std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02},
        std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02},
        std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02}, std::byte{0x02},
        std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03},
        std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03},
        std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03},
        std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03},
        std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03},
        std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03},
        std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x03}, std::byte{0x04}, std::byte{0x04},
        std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04},
        std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04},
        std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04},
        std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04},
        std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04}, std::byte{0x04},
        std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05},
        std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05},
        std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05},
        std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05}, std::byte{0x05},
        std::byte{0x06}, std::byte{0x06}, std::byte{0x06}, std::byte{0x06}, std::byte{0x06}, std::byte{0x06},
        std::byte{0x06}, std::byte{0x06}, std::byte{0x06}, std::byte{0x06}, std::byte{0x06}, std::byte{0x06},
        std::byte{0x06}, std::byte{0x06}, std::byte{0x06}, std::byte{0x06}, std::byte{0x07}, std::byte{0x07},
        std::byte{0x07}, std::byte{0x07}, std::byte{0x07}, std::byte{0x07}, std::byte{0x07}, std::byte{0x07},
    }};

    inline const std::array<std::byte, 311> ramp_encoded{{
        std::byte{0x22}, std::byte{0x40}, std::byte{0x3b}, std::byte{0x3e}, std::byte{0x71}, std::byte{0xc7},
        std::byte{0x6e}, std::byte{0x3b}, std::byte{0x6e}, std::byte{0x39}, std::byte{0x6e}, std::byte{0x39},
        std::byte{0x6e}, std::byte{0x3b}, std::byte{0x6e}, std::byte{0x39}, std::byte{0x6e}, std::byte{0x3b},
        std::byte{0x6e}, std::byte{0x39}, std::byte{0x6e}, std::byte{0x38}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0xaa}, std::byte{0xaa},
        std::byte{0xaa}, std::byte{0xaa}, std::byte{0xaa}, std::byte{0xaa}, std::byte{0xaa}, std::byte{0xaa},
        std::byte{0xaa}, std::byte{0xaa}, std::byte{0xaa}, std::byte{0xaa}, std::byte{0xaa}, std::byte{0xaa},
        std::byte{0xaa}, std::byte{0xaa}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0x6d}, std::byte{0xdb},
        std::byte{0xb6}, std::byte{0x6d}, std::byte{0xdb}, std::byte{0xb6}, std::byte{0x6d}, std::byte{0xdb},
        std::byte{0xb6}, std::byte{0x6d}, std::byte{0xdb}, std::byte{0xb6}, std::byte{0x6d}, std::byte{0xdb},
        std::byte{0xb6}, std::byte{0x49}, std::byte{0x92}, std::byte{0x24}, std::byte{0x49}, std::byte{0x92},
        std::byte{0x24}, std::byte{0x49}, std::byte{0x92}, std::byte{0x24}, std::byte{0x49}, std::byte{0x92},
        std::byte{0x24}, std::byte{0xbb}, std::byte{0xbb}, std::byte{0xbb}, std::byte{0xbb}, std::byte{0xbb},
        std::byte{0xbb}, std::byte{0xbb}, std::byte{0xbb}, std::byte{0xbb}, std::byte{0xbb}, std::byte{0xbb},
        std::byte{0xbb}, std::byte{0x73}, std::byte{0xce}, std::byte{0x39}, std::byte{0xe7}, std::byte{0x9c},
        std::byte{0x73}, std::byte{0xce}, std::byte{0x39}, std::byte{0xe7}, std::byte{0x9c}, std::byte{0xe3},
        std::byte{0x38}, std::byte{0x8e}, std::byte{0xe3}, std::byte{0x38}, std::byte{0x8e},
    }};

    // ---- repeat ----
    // encode 200 bytes -> need=224 written=224
    inline const std::array<std::byte, 200> repeat_plain{{
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41}, std::byte{0x41},
        std::byte{0x41}, std::byte{0x41},
    }};

    inline const std::array<std::byte, 224> repeat_encoded{{
        std::byte{0x1f}, std::byte{0x20}, std::byte{0x56}, std::byte{0x40}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0xef}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff},
    }};

    // ---- twosymbols ----
    // encode 128 bytes -> need=223 written=223
    inline const std::array<std::byte, 128> twosymbols_plain{{
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01}, std::byte{0x00}, std::byte{0x01},
        std::byte{0x00}, std::byte{0x01},
    }};

    inline const std::array<std::byte, 223> twosymbols_encoded{{
        std::byte{0x1e}, std::byte{0x00}, std::byte{0x56}, std::byte{0x39}, std::byte{0xff}, std::byte{0xfc},
        std::byte{0x0f}, std::byte{0x7f}, std::byte{0xff}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00}, std::byte{0x00},
        std::byte{0x00}, std::byte{0xdb}, std::byte{0xb6}, std::byte{0x6d}, std::byte{0xdb}, std::byte{0xb6},
        std::byte{0x6d}, std::byte{0xdb}, std::byte{0xb6}, std::byte{0x6d}, std::byte{0xdb}, std::byte{0xb6},
        std::byte{0x6d}, std::byte{0xdb}, std::byte{0xb6}, std::byte{0x6d}, std::byte{0xdb}, std::byte{0xb6},
        std::byte{0x6d}, std::byte{0xdb}, std::byte{0xb6}, std::byte{0x6d}, std::byte{0xdb}, std::byte{0xb6},
        std::byte{0x6d},
    }};

    // Boundaries observed in the frozen phrase vector: the header holds the two sizes (MSB-first)
    // followed by 256 weight deltas, and then the LSB-first payload starts on the next byte.
    constexpr std::size_t phrase_header_bytes = 222;
    constexpr std::size_t phrase_pressed_bytes = 23;

    struct source_failure final : std::runtime_error
    {
        using std::runtime_error::runtime_error;
    };

    // Distinct from source_failure: raised only when a byte-budgeted source is asked for a byte
    // past its declared budget, so a test can prove the decoder never over-requests past the point
    // where a bound check should already have fired.
    struct source_budget_exceeded final : std::runtime_error
    {
        using std::runtime_error::runtime_error;
    };

    // A scriptable byte source: it can drip the stream out in fixed chunks, simulate a premature
    // end of stream, throw on a chosen call, report an over-long fill, or expose its final position.
    struct scripted_source
    {
        bytes data;
        std::size_t chunk = 0;
        std::uint64_t limit = 0;
        std::size_t fail_call = 0;
        bool overfill = false;
        bool throw_past_limit = false;
        std::size_t calls = 0;
        std::size_t pos = 0;
        // Largest destination span the decoder has ever asked this source to fill. A normally
        // filling source hands over every requested byte, so the span size is the honest measure of
        // how far past a safe prefix the decoder tried to read.
        std::size_t max_span = 0;

        std::size_t operator()(std::span<std::byte> destination)
        {
            ++calls;
            max_span = (std::max)(max_span, destination.size());
            if (fail_call != 0 && calls == fail_call) {
                throw source_failure("scripted source failure");
            }
            std::uint64_t available = static_cast<std::uint64_t>(data.size() - pos);
            if (limit != 0) {
                const std::uint64_t left = limit > static_cast<std::uint64_t>(pos) ? limit - pos : 0;
                available = (std::min)(available, left);
            }
            if (throw_past_limit && available == 0) {
                throw source_budget_exceeded("scripted source byte budget exhausted");
            }
            std::size_t produced =
                static_cast<std::size_t>((std::min)(available, static_cast<std::uint64_t>(destination.size())));
            if (chunk != 0) {
                produced = (std::min)(produced, chunk);
            }
            if (produced != 0) {
                std::memcpy(destination.data(), data.data() + pos, produced);
            }
            pos += produced;
            if (overfill) {
                return destination.size() + 1;
            }
            return produced;
        }
    };

    scripted_source make_source(std::span<const std::byte> contents)
    {
        scripted_source source;
        source.data.assign(contents.begin(), contents.end());
        return source;
    }

    huffman_reader make_reader(scripted_source &source, std::uint64_t encoded_size, std::uint64_t expected_size)
    {
        return huffman_reader([&source](std::span<std::byte> destination) { return source(destination); }, encoded_size,
                              expected_size);
    }

    huffman_reader make_bounded_reader(scripted_source &source, std::uint64_t encoded_size,
                                       std::uint64_t max_decoded_size)
    {
        return huffman_reader::bounded([&source](std::span<std::byte> destination) { return source(destination); },
                                       encoded_size, max_decoded_size);
    }

    bytes decode_all(huffman_reader &reader)
    {
        bytes out;
        std::array<std::byte, 7> scratch{};
        for (;;) {
            const std::size_t written = reader.read(std::span<std::byte>(scratch.data(), scratch.size()));
            if (written == 0) {
                break;
            }
            out.insert(out.end(), scratch.begin(), scratch.begin() + static_cast<std::ptrdiff_t>(written));
            if (out.size() > (std::size_t{1} << 24u)) {
                FAIL("decode produced an unbounded number of bytes");
            }
        }
        return out;
    }

    void require_bytes(const bytes &actual, std::span<const std::byte> expected)
    {
        REQUIRE(actual.size() == expected.size());
        for (std::size_t i = 0; i < actual.size() && i < expected.size(); ++i) {
            INFO("byte index " << i);
            REQUIRE(actual[i] == expected[i]);
        }
    }

    template <typename Exception, typename Callable> void require_throws(const Callable &callable)
    {
        bool threw = false;
        try {
            callable();
        } catch (const Exception &) {
            threw = true;
        }
        REQUIRE(threw);
    }

    template <typename Exception, typename Callable>
    void require_throws_message(const Callable &callable, const char *message)
    {
        bool threw = false;
        try {
            callable();
        } catch (const Exception &error) {
            threw = true;
            INFO("thrown: " << error.what() << " expected: " << message);
            REQUIRE(std::string_view(error.what()) == message);
        }
        REQUIRE(threw);
    }

    // MSB-first bit writer used to hand-build headers for the boundary cases the frozen vectors
    // never reach (overflowing weight deltas, a zero-length stream, mismatched sizes).
    struct msb_bit_writer
    {
        bytes bytes_;
        unsigned current_ = 0;
        int pending_ = 0;

        void put(std::uint64_t value, int count)
        {
            for (int i = count - 1; i >= 0; --i) {
                const unsigned bit = static_cast<unsigned>((value >> static_cast<unsigned>(i)) & 1u);
                current_ = (current_ << 1u) | bit;
                ++pending_;
                if (pending_ == 8) {
                    bytes_.push_back(static_cast<std::byte>(current_));
                    current_ = 0;
                    pending_ = 0;
                }
            }
        }

        void finish()
        {
            if (pending_ != 0) {
                current_ <<= static_cast<unsigned>(8 - pending_);
                bytes_.push_back(static_cast<std::byte>(current_));
                current_ = 0;
                pending_ = 0;
            }
        }
    };

    void write_size(msb_bit_writer &writer, std::uint64_t value)
    {
        unsigned bits = 1;
        while (bits < 64u && value >= (std::uint64_t{1} << bits)) {
            ++bits;
        }
        writer.put(bits - 1, 6);
        writer.put(value, static_cast<int>(bits));
    }

    void write_weight(msb_bit_writer &writer, unsigned magnitude, bool negative)
    {
        unsigned bits = 2;
        while (bits < 16u && static_cast<std::uint64_t>(magnitude) >= (std::uint64_t{1} << bits)) {
            bits += 2;
        }
        writer.put(bits / 2 - 1, 3);
        writer.put(negative ? 1u : 0u, 1);
        writer.put(magnitude, static_cast<int>(bits));
    }

    std::vector<std::byte> build_header(std::uint64_t original, std::uint64_t pressed,
                                        const std::vector<std::uint16_t> &weights)
    {
        msb_bit_writer writer;
        write_size(writer, original);
        write_size(writer, pressed);
        for (const std::uint16_t weight : weights) {
            write_weight(writer, weight, false);
        }
        writer.finish();
        return writer.bytes_;
    }

    // A fully resident, deliberately malformed header: the first weight is the absolute 65535 and
    // the second adds 1 on top of it, so the reconstructed weight leaves the uint16 range. The
    // invalid declaration is detected inside the supplied bytes (the whole header is a handful of
    // bytes and the overflow fires while the last weight is still being read), so this is safe to
    // hand to the lengthless pointer ABI, which cannot be told where the allocation ends.
    std::vector<std::byte> overrange_weight_header()
    {
        msb_bit_writer writer;
        write_size(writer, 1);
        write_size(writer, 0);
        write_weight(writer, 65535, false);
        write_weight(writer, 1, false);
        writer.finish();
        return writer.bytes_;
    }
} // namespace

TEST_CASE("wolfrpg huffman restores the frozen oracle vectors", "[wolfrpg][huffman]")
{
    struct golden
    {
        const char *name;
        std::span<const std::byte> plain;
        std::span<const std::byte> encoded;
    };
    const golden vectors[] = {
        {"phrase", phrase_plain, phrase_encoded},
        {"allsymbols", allsymbols_plain, allsymbols_encoded},
        {"ramp", ramp_plain, ramp_encoded},
        {"repeat", repeat_plain, repeat_encoded},
        {"twosymbols", twosymbols_plain, twosymbols_encoded},
    };

    for (const golden &vector : vectors) {
        INFO(vector.name);
        for (const std::size_t chunk : {std::size_t{0}, std::size_t{1}, std::size_t{2}, std::size_t{5}}) {
            scripted_source source = make_source(vector.encoded);
            source.chunk = chunk;
            huffman_reader reader = make_reader(source, vector.encoded.size(), vector.plain.size());
            REQUIRE(reader.remaining() == vector.plain.size());
            require_bytes(decode_all(reader), vector.plain);
            REQUIRE(reader.remaining() == 0);
        }
    }
}

TEST_CASE("wolfrpg huffman honours destination granularity", "[wolfrpg][huffman]")
{
    struct golden
    {
        std::span<const std::byte> plain;
        std::span<const std::byte> encoded;
    };
    const golden vectors[] = {
        {phrase_plain, phrase_encoded}, {allsymbols_plain, allsymbols_encoded}, {ramp_plain, ramp_encoded},
        {repeat_plain, repeat_encoded}, {twosymbols_plain, twosymbols_encoded},
    };

    for (const golden &vector : vectors) {
        for (const std::size_t batch : {std::size_t{1}, std::size_t{3}, std::size_t{7}, std::size_t{245}}) {
            scripted_source source = make_source(vector.encoded);
            huffman_reader reader = make_reader(source, vector.encoded.size(), vector.plain.size());
            bytes out;
            std::vector<std::byte> buffer(batch);
            for (;;) {
                const std::size_t written = reader.read(std::span<std::byte>(buffer.data(), buffer.size()));
                if (written == 0) {
                    break;
                }
                out.insert(out.end(), buffer.begin(), buffer.begin() + static_cast<std::ptrdiff_t>(written));
            }
            require_bytes(out, vector.plain);
        }
    }
}

TEST_CASE("wolfrpg huffman enforces the encoded extent", "[wolfrpg][huffman]")
{
    scripted_source short_header = make_source(phrase_encoded);
    require_throws_message<huffman_error>(
        [&] { (void)make_reader(short_header, phrase_header_bytes - 1, phrase_plain.size()); },
        "wolfrpg huffman header truncated");

    scripted_source no_payload = make_source(phrase_encoded);
    require_throws_message<huffman_error>(
        [&] { (void)make_reader(no_payload, phrase_header_bytes, phrase_plain.size()); },
        "wolfrpg huffman payload exceeds encoded extent");

    scripted_source one_short = make_source(phrase_encoded);
    require_throws_message<huffman_error>(
        [&] { (void)make_reader(one_short, phrase_header_bytes + phrase_pressed_bytes - 1, phrase_plain.size()); },
        "wolfrpg huffman payload exceeds encoded extent");

    scripted_source exact = make_source(phrase_encoded);
    huffman_reader reader = make_reader(exact, phrase_header_bytes + phrase_pressed_bytes, phrase_plain.size());
    require_bytes(decode_all(reader), phrase_plain);
}

TEST_CASE("wolfrpg huffman requires the declared payload to be consumed exactly", "[wolfrpg][huffman]")
{
    // A one-symbol stream over an all-equal-weight tree: the header carries 256 weights of 1 (the
    // first as an absolute value, the rest as zero deltas), every symbol sits eight bits deep, and
    // symbol 0 is the all-zero path. A single 0x00 payload byte therefore reproduces output 0x00.
    std::vector<std::uint16_t> flat_deltas(256, 0);
    flat_deltas[0] = 1;

    auto single_symbol_stream = [&](std::uint64_t pressed, std::size_t payload_bytes) {
        std::vector<std::byte> data = build_header(1, pressed, flat_deltas);
        data.insert(data.end(), payload_bytes, std::byte{0x00});
        return data;
    };

    SECTION("a matching single-symbol payload decodes and completes")
    {
        const std::vector<std::byte> data = single_symbol_stream(1, 1);
        scripted_source source = make_source(data);
        huffman_reader reader = make_reader(source, data.size(), 1);
        std::array<std::byte, 1> out{};
        REQUIRE(reader.read(std::span<std::byte>(out.data(), out.size())) == 1);
        REQUIRE(out[0] == std::byte{0x00});
        REQUIRE(reader.remaining() == 0);
        REQUIRE(reader.read(std::span<std::byte>(out.data(), 0)) == 0);
    }

    SECTION("an unused whole payload byte is rejected")
    {
        const std::vector<std::byte> data = single_symbol_stream(2, 2);
        scripted_source source = make_source(data);
        huffman_reader reader = make_reader(source, data.size(), 1);
        std::array<std::byte, 1> out{};
        require_throws_message<huffman_error>([&] { (void)reader.read(std::span<std::byte>(out.data(), out.size())); },
                                              "wolfrpg huffman payload not fully consumed");
    }

    SECTION("bytes appended outside the declared payload are rejected at construction")
    {
        const std::vector<std::byte> data = single_symbol_stream(1, 2);
        scripted_source source = make_source(data);
        require_throws_message<huffman_error>([&] { (void)make_reader(source, data.size(), 1); },
                                              "wolfrpg huffman trailing bytes outside declared payload");
    }

    SECTION("an overstated payload with a physically absent tail is rejected")
    {
        const std::vector<std::byte> declared = single_symbol_stream(4, 4);
        const std::vector<std::byte> present = single_symbol_stream(4, 1);
        scripted_source source = make_source(present);
        huffman_reader reader = make_reader(source, declared.size(), 1);
        std::array<std::byte, 1> out{};
        require_throws_message<huffman_error>([&] { (void)reader.read(std::span<std::byte>(out.data(), out.size())); },
                                              "wolfrpg huffman payload not fully consumed");
    }

    SECTION("an empty stream that carries a payload is rejected at construction")
    {
        std::vector<std::uint16_t> zeros(256, 0);
        std::vector<std::byte> data = build_header(0, 1, zeros);
        data.push_back(std::byte{0x00});
        scripted_source source = make_source(data);
        require_throws_message<huffman_error>([&] { (void)make_reader(source, data.size(), 0); },
                                              "wolfrpg huffman empty stream carries a payload");
    }
}

TEST_CASE("wolfrpg huffman never reads past the encoded extent", "[wolfrpg][huffman]")
{
    scripted_source source = make_source(phrase_encoded);
    source.data.resize(phrase_encoded.size() + 16, std::byte{0xEE});
    huffman_reader reader = make_reader(source, phrase_encoded.size(), phrase_plain.size());
    require_bytes(decode_all(reader), phrase_plain);
    REQUIRE(source.pos == phrase_encoded.size());
}

TEST_CASE("wolfrpg huffman rejects an out-of-range weight delta", "[wolfrpg][huffman]")
{
    SECTION("positive delta past the uint16 ceiling")
    {
        const std::vector<std::byte> data = overrange_weight_header();
        scripted_source source = make_source(data);
        require_throws_message<huffman_error>([&] { (void)make_reader(source, source.data.size(), 1); },
                                              "wolfrpg huffman weight out of range");
    }

    SECTION("negative delta below zero")
    {
        msb_bit_writer writer;
        write_size(writer, 1);
        write_size(writer, 0);
        write_weight(writer, 0, false);
        write_weight(writer, 1, true);
        writer.finish();
        scripted_source source = make_source(writer.bytes_);
        require_throws_message<huffman_error>([&] { (void)make_reader(source, source.data.size(), 1); },
                                              "wolfrpg huffman weight out of range");
    }
}

TEST_CASE("wolfrpg huffman rejects a mismatched original size", "[wolfrpg][huffman]")
{
    const std::vector<std::byte> header = build_header(5, 0, {});
    scripted_source source = make_source(header);
    require_throws_message<huffman_error>([&] { (void)make_reader(source, source.data.size(), 6); },
                                          "wolfrpg huffman original size mismatch");
}

TEST_CASE("wolfrpg huffman decodes a zero-length stream", "[wolfrpg][huffman]")
{
    const std::vector<std::byte> header = build_header(0, 0, std::vector<std::uint16_t>(256, 0));
    scripted_source source = make_source(header);
    huffman_reader reader = make_reader(source, source.data.size(), 0);
    REQUIRE(reader.remaining() == 0);
    std::array<std::byte, 4> probe{};
    REQUIRE(reader.read(std::span<std::byte>(probe.data(), probe.size())) == 0);
    REQUIRE(reader.read(std::span<std::byte>(probe.data(), 0)) == 0);
}

TEST_CASE("wolfrpg huffman rejects an empty source", "[wolfrpg][huffman]")
{
    require_throws_message<huffman_error>([] { (void)huffman_reader(huffman_reader::source{}, 0, 0); },
                                          "wolfrpg huffman source is empty");
}

TEST_CASE("wolfrpg huffman rejects an overfilling source", "[wolfrpg][huffman]")
{
    scripted_source source = make_source(phrase_encoded);
    source.overfill = true;
    require_throws_message<huffman_error>([&] { (void)make_reader(source, source.data.size(), phrase_plain.size()); },
                                          "wolfrpg huffman source overfilled");
}

TEST_CASE("wolfrpg huffman surfaces source failures unchanged", "[wolfrpg][huffman]")
{
    SECTION("during the header probe")
    {
        scripted_source source = make_source(phrase_encoded);
        source.chunk = 1;
        source.fail_call = 1;
        CHECK_THROWS_AS((make_reader(source, source.data.size(), phrase_plain.size())), source_failure);
    }

    SECTION("while streaming the payload")
    {
        scripted_source source = make_source(phrase_encoded);
        source.chunk = 1;
        source.fail_call = phrase_header_bytes + 1;
        huffman_reader reader = make_reader(source, source.data.size(), phrase_plain.size());
        try {
            std::array<std::byte, phrase_plain.size()> out{};
            reader.read(std::span<std::byte>(out.data(), out.size()));
            FAIL("expected the scripted source failure to propagate");
        } catch (const source_failure &failure) {
            REQUIRE(std::string(failure.what()) == "scripted source failure");
        }
    }
}

TEST_CASE("wolfrpg huffman detects a payload that ends early", "[wolfrpg][huffman]")
{
    scripted_source source = make_source(phrase_encoded);
    source.limit = phrase_header_bytes + 4;
    huffman_reader reader = make_reader(source, phrase_encoded.size(), phrase_plain.size());
    std::array<std::byte, 43> out{};
    require_throws_message<huffman_error>([&] { (void)reader.read(std::span<std::byte>(out.data(), out.size())); },
                                          "wolfrpg huffman payload truncated");
}

TEST_CASE("wolfrpg huffman rejects a nonempty output that declares no payload", "[wolfrpg][huffman]")
{
    // A structurally valid header: it echoes the reported original size and declares no pressed
    // bytes, and the 256 weights are a legal all-zero delta run. Construction therefore succeeds.
    // Decoding one output byte needs a symbol, but the declared payload is empty, so no symbol can
    // ever be drained and the read must fail as truncated with the output still outstanding.
    const std::vector<std::byte> data = build_header(256, 0, std::vector<std::uint16_t>(256, 0));
    scripted_source source = make_source(data);
    huffman_reader reader = make_reader(source, data.size(), 256);
    REQUIRE(reader.remaining() == 256);
    std::array<std::byte, 1> out{};
    require_throws_message<huffman_error>([&] { (void)reader.read(std::span<std::byte>(out.data(), out.size())); },
                                          "wolfrpg huffman payload truncated");
    REQUIRE(reader.remaining() == 256);
}

TEST_CASE("wolfrpg huffman latches a physically truncated payload across reads", "[wolfrpg][huffman]")
{
    // The declared encoded extent promises the full phrase payload, but the source only yields the
    // header plus four payload bytes before signalling end of stream. That zero-length read is the
    // one-and-only end-of-stream witness: a second read must fail again without polling the source,
    // because no further byte can ever appear and the output must not creep forward.
    scripted_source source = make_source(phrase_encoded);
    source.limit = phrase_header_bytes + 4;
    huffman_reader reader = make_reader(source, phrase_encoded.size(), phrase_plain.size());
    std::array<std::byte, 43> out{};
    require_throws_message<huffman_error>([&] { (void)reader.read(std::span<std::byte>(out.data(), out.size())); },
                                          "wolfrpg huffman payload truncated");
    const std::size_t calls_after_first = source.calls;
    const std::uint64_t remaining_after_first = reader.remaining();
    require_throws_message<huffman_error>([&] { (void)reader.read(std::span<std::byte>(out.data(), out.size())); },
                                          "wolfrpg huffman payload truncated");
    REQUIRE(source.calls == calls_after_first);
    REQUIRE(reader.remaining() == remaining_after_first);
}

TEST_CASE("wolfrpg huffman supports move construction and assignment", "[wolfrpg][huffman]")
{
    std::array<std::byte, 4> probe{};

    scripted_source phrase = make_source(phrase_encoded);
    huffman_reader original = make_reader(phrase, phrase_encoded.size(), phrase_plain.size());
    huffman_reader moved(std::move(original));
    require_bytes(decode_all(moved), phrase_plain);
    // A moved-from reader is an explicitly guaranteed empty reader (remaining() == 0 and reads
    // return zero), not a real use-after-move: clang-tidy documents this via NOLINT and MSVC's
    // C26800 note needs the same statement-level suppression for this one contracted check.
#pragma warning(suppress : 26800)
    REQUIRE(original.remaining() == 0); // NOLINT(bugprone-use-after-move, clang-analyzer-cplusplus.Move)
    REQUIRE(original.read(std::span<std::byte>(probe.data(), probe.size())) == 0);

    scripted_source ramp = make_source(ramp_encoded);
    huffman_reader donor = make_reader(ramp, ramp_encoded.size(), ramp_plain.size());
    scripted_source repeat = make_source(repeat_encoded);
    huffman_reader target = make_reader(repeat, repeat_encoded.size(), repeat_plain.size());
    target = std::move(donor);
    require_bytes(decode_all(target), ramp_plain);
#pragma warning(suppress : 26800)
    REQUIRE(donor.remaining() == 0); // NOLINT(bugprone-use-after-move, clang-analyzer-cplusplus.Move)
    REQUIRE(donor.read(std::span<std::byte>(probe.data(), probe.size())) == 0);
}

TEST_CASE("wolfrpg huffman bounded factory derives the decoded size from the header", "[wolfrpg][huffman]")
{
    SECTION("an original size above the bound is rejected")
    {
        scripted_source source = make_source(phrase_encoded);
        require_throws_message<huffman_error>(
            [&] { (void)make_bounded_reader(source, phrase_encoded.size(), phrase_plain.size() - 1); },
            "wolfrpg huffman original size exceeds bound");
    }

    SECTION("an exact bound decodes the whole stream")
    {
        scripted_source source = make_source(phrase_encoded);
        huffman_reader reader = make_bounded_reader(source, phrase_encoded.size(), phrase_plain.size());
        REQUIRE(reader.remaining() == phrase_plain.size());
        require_bytes(decode_all(reader), phrase_plain);
        REQUIRE(reader.remaining() == 0);
    }

    SECTION("a loose bound still decodes exactly the declared original size")
    {
        // The bound is only a ceiling: the reader still trusts the header's original size (43) as
        // its exact output length, so remaining() reports 43 and all 43 bytes are produced.
        scripted_source source = make_source(phrase_encoded);
        huffman_reader reader = make_bounded_reader(source, phrase_encoded.size(), phrase_plain.size() + 1);
        REQUIRE(reader.remaining() == phrase_plain.size());
        require_bytes(decode_all(reader), phrase_plain);
        REQUIRE(reader.remaining() == 0);
    }
}

TEST_CASE("wolfrpg huffman bounded factory accepts an empty stream at a zero bound", "[wolfrpg][huffman]")
{
    const std::vector<std::byte> header = build_header(0, 0, std::vector<std::uint16_t>(256, 0));
    scripted_source source = make_source(header);
    huffman_reader reader = make_bounded_reader(source, header.size(), 0);
    REQUIRE(reader.remaining() == 0);
    std::array<std::byte, 4> probe{};
    REQUIRE(reader.read(std::span<std::byte>(probe.data(), probe.size())) == 0);
    REQUIRE(reader.remaining() == 0);
}

TEST_CASE("wolfrpg huffman bounded factory rejects a truncated original prefix", "[wolfrpg][huffman]")
{
    SECTION("a source that yields no byte at all")
    {
        scripted_source source = make_source(std::span<const std::byte>{});
        require_throws_message<huffman_error>([&] { (void)make_bounded_reader(source, 0, 100); },
                                              "wolfrpg huffman header truncated");
    }

    SECTION("a source that ends inside the original-size prefix")
    {
        scripted_source source = make_source(std::span<const std::byte>(phrase_encoded.data(), std::size_t{1}));
        require_throws_message<huffman_error>([&] { (void)make_bounded_reader(source, 1, phrase_plain.size()); },
                                              "wolfrpg huffman header truncated");
    }
}

TEST_CASE("wolfrpg huffman bounded factory rejects an overflow-sized original", "[wolfrpg][huffman]")
{
    SECTION("a full 64-bit original")
    {
        msb_bit_writer writer;
        write_size(writer, 0xFFFFFFFFFFFFFFFFull);
        writer.finish();
        scripted_source source = make_source(writer.bytes_);
        require_throws_message<huffman_error>([&] { (void)make_bounded_reader(source, source.data.size(), 4096); },
                                              "wolfrpg huffman original size exceeds bound");
    }

    SECTION("a wide original just above the bound")
    {
        msb_bit_writer writer;
        write_size(writer, std::uint64_t{1} << 20u);
        writer.finish();
        scripted_source source = make_source(writer.bytes_);
        require_throws_message<huffman_error>(
            [&] { (void)make_bounded_reader(source, source.data.size(), (std::uint64_t{1} << 20u) - 1); },
            "wolfrpg huffman original size exceeds bound");
    }
}

TEST_CASE("wolfrpg huffman bounded factory propagates source failures unchanged", "[wolfrpg][huffman]")
{
    scripted_source source = make_source(phrase_encoded);
    source.chunk = 1;
    source.fail_call = 1;
    bool propagated = false;
    try {
        (void)make_bounded_reader(source, phrase_encoded.size(), phrase_plain.size());
    } catch (const source_failure &failure) {
        propagated = true;
        REQUIRE(std::string(failure.what()) == "scripted source failure");
    } catch (const huffman_error &error) {
        FAIL("a source exception was translated into a huffman_error: " << error.what());
    }
    REQUIRE(propagated);
}

TEST_CASE("wolfrpg huffman bounded reader streams partial reads and moves", "[wolfrpg][huffman]")
{
    SECTION("single-byte reads still reconstruct the phrase")
    {
        scripted_source source = make_source(phrase_encoded);
        source.chunk = 1;
        huffman_reader reader = make_bounded_reader(source, phrase_encoded.size(), phrase_plain.size() + 8);
        REQUIRE(reader.remaining() == phrase_plain.size());
        bytes out;
        std::array<std::byte, 1> one{};
        for (;;) {
            const std::size_t written = reader.read(std::span<std::byte>(one.data(), one.size()));
            if (written == 0) {
                break;
            }
            out.push_back(one[0]);
        }
        require_bytes(out, phrase_plain);
        REQUIRE(reader.remaining() == 0);
    }

    SECTION("a bounded reader can be moved")
    {
        std::array<std::byte, 4> probe{};
        scripted_source source = make_source(phrase_encoded);
        huffman_reader original = make_bounded_reader(source, phrase_encoded.size(), phrase_plain.size());
        huffman_reader moved(std::move(original));
        require_bytes(decode_all(moved), phrase_plain);
#pragma warning(suppress : 26800)
        REQUIRE(original.remaining() == 0); // NOLINT(bugprone-use-after-move, clang-analyzer-cplusplus.Move)
        REQUIRE(original.read(std::span<std::byte>(probe.data(), probe.size())) == 0);
    }
}

TEST_CASE("wolfrpg huffman exact constructor stays exact alongside the bounded factory", "[wolfrpg][huffman]")
{
    // The pre-existing exact contract is untouched: a declared-size mismatch is still rejected, and
    // a matching declared size still decodes. The bounded factory lives beside it, not on top of it.
    scripted_source mismatched = make_source(phrase_encoded);
    require_throws_message<huffman_error>(
        [&] { (void)make_reader(mismatched, phrase_encoded.size(), phrase_plain.size() + 1); },
        "wolfrpg huffman original size mismatch");

    scripted_source exact = make_source(phrase_encoded);
    huffman_reader reader = make_reader(exact, phrase_encoded.size(), phrase_plain.size());
    require_bytes(decode_all(reader), phrase_plain);
}

TEST_CASE("wolfrpg huffman bounded factory rejects an oversize original before touching weights", "[wolfrpg][huffman]")
{
    // The source hands over exactly the two bytes that carry the original-size prefix for 43 and
    // then throws if asked for anything else. Because the declared original exceeds the bound, the
    // factory must reject with a huffman_error as soon as the size is known, before requesting a
    // weight byte; a decoder that kept reading would surface source_budget_exceeded instead.
    scripted_source source = make_source(phrase_encoded);
    source.limit = 2;
    source.throw_past_limit = true;
    bool rejected = false;
    try {
        (void)make_bounded_reader(source, phrase_encoded.size(), phrase_plain.size() - 1);
    } catch (const huffman_error &error) {
        rejected = true;
        REQUIRE(std::string_view(error.what()) == "wolfrpg huffman original size exceeds bound");
    } catch (const source_budget_exceeded &) {
        FAIL("the reader requested bytes beyond the size prefix before enforcing the bound");
    }
    REQUIRE(rejected);
    REQUIRE(source.pos == std::size_t{2});
}

TEST_CASE("wolfrpg huffman bounded factory confines its pulls to the original-size prefix", "[wolfrpg][huffman]")
{
    // The phrase's 43-byte original is carried by a two-byte prefix at the head of the stream. This
    // source fills whatever it is asked for out of the full encoded vector, so a factory that
    // requested a whole read buffer up front would already have been handed weight and payload
    // bytes before the ceiling was enforced. With the bound one below the declared 43 the request
    // must stay inside the prefix, and the source must never be asked for more than two bytes.
    constexpr std::size_t phrase_prefix_bytes = 2;

    scripted_source rejecting = make_source(phrase_encoded);
    require_throws_message<huffman_error>(
        [&] { (void)make_bounded_reader(rejecting, phrase_encoded.size(), phrase_plain.size() - 1); },
        "wolfrpg huffman original size exceeds bound");
    REQUIRE(rejecting.max_span <= phrase_prefix_bytes);
    REQUIRE(rejecting.pos == phrase_prefix_bytes);

    // Once the original size is known, an accepted bounded reader goes back to ordinary buffered
    // pulls instead of dribbling the stream out one byte at a time: decoding the phrase asks for
    // much more than the prefix in a single call.
    scripted_source accepting = make_source(phrase_encoded);
    huffman_reader reader = make_bounded_reader(accepting, phrase_encoded.size(), phrase_plain.size());
    require_bytes(decode_all(reader), phrase_plain);
    REQUIRE(accepting.max_span > phrase_prefix_bytes);
}

namespace
{
    // A byte source in the vendor's no-throw C function-pointer shape, so the shared
    // bounded_huffman context can be driven directly rather than only through the bridge.
    struct vendor_source
    {
        std::span<const std::byte> data;
        std::size_t position = 0;
        bool overfill = false;

        static std::size_t pull(void *context, unsigned char *destination, std::size_t capacity)
        {
            vendor_source &self = *static_cast<vendor_source *>(context);
            const std::size_t available = self.data.size() - self.position;
            const std::size_t produced = (std::min)(available, capacity);
            if (produced != 0) {
                std::memcpy(destination, self.data.data() + self.position, produced);
            }
            self.position += produced;
            if (self.overfill) {
                return capacity + 1;
            }
            return produced;
        }
    };
} // namespace

TEST_CASE("wolfrpg shared huffman context is exercised directly", "[wolfrpg][huffman]")
{
    using uberwolf::archive::bounded_huffman;
    using uberwolf::archive::huffman_size_mode;
    using uberwolf::archive::huffman_status;

    SECTION("the vendor context decodes the frozen phrase on its own")
    {
        vendor_source source{phrase_encoded, 0, false};
        auto decoder = std::make_unique<bounded_huffman>();
        REQUIRE(decoder->init(&vendor_source::pull, &source, huffman_size_mode::exact, phrase_encoded.size(),
                              phrase_plain.size()) == huffman_status::ok);
        REQUIRE(decoder->original_size() == phrase_plain.size());
        REQUIRE(decoder->produced() == 0);
        bytes out;
        std::array<unsigned char, 7> scratch{};
        for (;;) {
            std::size_t written = 0;
            REQUIRE(decoder->read(scratch.data(), scratch.size(), &written) == huffman_status::ok);
            out.insert(out.end(), reinterpret_cast<std::byte *>(scratch.data()),
                       reinterpret_cast<std::byte *>(scratch.data()) + written);
            if (written < scratch.size()) {
                break;
            }
        }
        require_bytes(out, phrase_plain);
        REQUIRE(decoder->remaining() == 0);
        REQUIRE(decoder->produced() == phrase_plain.size());
    }

    SECTION("the vendor context rejects an overfilling source before it overruns the window")
    {
        vendor_source source{phrase_encoded, 0, true};
        auto decoder = std::make_unique<bounded_huffman>();
        REQUIRE(decoder->init(&vendor_source::pull, &source, huffman_size_mode::exact, phrase_encoded.size(),
                              phrase_plain.size()) == huffman_status::source_failure);
    }
}

TEST_CASE("wolfrpg shared huffman context reports a truncated header at every cut", "[wolfrpg][huffman]")
{
    using uberwolf::archive::bounded_huffman;
    using uberwolf::archive::huffman_size_mode;
    using uberwolf::archive::huffman_status;

    // Cutting the valid phrase stream anywhere inside its header must be reported as a truncated
    // header, never as a wrong size or a partially built tree. This walks every cut length so each
    // header field's failure return is taken.
    for (std::size_t cut = 0; cut < phrase_header_bytes; ++cut) {
        INFO("cut " << cut);
        vendor_source source{std::span<const std::byte>(phrase_encoded.data(), cut), 0, false};
        auto decoder = std::make_unique<bounded_huffman>();
        REQUIRE(decoder->init(&vendor_source::pull, &source, huffman_size_mode::exact, cut, phrase_plain.size()) ==
                huffman_status::header_truncated);
    }
}

TEST_CASE("wolfrpg shared huffman context reports field-aligned header truncations", "[wolfrpg][huffman]")
{
    using uberwolf::archive::bounded_huffman;
    using uberwolf::archive::huffman_size_mode;
    using uberwolf::archive::huffman_status;

    // Byte-granular cuts of the real stream cannot land between every pair of header sub-fields, so
    // these hand-built streams place the cut exactly where a value or sign bit is missing.
    SECTION("a cut between the pressed-size field and its value")
    {
        msb_bit_writer writer;
        write_size(writer, 1);
        writer.put(4, 6); // pressed field = 4, so a five-bit value should follow but is absent
        writer.finish();
        vendor_source source{std::span<const std::byte>(writer.bytes_.data(), writer.bytes_.size()), 0, false};
        auto decoder = std::make_unique<bounded_huffman>();
        REQUIRE(decoder->init(&vendor_source::pull, &source, huffman_size_mode::exact, writer.bytes_.size(), 1) ==
                huffman_status::header_truncated);
    }

    SECTION("a cut between a weight width field and its sign bit")
    {
        msb_bit_writer writer;
        write_size(writer, 2);
        write_size(writer, 0);
        write_weight(writer, 0, false);
        writer.put(0, 3); // the next width field ends exactly on the byte boundary
        writer.finish();
        vendor_source source{std::span<const std::byte>(writer.bytes_.data(), writer.bytes_.size()), 0, false};
        auto decoder = std::make_unique<bounded_huffman>();
        REQUIRE(decoder->init(&vendor_source::pull, &source, huffman_size_mode::exact, writer.bytes_.size(), 2) ==
                huffman_status::header_truncated);
    }
}

namespace
{
    // Encodes plaintext with the pinned DXLib encoder. The reuse refactor never touched the
    // encoder, so it is the independent oracle that pins the decoder to the upstream byte layout.
    std::vector<unsigned char> legacy_encode(std::span<const std::byte> plain)
    {
        void *source = const_cast<std::byte *>(plain.data());
        const u64 need = Huffman_Encode(source, static_cast<u64>(plain.size()), nullptr);
        std::vector<unsigned char> encoded(static_cast<std::size_t>(need));
        const u64 written = Huffman_Encode(source, static_cast<u64>(plain.size()), encoded.data());
        REQUIRE(written == need);
        return encoded;
    }

    // Copies a byte vector with a small zeroed tail. The legacy C ABI receives no encoded extent, so
    // the slack keeps the original lookahead reader inside the allocation while still exposing the
    // declared-size behaviour under test.
    std::vector<unsigned char> legacy_stream(std::span<const std::byte> header)
    {
        std::vector<unsigned char> stream(header.size() + 4, 0);
        for (std::size_t i = 0; i < header.size(); ++i) {
            stream[i] = std::to_integer<unsigned char>(header[i]);
        }
        return stream;
    }
} // namespace

TEST_CASE("wolfrpg legacy C ABI Huffman_Decode keeps its contract", "[wolfrpg][huffman]")
{
    // The shipped entry point is the original void*/void* symbol with the original return width.
    static_assert(std::is_same_v<decltype(&Huffman_Decode), u64 (*)(void *, void *)>);

    SECTION("the destination-null query reports the exact original size")
    {
        std::vector<unsigned char> encoded = legacy_encode(phrase_plain);
        CHECK(Huffman_Decode(encoded.data(), nullptr) == static_cast<u64>(phrase_plain.size()));
    }

    SECTION("a valid stream decodes to exactly the original bytes")
    {
        std::vector<unsigned char> encoded = legacy_encode(phrase_plain);
        std::vector<unsigned char> decoded(phrase_plain.size());
        CHECK(Huffman_Decode(encoded.data(), decoded.data()) == static_cast<u64>(phrase_plain.size()));
        bool equal = true;
        for (std::size_t i = 0; i < decoded.size(); ++i) {
            equal = equal && decoded[i] == std::to_integer<unsigned char>(phrase_plain[i]);
        }
        CHECK(equal);
    }

    SECTION("a single byte round-trips")
    {
        const std::array<std::byte, 1> one{std::byte{0x41}};
        std::vector<unsigned char> encoded = legacy_encode(one);
        std::array<unsigned char, 1> decoded{0x00};
        CHECK(Huffman_Decode(encoded.data(), decoded.data()) == 1);
        CHECK(decoded[0] == 0x41);
    }

    SECTION("a small output round-trips")
    {
        const std::array<std::byte, 6> small{std::byte{0x68}, std::byte{0x65}, std::byte{0x6c},
                                             std::byte{0x6c}, std::byte{0x6f}, std::byte{0x21}};
        std::vector<unsigned char> encoded = legacy_encode(small);
        std::vector<unsigned char> decoded(small.size(), 0x00);
        CHECK(Huffman_Decode(encoded.data(), decoded.data()) == static_cast<u64>(small.size()));
        bool equal = true;
        for (std::size_t i = 0; i < decoded.size(); ++i) {
            equal = equal && decoded[i] == std::to_integer<unsigned char>(small[i]);
        }
        CHECK(equal);
    }

    SECTION("both empty representations produce zero bytes")
    {
        // original = 0, pressed = 0: the header declares an empty stream, so both the size query and
        // the real copy must report and write nothing.
        const std::vector<std::byte> header = build_header(0, 0, std::vector<std::uint16_t>(256, 0));
        std::vector<unsigned char> stream = legacy_stream(header);
        CHECK(Huffman_Decode(stream.data(), nullptr) == 0);
        std::array<unsigned char, 1> probe{0xee};
        CHECK(Huffman_Decode(stream.data(), probe.data()) == 0);
        CHECK(probe[0] == 0xee);
    }

    SECTION("an original size with no payload is rejected instead of fabricating bytes")
    {
        // original = 1 but pressed = 0: there is no payload to produce the declared byte, so the
        // shared decoder reports failure (0) rather than reading past the header into slack.
        const std::vector<std::byte> header = build_header(1, 0, std::vector<std::uint16_t>(256, 0));
        std::vector<unsigned char> stream = legacy_stream(header);
        std::array<unsigned char, 1> out{0xee};
        CHECK(Huffman_Decode(stream.data(), out.data()) == 0);
        CHECK(out[0] == 0xee);
    }

    SECTION("a malformed weight delta is rejected without producing bytes")
    {
        // The pointer ABI carries no encoded extent, so it cannot observe physical truncation; that
        // contract belongs to the bounded reader tests. This drives a fully resident header whose
        // second weight delta overflows the uint16 ceiling instead. The invalid declaration is
        // detected before any read would leave the supplied bytes, so no out-of-bounds read occurs
        // and both the size query and the copy must report 0 with the destination untouched.
        std::vector<unsigned char> stream = legacy_stream(overrange_weight_header());
        std::array<unsigned char, 1> out{0xee};
        CHECK(Huffman_Decode(stream.data(), nullptr) == 0);
        CHECK(Huffman_Decode(stream.data(), out.data()) == 0);
        CHECK(out[0] == 0xee);
    }

    SECTION("an output larger than the copy window round-trips")
    {
        // More than 0x10000 bytes forces the delegation loop through its windowed chunking twice.
        std::vector<std::byte> big(70000);
        for (std::size_t i = 0; i < big.size(); ++i) {
            big[i] = std::byte{static_cast<unsigned char>('a' + (i % 26))};
        }
        std::vector<unsigned char> encoded = legacy_encode(big);
        std::vector<unsigned char> decoded(big.size(), 0x00);
        CHECK(Huffman_Decode(encoded.data(), decoded.data()) == static_cast<u64>(big.size()));
        bool equal = true;
        for (std::size_t i = 0; i < decoded.size(); ++i) {
            equal = equal && decoded[i] == std::to_integer<unsigned char>(big[i]);
        }
        CHECK(equal);
    }
}
