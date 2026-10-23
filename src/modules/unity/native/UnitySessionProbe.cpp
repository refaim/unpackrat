// UnitySessionProbe.cpp
//
// TEST-ONLY native host for the frozen v1 UnitySession C ABI declared in
// unity_backend.h. It loads one absolute DLL with LoadLibraryExW, resolves the
// four session exports, and drives the behavioural contract end to end against
// a real fixture archive. It never launches a process/runtime, never builds a
// fake DLL, and never unloads the module (an AOT runtime pins itself).
//
// Usage:
//     UnitySessionProbe.exe <dll> <input> <outputDir> [expectedTextFile]
//
// Exit codes:
//     0  pass
//     2  bad command line
//     3  DLL load failure
//     7  a session export is missing            (RED baseline signal)
//    10  open null/argument contract failure
//    11  open output-zeroing contract failure
//    12  open of the real fixture failed or count <= 0
//    13  entry enumeration / size query failure
//    14  entry layout/canary violation or callback protocol violation
//    15  out-of-range index was not rejected
//    16  forged/zero handle was not rejected
//    17  per-entry extraction or output write failure
//    18  extracted text did not match the expected bytes
//    19  callback cancel did not return CANCELLED
//    20  callback write-reject did not return WRITE_FAILURE
//    21  callback-reentrant operations did not return BUSY
//    22  successful repeat export after reentrancy failed
//    23  close / stale-handle behaviour failure
//    24  second open returned an unstable count

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>

#include "unity_backend.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

namespace
{

    constexpr int kExitPass = 0;
    constexpr int kExitUsage = 2;
    constexpr int kExitLoad = 3;
    constexpr int kExitMissingExport = 7;
    constexpr int kExitOpenNull = 10;
    constexpr int kExitOpenZero = 11;
    constexpr int kExitOpenReal = 12;
    constexpr int kExitEnumerate = 13;
    constexpr int kExitLayout = 14;
    constexpr int kExitBadIndex = 15;
    constexpr int kExitForgedHandle = 16;
    constexpr int kExitExtract = 17;
    constexpr int kExitFixtureText = 18;
    constexpr int kExitCancel = 19;
    constexpr int kExitWriteReject = 20;
    constexpr int kExitBusy = 21;
    constexpr int kExitRepeatExport = 22;
    constexpr int kExitClose = 23;
    constexpr int kExitCountStable = 24;

    constexpr std::uint64_t kGarbageHandle = 0x123456789ABCDEF0ull;
    constexpr std::int32_t kGarbageCount = -987654321;
    constexpr std::uint16_t kCanaryUnit = 0xABCD;
    constexpr std::uint16_t kCanaryUnit2 = 0x1234;
    constexpr std::uint8_t kCanaryByte = 0xCD;
    constexpr std::int32_t kMaxChunk = 65536; // frozen per-callback byte bound

    struct Api
    {
        unpackrat_unity_open_fn open = nullptr;
        unpackrat_unity_get_entry_fn get_entry = nullptr;
        unpackrat_unity_export_fn export_fn = nullptr;
        unpackrat_unity_close_fn close = nullptr;
    };

    // Callback sink. `mode` selects accept(0)/cancel(1)/write-failure(2); a
    // reentrancy probe runs on the first real chunk.
    struct Sink
    {
        const Api *api = nullptr;
        std::uint64_t session = 0;
        int mode = 0;
        int calls = 0;      // every invocation
        int data_calls = 0; // invocations carrying bytes (length > 0)
        bool protocol_violation = false;
        bool probe_reentrant = false;
        std::vector<std::uint8_t> bytes;
        std::int32_t rc_get_entry = 0;
        std::int32_t rc_export = 0;
        std::int32_t rc_close = 0;
    };

    std::int32_t UNPACKRAT_UNITY_CDECL RejectCallback(void * /*context*/, const std::uint8_t * /*bytes*/,
                                                      std::int32_t /*length*/) noexcept
    {
        return 2; // write failure, used only for reentrant-export probing
    }

