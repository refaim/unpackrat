// Behavioral tests for unpackrat::wolfrpg::text, the platform-neutral decoder for Wolf RPG / DXLib
// string components. The encoded byte layouts and their expected Unicode scalars are hand-authored
// from the published code page assignments (Shift-JIS, GB2312, UHC, Big5, Windows-1252, ISO-8859-15,
// UTF-8, UTF-16, UTF-32) rather than produced by the decoder under test, so the expectations are
// independent of the DXLib primitives the adapter isolates.

#include "../../core/wolfrpg/text_encoding.h"

#include <barrier>
#include <cstddef>
#include <cstdint>
#include <initializer_list>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <thread>
#include <vector>

#include <windows.h>

#include <catch2/catch_test_macros.hpp>

namespace
{
    using unpackrat::wolfrpg::text::convert;
    using unpackrat::wolfrpg::text::encoding_error;
    using unpackrat::wolfrpg::text::encoding_error_code;

    std::vector<std::byte> encoded(std::initializer_list<unsigned> values)
    {
        std::vector<std::byte> out;
        out.reserve(values.size());
        for (const unsigned value : values) {
            out.push_back(static_cast<std::byte>(value));
        }
        return out;
    }

    std::vector<std::byte> encoded_repeated(unsigned value, std::size_t count)
    {
        return std::vector<std::byte>(count, static_cast<std::byte>(value));
    }

    std::string utf8(std::initializer_list<unsigned> values)
    {
        std::string out;
        out.reserve(values.size());
        for (const unsigned value : values) {
            out.push_back(static_cast<char>(value));
        }
        return out;
    }

    std::string convert_bytes(std::initializer_list<unsigned> values, std::uint32_t codepage)
    {
        const std::vector<std::byte> data = encoded(values);
        return convert(data, codepage);
    }

    std::optional<encoding_error_code> failure_of(const std::vector<std::byte> &data, std::uint32_t codepage)
    {
        try {
            (void)convert(data, codepage);
        } catch (const encoding_error &error) {
            return error.code();
        }
        return std::nullopt;
    }

    std::wstring current_executable_path()
    {
        std::wstring buffer(260, L'\0');
        for (;;) {
            const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
            if (length == 0) {
                return {};
            }
            if (length < buffer.size()) {
                buffer.resize(length);
                return buffer;
            }
            buffer.resize(buffer.size() * 2);
        }
    }
} // namespace

TEST_CASE("TEXT_ENCODING holds the documented component bounds", "[wolfrpg][text]")
{
    STATIC_REQUIRE(unpackrat::wolfrpg::text::max_component_bytes == 4092);
    STATIC_REQUIRE(unpackrat::wolfrpg::text::max_utf8_bytes == 4 * 4092);
}

TEST_CASE("TEXT_ENCODING keeps Shift-JIS trail bytes that collide with the 0x5C separator", "[wolfrpg][text]")
{
    // U+8868 is 0x95 0x5C in Shift-JIS: the trail byte equals the ASCII backslash. A decoder that
    // tested separators on the encoded bytes would split this character and reject it.
    REQUIRE(convert_bytes({0x95, 0x5C}, 932) == utf8({0xE8, 0xA1, 0xA8}));
    // U+30BD is 0x83 0x5C.
    REQUIRE(convert_bytes({0x83, 0x5C}, 932) == utf8({0xE3, 0x82, 0xBD}));
    // A run of ASCII around the colliding character must be preserved exactly.
    REQUIRE(convert_bytes({0x41, 0x95, 0x5C, 0x42}, 932) == utf8({0x41, 0xE8, 0xA1, 0xA8, 0x42}));
    // Plain ASCII is unchanged.
    REQUIRE(convert_bytes({0x61, 0x62, 0x63}, 932) == std::string("abc"));
}

TEST_CASE("TEXT_ENCODING decodes every supported multi-byte code page", "[wolfrpg][text]")
{
    // GB2312 U+4E2D is 0xD6 0xD0.
    REQUIRE(convert_bytes({0xD6, 0xD0}, 936) == utf8({0xE4, 0xB8, 0xAD}));
    // UHC U+AC00 is 0xB0 0xA1.
    REQUIRE(convert_bytes({0xB0, 0xA1}, 949) == utf8({0xEA, 0xB0, 0x80}));
    // Big5 U+4E2D is 0xA4 0xA4.
    REQUIRE(convert_bytes({0xA4, 0xA4}, 950) == utf8({0xE4, 0xB8, 0xAD}));
}

