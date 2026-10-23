// UnityObserverProbe.cpp
//
// TEST-ONLY native host for the Observer module API declared in src/api.h
// (Observer API version 6, packed Windows layout). It loads one absolute DLL
// path with LoadLibraryExW, resolves the Observer exports, and drives the
// module end to end through the frozen Observer contract against a real Unity
// fixture archive. It never launches a process/runtime, never builds or fakes
// a DLL, and never converts assets itself: conversion belongs to the module
// and is exercised only through ExtractItem.
//
// Usage:
//     UnityObserverProbe.exe <dll> <input> <outputDir> [expectedRawBinFile] [logFile]
//
// The optional sixth argument is a persistent log file. When supplied it
// captures both stdout and stderr plus flushed milestone lines (start, pre/post
// DLL load, open/prepare/enumerate/extract/validate), so an XP crash during
// load leaves a diagnosable trail on disk. Plain CRT file I/O only; it never
// changes the test semantics of the four/five-argument forms.
//
// Modes:
//   * argc == 5 (expectedRawBinFile supplied): qualification mode. Every catalog
//     entry must extract without error; the first raw ".bin" payload must equal
//     the expected file byte for byte. The listing cannot tell which asset type
//     produced a raw ".bin" (a TextAsset's own bytes, or a video / audio source
//     that fell back to raw bytes), so this compares bytes against the first
//     raw ".bin" entry and makes no asset-type claim. Any unexpected extraction
//     error fails.
//   * argc == 4 (no expected file): game/packet mode. Only a per-item
//     SER_ERROR_READ (the known empty placeholder texture) is tolerated; any
//     other per-item code, a callback protocol violation, a cap breach or a
//     post-cancel callback fails the run. When an allowed READ occurs the run
//     reports outcome "partial" with counts instead of claiming every asset
//     converted.
//
// Output naming: this host NEVER turns a module-provided asset label into a
// filesystem path. Listings record the module path/label as metadata only, and
// every generated output file is named "<index>-<kind ext>" (for example
// "0-bin" or "12-png").
//
// Exit codes (stable):
//     0  pass
//     2  bad command line (argc or a non-absolute path)
//     3  DLL load failure
//     7  a required Observer export is missing (LoadSubModule/UnloadSubModule)
//     8  LoadSubModule rejected the parameters, or the function table is incomplete
//     9  ApiVersion != 6, or ModuleVersion == 0
//    10  null-argument contract failure in OpenStorage/PrepareFiles/GetItem/ExtractItem
//    11  OpenStorage or PrepareFiles failed on the real fixture
//    12  entry enumeration failure (GetItem error path, or zero entries)
//    13  entry layout violation (Size != PackedSize or PackedSize < 0, a
//        rejected listing path/extension, or a successful raw ".bin" whose
//        PackedSize does not equal the exported byte count)
//    14  an out-of-range index was not answered with GET_ITEM_NOMOREITEMS
//    15  a negative index was not rejected with GET_ITEM_ERROR
//    17  an unexpected per-item extraction error in qualification mode
//    18  the first raw ".bin" payload was missing, empty, or did not match expected
//    19  callback cancel did not yield SER_USERABORT, or a partial destination survived
//    20  extraction after a cancel did not succeed
//    21  repeated close/reopen was unstable
//    22  the source-size metadata was invalid or changed after extraction
//    23  repeated PrepareFiles failed
//    24  a write to a bad destination was not reported as SER_ERROR_WRITE
//    25  an invalid input was not rejected by OpenStorage
//    27  a PNG payload failed structural validation (signature/IHDR)
//    28  a callback protocol violation was observed
//    29  a single output exceeded the 256 MiB host cap
//    30  the catalog exceeded the 200000 entry hard bound
//    31  the optional log file could not be created
//
// Codes 16 and 26 are intentionally reserved: this host does not dereference
// forged pointer handles or assume Observer-side stale-pointer protection
// (only the managed C ABI session layer offers that), so there is nothing
// falsifiable to assert there.
//
// Metadata contract: Observer FileSize and PackedSize both carry the declared
// source payload length before conversion, or 0 when unknown; they must be
// equal and non-negative. PackedSize is NOT the encoded PNG length and must
// never be equated with a texture's converted output. Every successful
// raw ".bin" payload has an exactly known source length, so its PackedSize must
// equal the exported byte count (exit 13 otherwise). Every converted kind is
// exempt: the image kind (".png") and the transformed binary kinds (font, video,
// audio, mesh, movie) export a conversion of the source, so PackedSize and the
// output length must never be equated.
//
// Invariant note: a native host cannot, without backend instrumentation, prove
// that the module performed zero conversions during OpenStorage/PrepareFiles/
// GetItem; it only observes the reported metadata. The "no conversion before
// extract" invariant is covered by the module's own unit stub tests, not by
// this host.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>

#include "../../../api.h"
#include "UnityObserverProbePolicy.h"

#include <algorithm>
#include <climits>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <string_view>
#include <vector>

namespace
{

    // ---- exit codes ----------------------------------------------------------
    enum ExitCode : int
    {
        kPass = 0,
        kUsage = 2,
        kLoad = 3,
        kMissingExport = 7,
        kLoadRejected = 8,
        kApiVersion = 9,
        kNullContract = 10,
        kOpenFixture = 11,
        kEnumerate = 12,
        kLayout = 13,
        kBadIndexAfter = 14,
        kBadIndexNegative = 15,
        kExtract = 17,
        kExpectedMismatch = 18,
        kCancel = 19,
        kRepeatAfterCancel = 20,
        kCloseReopen = 21,
        kSizePolicy = 22,
        kPrepareRepeat = 23,
        kWriteError = 24,
        kInvalidInput = 25,
        kPngValidation = 27,
        kCallbackProtocol = 28,
        kCapExceeded = 29,
        kEntryBound = 30,
        kLogUnavailable = 31,
    };