    // The callback is `noexcept`: no C++ exception may cross the C ABI. A genuine
    // allocation failure from `insert` therefore terminates the process (the
    // repository's fatal-OOM policy) rather than being swallowed to force a pass.
    std::int32_t UNPACKRAT_UNITY_CDECL SinkCallback(void *context, const std::uint8_t *bytes,
                                                    std::int32_t length) noexcept
    {
        Sink *const sink = static_cast<Sink *>(context);
        ++sink->calls;

        // Protocol boundary. A chunk is 1..65536 bytes; a zero-length chunk is a
        // legal no-op (and must not do pointer arithmetic on a null pointer). A
        // negative or oversize length, or a null pointer with a positive length, is
        // a violated protocol and is rejected as a write failure.
        if (length < 0 || length > kMaxChunk) {
            sink->protocol_violation = true;
            return 2;
        }
        if (length == 0) {
            return 0;
        }
        if (bytes == nullptr) {
            sink->protocol_violation = true;
            return 2;
        }
        ++sink->data_calls;
        if (sink->probe_reentrant && sink->data_calls == 1) {
            UnityEntry entry{};
            entry.struct_size = 32;
            std::uint16_t name[8] = {0};
            sink->rc_get_entry = sink->api->get_entry(sink->session, 0, &entry, name, 8);
            sink->rc_export = sink->api->export_fn(sink->session, 0, &RejectCallback, nullptr);
            sink->rc_close = sink->api->close(sink->session);
        }
        if (sink->mode == 1) {
            return 1; // cancel: this chunk is not stored
        }
        if (sink->mode == 2) {
            return 2; // write failure: this chunk is not stored
        }
        sink->bytes.insert(sink->bytes.end(), bytes, bytes + length);
        return 0;
    }

    void Fail(const wchar_t *stage, const wchar_t *detail)
    {
        std::fwprintf(stderr, L"UnitySessionProbe: FAIL [%ls] %ls\n", stage, detail);
    }

    int FailRc(const wchar_t *stage, const wchar_t *detail, int code, int exit_code)
    {
        std::fwprintf(stderr, L"UnitySessionProbe: FAIL [%ls] %ls (got %d)\n", stage, detail, code);
        return exit_code;
    }

    bool WideLength(const wchar_t *text, std::int32_t *out)
    {
        const std::size_t len = std::wcslen(text);
        if (len > 0x7fffffffu) {
            return false;
        }
        *out = static_cast<std::int32_t>(len);
        return true;
    }