TEST_CASE("TEXT_ENCODING decodes every supported single-byte code page", "[wolfrpg][text]")
{
    // Windows-1252 U+00E9 is 0xE9 and U+20AC is 0x80.
    REQUIRE(convert_bytes({0xE9}, 1252) == utf8({0xC3, 0xA9}));
    REQUIRE(convert_bytes({0x80}, 1252) == utf8({0xE2, 0x82, 0xAC}));
    // ISO-8859-15 U+20AC is 0xA4 and U+0160 is 0xA6.
    REQUIRE(convert_bytes({0xA4}, 32764) == utf8({0xE2, 0x82, 0xAC}));
    REQUIRE(convert_bytes({0xA6}, 32764) == utf8({0xC5, 0xA0}));
    // ASCII is unchanged.
    REQUIRE(convert_bytes({0x41, 0x7A, 0x30}, 32765) == std::string("Az0"));
}

TEST_CASE("TEXT_ENCODING decodes UTF-8 including supplementary scalars", "[wolfrpg][text]")
{
    // Passes through well-formed UTF-8 unchanged, including a supplementary scalar.
    REQUIRE(convert_bytes({0xE6, 0x97, 0xA5}, 65001) == utf8({0xE6, 0x97, 0xA5}));             // U+65E5
    REQUIRE(convert_bytes({0xF0, 0x9F, 0x98, 0x80}, 65001) == utf8({0xF0, 0x9F, 0x98, 0x80})); // U+1F600
    REQUIRE(convert_bytes({0xF0, 0x90, 0x80, 0x80}, 65001) == utf8({0xF0, 0x90, 0x80, 0x80})); // U+10000
    REQUIRE(convert_bytes({0x41, 0xE6, 0x97, 0xA5, 0xF0, 0x9F, 0x98, 0x80}, 65001) ==
            utf8({0x41, 0xE6, 0x97, 0xA5, 0xF0, 0x9F, 0x98, 0x80}));
}

TEST_CASE("TEXT_ENCODING decodes UTF-16 and UTF-32 in both byte orders", "[wolfrpg][text]")
{
    // U+0041 then U+4E2D.
    REQUIRE(convert_bytes({0x41, 0x00, 0x2D, 0x4E}, 1200) == utf8({0x41, 0xE4, 0xB8, 0xAD}));
    REQUIRE(convert_bytes({0x00, 0x41, 0x4E, 0x2D}, 1201) == utf8({0x41, 0xE4, 0xB8, 0xAD}));
    // U+1F600 as the UTF-16 surrogate pair 0xD83D 0xDE00.
    REQUIRE(convert_bytes({0x3D, 0xD8, 0x00, 0xDE}, 1200) == utf8({0xF0, 0x9F, 0x98, 0x80}));
    REQUIRE(convert_bytes({0xD8, 0x3D, 0xDE, 0x00}, 1201) == utf8({0xF0, 0x9F, 0x98, 0x80}));
    // U+4E2D and U+1F600 as UTF-32 scalars.
    REQUIRE(convert_bytes({0x2D, 0x4E, 0x00, 0x00}, 32766) == utf8({0xE4, 0xB8, 0xAD}));
    REQUIRE(convert_bytes({0x00, 0x00, 0x4E, 0x2D}, 32767) == utf8({0xE4, 0xB8, 0xAD}));
    REQUIRE(convert_bytes({0x00, 0xF6, 0x01, 0x00}, 32766) == utf8({0xF0, 0x9F, 0x98, 0x80}));
    REQUIRE(convert_bytes({0x00, 0x01, 0xF6, 0x00}, 32767) == utf8({0xF0, 0x9F, 0x98, 0x80}));
}

