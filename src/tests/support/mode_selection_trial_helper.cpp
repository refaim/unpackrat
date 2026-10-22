#include "mode_selection_trial_helper.h"
#include <vector>
int wmain(int argc, wchar_t **argv)
{
    // The CRT owns argv[0]; all remaining arguments are borrowed for this invocation.
    const std::vector<std::wstring_view> arguments(argv + 1, argv + argc);
    return test::support::mode_selection_trial_exit(arguments);
}