    bool IsAbsolutePath(const wchar_t *path)
    {
        if (path[0] == L'\\' && path[1] == L'\\') {
            return true;
        }
        const bool drive = ((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) &&
                           path[1] == L':' && (path[2] == L'\\' || path[2] == L'/');
        return drive;
    }

    bool ReadAll(const std::filesystem::path &path, std::vector<std::uint8_t> *out)
    {
        std::ifstream stream(path, std::ios::binary);
        if (!stream) {
            return false;
        }
        out->assign(std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>());
        return true;
    }

    bool WriteAll(const std::filesystem::path &path, const std::vector<std::uint8_t> &data)
    {
        std::ofstream stream(path, std::ios::binary | std::ios::trunc);
        if (!stream) {
            return false;
        }
        stream.write(reinterpret_cast<const char *>(data.data()), static_cast<std::streamsize>(data.size()));
        return stream.good();
    }

    // Fills the first 32 bytes from `entry`, then verifies the trailing canary of
    // the backing buffer is untouched so no write spills past the frozen struct.
    bool EntryCanaryIntact(const std::uint8_t *raw, std::size_t size)
    {
        for (std::size_t i = 32; i < size; ++i) {
            if (raw[i] != kCanaryByte) {
                return false;
            }
        }
        return true;
    }

    // Returns true when every field other than struct_size is empty/zero.
    bool EntryIsEmpty(const UnityEntry &e)
    {
        return e.kind == 0 && e.collection_index == 0 && e.reserved == 0 && e.path_id == 0 && e.name_length == 0 &&
               e.reserved2 == 0;
    }

    // A valid-sized but fully non-zero entry, used to prove a failed lookup resets
    // the caller's output rather than leaving stale bytes in place.
    UnityEntry PoisonEntry()
    {
        UnityEntry e{};
        e.struct_size = 32;
        e.kind = 0x5A5A5A5A;
        e.collection_index = -1234567;
        e.reserved = 0xDEADBEEFu;
        e.path_id = -0x0112233445566778LL;
        e.name_length = 0x00ABCDEFu;
        e.reserved2 = 0xCAFEBABEu;
        return e;
    }

    std::int32_t CloseQuiet(const Api &api, std::uint64_t session)
    {
        return api.close(session);
    }

    // Validity of a cancel/write-reject probe result. Extracted into one helper so
    // the host and the direct callback regression test share exactly one predicate.
    bool RejectProbeClean(const Sink &sink)
    {
        // A failed export must stop after exactly one callback that carried data,
        // with no protocol violation and nothing stored. A zero-length callback is
        // legal on its own, but once a callback has cancelled or reported a write
        // failure the contract forbids every later callback, empty ones included.
        return sink.calls == 1 && sink.data_calls == 1 && !sink.protocol_violation && sink.bytes.empty();
    }

} // namespace

int wmain(int argc, wchar_t **argv)
{
    if (argc != 4 && argc != 5) {
        std::fwprintf(stderr, L"UnitySessionProbe: usage: UnitySessionProbe.exe "
                              L"<dll> <input> <outputDir> [expectedTextFile]\n");
        return kExitUsage;
    }

    const wchar_t *const dll_path = argv[1];
    const wchar_t *const input_path = argv[2];
    const wchar_t *const output_dir = argv[3];
    const wchar_t *const expected_path = (argc == 5) ? argv[4] : nullptr;
    const bool has_expected = (expected_path != nullptr) && (expected_path[0] != L'\0');

    if (expected_path != nullptr && !has_expected) {
        // A supplied-but-empty expected path must not silently disable the
        // exact-text comparison.
        Fail(L"arguments", L"<expectedTextFile> supplied but empty");
        return kExitUsage;
    }
    if (dll_path[0] == L'\0' || input_path[0] == L'\0' || output_dir[0] == L'\0') {
        Fail(L"arguments", L"empty <dll>/<input>/<outputDir> not allowed");
        return kExitUsage;
    }
    if (!IsAbsolutePath(dll_path)) {
        Fail(L"arguments", L"<dll> must be an absolute path");
        return kExitUsage;
    }

    std::int32_t input_len = 0;
    if (!WideLength(input_path, &input_len) || input_len <= 0) {
        Fail(L"arguments", L"<input> must be 1..32767 UTF-16 units");
        return kExitUsage;
    }

    std::error_code dir_ec;
    std::filesystem::create_directories(output_dir, dir_ec);

    HMODULE module = LoadLibraryExW(dll_path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr) {
        std::fwprintf(stderr, L"UnitySessionProbe: FAIL [LoadLibraryExW] err=%lu\n",
                      static_cast<unsigned long>(GetLastError()));
        return kExitLoad;
    }
    // Pin the image; an AOT runtime must never be unloaded.
    HMODULE pinned = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                       reinterpret_cast<LPCWSTR>(module), &pinned);

    Api api;
    api.open = reinterpret_cast<unpackrat_unity_open_fn>(GetProcAddress(module, "unpackrat_unity_open"));
    api.get_entry = reinterpret_cast<unpackrat_unity_get_entry_fn>(GetProcAddress(module, "unpackrat_unity_get_entry"));
    api.export_fn = reinterpret_cast<unpackrat_unity_export_fn>(GetProcAddress(module, "unpackrat_unity_export"));
    api.close = reinterpret_cast<unpackrat_unity_close_fn>(GetProcAddress(module, "unpackrat_unity_close"));

