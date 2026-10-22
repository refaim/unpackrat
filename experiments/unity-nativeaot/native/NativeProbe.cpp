// NativeProbe.cpp
//
// Independent native TEST HOST for the authorized Unity NativeAOT prototype.
//
// It does NOT link the backend, does NOT call dotnet, and spawns no child
// processes. It only proves that a shipped NativeAOT native DLL can be loaded
// from an absolute path and that its agreed C ABI entry point behaves for the
// two guaranteed-good paths (null-argument rejection, and a valid input/output
// pair called twice in the same process).
//
// Agreed C ABI (do not deviate):
//     name            unpackrat_unity_probe
//     calling conv    __cdecl
//     signature       int32_t(const uint16_t* input, int32_t input_length,
//                             const uint16_t* output_directory, int32_t output_length)
//     lengths         UTF-16 code units, excluding the terminating NUL
//     returns         >=0  resource count
//                     -1   bad argument
//                     -2   data / IO failure
//
// Usage:
//     NativeProbe.exe <dll> <input> <outputDir>
//
// Exit codes:
//     0  pass
//     2  bad command line
//     3  DLL load / export resolution failure
//     4  null-argument contract violation (expected -1)
//     5  valid run did not return a positive count
//     6  the two valid runs disagreed or the second failed

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <string>

namespace
{

    // Exact C ABI shape agreed with the backend.
    using ProbeFn = std::int32_t(__cdecl *)(const std::uint16_t *input, std::int32_t input_length,
                                            const std::uint16_t *output_directory, std::int32_t output_length);

    constexpr wchar_t kExportName[] = L"unpackrat_unity_probe";

    constexpr int kExitPass = 0;
    constexpr int kExitUsage = 2;
    constexpr int kExitLoad = 3;
    constexpr int kExitNullContract = 4;
    constexpr int kExitFirstCall = 5;
    constexpr int kExitSecondCall = 6;

    void Fail(const wchar_t *stage, const wchar_t *detail)
    {
        std::fwprintf(stderr, L"NativeProbe: FAIL [%ls] %ls\n", stage, detail);
    }

    void FailCode(const wchar_t *stage, const wchar_t *detail, DWORD code)
    {
        std::fwprintf(stderr, L"NativeProbe: FAIL [%ls] %ls (GetLastError=%lu)\n", stage, detail,
                      static_cast<unsigned long>(code));
    }

    // Returns the number of UTF-16 code units excluding the terminating NUL, and
    // guards against an out-of-range int32 length for the agreed ABI.
    bool WideLength(const wchar_t *text, std::int32_t *out_length)
    {
        const std::size_t len = std::wcslen(text);
        if (len > 0x7fffffffu) {
            return false;
        }
        *out_length = static_cast<std::int32_t>(len);
        return true;
    }

    // Always emits one line to stdout so the caller can capture the observed count.
    void ReportCount(const wchar_t *label, std::int32_t count)
    {
        std::wprintf(L"NativeProbe: %ls returned %ld\n", label, static_cast<long>(count));
        std::fflush(stdout);
    }