TEST_CASE("TEXT_ENCODING rejects malformed UTF-8 without dropping or replacing", "[wolfrpg][text]")
{
    // Control: the same shape decodes when well-formed.
    REQUIRE(convert_bytes({0xE6, 0x97, 0xA5}, 65001) == utf8({0xE6, 0x97, 0xA5}));
    REQUIRE_FALSE(failure_of(encoded({0xE6, 0x97, 0xA5}), 65001).has_value());

    // A continuation byte or 0xFE / 0xFF is never a lead byte.
    REQUIRE(failure_of(encoded({0x80}), 65001) == encoding_error_code::invalid_sequence);
    REQUIRE(failure_of(encoded({0xBF}), 65001) == encoding_error_code::invalid_sequence);
    REQUIRE(failure_of(encoded({0xFE}), 65001) == encoding_error_code::invalid_sequence);
    REQUIRE(failure_of(encoded({0xFF}), 65001) == encoding_error_code::invalid_sequence);

    // A bad continuation byte.
    REQUIRE(failure_of(encoded({0xE3, 0x28, 0xA1}), 65001) == encoding_error_code::invalid_sequence);
    REQUIRE(failure_of(encoded({0xF0, 0x9F, 0x28, 0x80}), 65001) == encoding_error_code::invalid_sequence);

    // A truncated lead byte.
    REQUIRE(failure_of(encoded({0xE3, 0x81}), 65001) == encoding_error_code::truncated_sequence);
    REQUIRE(failure_of(encoded({0xF0, 0x9F, 0x98}), 65001) == encoding_error_code::truncated_sequence);
    REQUIRE(failure_of(encoded({0xC3}), 65001) == encoding_error_code::truncated_sequence);

    // Non-shortest encodings.
    REQUIRE(failure_of(encoded({0xC0, 0x80}), 65001) == encoding_error_code::overlong_sequence);
    REQUIRE(failure_of(encoded({0xC1, 0xBF}), 65001) == encoding_error_code::overlong_sequence);
    REQUIRE(failure_of(encoded({0xE0, 0x80, 0x80}), 65001) == encoding_error_code::overlong_sequence);
    REQUIRE(failure_of(encoded({0xF0, 0x80, 0x80, 0x80}), 65001) == encoding_error_code::overlong_sequence);

    // A surrogate scalar and a scalar above U+10FFFF.
    REQUIRE(failure_of(encoded({0xED, 0xA0, 0x80}), 65001) == encoding_error_code::surrogate_code_point);
    REQUIRE(failure_of(encoded({0xF4, 0x90, 0x80, 0x80}), 65001) == encoding_error_code::invalid_sequence);

    // The retired five- and six-byte forms always encode values above U+10FFFF.
    REQUIRE(failure_of(encoded({0xF8, 0x88, 0x80, 0x80, 0x80}), 65001) == encoding_error_code::invalid_sequence);
    REQUIRE(failure_of(encoded({0xFC, 0x84, 0x80, 0x80, 0x80, 0x80}), 65001) == encoding_error_code::invalid_sequence);
}