    if (api.open == nullptr || api.get_entry == nullptr || api.export_fn == nullptr || api.close == nullptr) {
        std::fwprintf(stderr,
                      L"UnitySessionProbe: FAIL [exports] missing session "
                      L"export(s): open=%d get_entry=%d export=%d close=%d\n",
                      api.open ? 1 : 0, api.get_entry ? 1 : 0, api.export_fn ? 1 : 0, api.close ? 1 : 0);
        std::fflush(stderr);
        return kExitMissingExport;
    }

    // Convert the input path to UTF-16 units (Windows wchar_t is 16-bit).
    std::vector<std::uint16_t> input_units(static_cast<std::size_t>(input_len));
    for (std::int32_t i = 0; i < input_len; ++i) {
        input_units[static_cast<std::size_t>(i)] = static_cast<std::uint16_t>(input_path[i]);
    }
    const std::uint16_t *const input_u16 = input_units.data();

    // --- open null/argument contract ---------------------------------------
    {
        std::uint64_t session = kGarbageHandle;
        std::int32_t count = kGarbageCount;
        const std::int32_t rc = api.open(nullptr, input_len, &session, &count);
        if (rc != UNITY_INVALID_ARGUMENT) {
            return FailRc(L"open null path", L"expected INVALID_ARGUMENT", rc, kExitOpenNull);
        }
        if (session != 0 || count != 0) {
            Fail(L"open null path", L"non-null outputs not zeroed");
            return kExitOpenNull;
        }
        count = kGarbageCount;
        if (api.open(input_u16, input_len, nullptr, &count) != UNITY_INVALID_ARGUMENT || count != 0) {
            Fail(L"open null session_out", L"expected INVALID_ARGUMENT and zeroed count");
            return kExitOpenNull;
        }
        session = kGarbageHandle;
        if (api.open(input_u16, input_len, &session, nullptr) != UNITY_INVALID_ARGUMENT || session != 0) {
            Fail(L"open null count_out", L"expected INVALID_ARGUMENT and zeroed session");
            return kExitOpenNull;
        }
        session = kGarbageHandle;
        count = kGarbageCount;
        if (api.open(input_u16, -1, &session, &count) != UNITY_INVALID_ARGUMENT || session != 0 || count != 0) {
            Fail(L"open negative length", L"expected INVALID_ARGUMENT and zeroed outputs");
            return kExitOpenNull;
        }
        session = kGarbageHandle;
        count = kGarbageCount;
        if (api.open(input_u16, 0, &session, &count) != UNITY_INVALID_ARGUMENT || session != 0 || count != 0) {
            Fail(L"open zero length", L"expected INVALID_ARGUMENT and zeroed outputs");
            return kExitOpenNull;
        }
    }

    // --- open real fixture: OK, nonzero handle, positive count --------------
    std::uint64_t session = 0;
    std::int32_t count = 0;
    {
        const std::int32_t rc = api.open(input_u16, input_len, &session, &count);
        if (rc != UNITY_OK) {
            return FailRc(L"open fixture", L"expected OK", rc, kExitOpenReal);
        }
        if (session == 0) {
            Fail(L"open fixture", L"returned zero handle");
            return kExitOpenReal;
        }
        if (count <= 0) {
            std::fwprintf(stderr, L"UnitySessionProbe: FAIL [open fixture] count=%d\n", count);
            return kExitOpenReal;
        }
        std::wprintf(L"UnitySessionProbe: open ok, count=%d\n", count);
    }