    constexpr int kMaxEntries = 200000;
    constexpr int64_t kMaxBytesPerFile = 256LL * 1024 * 1024; // 256 MiB

    // Fail is defined after the logging helpers below so its message is mirrored
    // into the optional persistent log.
    void Fail(const wchar_t *stage, const wchar_t *detail);

    // ---- small string helpers ------------------------------------------------

    bool IsAbsolutePath(const wchar_t *path)
    {
        if (path == nullptr || path[0] == L'\0') {
            return false;
        }
        if (path[0] == L'\\' && path[1] == L'\\') {
            return true; // UNC
        }
        if (path[0] == L'\\' || path[0] == L'/') {
            return true; // rooted
        }
        if (path[1] == L':' && (path[2] == L'\\' || path[2] == L'/')) {
            return true; // drive-absolute
        }
        return false;
    }

    std::wstring WStrFromFixed(const wchar_t *buffer, std::size_t capacity)
    {
        std::size_t length = 0;
        while (length < capacity && buffer[length] != L'\0') {
            ++length;
        }
        return std::wstring(buffer, length);
    }

    std::wstring ToLower(std::wstring value)
    {
        std::transform(value.begin(), value.end(), value.begin(),
                       [](wchar_t c) { return static_cast<wchar_t>(::towlower(c)); });
        return value;
    }