TEST_CASE("TEXT_ENCODING rejects malformed UTF-16 and UTF-32", "[wolfrpg][text]")
{
    // Control: valid UTF-16 and UTF-32 still decode.
    REQUIRE(convert_bytes({0x41, 0x00}, 1200) == std::string("A"));
    // U+E000 sits above the surrogate range and must decode as an ordinary BMP scalar.
    REQUIRE(convert_bytes({0x00, 0xE0}, 1200) == utf8({0xEE, 0x80, 0x80}));
    REQUIRE(convert_bytes({0xE0, 0x00}, 1201) == utf8({0xEE, 0x80, 0x80}));
    REQUIRE(convert_bytes({0x2D, 0x4E, 0x00, 0x00}, 32766) == utf8({0xE4, 0xB8, 0xAD}));

    // Truncated units.
    REQUIRE(failure_of(encoded({0x41}), 1200) == encoding_error_code::truncated_sequence);
    REQUIRE(failure_of(encoded({0x00, 0xD8}), 1200) == encoding_error_code::truncated_sequence);
    REQUIRE(failure_of(encoded({0x2D, 0x4E, 0x00}), 32766) == encoding_error_code::truncated_sequence);

    // An unpaired high surrogate and a lone low surrogate.
    REQUIRE(failure_of(encoded({0x00, 0xD8, 0x41, 0x00}), 1200) == encoding_error_code::invalid_sequence);
    REQUIRE(failure_of(encoded({0xD8, 0x00, 0x00, 0x41}), 1201) == encoding_error_code::invalid_sequence);
    // A high surrogate followed by a unit above the low-surrogate range is also unpaired.
    REQUIRE(failure_of(encoded({0x00, 0xD8, 0x00, 0xE0}), 1200) == encoding_error_code::invalid_sequence);
    REQUIRE(failure_of(encoded({0xD8, 0x00, 0xE0, 0x00}), 1201) == encoding_error_code::invalid_sequence);
    REQUIRE(failure_of(encoded({0x00, 0xDC}), 1200) == encoding_error_code::surrogate_code_point);
    REQUIRE(failure_of(encoded({0xDC, 0x00}), 1201) == encoding_error_code::surrogate_code_point);

    // Surrogate and out-of-range UTF-32 scalars.
    REQUIRE(failure_of(encoded({0x00, 0xD8, 0x00, 0x00}), 32766) == encoding_error_code::surrogate_code_point);
    REQUIRE(failure_of(encoded({0x00, 0x00, 0xD8, 0x00}), 32767) == encoding_error_code::surrogate_code_point);
    REQUIRE(failure_of(encoded({0x00, 0x00, 0x11, 0x00}), 32766) == encoding_error_code::invalid_sequence);
    REQUIRE(failure_of(encoded({0x00, 0x11, 0x00, 0x00}), 32767) == encoding_error_code::invalid_sequence);
}

TEST_CASE("TEXT_ENCODING rejects embedded NUL, unsupported code pages and oversize components", "[wolfrpg][text]")
{
    REQUIRE(failure_of(encoded({0x00}), 932) == encoding_error_code::embedded_nul);
    REQUIRE(failure_of(encoded({0x61, 0x00, 0x62}), 932) == encoding_error_code::embedded_nul);
    REQUIRE(failure_of(encoded({0x61, 0x00}), 65001) == encoding_error_code::embedded_nul);
    REQUIRE(failure_of(encoded({0x00, 0x00}), 1200) == encoding_error_code::embedded_nul);
    REQUIRE(failure_of(encoded({0x00, 0x00, 0x00, 0x00}), 32766) == encoding_error_code::embedded_nul);

    REQUIRE(failure_of(encoded({0x41}), 934) == encoding_error_code::unsupported_codepage);
    REQUIRE(failure_of(encoded({0x41}), 0) == encoding_error_code::unsupported_codepage);
    REQUIRE(failure_of(encoded({0x41}), 99999) == encoding_error_code::unsupported_codepage);
    REQUIRE(failure_of(std::vector<std::byte>{}, 99999) == encoding_error_code::unsupported_codepage);

    // The component cap is inclusive at 4092 bytes.
    REQUIRE(convert(encoded_repeated(0x41, 4092), 65001).size() == 4092);
    REQUIRE(failure_of(encoded_repeated(0x41, 4093), 65001) == encoding_error_code::input_too_large);

    // Expansion of a full two-byte component stays inside the four-times bound.
    const std::vector<std::byte> korean = [&] {
        std::vector<std::byte> data;
        data.reserve(4092);
        for (int index = 0; index < 2046; ++index) {
            data.push_back(static_cast<std::byte>(0xB0));
            data.push_back(static_cast<std::byte>(0xA1));
        }
        return data;
    }();
    REQUIRE(convert(korean, 949).size() == 3 * 2046);
}