    // --- enumerate: size query, name copy, layout, bounds -------------------
    for (std::int32_t index = 0; index < count; ++index) {
        alignas(8) std::uint8_t raw[64];
        std::memset(raw, kCanaryByte, sizeof(raw));
        std::memset(raw, 0, 32);
        UnityEntry *const entry = reinterpret_cast<UnityEntry *>(raw);
        entry->struct_size = 32;

        const std::int32_t qrc = api.get_entry(session, index, entry, nullptr, 0);
        if (qrc != UNITY_OK) {
            std::fwprintf(stderr, L"UnitySessionProbe: FAIL [query] index=%d rc=%d\n", index, qrc);
            return kExitEnumerate;
        }
        if (entry->struct_size != 32 || entry->reserved != 0 || entry->reserved2 != 0) {
            Fail(L"query metadata", L"bad struct_size/reserved fields");
            return kExitEnumerate;
        }
        if (entry->kind != UNITY_KIND_TEXTURE_PNG && entry->kind != UNITY_KIND_TEXT_RAW) {
            std::fwprintf(stderr, L"UnitySessionProbe: FAIL [query] index=%d kind=%d\n", index, entry->kind);
            return kExitEnumerate;
        }
        if (!EntryCanaryIntact(raw, sizeof(raw))) {
            Fail(L"query canary", L"write spilled past 32 bytes");
            return kExitLayout;
        }
        const std::uint32_t name_len = entry->name_length;

        // Read the name twice into buffers with different fill bytes: the
        // copied units must agree, which proves each unit was written without
        // relying on a sentinel that a real name could collide with. A trailing
        // NUL is not expected, so the pads after name_length must survive.
        std::vector<std::uint16_t> name_a(name_len + 4, kCanaryUnit);
        std::vector<std::uint16_t> name_b(name_len + 4, kCanaryUnit2);
        UnityEntry full_a{};
        full_a.struct_size = 32;
        UnityEntry full_b{};
        full_b.struct_size = 32;
        const std::int32_t nrc_a =
            api.get_entry(session, index, &full_a, name_a.data(), static_cast<std::int32_t>(name_len));
        const std::int32_t nrc_b =
            api.get_entry(session, index, &full_b, name_b.data(), static_cast<std::int32_t>(name_len));
        if (nrc_a != UNITY_OK || nrc_b != UNITY_OK || full_a.name_length != name_len ||
            full_b.name_length != name_len) {
            std::fwprintf(stderr, L"UnitySessionProbe: FAIL [name] index=%d rc=%d/%d\n", index, nrc_a, nrc_b);
            return kExitEnumerate;
        }
        for (std::uint32_t i = 0; i < name_len; ++i) {
            if (name_a[i] != name_b[i]) {
                Fail(L"name copy", L"name unit not consistently written");
                return kExitEnumerate;
            }
        }
        for (std::uint32_t i = name_len; i < name_len + 4; ++i) {
            if (name_a[i] != kCanaryUnit || name_b[i] != kCanaryUnit2) {
                Fail(L"name terminator", L"wrote past name_length");
                return kExitLayout;
            }
        }
    }

