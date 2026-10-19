// Focused native tests for the shared UberWolfLib mode-selection policy added by
// build/vcpkg/ports/uberwolf/mode-selection.patch.
//
// The patch factors the policy that used to be written inline in WolfDec::detectCrypt /
// detectMode / getCryptVersion / UnpackArchive into four narrow library helpers:
//
//   * WolfDec::CryptVersionFromHeader -- bounded DARC_HEAD interpretation (Flags >> 16);
//   * WolfDec::SelectMode             -- the ordered default-then-additional crypt-version lookup;
//   * WolfDec::ModeCount / ModeAt     -- the combined default-then-additional ordering;
//   * WolfDec::ClassifyCryptVersion   -- the automatic UnpackArchive classification.
//
// These tests drive the helpers directly and also through the original public filesystem path
// (WolfDec::UnpackArchive in its own temporary directory) so the delegations are exercised, not
// just the extracted bodies. Every fixture is generated here: a packed 64-byte DARC_HEAD with the
// "DX" magic and a crypt version in the Flags high word. The deliberate non-existent program name
// makes runProcess's CreateProcess fail, so the trial loop and the explicit-mode path both return
// their failure verdict without ever spawning the archive decoder.

#include "../support/archive_lazy_fixtures.h"

#include <cerrno>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <ios>
#include <stdexcept>
#include <string>
#include <system_error>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include <uberwolf/WolfDec.h>

namespace
{
    // DARC_HEAD is a packed 64-byte record (3rdParty/DXLib/DXArchive.h): u16 Head, u16 Version,
    // u32 HeadSize, four u64 addresses, u32 CharCodeFormat, u32 Flags, u8 HuffmanEncodeKB and a
    // 15-byte reserve. DXA_HEAD is the little-endian u16 "DX" and the crypt version is Flags >> 16.
    constexpr std::size_t kDarcHeadBytes = 64;
    constexpr std::size_t kFlagsOffset = 44;
    constexpr std::uint16_t kDxMagic = 0x5844; // *((u16 *)"DX")

    [[nodiscard]] std::vector<std::uint8_t> header_bytes(const std::uint16_t magic, const std::uint16_t crypt_version)
    {
        std::vector<std::uint8_t> bytes(kDarcHeadBytes, 0x00);
        bytes[0] = static_cast<std::uint8_t>(magic & 0xFF);
        bytes[1] = static_cast<std::uint8_t>(magic >> 8);
        bytes[kFlagsOffset + 2] = static_cast<std::uint8_t>(crypt_version & 0xFF);
        bytes[kFlagsOffset + 3] = static_cast<std::uint8_t>(crypt_version >> 8);
        return bytes;
    }

    [[nodiscard]] CryptMode additional_mode(const std::string &name, const std::uint16_t crypt_version)
    {
        return CryptMode(name, crypt_version, nullptr, nullptr, std::vector<unsigned char>{0x11, 0x22, 0x00});
    }

    // An empty program name makes runProcess's CreateProcess fail, so every path that would launch
    // the archive decoder instead returns its failure verdict without spawning a subprocess.
    [[nodiscard]] WolfDec detector()
    {
        return WolfDec(L"unpackrat-mode-selection-no-such-program");
    }

    void require_fixture_written(const std::filesystem::path &path, const std::ios &output)
    {
        if (!output) {
            throw std::runtime_error("mode-selection fixture could not be written: " + path.string());
        }
    }