TEST_CASE("TEXT_ENCODING reports unmappable characters and rejects ASCII high bytes", "[wolfrpg][text]")
{
    // 0xA0 and the truncated pair 0x81 0x20 have no Shift-JIS mapping.
    REQUIRE(failure_of(encoded({0xA0}), 932) == encoding_error_code::unmappable_character);
    REQUIRE(failure_of(encoded({0x81, 0x20}), 932) == encoding_error_code::unmappable_character);
    REQUIRE(failure_of(encoded({0x81, 0x20}), 936) == encoding_error_code::unmappable_character);
    REQUIRE(failure_of(encoded({0x81, 0x20}), 949) == encoding_error_code::unmappable_character);
    REQUIRE(failure_of(encoded({0x81, 0x20}), 950) == encoding_error_code::unmappable_character);

    // A two-byte lead without its trail is truncated, not unmappable.
    REQUIRE(failure_of(encoded({0x81}), 932) == encoding_error_code::truncated_sequence);

    // ASCII permits only 0x00..0x7F.
    REQUIRE(convert_bytes({0x7F}, 32765) == utf8({0x7F}));
    REQUIRE(failure_of(encoded({0x80}), 32765) == encoding_error_code::invalid_sequence);
    REQUIRE(failure_of(encoded({0xFF}), 32765) == encoding_error_code::invalid_sequence);
}

TEST_CASE("TEXT_ENCODING classifies code page units and scans for a logical terminator", "[wolfrpg][text]")
{
    using unpackrat::wolfrpg::text::encoding_unit_size;
    using unpackrat::wolfrpg::text::find_terminator;
    using unpackrat::wolfrpg::text::is_supported_codepage;

    REQUIRE(is_supported_codepage(932));
    REQUIRE(is_supported_codepage(936));
    REQUIRE(is_supported_codepage(949));
    REQUIRE(is_supported_codepage(950));
    REQUIRE(is_supported_codepage(1252));
    REQUIRE(is_supported_codepage(32764));
    REQUIRE(is_supported_codepage(32765));
    REQUIRE(is_supported_codepage(65001));
    REQUIRE(is_supported_codepage(1200));
    REQUIRE(is_supported_codepage(1201));
    REQUIRE(is_supported_codepage(32766));
    REQUIRE(is_supported_codepage(32767));
    REQUIRE_FALSE(is_supported_codepage(934));
    REQUIRE_FALSE(is_supported_codepage(0));

    REQUIRE(encoding_unit_size(932) == 1);
    REQUIRE(encoding_unit_size(936) == 1);
    REQUIRE(encoding_unit_size(949) == 1);
    REQUIRE(encoding_unit_size(950) == 1);
    REQUIRE(encoding_unit_size(1252) == 1);
    REQUIRE(encoding_unit_size(32764) == 1);
    REQUIRE(encoding_unit_size(32765) == 1);
    REQUIRE(encoding_unit_size(65001) == 1);
    REQUIRE(encoding_unit_size(1200) == 2);
    REQUIRE(encoding_unit_size(1201) == 2);
    REQUIRE(encoding_unit_size(32766) == 4);
    REQUIRE(encoding_unit_size(32767) == 4);
    REQUIRE(encoding_unit_size(934) == 0);

    // A single zero byte terminates a one-unit code page.
    REQUIRE(find_terminator(encoded({0x61, 0x00, 0x62}), 65001) == 1u);
    REQUIRE(find_terminator(encoded({0x95, 0x5C, 0x00}), 932) == 2u);
    REQUIRE_FALSE(find_terminator(encoded({0x61, 0x62}), 65001).has_value());

    // UTF-16 and UTF-32 terminators are unit-aligned; a lone zero byte inside a unit is not one.
    // "AB" (0x0041 0x0042) carries no terminator at all.
    REQUIRE_FALSE(find_terminator(encoded({0x41, 0x00, 0x42, 0x00}), 1200).has_value());
    REQUIRE(find_terminator(encoded({0x41, 0x00, 0x00, 0x00}), 1200) == 2u);
    REQUIRE_FALSE(find_terminator(encoded({0x41, 0x00}), 1200).has_value());
    // A trailing partial unit is ignored rather than scanned.
    REQUIRE_FALSE(find_terminator(encoded({0x41, 0x00, 0x00}), 1200).has_value());
    REQUIRE(find_terminator(encoded({0x2D, 0x4E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00}), 32766) == 4u);

    REQUIRE(failure_of(encoded({0x00}), 934) == encoding_error_code::unsupported_codepage);
    try {
        (void)find_terminator(encoded({0x00}), 934);
        FAIL("find_terminator accepted an unsupported code page");
    } catch (const encoding_error &error) {
        REQUIRE(error.code() == encoding_error_code::unsupported_codepage);
    }
}