    // --- get_entry argument/range rejections --------------------------------
    {
        UnityEntry entry{};
        entry.struct_size = 32;
        std::uint16_t name_probe[4] = {kCanaryUnit, kCanaryUnit, 0, 0};

        // Bad struct_size (16) => INVALID_ARGUMENT, and nothing may be written
        // from the declared boundary (offset 16) onward.
        alignas(8) std::uint8_t short_raw[64];
        std::memset(short_raw, kCanaryByte, sizeof(short_raw));
        UnityEntry *const short_entry = reinterpret_cast<UnityEntry *>(short_raw);
        short_entry->struct_size = 16;
        if (api.get_entry(session, 0, short_entry, name_probe, 4) != UNITY_INVALID_ARGUMENT) {
            Fail(L"get_entry size", L"bad struct_size accepted");
            return kExitLayout;
        }
        for (std::size_t i = 16; i < sizeof(short_raw); ++i) {
            if (short_raw[i] != kCanaryByte) {
                Fail(L"get_entry size", L"wrote past declared struct_size 16");
                return kExitLayout;
            }
        }

        // Null name with positive capacity => INVALID_ARGUMENT.
        if (api.get_entry(session, 0, &entry, nullptr, 4) != UNITY_INVALID_ARGUMENT) {
            Fail(L"get_entry null name", L"null name with capacity accepted");
            return kExitEnumerate;
        }
        // Negative capacity => INVALID_ARGUMENT.
        if (api.get_entry(session, 0, &entry, name_probe, -1) != UNITY_INVALID_ARGUMENT) {
            Fail(L"get_entry neg capacity", L"negative capacity accepted");
            return kExitEnumerate;
        }

        // Buffers too small => BUFFER_TOO_SMALL, no partial write.
        UnityEntry q{};
        q.struct_size = 32;
        if (api.get_entry(session, 0, &q, nullptr, 0) == UNITY_OK && q.name_length >= 2) {
            std::vector<std::uint16_t> small(q.name_length + 4, kCanaryUnit);
            UnityEntry q2{};
            q2.struct_size = 32;
            const std::int32_t src =
                api.get_entry(session, 0, &q2, small.data(), static_cast<std::int32_t>(q.name_length - 1));
            if (src != UNITY_BUFFER_TOO_SMALL || q2.name_length != q.name_length) {
                std::fwprintf(stderr, L"UnitySessionProbe: FAIL [buffer small] rc=%d\n", src);
                return kExitEnumerate;
            }
            if (std::ranges::any_of(small, [](const std::uint16_t unit) { return unit != kCanaryUnit; })) {
                Fail(L"buffer small", L"partial name write on failure");
                return kExitLayout;
            }
        }
    }

    // --- bad index => OUT_OF_RANGE with empty metadata ----------------------
    {
        const std::int32_t probes[] = {-1, count, count + 1000};
        for (std::int32_t idx : probes) {
            UnityEntry entry = PoisonEntry();
            const std::int32_t rc = api.get_entry(session, idx, &entry, nullptr, 0);
            if (rc != UNITY_OUT_OF_RANGE) {
                std::fwprintf(stderr, L"UnitySessionProbe: FAIL [bad index] idx=%d rc=%d\n", idx, rc);
                return kExitBadIndex;
            }
            if (entry.struct_size != 32 || !EntryIsEmpty(entry)) {
                Fail(L"bad index metadata", L"failed lookup did not reset entry to empty/32");
                return kExitBadIndex;
            }
        }
    }

    // --- forged / zero handle => INVALID_HANDLE -----------------------------
    {
        const std::uint64_t forged[] = {0, session ^ 0xFFFFull, session + 0x9E3779B97F4A7C15ull, 0x8000000000000000ull};
        for (std::uint64_t handle : forged) {
            if (handle == session) {
                continue;
            }
            UnityEntry entry = PoisonEntry();
            if (api.get_entry(handle, 0, &entry, nullptr, 0) != UNITY_INVALID_HANDLE) {
                Fail(L"forged handle get_entry", L"not rejected");
                return kExitForgedHandle;
            }
            if (entry.struct_size != 32 || !EntryIsEmpty(entry)) {
                Fail(L"forged handle metadata", L"failed lookup did not reset entry to empty/32");
                return kExitForgedHandle;
            }
            if (api.export_fn(handle, 0, &RejectCallback, nullptr) != UNITY_INVALID_HANDLE) {
                Fail(L"forged handle export", L"not rejected");
                return kExitForgedHandle;
            }
            if (api.close(handle) != UNITY_INVALID_HANDLE) {
                Fail(L"forged handle close", L"not rejected");
                return kExitForgedHandle;
            }
        }
    }