    [[nodiscard]] std::filesystem::path write_fixture(const std::filesystem::path &directory, const std::string &name,
                                                      const std::vector<std::uint8_t> &bytes)
    {
        const std::filesystem::path path = directory / name;
        std::ofstream output(path, std::ios::binary | std::ios::trunc);
        if (!output.is_open()) {
            throw std::runtime_error("mode-selection fixture could not be created: " + path.string());
        }
        output.write(reinterpret_cast<const char *>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
        output.close();
        require_fixture_written(path, output);
        return path;
    }

    // The harness builds a tiny owned helper executable whose exit status is a controlled
    // function of its actual "-m <mode>" argument: it succeeds only for the single working mode,
    // so a trial loop walks the earlier candidates, fails them, and stops at that mode. The test
    // locates it from the environment the harness sets, falling back to the working directory.
    using environment_reader = errno_t(std::size_t *, wchar_t *, std::size_t, const wchar_t *);
    errno_t secure_environment(std::size_t *required, wchar_t *value, std::size_t capacity, const wchar_t *name)
    {
        return _wgetenv_s(required, value, capacity, name);
    }
    std::wstring trial_helper_fallback()
    {
        return (std::filesystem::current_path() / L"mode-selection-trial-helper.exe").wstring();
    }
    std::wstring read_trial_helper(environment_reader &reader)
    {
        std::size_t required = 0;
        const auto query = reader(&required, nullptr, 0, L"UNPACKRAT_WOLF_MODE_HELPER");
        if (query != 0)
            throw std::system_error(query, std::generic_category(), "Cannot query owned mode helper environment");
        if (required <= 1)
            return trial_helper_fallback();
        std::wstring value(required, L'\0');
        std::size_t actual = 0;
        const auto status = reader(&actual, value.data(), value.size(), L"UNPACKRAT_WOLF_MODE_HELPER");
        if (status != 0)
            throw std::system_error(status, std::generic_category(), "Cannot read owned mode helper environment");
        value.resize(actual == 0 ? 0 : actual - 1);
        return value.empty() ? trial_helper_fallback() : value;
    }
    [[nodiscard]] const std::wstring &trial_helper()
    {
        static const std::wstring helper = read_trial_helper(secure_environment);
        return helper;
    }
} // namespace

TEST_CASE("CryptVersionFromHeader reads the crypt version from a complete DARC header",
          "[wolfrpg][uberwolf][mode-selection]")
{
    const std::vector<std::uint8_t> v350 = header_bytes(kDxMagic, 350);
    CHECK(WolfDec::CryptVersionFromHeader(v350.data(), v350.size()) == 350);

    // The whole 16-bit high word is reported, not just a low byte.
    const std::vector<std::uint8_t> cc2 = header_bytes(kDxMagic, 0xC8);
    CHECK(WolfDec::CryptVersionFromHeader(cc2.data(), cc2.size()) == 0xC8);

    // A real header version past the known Pro recovery values is returned unchanged.
    const std::vector<std::uint8_t> future = header_bytes(kDxMagic, 1020);
    CHECK(WolfDec::CryptVersionFromHeader(future.data(), future.size()) == 1020);
}

TEST_CASE("CryptVersionFromHeader rejects a header that is not the DX magic", "[wolfrpg][uberwolf][mode-selection]")
{
    const std::vector<std::uint8_t> not_dx = header_bytes(0x5950 /* "PY" */, 350);
    CHECK(WolfDec::CryptVersionFromHeader(not_dx.data(), not_dx.size()) == 0);
}

TEST_CASE("CryptVersionFromHeader bounds every header truncation", "[wolfrpg][uberwolf][mode-selection]")
{
    const std::vector<std::uint8_t> full = header_bytes(kDxMagic, 350);

    // No buffer, an empty buffer, the DX magic without any flags word, the magic up to the flags
    // word and a one-byte-short header all yield 0 rather than reading past the buffer.
    CHECK(WolfDec::CryptVersionFromHeader(nullptr, kDarcHeadBytes) == 0);
    CHECK(WolfDec::CryptVersionFromHeader(full.data(), 0) == 0);
    CHECK(WolfDec::CryptVersionFromHeader(full.data(), 2) == 0);
    CHECK(WolfDec::CryptVersionFromHeader(full.data(), kFlagsOffset) == 0);
    CHECK(WolfDec::CryptVersionFromHeader(full.data(), kDarcHeadBytes - 1) == 0);
    CHECK(WolfDec::CryptVersionFromHeader(full.data(), kDarcHeadBytes) == 350);
}

TEST_CASE("SelectMode resolves the real builtin order by exact crypt version", "[wolfrpg][uberwolf][mode-selection]")
{
    const CryptModes none;
    const CryptModes &defaults = WolfDec::DefaultCryptModes();
    std::uint32_t index = 0;

    // Index 4..8 of the pinned table carry crypt versions 300, 314, 331, 350 and 100.
    CHECK(WolfDec::SelectMode(none, 300, index));
    CHECK(index == 4);
    CHECK(defaults[index].cryptVersion == 300);

    CHECK(WolfDec::SelectMode(none, 314, index));
    CHECK(index == 5);
    CHECK(defaults[index].cryptVersion == 314);

    CHECK(WolfDec::SelectMode(none, 331, index));
    CHECK(index == 6);

    CHECK(WolfDec::SelectMode(none, 350, index));
    CHECK(index == 7);
    CHECK(defaults[index].name == "Wolf RPG v3.50");

    CHECK(WolfDec::SelectMode(none, 100, index));
    CHECK(index == 8);
    CHECK(defaults[index].name == "Wolf RPG ChaCha2 v1");
}

TEST_CASE("SelectMode prefers the default table over an equal additional version",
          "[wolfrpg][uberwolf][mode-selection]")
{
    CryptModes additional;
    additional.push_back(additional_mode("override", 350));

    std::uint32_t index = 0;
    CHECK(WolfDec::SelectMode(additional, 350, index));
    CHECK(index == 7); // the pinned v3.50 entry, not the caller's additional override
}

TEST_CASE("SelectMode falls back to the additional modes in order", "[wolfrpg][uberwolf][mode-selection]")
{
    CryptModes additional;
    additional.push_back(additional_mode("first", 999));
    additional.push_back(additional_mode("second", 998));

    const std::uint32_t base = static_cast<std::uint32_t>(WolfDec::DefaultCryptModes().size());

    std::uint32_t index = 0;
    CHECK(WolfDec::SelectMode(additional, 999, index));
    CHECK(index == base);

    // The first additional entry does not match 998, so the second one has to be reached.
    CHECK(WolfDec::SelectMode(additional, 998, index));
    CHECK(index == base + 1);
}

TEST_CASE("SelectMode rejects zero and unknown versions without touching the index",
          "[wolfrpg][uberwolf][mode-selection]")
{
    const CryptModes none;
    const std::uint32_t sentinel = 0xDEADBEEFu;
    std::uint32_t index = sentinel;

    // The pinned table carries several 0-version entries, but a zero version is the trial policy.
    CHECK_FALSE(WolfDec::SelectMode(none, 0, index));
    CHECK(index == sentinel);

    CHECK_FALSE(WolfDec::SelectMode(none, 0xFFFF, index));
    CHECK(index == sentinel);
}

TEST_CASE("ModeCount and ModeAt keep the default-then-additional ordering", "[wolfrpg][uberwolf][mode-selection]")
{
    const CryptModes none;
    const CryptModes &defaults = WolfDec::DefaultCryptModes();
    CHECK(WolfDec::ModeCount(none) == defaults.size());

    CryptModes additional;
    additional.push_back(additional_mode("first", 999));
    additional.push_back(additional_mode("second", 998));
    CHECK(WolfDec::ModeCount(additional) == defaults.size() + 2);

    const std::uint32_t last_default = static_cast<std::uint32_t>(defaults.size() - 1);
    CHECK(WolfDec::ModeAt(additional, 0).name == defaults.front().name);
    CHECK(WolfDec::ModeAt(additional, last_default).name == defaults.back().name);
    CHECK(WolfDec::ModeAt(additional, last_default + 1).name == "first");
    CHECK(WolfDec::ModeAt(additional, last_default + 2).name == "second");
}

TEST_CASE("ClassifyCryptVersion mirrors the automatic UnpackArchive decision", "[wolfrpg][uberwolf][mode-selection]")
{
    using Class = CryptVersionClass;

    const CryptVersionDecision trial = WolfDec::ClassifyCryptVersion(0);
    CHECK(trial.classification == Class::Trial);
    CHECK(trial.cryptVersion == 0);

    CHECK(WolfDec::ClassifyCryptVersion(1).classification == Class::ExactMatch);
    CHECK(WolfDec::ClassifyCryptVersion(300).classification == Class::ExactMatch);
    CHECK(WolfDec::ClassifyCryptVersion(0xC8).classification == Class::Cc2Recovery);
    CHECK(WolfDec::ClassifyCryptVersion(1000).classification == Class::ProRecovery);
    CHECK(WolfDec::ClassifyCryptVersion(1010).classification == Class::ProRecovery);
    CHECK(WolfDec::ClassifyCryptVersion(0xFFFF).classification == Class::ProRecovery);

    // A real header version past the known Pro recovery values stays the version the later crypto
    // must use; the classification never rewrites it to the 1010 companion version.
    const CryptVersionDecision future = WolfDec::ClassifyCryptVersion(1020);
    CHECK(future.classification == Class::ProRecovery);
    CHECK(future.cryptVersion == 1020);
}

TEST_CASE("the factored policy leaves the pinned default table intact", "[wolfrpg][uberwolf][mode-selection]")
{
    const CryptModes &modes = WolfDec::DefaultCryptModes();

    REQUIRE(modes.size() == 11);
    CHECK(modes.front().name == "Wolf RPG v2.01");
    CHECK(modes[8].name == "Wolf RPG ChaCha2 v1");
    CHECK(modes.back().name == "One Way Heroics Plus");

    // The binary keys keep their trailing material: the first legacy key ends in 0xE1, not a NUL.
    REQUIRE_FALSE(modes.front().key.empty());
    CHECK(static_cast<unsigned char>(modes.front().key.back()) == 0xE1);
    REQUIRE_FALSE(modes[4].key.empty());
    CHECK(modes[4].key.back() == 0x00);
}

TEST_CASE("UnpackArchive delegates header version detection to the shared selection",
          "[wolfrpg][uberwolf][mode-selection]")
{
    test::support::temporary_directory directory("uberwolf-mode-selection");

    SECTION("a known header version selects its exact mode")
    {
        const std::filesystem::path path =
            write_fixture(directory.path(), "auto-v350.wolf", header_bytes(kDxMagic, 350));
        WolfDec dec = detector();
        CHECK_FALSE(dec.UnpackArchive(path.wstring()));
        CHECK(dec.IsModeSet());
    }

    SECTION("an unknown nonzero header version selects nothing")
    {
        const std::filesystem::path path = write_fixture(directory.path(), "unknown.wolf", header_bytes(kDxMagic, 299));
        WolfDec dec = detector();
        CHECK_FALSE(dec.UnpackArchive(path.wstring()));
        CHECK_FALSE(dec.IsModeSet());
    }

    SECTION("an additional-mode crypt version is resolved through the same lookup")
    {
        // 500 is below the Pro/CC2 recovery range but absent from the pinned table, so the exact
        // lookup has to fall through to the caller's additional mode.
        const std::filesystem::path path =
            write_fixture(directory.path(), "additional.wolf", header_bytes(kDxMagic, 500));
        WolfDec dec = detector();
        dec.AddKey("owned", 500, false, Key{0x01, 0x02, 0x00});
        CHECK_FALSE(dec.UnpackArchive(path.wstring()));
        CHECK(dec.IsModeSet());
    }
}

TEST_CASE("UnpackArchive runs the ordered trial policy when the header carries no version",
          "[wolfrpg][uberwolf][mode-selection]")
{
    test::support::temporary_directory directory("uberwolf-mode-selection");

    SECTION("a zero version tries every candidate and reports failure")
    {
        const std::filesystem::path path = write_fixture(directory.path(), "auto-v0.wolf", header_bytes(kDxMagic, 0));
        WolfDec dec = detector();
        CHECK_FALSE(dec.UnpackArchive(path.wstring()));
        CHECK_FALSE(dec.IsModeSet());
    }

    SECTION("a truncated header falls back to the trial policy")
    {
        std::vector<std::uint8_t> truncated = header_bytes(kDxMagic, 350);
        truncated.resize(2); // the DX magic only, so the bounded read reports no version
        const std::filesystem::path path = write_fixture(directory.path(), "truncated.wolf", truncated);
        WolfDec dec = detector();
        CHECK_FALSE(dec.UnpackArchive(path.wstring()));
        CHECK_FALSE(dec.IsModeSet());
    }

    SECTION("a header with the wrong magic falls back to the trial policy")
    {
        const std::filesystem::path path = write_fixture(directory.path(), "badmagic.wolf", header_bytes(0x5950, 350));
        WolfDec dec = detector();
        CHECK_FALSE(dec.UnpackArchive(path.wstring()));
        CHECK_FALSE(dec.IsModeSet());
    }
}

TEST_CASE("UnpackArchive defers Pro and ChaCha2 headers to companion recovery", "[wolfrpg][uberwolf][mode-selection]")
{
    test::support::temporary_directory directory("uberwolf-mode-selection");

    SECTION("a Pro header version is not selected as a builtin mode")
    {
        const std::filesystem::path path = write_fixture(directory.path(), "pro.wolf", header_bytes(kDxMagic, 1000));
        WolfDec dec = detector();
        CHECK_FALSE(dec.UnpackArchive(path.wstring()));
        CHECK_FALSE(dec.IsModeSet());
    }

    SECTION("a ChaCha2 Pro header version is not selected as a builtin mode")
    {
        const std::filesystem::path path = write_fixture(directory.path(), "cc2.wolf", header_bytes(kDxMagic, 0xC8));
        WolfDec dec = detector();
        CHECK_FALSE(dec.UnpackArchive(path.wstring()));
        CHECK_FALSE(dec.IsModeSet());
    }
}

TEST_CASE("UnpackArchive reproduces the explicit-mode and out-of-range paths", "[wolfrpg][uberwolf][mode-selection]")
{
    test::support::temporary_directory directory("uberwolf-mode-selection");
    const std::filesystem::path path = write_fixture(directory.path(), "explicit.wolf", header_bytes(kDxMagic, 350));

    SECTION("an explicit in-range mode skips detection and fails at the decoder process")
    {
        WolfDec dec = detector();
        dec.SetMode(7);
        CHECK_FALSE(dec.UnpackArchive(path.wstring()));
        CHECK(dec.IsModeSet());
    }

    SECTION("an explicit out-of-range mode is rejected before the decoder process")
    {
        WolfDec dec = detector();
        dec.SetMode(9999);
        CHECK_FALSE(dec.UnpackArchive(path.wstring()));
        CHECK(dec.IsModeSet());
    }
}

namespace
{
    struct callback_exception final : std::runtime_error
    {
        using std::runtime_error::runtime_error;
    };