// Hidden probe: it runs only when selected by the parent test below, so it observes the very first
// use of the DXLib character tables inside a fresh process. Every worker waits on the barrier and
// then decodes concurrently; a defect in the one-time table initialisation would surface as a wrong
// result or a crash rather than a clean exit.
TEST_CASE("TEXT_ENCODING decodes concurrently from a cold start", "[.wolfrpg_text_encoding_child]")
{
    constexpr int thread_count = 8;
    std::barrier gate(thread_count);
    std::vector<int> failures(static_cast<std::size_t>(thread_count), 0);
    std::vector<std::thread> workers;
    workers.reserve(static_cast<std::size_t>(thread_count));

    for (int index = 0; index < thread_count; ++index) {
        workers.emplace_back([&gate, &failures, index]() {
            gate.arrive_and_wait();
            int bad = 0;
            try {
                if (convert_bytes({0x95, 0x5C}, 932) != utf8({0xE8, 0xA1, 0xA8})) {
                    ++bad;
                }
                if (convert_bytes({0xD6, 0xD0}, 936) != utf8({0xE4, 0xB8, 0xAD})) {
                    ++bad;
                }
                if (convert_bytes({0xB0, 0xA1}, 949) != utf8({0xEA, 0xB0, 0x80})) {
                    ++bad;
                }
                if (convert_bytes({0xA4, 0xA4}, 950) != utf8({0xE4, 0xB8, 0xAD})) {
                    ++bad;
                }
                if (convert_bytes({0xE9}, 1252) != utf8({0xC3, 0xA9})) {
                    ++bad;
                }
                if (convert_bytes({0xA4}, 32764) != utf8({0xE2, 0x82, 0xAC})) {
                    ++bad;
                }
                if (convert_bytes({0xF0, 0x9F, 0x98, 0x80}, 65001) != utf8({0xF0, 0x9F, 0x98, 0x80})) {
                    ++bad;
                }
                if (convert_bytes({0x3D, 0xD8, 0x00, 0xDE}, 1200) != utf8({0xF0, 0x9F, 0x98, 0x80})) {
                    ++bad;
                }
            } catch (const encoding_error &) {
                ++bad;
            }
            failures[static_cast<std::size_t>(index)] = bad;
        });
    }

    for (std::thread &worker : workers) {
        worker.join();
    }
    for (const int bad : failures) {
        REQUIRE(bad == 0);
    }
}

TEST_CASE("TEXT_ENCODING cold-start concurrency is exercised in a fresh process", "[wolfrpg][text][concurrency]")
{
    const std::wstring executable = current_executable_path();
    REQUIRE_FALSE(executable.empty());

    // Do not carry the parent's CRT descriptor table into this fresh process. Another ordinary
    // test may have exhausted and released CRT descriptors before this case runs.
    std::wstring command = L"\"" + executable + L"\" \"[.wolfrpg_text_encoding_child]\"";
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION child{};
    const BOOL created = ::CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr,
                                          nullptr, &startup, &child);
    const DWORD launch_error = ::GetLastError();
    const auto close_handle = [](void *handle) noexcept { static_cast<void>(::CloseHandle(handle)); };
    const std::unique_ptr<void, decltype(close_handle)> process(child.hProcess, close_handle);
    const std::unique_ptr<void, decltype(close_handle)> thread(child.hThread, close_handle);
    INFO("CreateProcessW result: " << created << ", last error: " << launch_error);
    REQUIRE(created != FALSE);
    const DWORD waited = ::WaitForSingleObject(process.get(), INFINITE);
    const DWORD wait_error = ::GetLastError();
    INFO("WaitForSingleObject result: " << waited << ", last error: " << wait_error);
    REQUIRE(waited == WAIT_OBJECT_0);
    DWORD exit_code = 1;
    const BOOL queried = ::GetExitCodeProcess(process.get(), &exit_code);
    const DWORD query_error = ::GetLastError();
    INFO("GetExitCodeProcess result: " << queried << ", last error: " << query_error);
    REQUIRE(queried != FALSE);
    INFO("child process exit status: " << exit_code);
    REQUIRE(exit_code == 0);
}