    // Self-contained absolute-path check: "X:\..." or a UNC "\\server\share\...".
    // Avoids a shlwapi dependency so the probe links with only kernel32 + CRT.
    bool IsAbsolutePath(const wchar_t *path)
    {
        if (path[0] == L'\\' && path[1] == L'\\') {
            return true; // UNC prefix
        }
        if (((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) && path[1] == L':' &&
            (path[2] == L'\\' || path[2] == L'/')) {
            return true; // drive-letter prefix
        }
        return false;
    }

} // namespace

int wmain(int argc, wchar_t **argv)
{
    if (argc != 4) {
        std::fwprintf(stderr, L"NativeProbe: usage: NativeProbe.exe <dll> <input> "
                              L"<outputDir>\n");
        return kExitUsage;
    }

    const wchar_t *const dll_path = argv[1];
    const wchar_t *const input_path = argv[2];
    const wchar_t *const output_dir = argv[3];

    if (dll_path[0] == L'\0' || input_path[0] == L'\0' || output_dir[0] == L'\0') {
        Fail(L"arguments", L"empty <dll>/<input>/<outputDir> argument not allowed");
        return kExitUsage;
    }

    // Absolute-path requirement: LoadLibraryExW with LOAD_WITH_ALTERED_SEARCH_PATH
    // only honours the altered search order for an absolute path, and it keeps
    // the module search away from the current directory / PATH.
    if (!IsAbsolutePath(dll_path)) {
        Fail(L"arguments", L"<dll> must be an absolute path");
        return kExitUsage;
    }

    std::int32_t input_len = 0;
    std::int32_t output_len = 0;
    if (!WideLength(input_path, &input_len) || !WideLength(output_dir, &output_len)) {
        Fail(L"arguments", L"argument length exceeds int32 range");
        return kExitUsage;
    }

    // Ensure the output directory exists; the probe must not depend on the
    // backend creating it, and CreateDirectoryW on an existing dir is benign.
    if (!CreateDirectoryW(output_dir, nullptr)) {
        const DWORD err = GetLastError();
        if (err != ERROR_ALREADY_EXISTS) {
            FailCode(L"output dir", L"cannot create output directory", err);
            return kExitUsage;
        }
    }

    // Load the AOT module by absolute path. LOAD_WITH_ALTERED_SEARCH_PATH uses
    // the DLL's own directory for dependency resolution.
    HMODULE module = LoadLibraryExW(dll_path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr) {
        FailCode(L"LoadLibraryExW", L"cannot load the target DLL", GetLastError());
        return kExitLoad;
    }

    // Resolve the exact agreed export.
    FARPROC raw = GetProcAddress(module, "unpackrat_unity_probe");
    if (raw == nullptr) {
        FailCode(L"GetProcAddress", L"export unpackrat_unity_probe not found", GetLastError());
        return kExitLoad;
    }
    const ProbeFn probe = reinterpret_cast<ProbeFn>(raw);

    // NativeAOT modules cannot be unloaded, so pin this one. Pinning also keeps
    // the resolved function pointer valid for the remainder of the process and
    // makes the intentional absence of FreeLibrary explicit.
    HMODULE pinned = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                            reinterpret_cast<LPCWSTR>(raw), &pinned)) {
        FailCode(L"GetModuleHandleExW", L"cannot pin the AOT module", GetLastError());
        return kExitLoad;
    }
    (void)pinned; // intentionally never FreeLibrary'd

    // --- Contract check 1: null input must be rejected with -1. ---
    const std::int32_t null_input_rc =
        probe(nullptr, input_len, reinterpret_cast<const std::uint16_t *>(output_dir), output_len);
    ReportCount(L"null-input", null_input_rc);
    if (null_input_rc != -1) {
        std::fwprintf(stderr,
                      L"NativeProbe: FAIL [null contract] expected -1 for null "
                      L"input, got %ld\n",
                      static_cast<long>(null_input_rc));
        return kExitNullContract;
    }

    // --- Contract check 2: valid input/output, called twice in-process. ---
    const std::int32_t first_rc = probe(reinterpret_cast<const std::uint16_t *>(input_path), input_len,
                                        reinterpret_cast<const std::uint16_t *>(output_dir), output_len);
    ReportCount(L"run#1", first_rc);
    if (first_rc <= 0) {
        std::fwprintf(stderr,
                      L"NativeProbe: FAIL [run#1] expected a positive resource "
                      L"count, got %ld\n",
                      static_cast<long>(first_rc));
        return kExitFirstCall;
    }

    // Second call in the same process. Reusing the same directory (overwrite)
    // is acceptable per the agreed harness contract.
    const std::int32_t second_rc = probe(reinterpret_cast<const std::uint16_t *>(input_path), input_len,
                                         reinterpret_cast<const std::uint16_t *>(output_dir), output_len);
    ReportCount(L"run#2", second_rc);
    if (second_rc <= 0) {
        std::fwprintf(stderr,
                      L"NativeProbe: FAIL [run#2] expected a positive resource "
                      L"count, got %ld\n",
                      static_cast<long>(second_rc));
        return kExitSecondCall;
    }
    if (second_rc != first_rc) {
        std::fwprintf(stderr,
                      L"NativeProbe: FAIL [repeatability] run#1=%ld run#2=%ld "
                      L"counts differ\n",
                      static_cast<long>(first_rc), static_cast<long>(second_rc));
        return kExitSecondCall;
    }

    std::wprintf(L"NativeProbe: PASS unpackrat_unity_probe resource_count=%ld "
                 L"(stable across 2 in-process runs)\n",
                 static_cast<long>(first_rc));
    std::fflush(stdout);
    return kExitPass;
}
