#ifndef UNPACKRAT_TEST_MODE_SELECTION_TRIAL_HELPER_H
#define UNPACKRAT_TEST_MODE_SELECTION_TRIAL_HELPER_H
#include <span>
#include <string_view>
namespace test::support
{
    // A controlled trial verdict only. The archive argument is opaque and is never opened.
    // WolfDec emits canonical decimal mode strings; only its mode 4 is a working fixture.
    inline int mode_selection_trial_exit(std::span<const std::wstring_view> args) noexcept
    {
        if (args.size() != 3 && args.size() != 4)
            return 64;
        if (args[0] != L"-m" || args[2].empty())
            return 64;
        if (args.size() == 4 && args[3] != L"-o")
            return 64;
        return args[1] == L"4" ? 0 : 1;
    }
} // namespace test::support
#endif