    std::string ToUtf8(const std::wstring &value)
    {
        if (value.empty()) {
            return {};
        }
        const int needed = ::WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0,
                                                 nullptr, nullptr);
        if (needed <= 0) {
            return {};
        }
        std::string out(static_cast<std::size_t>(needed), '\0');
        ::WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), out.data(), needed, nullptr,
                              nullptr);
        return out;
    }

    // ---- logging -------------------------------------------------------------
    // Optional sixth argument: a persistent log file. It captures both stdout and
    // stderr, plus milestone lines flushed before each risky step, so an XP crash
    // is diagnosable. Plain CRT file I/O only: no framework and no command or
    // shell execution, and no change to the test semantics.

    struct Logger
    {
        std::FILE *file = nullptr;
        Logger() = default;
        Logger(const Logger &) = delete;
        Logger &operator=(const Logger &) = delete;
        ~Logger()
        {
            if (file != nullptr) {
                std::fclose(file);
            }
        }
        bool active() const
        {
            return file != nullptr;
        }
        void write(const wchar_t *text)
        {
            if (file == nullptr || text == nullptr) {
                return;
            }
            const std::string bytes = ToUtf8(text);
            if (!bytes.empty()) {
                std::fwrite(bytes.data(), 1, bytes.size(), file);
            }
            std::fflush(file);
        }
    };

    Logger *g_logger = nullptr;

    std::wstring FormatWide(const wchar_t *format, va_list args)
    {
        wchar_t buffer[2048];
        const int written = _vsnwprintf_s(buffer, _countof(buffer), _TRUNCATE, format, args);
        if (written < 0) {
            buffer[_countof(buffer) - 1] = L'\0';
        }
        return std::wstring(buffer);
    }

    void EmitLine(bool to_stderr, const wchar_t *format, va_list args)
    {
        const std::wstring line = FormatWide(format, args);
        std::FILE *console = to_stderr ? stderr : stdout;
        std::fwprintf(console, L"%ls", line.c_str());
        std::fflush(console);
        if (g_logger != nullptr) {
            g_logger->write(line.c_str());
        }
    }

    void Out(const wchar_t *format, ...)
    {
        va_list args;
        va_start(args, format);
        EmitLine(false, format, args);
        va_end(args);
    }

    void Err(const wchar_t *format, ...)
    {
        va_list args;
        va_start(args, format);
        EmitLine(true, format, args);
        va_end(args);
    }

    // Log-only diagnostic milestone, flushed every time.
    void MilestoneF(const wchar_t *format, ...)
    {
        if (g_logger == nullptr) {
            return;
        }
        va_list args;
        va_start(args, format);
        const std::wstring line = FormatWide(format, args);
        va_end(args);
        g_logger->write(line.c_str());
    }

    void Fail(const wchar_t *stage, const wchar_t *detail)
    {
        Err(L"UnityObserverProbe: FAIL [%ls] %ls\n", stage, detail);
    }

    std::string JsonEscape(std::string_view value)
    {
        std::string out;
        out.reserve(value.size() + 8);
        for (char raw : value) {
            switch (raw) {
            case '"':
                out += "\\\"";
                break;
            case '\\':
                out += "\\\\";
                break;
            case '\n':
                out += "\\n";
                break;
            case '\r':
                out += "\\r";
                break;
            case '\t':
                out += "\\t";
                break;
            default: {
                const unsigned char byte = static_cast<unsigned char>(raw);
                if (byte < 0x20) {
                    char buffer[8];
                    std::snprintf(buffer, sizeof(buffer), "\\u%04x", byte);
                    out += buffer;
                } else {
                    out += raw;
                }
                break;
            }
            }
        }
        return out;
    }

    std::string JsonStr(const std::wstring &value)
    {
        return "\"" + JsonEscape(ToUtf8(value)) + "\"";
    }

    // ---- file helpers --------------------------------------------------------

    bool ReadFileCapped(const std::wstring &path, std::vector<unsigned char> &out, int64_t cap, bool &too_big)
    {
        too_big = false;
        std::ifstream in(std::filesystem::path(path), std::ios::binary);
        if (!in) {
            return false;
        }
        in.seekg(0, std::ios::end);
        const std::streamoff size = in.tellg();
        if (size < 0) {
            return false;
        }
        if (static_cast<int64_t>(size) > cap) {
            too_big = true;
            return false;
        }
        in.seekg(0, std::ios::beg);
        out.resize(static_cast<std::size_t>(size));
        if (!out.empty()) {
            in.read(reinterpret_cast<char *>(out.data()), static_cast<std::streamsize>(out.size()));
        }
        return in.good() || in.eof();
    }

    bool WriteUtf8File(const std::wstring &path, std::string_view text)
    {
        std::ofstream out(std::filesystem::path(path), std::ios::binary | std::ios::trunc);
        if (!out) {
            return false;
        }
        out.write(text.data(), static_cast<std::streamsize>(text.size()));
        return out.good();
    }

    bool FileExists(const std::wstring &path)
    {
        std::error_code error;
        return std::filesystem::exists(std::filesystem::path(path), error);
    }

    // Structural PNG check only: signature, a first IHDR chunk of length 13, and
    // positive width/height. No decoding and no image viewer is involved.
    bool ValidatePngStructure(const std::vector<unsigned char> &bytes)
    {
        static const unsigned char kSignature[8] = {0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A};
        if (bytes.size() < 8 + 8 + 13) {
            return false;
        }
        if (std::memcmp(bytes.data(), kSignature, sizeof(kSignature)) != 0) {
            return false;
        }
        const unsigned char *chunk = bytes.data() + 8;
        const std::uint32_t length = (static_cast<std::uint32_t>(chunk[0]) << 24) |
                                     (static_cast<std::uint32_t>(chunk[1]) << 16) |
                                     (static_cast<std::uint32_t>(chunk[2]) << 8) | static_cast<std::uint32_t>(chunk[3]);
        if (length != 13) {
            return false;
        }
        if (std::memcmp(chunk + 4, "IHDR", 4) != 0) {
            return false;
        }
        const unsigned char *ihdr = chunk + 8;
        const std::uint32_t width = (static_cast<std::uint32_t>(ihdr[0]) << 24) |
                                    (static_cast<std::uint32_t>(ihdr[1]) << 16) |
                                    (static_cast<std::uint32_t>(ihdr[2]) << 8) | static_cast<std::uint32_t>(ihdr[3]);
        const std::uint32_t height = (static_cast<std::uint32_t>(ihdr[4]) << 24) |
                                     (static_cast<std::uint32_t>(ihdr[5]) << 16) |
                                     (static_cast<std::uint32_t>(ihdr[6]) << 8) | static_cast<std::uint32_t>(ihdr[7]);
        return width > 0 && height > 0;
    }

    // ---- module / storage RAII ----------------------------------------------

    struct ModuleHandle
    {
        HMODULE handle = nullptr;
        ModuleHandle() = default;
        ModuleHandle(const ModuleHandle &) = delete;
        ModuleHandle &operator=(const ModuleHandle &) = delete;
        ~ModuleHandle()
        {
            if (handle != nullptr) {
                ::FreeLibrary(handle);
            }
        }
    };

    struct ObserverApi
    {
        LoadSubModuleFunc load = nullptr;
        UnloadSubModuleFunc unload = nullptr;
        module_cbs cbs{};
        DWORD module_version = 0;
        DWORD api_version = 0;
    };

    struct StorageHandle
    {
        const module_cbs *cbs = nullptr;
        HANDLE handle = nullptr;
        StorageHandle() = default;
        StorageHandle(const StorageHandle &) = delete;
        StorageHandle &operator=(const StorageHandle &) = delete;
        ~StorageHandle()
        {
            Close();
        }
        void Close()
        {
            if (handle != nullptr && cbs != nullptr && cbs->CloseStorage != nullptr) {
                cbs->CloseStorage(handle);
            }
            handle = nullptr;
        }
    };

    // ---- progress callback ---------------------------------------------------

    // `mode` picks accept(0) or cancel-on-first-callback(1). The bookkeeping lives
    // in the test-only policy helper so the same decisions are proven by the
    // standalone harness. The callback is noexcept so no C++ exception can cross
    // the Observer ABI; malformed protocol is recorded rather than thrown.
    using ProgressSink = probe::SinkState;

    static_assert(kMaxBytesPerFile == probe::kMaxBytesPerFile, "host and helper caps must agree");

    int CALLBACK ProgressCallback(HANDLE context, __int64 bytes) noexcept
    {
        ProgressSink *sink = static_cast<ProgressSink *>(context);
        if (sink == nullptr) {
            return 0;
        }
        return probe::SinkInvoke(*sink, static_cast<std::int64_t>(bytes));
    }

    // ---- catalog records -----------------------------------------------------

    struct EntryRec
    {
        int index = 0;
        std::wstring path;
        std::wstring label; // name portion, recorded as metadata only
        std::wstring ext;   // the leaf's output extension, e.g. "png", "bin", "ttf", "mp4"
        probe::ResourceKind kind = probe::kResourceNone;
        int64_t size = 0;
        int64_t packed = 0;
        int extract_rc = INT_MIN;
        int64_t bytes = -1;
    };

    std::wstring OutputNameFor(const EntryRec &entry)
    {
        return std::to_wstring(entry.index) + L"-" + entry.ext;
    }

} // namespace