    // --- per-entry extraction into safe generated filenames -----------------
    std::vector<std::vector<std::uint8_t>> extracted(static_cast<std::size_t>(count));
    std::int32_t probe_index = -1;
    for (std::int32_t index = 0; index < count; ++index) {
        Sink sink;
        sink.api = &api;
        sink.session = session;
        sink.mode = 0;
        const std::int32_t rc = api.export_fn(session, index, &SinkCallback, &sink);
        if (rc != UNITY_OK) {
            std::fwprintf(stderr, L"UnitySessionProbe: FAIL [export] index=%d rc=%d\n", index, rc);
            return kExitExtract;
        }
        if (sink.protocol_violation) {
            std::fwprintf(stderr, L"UnitySessionProbe: FAIL [export protocol] index=%d\n", index);
            return kExitLayout;
        }
        // Generated, safe filename: index and kind only; the untrusted asset
        // name is never used for a path.
        wchar_t file_name[64];
        UnityEntry info{};
        info.struct_size = 32;
        std::int32_t kind = 0;
        if (api.get_entry(session, index, &info, nullptr, 0) == UNITY_OK) {
            kind = info.kind;
        }
        std::swprintf(file_name, 64, L"entry_%d_kind%d.bin", index, kind);
        const std::filesystem::path out_path = std::filesystem::path(output_dir) / file_name;
        if (!WriteAll(out_path, sink.bytes)) {
            Fail(L"export write", L"cannot write extracted file");
            return kExitExtract;
        }
        extracted[static_cast<std::size_t>(index)] = std::move(sink.bytes);
        // Callback-protocol probes need a genuinely non-empty entry; an empty
        // text asset is legal and produces no callbacks.
        if (probe_index < 0 && !extracted[static_cast<std::size_t>(index)].empty()) {
            probe_index = index;
        }
        std::wprintf(L"UnitySessionProbe: entry %d extracted %zu bytes -> %ls\n", index,
                     extracted[static_cast<std::size_t>(index)].size(), file_name);
    }

    // --- optional exact fixture-text comparison -----------------------------
    if (has_expected) {
        std::vector<std::uint8_t> expected;
        if (!ReadAll(expected_path, &expected)) {
            Fail(L"expected", L"cannot read expected file");
            return kExitFixtureText;
        }
        // Empty expected bytes match an empty entry.
        const bool matched = std::ranges::any_of(
            extracted, [&expected](const std::vector<std::uint8_t> &data) { return data == expected; });
        if (!matched) {
            Fail(L"fixture text", L"no entry matches expected bytes");
            return kExitFixtureText;
        }
    }