    // The -1 "not yet detected" sentinel the automatic callers hold while they trial modes.
    constexpr std::uint32_t kAutoMode = 0xFFFFFFFFu;
} // namespace

TEST_CASE("SelectWorkingMode walks the combined order and stops at the first success",
          "[wolfrpg][uberwolf][mode-selection]")
{
    CryptModes additional;
    additional.push_back(additional_mode("first", 999));
    additional.push_back(additional_mode("second", 998));

    std::vector<std::uint32_t> attempts;
    std::uint32_t selected = kAutoMode;
    const bool found =
        WolfDec::SelectWorkingMode(additional, kAutoMode, selected, [&attempts](const std::uint32_t mode) {
            attempts.push_back(mode);
            return mode == 4;
        });

    CHECK(found);
    CHECK(selected == 4);
    // The loop tried every earlier candidate and stopped the instant mode 4 succeeded.
    REQUIRE(attempts.size() == 5);
    CHECK(attempts[0] == 0);
    CHECK(attempts[1] == 1);
    CHECK(attempts[2] == 2);
    CHECK(attempts[3] == 3);
    CHECK(attempts[4] == 4);
    // The additional entries sit after every pinned default mode.
    CHECK(WolfDec::ModeCount(additional) == WolfDec::DefaultCryptModes().size() + 2);
}

TEST_CASE("SelectWorkingMode reports no candidate and keeps the automatic state on failure",
          "[wolfrpg][uberwolf][mode-selection]")
{
    const CryptModes none;
    const std::uint32_t sentinel = 0xDEADBEEFu;
    std::uint32_t selected = sentinel;

    const bool found = WolfDec::SelectWorkingMode(none, kAutoMode, selected, [](const std::uint32_t) { return false; });

    CHECK_FALSE(found);
    CHECK(selected == sentinel);
}

TEST_CASE("SelectWorkingMode attempts exactly the explicit mode", "[wolfrpg][uberwolf][mode-selection]")
{
    const CryptModes none;
    const std::uint32_t last = static_cast<std::uint32_t>(WolfDec::ModeCount(none)) - 1;

    int calls = 0;
    std::uint32_t observed = 0;
    std::uint32_t selected = 0;
    const bool found = WolfDec::SelectWorkingMode(none, last, selected, [&calls, &observed](const std::uint32_t mode) {
        ++calls;
        observed = mode;
        return true;
    });

    CHECK(found);
    CHECK(calls == 1);
    CHECK(observed == last);
    CHECK(selected == last);
}

TEST_CASE("SelectWorkingMode keeps the explicit index when that mode fails", "[wolfrpg][uberwolf][mode-selection]")
{
    const CryptModes none;
    const std::uint32_t explicitMode = 3;
    int calls = 0;
    std::uint32_t selected = explicitMode;

    const bool found = WolfDec::SelectWorkingMode(none, explicitMode, selected, [&calls](const std::uint32_t) {
        ++calls;
        return false;
    });

    CHECK_FALSE(found);
    CHECK(calls == 1);
    CHECK(selected == explicitMode);
}

TEST_CASE("SelectWorkingMode forwards an out-of-range explicit mode unchanged", "[wolfrpg][uberwolf][mode-selection]")
{
    // The original detectMode attempted an explicit mode without a bound check; the driver keeps
    // that contract so a caller that already validated its mode sees it attempted exactly once.
    const CryptModes none;
    const std::uint32_t outOfRange = static_cast<std::uint32_t>(WolfDec::ModeCount(none)) + 990;
    std::uint32_t observed = 0;
    std::uint32_t selected = 0;

    const bool found = WolfDec::SelectWorkingMode(none, outOfRange, selected, [&observed](const std::uint32_t mode) {
        observed = mode;
        return true;
    });

    CHECK(found);
    CHECK(observed == outOfRange);
    CHECK(selected == outOfRange);
}

TEST_CASE("SelectWorkingMode lets a callback exception propagate unchanged", "[wolfrpg][uberwolf][mode-selection]")
{
    const CryptModes none;

    SECTION("an automatic trial")
    {
        std::uint32_t selected = 0x55;
        bool caught = false;
        try {
            WolfDec::SelectWorkingMode(none, kAutoMode, selected, [](const std::uint32_t) -> bool {
                throw callback_exception("automatic trial failed");
            });
        } catch (const callback_exception &error) {
            caught = std::string(error.what()) == "automatic trial failed";
        }
        CHECK(caught);
        CHECK(selected == 0x55);
    }

    SECTION("an explicit attempt")
    {
        std::uint32_t selected = 7;
        bool caught = false;
        try {
            WolfDec::SelectWorkingMode(none, 7, selected, [](const std::uint32_t) -> bool {
                throw callback_exception("explicit attempt failed");
            });
        } catch (const callback_exception &error) {
            caught = std::string(error.what()) == "explicit attempt failed";
        }
        CHECK(caught);
        CHECK(selected == 7);
    }
}

TEST_CASE("UnpackArchive records the first working trial mode for a versionless archive",
          "[wolfrpg][uberwolf][mode-selection]")
{
    test::support::temporary_directory directory("uberwolf-mode-selection");
    const std::filesystem::path path = write_fixture(directory.path(), "auto-working.wolf", header_bytes(kDxMagic, 0));

    // The owned helper exits 0 only for its single working mode, so the automatic trial loop has to
    // fail the earlier candidates and stop at that mode before the unpacker reports success.
    WolfDec dec(trial_helper());
    CHECK(dec.UnpackArchive(path.wstring()));
    CHECK(dec.IsModeSet());
}

TEST_CASE("getCryptVersion reads an unreadable header as version zero", "[wolfrpg][uberwolf][mode-selection]")
{
    const std::filesystem::path missing = test::support::unused_path("uberwolf-mode-selection-missing");
    WolfDec dec = detector();

    // A header that cannot be opened is read as version 0, so the automatic path falls back to the
    // (failing) trial policy instead of claiming a mode.
    CHECK_FALSE(dec.UnpackArchive(missing.wstring()));
    CHECK_FALSE(dec.IsModeSet());
}

#include <array>
#include <cerrno>
#include <optional>
#include <system_error>
namespace
{
    struct environment_plan
    {
        errno_t query_status = 0, value_status = 0;
        std::size_t queried_count = 0;
        std::wstring value;
        bool disappeared = false;
    };
    thread_local environment_plan planned_environment;
    errno_t owned_environment_reader(std::size_t *count, wchar_t *buffer, std::size_t, const wchar_t *)
    {
        if (buffer == nullptr) {
            *count = planned_environment.queried_count;
            return planned_environment.query_status;
        }
        if (planned_environment.value_status != 0)
            return planned_environment.value_status;
        if (planned_environment.disappeared) {
            *count = 0;
            return 0;
        }
        *count = planned_environment.value.size() + 1;
        std::copy(planned_environment.value.begin(), planned_environment.value.end(), buffer);
        buffer[planned_environment.value.size()] = L'\0';
        return 0;
    }
    std::optional<std::error_code> observed_environment_error()
    {
        try {
            (void)read_trial_helper(owned_environment_reader);
        } catch (const std::system_error &error) {
            return error.code();
        }
        return {};
    }
} // namespace
TEST_CASE("trial helper path preserves unset empty owned overrides and secure CRT errors", "[mode-helper-env]")
{
    const auto fallback = (std::filesystem::current_path() / L"mode-selection-trial-helper.exe").wstring();
    struct row
    {
        environment_plan environment;
        std::wstring expected;
        int error;
    };
    const std::array rows{row{{0, 0, 0, {}, false}, fallback, 0},
                          row{{0, 0, 1, {}, false}, fallback, 0},
                          row{{0, 0, 22, L"owned mode helper.exe", false}, L"owned mode helper.exe", 0},
                          row{{EIO, 0, 0, {}, false}, {}, EIO},
                          row{{0, ERANGE, 22, {}, false}, {}, ERANGE},
                          row{{0, 0, 22, {}, false}, fallback, 0},
                          row{{0, 0, 22, {}, true}, fallback, 0}};
    for (const auto &r : rows) {
        planned_environment = r.environment;
        if (r.error == 0) {
            CHECK(read_trial_helper(owned_environment_reader) == r.expected);
            CHECK_FALSE(observed_environment_error().has_value());
        } else {
            const auto error = observed_environment_error();
            REQUIRE(error.has_value());
            CHECK(error->value() == r.error);
            CHECK(error->category() == std::generic_category());
        }
    }
}
#include "../support/mode_selection_trial_helper.h"
TEST_CASE("owned mode trial helper validates its argument shape and succeeds only for mode four",
          "[mode-helper-protocol]")
{
    using namespace std::literals;
    struct row
    {
        std::array<std::wstring_view, 5> arguments;
        std::size_t count;
        int exit;
    };
    const std::array rows{row{{}, 0, 64},
                          row{{L"-m"sv}, 1, 64},
                          row{{L"-m"sv, L"4"sv}, 2, 64},
                          row{{L"-m"sv, L"4"sv, L"owned.wolf"sv, L"-o"sv, L"extra"sv}, 5, 64},
                          row{{L"wrong"sv, L"4"sv, L"owned.wolf"sv}, 3, 64},
                          row{{L"-m"sv, L"4"sv, L""sv}, 3, 64},
                          row{{L"-m"sv, L"4"sv, L"owned.wolf"sv, L"wrong"sv}, 4, 64},
                          row{{L"-m"sv, L"4"sv, L"owned.wolf"sv}, 3, 0},
                          row{{L"-m"sv, L"4"sv, L"owned.wolf"sv, L"-o"sv}, 4, 0},
                          row{{L"-m"sv, L"0"sv, L"owned.wolf"sv}, 3, 1},
                          row{{L"-m"sv, L"four"sv, L"owned.wolf"sv}, 3, 1},
                          row{{L"-m"sv, L""sv, L"owned.wolf"sv}, 3, 1}};
    for (const auto &r : rows)
        CHECK(test::support::mode_selection_trial_exit({r.arguments.data(), r.count}) == r.exit);
}
#include <functional>
#include <sstream>
namespace
{
    std::optional<std::string> observed_fixture_error(const std::function<void()> &operation)
    {
        try {
            operation();
        } catch (const std::runtime_error &error) {
            return error.what();
        }
        return {};
    }
} // namespace
TEST_CASE("mode fixture reports exact owned path failures and accepts a successful stream", "[mode-helper-fixture]")
{
    test::support::temporary_directory directory("mode-fixture-failures");
    const auto directory_output = directory.path() / ".";
    const auto creation_error =
        observed_fixture_error([&] { (void)write_fixture(directory.path(), ".", header_bytes(kDxMagic, 0)); });
    CHECK(creation_error ==
          std::optional<std::string>{"mode-selection fixture could not be created: " + directory_output.string()});

    const auto output_path = directory.path() / "owned-output.wolf";
    std::ostringstream failed_output;
    failed_output.setstate(std::ios::badbit);
    const auto write_error = observed_fixture_error([&] { require_fixture_written(output_path, failed_output); });
    CHECK(write_error ==
          std::optional<std::string>{"mode-selection fixture could not be written: " + output_path.string()});

    std::ostringstream good_output;
    good_output << "owned fixture bytes";
    CHECK_FALSE(observed_fixture_error([&] { require_fixture_written(output_path, good_output); }).has_value());
    CHECK(good_output.str() == "owned fixture bytes");
}