int wmain(int argc, wchar_t **argv)
{
    if (argc < 4 || argc > 6) {
        Fail(L"usage", L"UnityObserverProbe.exe <dll> <input> <outputDir> [expectedRawBinFile] [logFile]");
        return kUsage;
    }

    const std::wstring dll_path = argv[1];
    const std::wstring input_path = argv[2];
    const std::wstring output_dir = argv[3];
    const bool qualification_mode = (argc >= 5);
    const std::wstring expected_path = qualification_mode ? std::wstring(argv[4]) : std::wstring();
    const std::wstring log_path = (argc == 6) ? std::wstring(argv[5]) : std::wstring();

    if (!IsAbsolutePath(dll_path.c_str()) || !IsAbsolutePath(input_path.c_str()) ||
        !IsAbsolutePath(output_dir.c_str()) || (qualification_mode && !IsAbsolutePath(expected_path.c_str())) ||
        (argc == 6 && !IsAbsolutePath(log_path.c_str()))) {
        Fail(L"cli", L"dll, input, outputDir, expectedRawBinFile and logFile must be absolute paths");
        return kUsage;
    }

    // Open the optional persistent log before anything risky so an XP crash
    // during DLL load still leaves a diagnosable trail on disk.
    Logger logger;
    if (argc == 6) {
        std::FILE *opened = nullptr;
        if (_wfopen_s(&opened, log_path.c_str(), L"wb") != 0 || opened == nullptr) {
            Fail(L"log", L"cannot create log file");
            return kLogUnavailable;
        }
        logger.file = opened;
        g_logger = &logger;
        MilestoneF(L"milestone: start argc=%d\n", argc);
        MilestoneF(L"milestone: dll=%ls\n", dll_path.c_str());
    }

    std::error_code dir_error;
    std::filesystem::create_directories(std::filesystem::path(output_dir), dir_error);
    if (dir_error) {
        Fail(L"setup", L"cannot create outputDir");
        return kUsage;
    }

    // ---- module load / export resolution ----
    MilestoneF(L"milestone: pre DLL load\n");
    ModuleHandle module;
    module.handle = ::LoadLibraryExW(dll_path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module.handle == nullptr) {
        Fail(L"load", L"LoadLibraryExW failed");
        return kLoad;
    }
    MilestoneF(L"milestone: post DLL load handle=%p\n", static_cast<void *>(module.handle));
    ObserverApi api;
    api.load = reinterpret_cast<LoadSubModuleFunc>(::GetProcAddress(module.handle, "LoadSubModule"));
    api.unload = reinterpret_cast<UnloadSubModuleFunc>(::GetProcAddress(module.handle, "UnloadSubModule"));
    if (api.load == nullptr) {
        Fail(L"exports", L"LoadSubModule export missing");
        return kMissingExport;
    }
    if (api.unload == nullptr) {
        Fail(L"exports", L"UnloadSubModule export missing");
        return kMissingExport;
    }
    MilestoneF(L"milestone: exports resolved load=%p unload=%p\n", reinterpret_cast<void *>(api.load),
               reinterpret_cast<void *>(api.unload));

    // LoadSubModule(nullptr) is a validated common-code contract: reject quietly.
    if (api.load(nullptr) != FALSE) {
        Fail(L"load-contract", L"LoadSubModule(nullptr) did not return FALSE");
        return kLoadRejected;
    }

    ModuleLoadParameters params{};
    params.StructSize = sizeof(ModuleLoadParameters);
    params.Settings = L"";
    if (api.load(&params) == FALSE) {
        Fail(L"load-contract", L"LoadSubModule returned FALSE for valid parameters");
        return kLoadRejected;
    }
    api.cbs = params.ApiFuncs;
    api.module_version = params.ModuleVersion;
    api.api_version = params.ApiVersion;
    if (api.api_version != ACTUAL_API_VERSION) {
        Fail(L"version", L"ApiVersion is not 6");
        return kApiVersion;
    }
    if (api.module_version == 0) {
        Fail(L"version", L"ModuleVersion is zero");
        return kApiVersion;
    }
    if (api.cbs.OpenStorage == nullptr || api.cbs.CloseStorage == nullptr || api.cbs.GetItem == nullptr ||
        api.cbs.ExtractItem == nullptr || api.cbs.PrepareFiles == nullptr) {
        Fail(L"load-contract", L"incomplete function table");
        return kLoadRejected;
    }
    MilestoneF(L"milestone: module ready api=%lu version=%lu\n", api.api_version, api.module_version);

    // ---- null / invalid argument contracts (validated common code only) ----
    // Each probe varies exactly ONE input and supplies a valid value for every
    // other field (the earlier revision reused an already-null FilePath across
    // every case, evaluating two unknowns at once).
    {
        StorageGeneralInfo info{};
        HANDLE handle = nullptr;
        const wchar_t *const valid_path = input_path.c_str();

        // Only FilePath is invalid.
        StorageOpenParams null_path{};
        null_path.StructSize = sizeof(StorageOpenParams);
        null_path.FilePath = nullptr;
        if (api.cbs.OpenStorage(null_path, &handle, &info) != SOR_INVALID_FILE) {
            Fail(L"open-contract", L"null FilePath not rejected");
            return kNullContract;
        }
        // Only the storage out pointer is invalid.
        StorageOpenParams null_storage{};
        null_storage.StructSize = sizeof(StorageOpenParams);
        null_storage.FilePath = valid_path;
        if (api.cbs.OpenStorage(null_storage, nullptr, &info) != SOR_INVALID_FILE) {
            Fail(L"open-contract", L"null storage out not rejected");
            return kNullContract;
        }
        // Only the info out pointer is invalid.
        StorageOpenParams null_info{};
        null_info.StructSize = sizeof(StorageOpenParams);
        null_info.FilePath = valid_path;
        if (api.cbs.OpenStorage(null_info, &handle, nullptr) != SOR_INVALID_FILE) {
            Fail(L"open-contract", L"null info not rejected");
            return kNullContract;
        }
        // Only StructSize is invalid.
        StorageOpenParams wrong_size{};
        wrong_size.StructSize = 0;
        wrong_size.FilePath = valid_path;
        if (api.cbs.OpenStorage(wrong_size, &handle, &info) != SOR_INVALID_FILE) {
            Fail(L"open-contract", L"wrong StructSize not rejected");
            return kNullContract;
        }
        if (api.cbs.PrepareFiles(nullptr) != FALSE) {
            Fail(L"prepare-contract", L"PrepareFiles(nullptr) did not return FALSE");
            return kNullContract;
        }
        const std::wstring invalid_path = (std::filesystem::path(output_dir) / L"__no_such_input__.assets").wstring();
        StorageOpenParams invalid{};
        invalid.StructSize = sizeof(StorageOpenParams);
        invalid.FilePath = invalid_path.c_str();
        if (api.cbs.OpenStorage(invalid, &handle, &info) != SOR_INVALID_FILE) {
            Fail(L"open-invalid", L"nonexistent input not rejected");
            return kInvalidInput;
        }
    }

    // ---- open the real fixture ----
    MilestoneF(L"milestone: pre OpenStorage input=%ls\n", input_path.c_str());
    StorageHandle storage;
    storage.cbs = &api.cbs;
    {
        StorageGeneralInfo info{};
        StorageOpenParams open{};
        open.StructSize = sizeof(StorageOpenParams);
        open.FilePath = input_path.c_str();
        open.Password = nullptr;
        open.Data = nullptr;
        open.DataSize = 0;
        const int rc = api.cbs.OpenStorage(open, &storage.handle, &info);
        if (rc != SOR_SUCCESS || storage.handle == nullptr) {
            Fail(L"open", L"OpenStorage failed on the fixture");
            return kOpenFixture;
        }
        MilestoneF(L"milestone: post OpenStorage format=%ls\n", info.Format);
    }
    if (api.cbs.PrepareFiles(storage.handle) != TRUE) {
        Fail(L"prepare", L"PrepareFiles failed");
        return kOpenFixture;
    }
    if (api.cbs.PrepareFiles(storage.handle) != TRUE) {
        Fail(L"prepare", L"repeated PrepareFiles failed");
        return kPrepareRepeat;
    }
    MilestoneF(L"milestone: prepared\n");

    // ---- enumerate ----
    std::vector<EntryRec> entries;
    for (int index = 0; index <= kMaxEntries; ++index) {
        if (index == kMaxEntries) {
            Fail(L"enumerate", L"catalog exceeded the 200000 entry hard bound");
            return kEntryBound;
        }
        StorageItemInfo item{};
        const int rc = api.cbs.GetItem(storage.handle, index, &item);
        if (rc == GET_ITEM_NOMOREITEMS) {
            break;
        }
        if (rc != GET_ITEM_OK) {
            Fail(L"enumerate", L"GetItem returned an error before NOMOREITEMS");
            return kEnumerate;
        }
        if (!probe::ListingSizeOk(item.Size, item.PackedSize)) {
            Fail(L"layout", L"entry Size and PackedSize must be equal and >= 0 at listing");
            return kLayout;
        }
        EntryRec rec;
        rec.index = index;
        rec.path = WStrFromFixed(item.Path, std::size(item.Path));
        rec.size = item.Size;
        rec.packed = item.PackedSize;
        const std::size_t sep = rec.path.find_last_of(L"\\/");
        rec.label = (sep == std::wstring::npos) ? rec.path : rec.path.substr(sep + 1);
        const std::size_t dot = rec.label.find_last_of(L'.');
        rec.ext = (dot == std::wstring::npos) ? std::wstring() : ToLower(rec.label.substr(dot + 1));
        probe::ResourceKind kind = probe::kResourceNone;
        if (!probe::ClassifyListingPath(rec.path, &kind)) {
            Fail(L"layout", L"entry path is not an accepted listing shape");
            return kLayout;
        }
        rec.kind = kind;
        entries.push_back(rec);
    }
    if (entries.empty()) {
        Fail(L"enumerate", L"catalog exposed zero entries");
        return kEnumerate;
    }
    MilestoneF(L"milestone: enumerated entries=%zu\n", entries.size());

    // ---- index boundaries ----
    {
        StorageItemInfo item{};
        if (api.cbs.GetItem(storage.handle, -1, &item) != GET_ITEM_ERROR) {
            Fail(L"index", L"negative index not rejected");
            return kBadIndexNegative;
        }
        if (api.cbs.GetItem(storage.handle, static_cast<int>(entries.size()), &item) != GET_ITEM_NOMOREITEMS) {
            Fail(L"index", L"index at count not answered with NOMOREITEMS");
            return kBadIndexAfter;
        }
        if (api.cbs.GetItem(storage.handle, static_cast<int>(entries.size()) + 1000, &item) != GET_ITEM_NOMOREITEMS) {
            Fail(L"index", L"far index not answered with NOMOREITEMS");
            return kBadIndexAfter;
        }
        if (api.cbs.GetItem(storage.handle, 0, nullptr) != GET_ITEM_ERROR) {
            Fail(L"index", L"null item_info not rejected");
            return kNullContract;
        }
    }

    // ---- extraction ----
    MilestoneF(L"milestone: extraction start\n");
    const auto first_raw_bin =
        std::ranges::find_if(entries, [](const EntryRec &entry) { return entry.kind == probe::kResourceRawBin; });
    const int first_raw_bin_index = first_raw_bin == entries.end() ? -1 : first_raw_bin->index;

    auto extract_entry = [&](int index, const std::wstring &destination, int mode, ProgressSink &sink) {
        sink.mode = mode;
        ExtractOperationParams op{};
        op.ItemIndex = index;
        op.Flags = 0;
        op.DestPath = destination.c_str();
        op.Password = nullptr;
        op.Callbacks.signalContext = &sink;
        op.Callbacks.FileProgress = &ProgressCallback;
        return api.cbs.ExtractItem(storage.handle, op);
    };

    std::vector<unsigned char> first_raw_bin_bytes;
    bool first_raw_bin_captured = false;
    std::vector<unsigned char> reference_bytes; // first successful non-empty export
    int allowed_read_failures = 0;
    int recorded_successes = 0;

    for (EntryRec &entry : entries) {
        const std::wstring destination = (std::filesystem::path(output_dir) / OutputNameFor(entry)).wstring();
        ProgressSink sink;
        const int rc = extract_entry(entry.index, destination, 0, sink);
        entry.extract_rc = rc;
        if (sink.protocol_violation) {
            Fail(L"callback", L"progress callback observed a protocol violation");
            return kCallbackProtocol;
        }
        if (sink.over_cap) {
            Fail(L"cap", L"progress callback exceeded the 256 MiB cap");
            return kCapExceeded;
        }
        if (sink.post_cancel_violation) {
            Fail(L"callback", L"a callback ran after a cancel in an accepting export");
            return kCallbackProtocol;
        }
        if (rc != SER_SUCCESS) {
            if (qualification_mode) {
                Fail(L"extract", L"unexpected per-item extraction error in qualification mode");
                return kExtract;
            }
            if (!probe::IsAllowedPerItemError(rc)) {
                Fail(L"game-error", L"a per-item error other than SER_ERROR_READ is not tolerated");
                return kExtract;
            }
            ++allowed_read_failures;
            continue;
        }
        ++recorded_successes;
        std::vector<unsigned char> bytes;
        bool too_big = false;
        if (!ReadFileCapped(destination, bytes, kMaxBytesPerFile, too_big)) {
            if (too_big) {
                Fail(L"cap", L"single output exceeded the 256 MiB cap");
                return kCapExceeded;
            }
            Fail(L"extract", L"could not read back an extracted file");
            return kExtract;
        }
        entry.bytes = static_cast<int64_t>(bytes.size());
        if (probe::IsPngPayloadKind(entry.kind)) {
            if (!ValidatePngStructure(bytes)) {
                Fail(L"png", L"a texture payload failed signature/IHDR validation");
                return kPngValidation;
            }
        } else if (entry.index == first_raw_bin_index) {
            first_raw_bin_bytes = bytes;
            first_raw_bin_captured = true;
        }
        if (reference_bytes.empty() && !bytes.empty()) {
            reference_bytes = bytes;
        }
    }
    MilestoneF(L"milestone: extraction done successes=%d reads=%d\n", recorded_successes, allowed_read_failures);

    // The cancel/retry/write-error probes run only against an entry that
    // already exported a non-empty body; when none exists they are explicitly
    // not exercised rather than silently pointed at entries[0].
    std::vector<int> probe_results;
    std::vector<std::int64_t> probe_bytes;
    probe_results.reserve(entries.size());
    probe_bytes.reserve(entries.size());
    for (const EntryRec &entry : entries) {
        probe_results.push_back(entry.extract_rc);
        probe_bytes.push_back(entry.bytes);
    }
    const int candidate = probe::SelectProbeTarget(probe_results, probe_bytes);

    // ---- expected comparison (qualification mode) ----
    if (qualification_mode) {
        if (first_raw_bin_index < 0 || !first_raw_bin_captured || first_raw_bin_bytes.empty()) {
            Fail(L"expected", L"no non-empty raw .bin payload was extracted");
            return kExpectedMismatch;
        }
        std::vector<unsigned char> expected;
        bool too_big = false;
        if (!ReadFileCapped(expected_path, expected, kMaxBytesPerFile, too_big)) {
            Fail(L"expected", L"could not read the expected raw .bin file");
            return kUsage;
        }
        if (expected != first_raw_bin_bytes) {
            Fail(L"expected", L"the extracted raw .bin payload did not match the expected bytes");
            return kExpectedMismatch;
        }
    }

    // ---- source-size metadata (after the expected comparison, so a wrong
    //      expected file is never masked by a PackedSize error) ----
    // Size equals PackedSize and both carry the declared source length. For a
    // successfully exported raw ".bin" payload the source length is known exactly, so
    // PackedSize must equal the exported byte count. Every converted kind (the
    // image and the transformed font/video/audio/mesh/movie binaries) is exempt:
    // its PackedSize is the source payload, not the converted output, so the two
    // must never be equated.
    for (const EntryRec &entry : entries) {
        if (!probe::RequiresPackedEqualsExported(entry.kind) || entry.extract_rc != SER_SUCCESS) {
            continue;
        }
        if (entry.packed != entry.bytes) {
            const std::wstring detail = L"successful raw .bin PackedSize=" + std::to_wstring(entry.packed) +
                                        L" != exported bytes=" + std::to_wstring(entry.bytes);
            Fail(L"source-size", detail.c_str());
            return kLayout;
        }
    }

    // ---- source-size metadata is unchanged across the whole catalogue ---
    // Re-read every listed entry and compare it with the value captured at
    // listing; any changed or invalid entry (not just index 0) fails.
    {
        std::vector<probe::SourceSize> listing;
        std::vector<probe::SourceSize> current;
        listing.reserve(entries.size());
        current.reserve(entries.size());
        std::ranges::transform(entries, std::back_inserter(listing),
                               [](const EntryRec &entry) { return probe::SourceSize{entry.size, entry.packed}; });
        for (const EntryRec &entry : entries) {
            StorageItemInfo item{};
            if (api.cbs.GetItem(storage.handle, entry.index, &item) != GET_ITEM_OK) {
                Fail(L"metadata", L"GetItem failed after extraction");
                return kEnumerate;
            }
            current.push_back(probe::SourceSize{item.Size, item.PackedSize});
        }
        if (!probe::CatalogueSourceSizeUnchanged(listing, current)) {
            Fail(L"metadata", L"source-size metadata changed or became invalid after extraction");
            return kSizePolicy;
        }
    }

    // ---- cancel / retry / write-error probes (only on a proven-good entry) ----
    MilestoneF(L"milestone: validation done\n");
    const bool probes_exercised = (candidate >= 0);
    if (!probes_exercised) {
        Out(L"UnityObserverProbe: cancel/retry/write-error probes NOT EXERCISED "
            L"(no successful non-empty export)\n");
    } else {
        const int target = entries[static_cast<std::size_t>(candidate)].index;
        std::error_code remove_error;

        const std::wstring cancel_dest = (std::filesystem::path(output_dir) / L"cancel-probe.bin").wstring();
        std::filesystem::remove(std::filesystem::path(cancel_dest), remove_error);
        ProgressSink sink;
        const int rc = extract_entry(target, cancel_dest, 1, sink);
        if (rc != SER_USERABORT) {
            Fail(L"cancel", L"cancel did not yield SER_USERABORT");
            return kCancel;
        }
        if (!sink.cancellation_requested) {
            Fail(L"cancel", L"no callback was made, so cancellation was never requested");
            return kCancel;
        }
        if (sink.calls_after_cancel != 0 || sink.post_cancel_violation) {
            Fail(L"cancel", L"the module kept calling the progress callback after a cancel");
            return kCancel;
        }
        if (sink.protocol_violation || sink.over_cap) {
            Fail(L"cancel", L"callback protocol or cap violation during cancel");
            return kCancel;
        }
        if (FileExists(cancel_dest)) {
            Fail(L"cancel", L"partial destination survived a cancelled extraction");
            return kCancel;
        }

        const std::wstring repeat_dest = (std::filesystem::path(output_dir) / L"repeat-probe.bin").wstring();
        std::filesystem::remove(std::filesystem::path(repeat_dest), remove_error);
        ProgressSink repeat_sink;
        const int repeat_rc = extract_entry(target, repeat_dest, 0, repeat_sink);
        if (repeat_rc != SER_SUCCESS) {
            Fail(L"cancel", L"repeat extraction after cancel did not succeed");
            return kRepeatAfterCancel;
        }
        if (repeat_sink.protocol_violation || repeat_sink.over_cap || repeat_sink.post_cancel_violation ||
            repeat_sink.cancellation_requested || repeat_sink.calls_after_cancel != 0) {
            Fail(L"cancel", L"repeat extraction reported a callback violation");
            return kRepeatAfterCancel;
        }
        std::vector<unsigned char> repeat_bytes;
        bool repeat_too_big = false;
        if (!ReadFileCapped(repeat_dest, repeat_bytes, kMaxBytesPerFile, repeat_too_big) ||
            repeat_bytes != reference_bytes) {
            Fail(L"cancel", L"repeat output did not match the first successful export");
            return kRepeatAfterCancel;
        }

        // ---- write to a bad destination must be SER_ERROR_WRITE ----
        const std::wstring bad_dest =
            (std::filesystem::path(output_dir) / L"__missing_dir__" / L"write-error-probe.bin").wstring();
        ProgressSink write_sink;
        const int write_rc = extract_entry(target, bad_dest, 0, write_sink);
        if (write_rc != SER_ERROR_WRITE) {
            Fail(L"write-error", L"bad destination did not yield SER_ERROR_WRITE");
            return kWriteError;
        }
    }

    // ---- ExtractItem null-argument contracts ----
    {
        const int contract_index = probes_exercised ? entries[static_cast<std::size_t>(candidate)].index : 0;
        ExtractOperationParams op{};
        op.ItemIndex = contract_index;
        op.DestPath = nullptr;
        op.Callbacks.signalContext = nullptr;
        op.Callbacks.FileProgress = &ProgressCallback;
        if (api.cbs.ExtractItem(storage.handle, op) != SER_ERROR_SYSTEM) {
            Fail(L"extract-contract", L"null DestPath not rejected");
            return kNullContract;
        }
        const std::wstring scratch = (std::filesystem::path(output_dir) / L"null-callback.bin").wstring();
        ExtractOperationParams op2{};
        op2.ItemIndex = contract_index;
        op2.DestPath = scratch.c_str();
        op2.Callbacks.signalContext = nullptr;
        op2.Callbacks.FileProgress = nullptr;
        if (api.cbs.ExtractItem(storage.handle, op2) != SER_ERROR_SYSTEM) {
            Fail(L"extract-contract", L"null progress callback not rejected");
            return kNullContract;
        }
    }

    // ---- close / reopen repeatedly ----
    storage.Close();
    for (int round = 0; round < 3; ++round) {
        StorageOpenParams open{};
        open.StructSize = sizeof(StorageOpenParams);
        open.FilePath = input_path.c_str();
        HANDLE handle = nullptr;
        StorageGeneralInfo info{};
        if (api.cbs.OpenStorage(open, &handle, &info) != SOR_SUCCESS || handle == nullptr) {
            Fail(L"reopen", L"reopen failed");
            return kCloseReopen;
        }
        int count = 0;
        int terminal_rc = -1;
        if (api.cbs.PrepareFiles(handle) == TRUE) {
            StorageItemInfo item{};
            for (;;) {
                if (count > kMaxEntries) {
                    break;
                }
                const int gr = api.cbs.GetItem(handle, count, &item);
                if (gr != GET_ITEM_OK) {
                    terminal_rc = gr;
                    break;
                }
                ++count;
            }
        }
        api.cbs.CloseStorage(handle);
        if (!probe::TerminalEnumerationOk(terminal_rc, count, static_cast<int>(entries.size()))) {
            Fail(L"reopen", L"reopened enumeration did not terminate with NOMOREITEMS at the stable count");
            return kCloseReopen;
        }
    }

    // ---- unload ----
    api.unload();

    // ---- report ----
    std::string report;
    report += "{\n";
    report += "  \"schema\": \"unity-observer-host-report/v1\",\n";
    report += "  \"mode\": ";
    report += qualification_mode ? "\"qualification\"" : "\"game\"";
    report += ",\n";
    report += "  \"dll\": " + JsonStr(dll_path) + ",\n";
    report += "  \"input\": " + JsonStr(input_path) + ",\n";
    report += "  \"api_version\": " + std::to_string(api.api_version) + ",\n";
    report += "  \"module_version\": " + std::to_string(api.module_version) + ",\n";
    report += "  \"entry_count\": " + std::to_string(entries.size()) + ",\n";
    const bool partial = !qualification_mode && allowed_read_failures > 0;
    report += "  \"outcome\": ";
    report += partial ? "\"partial\"" : "\"complete\"";
    report += ",\n";
    if (qualification_mode) {
        report += "  \"expected_match\": true,\n";
    }
    report += "  \"probes_exercised\": ";
    report += probes_exercised ? "true" : "false";
    report += ",\n";
    report += "  \"reference_bytes\": " + std::to_string(reference_bytes.size()) + ",\n";
    if (!log_path.empty()) {
        report += "  \"log\": " + JsonStr(log_path) + ",\n";
    }
    report += "  \"no_conversion_before_extract\": \"not provable without backend instrumentation\",\n";
    report += "  \"packed_semantics\": \"source payload length or 0 (never encoded PNG)\",\n";
    report += "  \"entries\": [\n";
    for (std::size_t i = 0; i < entries.size(); ++i) {
        const EntryRec &entry = entries[i];
        report += "    {";
        report += "\"index\": " + std::to_string(entry.index);
        report += ", \"path\": " + JsonStr(entry.path);
        report += ", \"label\": " + JsonStr(entry.label);
        report += ", \"ext\": " + JsonStr(entry.ext);
        report += ", \"kind\": \"";
        report += probe::ResourceKindName(entry.kind);
        report += "\"";
        report += ", \"size\": " + std::to_string(entry.size);
        report += ", \"packed\": " + std::to_string(entry.packed);
        report += ", \"extract_rc\": " + std::to_string(entry.extract_rc);
        report += ", \"bytes\": " + std::to_string(entry.bytes);
        report += "}";
        if (i + 1 != entries.size()) {
            report += ",";
        }
        report += "\n";
    }
    report += "  ],\n";
    report += "  \"successes\": " + std::to_string(recorded_successes) + ",\n";
    report += "  \"allowed_read_failures\": " + std::to_string(allowed_read_failures) + "\n";
    report += "}\n";

    const std::wstring report_path = (std::filesystem::path(output_dir) / L"probe-report.json").wstring();
    if (!WriteUtf8File(report_path, report)) {
        Fail(L"report", L"could not write probe-report.json");
        return kUsage;
    }

    if (partial) {
        Out(L"UnityObserverProbe: PARTIAL mode=game entries=%zu successes=%d allowed-read-failures=%d "
            L"(not every asset converted)\n",
            entries.size(), recorded_successes, allowed_read_failures);
    } else {
        Out(L"UnityObserverProbe: PASS mode=%ls entries=%zu successes=%d failures=%d\n",
            qualification_mode ? L"qualification" : L"game", entries.size(), recorded_successes, allowed_read_failures);
    }
    MilestoneF(L"milestone: done exit=0\n");
    return kPass;
}