    if (probe_index < 0) {
        // Every entry legitimately produced zero bytes, so the callback
        // protocol probes cannot run. Report that honestly instead of claiming
        // them as passes; the root's non-empty fixture exercises all of them.
        std::wprintf(L"UnitySessionProbe: NOTE callback probes NOT EXERCISED "
                     L"(no non-empty entry in this archive)\n");
    } else {
        const std::vector<std::uint8_t> &reference = extracted[static_cast<std::size_t>(probe_index)];

        // --- callback cancel => CANCELLED, exactly one chunk, no writes ------
        {
            Sink sink;
            sink.api = &api;
            sink.session = session;
            sink.mode = 1;
            const std::int32_t rc = api.export_fn(session, probe_index, &SinkCallback, &sink);
            if (rc != UNITY_CANCELLED) {
                std::fwprintf(stderr, L"UnitySessionProbe: FAIL [cancel] rc=%d\n", rc);
                return kExitCancel;
            }
            if (!RejectProbeClean(sink)) {
                std::fwprintf(stderr,
                              L"UnitySessionProbe: FAIL [cancel] calls=%d "
                              L"data_calls=%d protocol=%d stored=%zu\n",
                              sink.calls, sink.data_calls, sink.protocol_violation ? 1 : 0, sink.bytes.size());
                return kExitCancel;
            }
        }

        // --- callback write-reject => WRITE_FAILURE, no later writes ---------
        {
            Sink sink;
            sink.api = &api;
            sink.session = session;
            sink.mode = 2;
            const std::int32_t rc = api.export_fn(session, probe_index, &SinkCallback, &sink);
            if (rc != UNITY_WRITE_FAILURE) {
                std::fwprintf(stderr, L"UnitySessionProbe: FAIL [write reject] rc=%d\n", rc);
                return kExitWriteReject;
            }
            if (!RejectProbeClean(sink)) {
                std::fwprintf(stderr,
                              L"UnitySessionProbe: FAIL [write reject] calls=%d "
                              L"data_calls=%d protocol=%d stored=%zu\n",
                              sink.calls, sink.data_calls, sink.protocol_violation ? 1 : 0, sink.bytes.size());
                return kExitWriteReject;
            }
        }

        // --- callback-reentrant ops => BUSY ---------------------------------
        {
            Sink sink;
            sink.api = &api;
            sink.session = session;
            sink.mode = 0;
            sink.probe_reentrant = true;
            const std::int32_t rc = api.export_fn(session, probe_index, &SinkCallback, &sink);
            if (rc != UNITY_OK) {
                std::fwprintf(stderr,
                              L"UnitySessionProbe: FAIL [reentrant outer] "
                              L"rc=%d\n",
                              rc);
                return kExitBusy;
            }
            if (sink.protocol_violation) {
                Fail(L"reentrant protocol", L"callback protocol violated");
                return kExitLayout;
            }
            if (sink.rc_get_entry != UNITY_BUSY || sink.rc_export != UNITY_BUSY || sink.rc_close != UNITY_BUSY) {
                std::fwprintf(stderr,
                              L"UnitySessionProbe: FAIL [reentrant] get_entry=%d "
                              L"export=%d close=%d\n",
                              sink.rc_get_entry, sink.rc_export, sink.rc_close);
                return kExitBusy;
            }
            if (sink.bytes != reference) {
                Fail(L"reentrant bytes", L"outer export bytes diverged");
                return kExitBusy;
            }
        }

        // --- successful repeat export after BUSY ----------------------------
        {
            Sink sink;
            sink.api = &api;
            sink.session = session;
            sink.mode = 0;
            const std::int32_t rc = api.export_fn(session, probe_index, &SinkCallback, &sink);
            if (rc != UNITY_OK || sink.bytes != reference) {
                Fail(L"repeat export", L"export after BUSY failed or diverged");
                return kExitRepeatExport;
            }
            if (sink.protocol_violation) {
                Fail(L"repeat export protocol", L"callback protocol violated");
                return kExitLayout;
            }
        }
    }

    // --- second open: count must be stable ----------------------------------
    {
        std::uint64_t session_b = 0;
        std::int32_t count_b = 0;
        const std::int32_t rc = api.open(input_u16, input_len, &session_b, &count_b);
        if (rc != UNITY_OK || session_b == 0) {
            Fail(L"second open", L"failed or zero handle");
            return kExitCountStable;
        }
        if (count_b != count) {
            std::fwprintf(stderr, L"UnitySessionProbe: FAIL [count stable] %d != %d\n", count_b, count);
            return kExitCountStable;
        }
        if (CloseQuiet(api, session_b) != UNITY_OK) {
            Fail(L"second close", L"close of second session failed");
            return kExitCountStable;
        }
    }

    // --- close and stale-handle rejection -----------------------------------
    {
        if (api.close(session) != UNITY_OK) {
            Fail(L"close", L"close of primary session failed");
            return kExitClose;
        }
        std::uint64_t stale = session;
        if (api.close(stale) != UNITY_INVALID_HANDLE) {
            Fail(L"close twice", L"second close not rejected");
            return kExitClose;
        }
        UnityEntry entry{};
        entry.struct_size = 32;
        if (api.get_entry(stale, 0, &entry, nullptr, 0) != UNITY_INVALID_HANDLE) {
            Fail(L"stale get_entry", L"not rejected");
            return kExitClose;
        }
        if (api.export_fn(stale, 0, &RejectCallback, nullptr) != UNITY_INVALID_HANDLE) {
            Fail(L"stale export", L"not rejected");
            return kExitClose;
        }
    }

    std::wprintf(L"UnitySessionProbe: PASS count=%d entries, all session "
                 L"contract checks green\n",
                 count);
    std::fflush(stdout);
    return kExitPass;
}
