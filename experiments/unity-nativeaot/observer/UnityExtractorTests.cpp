// UnityExtractorTests.cpp
//
// Behavioural unit tests for the experimental Unity Observer adapter. The adapter
// translation unit is compiled unchanged into this test binary together with the real
// common Observer layer (src/dll.cpp and src/archive.cpp); only the four frozen
// UnitySession C ABI entry points are replaced by in-process stubs. The
// LoadSubModule/OpenStorage/PrepareFiles/GetItem/ExtractItem callbacks exercised here
// are therefore the real shipping functions, driven through the real archive lifetime
// and custom-extract path. A stubbed C ABI is the unit boundary only; it is not
// evidence about the real NativeAOT backend.

#include "api.h"
#include "archive.h"
#include "modules/extractor.h"
#include "unity_backend.h"

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <malloc.h>
#include <memory>
#include <new>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>

extern "C" int MODULE_EXPORT LoadSubModule(ModuleLoadParameters *);
extern "C" void MODULE_EXPORT UnloadSubModule();

namespace
{
    // ---- UnitySession C ABI stubs -------------------------------------------------

    struct stub_entry
    {
        int kind = UNITY_KIND_TEXT_RAW;
        std::u16string name;
        std::string payload;
    };

    struct stub_state
    {
        int open_result = UNITY_OK;
        std::uint64_t token = 0x1122334455667788ull;
        std::optional<int> count_override;
        std::vector<stub_entry> entries;

        int query_result = UNITY_OK;
        int copy_result = UNITY_OK;
        std::optional<int> struct_size_out;
        std::optional<int> reserved_out;
        std::optional<int> reserved2_out;
        std::optional<int> query_kind;
        std::optional<std::uint32_t> query_name_length;

        int export_result = UNITY_OK;
        bool export_call_callback = true;
        std::string export_payload;
        std::size_t export_chunk = 65536;
        bool export_prepend_empty = false;
        bool export_bad_length = false;
        std::int32_t export_bad_length_value = 0;
        bool export_null_positive = false;

        int close_result = UNITY_OK;

        std::vector<std::int64_t> source_sizes;
        int source_size_result = UNITY_OK;

        std::vector<std::u16string> original_paths;
        int entry_path_result = UNITY_OK;
        int entry_path_copy_result = UNITY_OK;
        std::optional<std::int32_t> entry_path_length_out;
        std::optional<std::int32_t> entry_path_copy_length_out;

        // Per-entry output extension metadata (dotless lower-case ASCII). The additive
        // getter mirrors get_entry_path: a null buffer is a size query.
        std::vector<std::u16string> extensions;
        int entry_extension_result = UNITY_OK;
        int entry_extension_copy_result = UNITY_OK;
        std::optional<std::int32_t> entry_extension_length_out;
        std::optional<std::int32_t> entry_extension_copy_length_out;

        int open_calls = 0;
        int size_query_calls = 0;
        int copy_calls = 0;
        int export_calls = 0;
        int close_calls = 0;
        int source_size_calls = 0;
        int entry_path_query_calls = 0;
        int entry_path_copy_calls = 0;
        int entry_extension_query_calls = 0;
        int entry_extension_copy_calls = 0;
    };

    stub_state *g_stub = nullptr;

    struct stub_binding
    {
        stub_state state;

        stub_binding()
        {
            g_stub = &state;
        }
        ~stub_binding()
        {
            g_stub = nullptr;
        }

        stub_binding(const stub_binding &) = delete;
        stub_binding &operator=(const stub_binding &) = delete;

        stub_state *operator->() noexcept
        {
            return &state;
        }
    };

    // ---- test-only allocation fault injection --------------------------------------
    // One-shot, scope-bounded failure of the next dynamic allocation, armed only from
    // inside stub_open so it targets exactly the first allocation the adapter performs
    // after the backend has produced a live session. The replaced allocator below is a
    // byte-for-byte malloc/free equivalent while unarmed, so production behaviour is
    // unchanged; the fault can only ever fire once and never escapes the owning test.
    bool fault_arm_on_open = false;
    bool fault_armed = false;

    class open_allocation_fault
    {
      public:
        open_allocation_fault()
        {
            fault_arm_on_open = true;
        }

        ~open_allocation_fault()
        {
            fault_arm_on_open = false;
            fault_armed = false;
        }

        open_allocation_fault(const open_allocation_fault &) = delete;
        open_allocation_fault &operator=(const open_allocation_fault &) = delete;
    };

    // ---- test-only live-heap accounting --------------------------------------------
    // The adapter translation unit is compiled unchanged into this binary, so the global
    // new/delete replacements below observe every allocation the real adapter makes. Live bytes
    // are tracked for the whole process lifetime, never only inside a window, so the counter
    // stays balanced and cannot underflow when memory allocated before a window is freed inside
    // it. _msize reports the actual malloc block size, keeping new/delete and the malloc/free
    // paths symmetric. A separate flag records the peak only while a measurement window is open,
    // so ordinary runs pay only the integer add/subtract.
    std::size_t g_live_bytes = 0;
    std::size_t g_peak_bytes = 0;
    bool g_measuring_heap = false;

    // Opens a peak-tracking window for the current scope. The baseline is the live heap at
    // construction, so peak_delta() is the incremental live heap observed inside the window.
    class heap_window
    {
      public:
        heap_window() noexcept : baseline_(g_live_bytes)
        {
            g_peak_bytes = g_live_bytes;
            g_measuring_heap = true;
        }

        ~heap_window()
        {
            g_measuring_heap = false;
        }

        [[nodiscard]] std::size_t peak_delta() const noexcept
        {
            return g_peak_bytes - baseline_;
        }

        [[nodiscard]] std::size_t peak_over_current() const noexcept
        {
            return g_peak_bytes - g_live_bytes;
        }

        heap_window(const heap_window &) = delete;
        heap_window &operator=(const heap_window &) = delete;

      private:
        std::size_t baseline_ = 0;
    };

    std::int32_t stub_open(const std::uint16_t *path, const std::int32_t path_length, std::uint64_t *session_out,
                           std::int32_t *count_out)
    {
        stub_state &s = *g_stub;
        ++s.open_calls;
        if (session_out != nullptr) {
            *session_out = 0;
        }
        if (count_out != nullptr) {
            *count_out = 0;
        }
        if (session_out == nullptr || count_out == nullptr) {
            return UNITY_INVALID_ARGUMENT;
        }
        if (path == nullptr || path_length <= 0) {
            return UNITY_INVALID_ARGUMENT;
        }
        if (s.open_result != UNITY_OK) {
            return s.open_result;
        }
        if (fault_arm_on_open) {
            fault_armed = true;
        }
        *session_out = s.token;
        *count_out = s.count_override.value_or(static_cast<std::int32_t>(s.entries.size()));
        return UNITY_OK;
    }

    std::int32_t stub_get_entry(const std::uint64_t session, const std::int32_t index, UnityEntry *entry,
                                std::uint16_t *name, const std::int32_t name_capacity)
    {
        stub_state &s = *g_stub;
        if (entry == nullptr || entry->struct_size != 32) {
            return UNITY_INVALID_ARGUMENT;
        }
        *entry = UnityEntry{};
        entry->struct_size = 32;
        if (name_capacity < 0 || (name == nullptr && name_capacity > 0)) {
            return UNITY_INVALID_ARGUMENT;
        }
        if (session != s.token) {
            return UNITY_INVALID_HANDLE;
        }
        if (index < 0 || index >= static_cast<std::int32_t>(s.entries.size())) {
            return UNITY_OUT_OF_RANGE;
        }
        const stub_entry &e = s.entries[static_cast<std::size_t>(index)];
        entry->kind = s.query_kind.value_or(e.kind);
        entry->reserved = static_cast<std::uint32_t>(s.reserved_out.value_or(0));
        entry->reserved2 = static_cast<std::uint32_t>(s.reserved2_out.value_or(0));
        entry->struct_size = static_cast<std::uint32_t>(s.struct_size_out.value_or(32));
        entry->name_length =
            s.query_name_length.value_or(static_cast<std::uint32_t>(e.name.size()));

        if (name == nullptr) {
            ++s.size_query_calls;
            return s.query_result;
        }
        ++s.copy_calls;
        if (s.copy_result != UNITY_OK) {
            return s.copy_result;
        }
        if (name_capacity < static_cast<std::int32_t>(e.name.size())) {
            return UNITY_BUFFER_TOO_SMALL;
        }
        if (!e.name.empty()) {
            std::memcpy(name, e.name.data(), e.name.size() * sizeof(std::uint16_t));
        }
        return UNITY_OK;
    }

    std::int32_t stub_export(const std::uint64_t session, const std::int32_t index,
                             const unpackrat_unity_write_callback write, void *const context)
    {
        stub_state &s = *g_stub;
        ++s.export_calls;
        if (write == nullptr) {
            return UNITY_INVALID_ARGUMENT;
        }
        if (session != s.token) {
            return UNITY_INVALID_HANDLE;
        }
        if (index < 0 || index >= static_cast<std::int32_t>(s.entries.size())) {
            return UNITY_OUT_OF_RANGE;
        }
        if (!s.export_call_callback) {
            return s.export_result;
        }

        const auto deliver = [&](const std::uint8_t *bytes, const std::int32_t length) -> std::int32_t {
            const std::int32_t outcome = write(context, bytes, length);
            if (outcome == 0) {
                return UNITY_OK;
            }
            return outcome == 1 ? UNITY_CANCELLED : UNITY_WRITE_FAILURE;
        };

        if (s.export_prepend_empty) {
            const std::int32_t outcome = deliver(nullptr, 0);
            if (outcome != UNITY_OK) {
                return outcome;
            }
        }
        if (s.export_bad_length) {
            const std::uint8_t probe = 0x5A;
            return deliver(&probe, s.export_bad_length_value);
        }
        if (s.export_null_positive) {
            return deliver(nullptr, 4);
        }

        const std::string &payload = s.entries[static_cast<std::size_t>(index)].payload;
        const std::uint8_t *const base = reinterpret_cast<const std::uint8_t *>(payload.data());
        for (std::size_t offset = 0; offset < payload.size(); offset += s.export_chunk) {
            const std::size_t length = std::min(s.export_chunk, payload.size() - offset);
            const std::int32_t outcome = deliver(base + offset, static_cast<std::int32_t>(length));
            if (outcome != UNITY_OK) {
                return outcome;
            }
        }
        return s.export_result;
    }

    std::int32_t stub_get_source_size(const std::uint64_t session, const std::int32_t index,
                                      std::int64_t *const size_out)
    {
        stub_state &s = *g_stub;
        ++s.source_size_calls;
        if (size_out == nullptr) {
            return UNITY_INVALID_ARGUMENT;
        }
        *size_out = 0;
        if (session != s.token) {
            return UNITY_INVALID_HANDLE;
        }
        if (index < 0 || index >= static_cast<std::int32_t>(s.entries.size())) {
            return UNITY_OUT_OF_RANGE;
        }
        if (s.source_size_result != UNITY_OK) {
            return s.source_size_result;
        }
        if (index < static_cast<std::int32_t>(s.source_sizes.size())) {
            *size_out = s.source_sizes[static_cast<std::size_t>(index)];
        }
        return UNITY_OK;
    }

    std::int32_t stub_close(const std::uint64_t session)
    {
        stub_state &s = *g_stub;
        ++s.close_calls;
        if (session != s.token) {
            return UNITY_INVALID_HANDLE;
        }
        return s.close_result;
    }

    std::int32_t stub_get_entry_path(const std::uint64_t session, const std::int32_t index,
                                     std::uint16_t *const path, const std::int32_t path_capacity,
                                     std::int32_t *const path_length)
    {
        stub_state &s = *g_stub;
        if (path_length == nullptr) {
            return UNITY_INVALID_ARGUMENT;
        }
        *path_length = 0;
        if (path_capacity < 0 || (path == nullptr && path_capacity > 0)) {
            return UNITY_INVALID_ARGUMENT;
        }
        if (session != s.token) {
            return UNITY_INVALID_HANDLE;
        }
        if (index < 0 || index >= static_cast<std::int32_t>(s.entries.size())) {
            return UNITY_OUT_OF_RANGE;
        }
        if (s.entry_path_result != UNITY_OK) {
            return s.entry_path_result;
        }
        const std::u16string original =
            index < static_cast<std::int32_t>(s.original_paths.size())
                ? s.original_paths[static_cast<std::size_t>(index)]
                : std::u16string{};
        if (path == nullptr) {
            *path_length = s.entry_path_length_out.value_or(static_cast<std::int32_t>(original.size()));
            ++s.entry_path_query_calls;
            return UNITY_OK;
        }
        ++s.entry_path_copy_calls;
        *path_length = s.entry_path_copy_length_out.value_or(static_cast<std::int32_t>(original.size()));
        if (s.entry_path_copy_result != UNITY_OK) {
            return s.entry_path_copy_result;
        }
        if (path_capacity < static_cast<std::int32_t>(original.size())) {
            return UNITY_BUFFER_TOO_SMALL;
        }
        if (!original.empty()) {
            std::memcpy(path, original.data(), original.size() * sizeof(std::uint16_t));
        }
        return UNITY_OK;
    }

    std::int32_t stub_get_entry_extension(const std::uint64_t session, const std::int32_t index,
                                          std::uint16_t *const extension, const std::int32_t extension_capacity,
                                          std::int32_t *const extension_length)
    {
        stub_state &s = *g_stub;
        if (extension_length == nullptr) {
            return UNITY_INVALID_ARGUMENT;
        }
        *extension_length = 0;
        if (extension_capacity < 0 || (extension == nullptr && extension_capacity > 0)) {
            return UNITY_INVALID_ARGUMENT;
        }
        if (session != s.token) {
            return UNITY_INVALID_HANDLE;
        }
        if (index < 0 || index >= static_cast<std::int32_t>(s.entries.size())) {
            return UNITY_OUT_OF_RANGE;
        }
        if (s.entry_extension_result != UNITY_OK) {
            return s.entry_extension_result;
        }
        const std::u16string declared =
            index < static_cast<std::int32_t>(s.extensions.size())
                ? s.extensions[static_cast<std::size_t>(index)]
                : std::u16string{};
        if (extension == nullptr) {
            *extension_length =
                s.entry_extension_length_out.value_or(static_cast<std::int32_t>(declared.size()));
            ++s.entry_extension_query_calls;
            return UNITY_OK;
        }
        ++s.entry_extension_copy_calls;
        *extension_length =
            s.entry_extension_copy_length_out.value_or(static_cast<std::int32_t>(declared.size()));
        if (s.entry_extension_copy_result != UNITY_OK) {
            return s.entry_extension_copy_result;
        }
        if (extension_capacity < static_cast<std::int32_t>(declared.size())) {
            return UNITY_BUFFER_TOO_SMALL;
        }
        if (!declared.empty()) {
            std::memcpy(extension, declared.data(), declared.size() * sizeof(std::uint16_t));
        }
        return UNITY_OK;
    }

    // ---- Observer harness helpers -------------------------------------------------

    struct loaded_module
    {
        module_cbs api{};
        GUID id{};
        DWORD version = 0;
        DWORD api_version = 0;
        bool loaded = false;

        loaded_module()
        {
            ModuleLoadParameters params{};
            params.StructSize = sizeof(params);
            params.Settings = L"";
            REQUIRE(::LoadSubModule(&params) == TRUE);
            api = params.ApiFuncs;
            id = params.ModuleId;
            version = params.ModuleVersion;
            api_version = params.ApiVersion;
            loaded = true;
        }

        ~loaded_module()
        {
            if (loaded) {
                ::UnloadSubModule();
            }
        }

        loaded_module(const loaded_module &) = delete;
        loaded_module &operator=(const loaded_module &) = delete;
    };

    struct temp_archive
    {
        std::filesystem::path path;

        explicit temp_archive(const std::string &bytes = "unity")
        {
            static int counter = 0;
            path = std::filesystem::temp_directory_path() /
                   ("unpackrat-unity-adapter-" + std::to_string(::GetCurrentProcessId()) + "-" +
                    std::to_string(counter++) + ".bin");
            std::ofstream out(path, std::ios::binary | std::ios::trunc);
            REQUIRE(out.is_open());
            out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
        }

        ~temp_archive()
        {
            std::error_code error;
            std::filesystem::remove(path, error);
        }

        temp_archive(const temp_archive &) = delete;
        temp_archive &operator=(const temp_archive &) = delete;
    };

    int module_open(const loaded_module &mod, const std::filesystem::path &path, HANDLE *storage_out = nullptr)
    {
        alignas(std::max_align_t) StorageGeneralInfo info{};
        HANDLE storage = nullptr;
        StorageOpenParams params{};
        params.StructSize = sizeof(params);
        params.FilePath = path.c_str();
        params.Password = nullptr;
        params.Data = nullptr;
        params.DataSize = 0;
        const int status = mod.api.OpenStorage(params, &storage, &info);
        if (storage_out != nullptr) {
            *storage_out = storage;
        }
        return status;
    }

    int CALLBACK accept_progress(HANDLE, __int64)
    {
        return TRUE;
    }

    int CALLBACK cancel_progress(HANDLE, __int64)
    {
        return FALSE;
    }

    struct progress_recorder
    {
        std::int64_t total = 0;
        int calls = 0;
    };

    int CALLBACK record_progress(HANDLE context, const __int64 bytes)
    {
        auto *recorder = static_cast<progress_recorder *>(context);
        recorder->total += bytes;
        ++recorder->calls;
        return TRUE;
    }

    int module_extract(const loaded_module &mod, const HANDLE storage, const int index,
                       const std::filesystem::path &destination,
                       const ExtractProgressFunc progress = accept_progress, HANDLE context = nullptr)
    {
        const ExtractProcessCallbacks callbacks{
            .signalContext = context,
            .FileProgress = progress,
        };
        const ExtractOperationParams params{
            .ItemIndex = index,
            .Flags = 0,
            .DestPath = destination.c_str(),
            .Password = nullptr,
            .Callbacks = callbacks,
        };
        return mod.api.ExtractItem(storage, params);
    }

    std::wstring item_path(const loaded_module &mod, const HANDLE storage, const int index)
    {
        alignas(std::max_align_t) StorageItemInfo item{};
        REQUIRE(mod.api.GetItem(storage, index, &item) == GET_ITEM_OK);
        return std::wstring(item.Path);
    }

    std::int64_t item_size(const loaded_module &mod, const HANDLE storage, const int index)
    {
        alignas(std::max_align_t) StorageItemInfo item{};
        REQUIRE(mod.api.GetItem(storage, index, &item) == GET_ITEM_OK);
        return static_cast<std::int64_t>(item.Size);
    }

    std::int64_t item_packed_size(const loaded_module &mod, const HANDLE storage, const int index)
    {
        alignas(std::max_align_t) StorageItemInfo item{};
        REQUIRE(mod.api.GetItem(storage, index, &item) == GET_ITEM_OK);
        return static_cast<std::int64_t>(item.PackedSize);
    }

    std::string read_file(const std::filesystem::path &path)
    {
        std::ifstream input(path, std::ios::binary);
        REQUIRE(input.is_open());
        return {std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
    }

    void remove_file(const std::filesystem::path &path)
    {
        std::error_code error;
        std::filesystem::remove(path, error);
    }

    // Holds an exclusive byte-range lock on the destination so the archive's own output
    // stream opens successfully but its first write fails with a real IO error.
    class locked_destination
    {
      public:
        locked_destination(const std::filesystem::path &path, const DWORD length)
        {
            file_ = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE,
                                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                                FILE_ATTRIBUTE_NORMAL, nullptr);
            REQUIRE(file_ != INVALID_HANDLE_VALUE);
            length_ = length;
            REQUIRE(LockFileEx(file_, LOCKFILE_EXCLUSIVE_LOCK, 0, length, 0, &overlapped_) != FALSE);
        }

        ~locked_destination()
        {
            if (file_ != INVALID_HANDLE_VALUE) {
                static_cast<void>(UnlockFileEx(file_, 0, length_, 0, &overlapped_));
                static_cast<void>(CloseHandle(file_));
            }
        }

        locked_destination(const locked_destination &) = delete;
        locked_destination &operator=(const locked_destination &) = delete;

      private:
        HANDLE file_ = INVALID_HANDLE_VALUE;
        OVERLAPPED overlapped_{};
        DWORD length_ = 0;
    };

    stub_entry texture_entry(const std::u16string &name, std::string payload = {})
    {
        return stub_entry{.kind = UNITY_KIND_TEXTURE_PNG, .name = name, .payload = std::move(payload)};
    }

    stub_entry text_entry(const std::u16string &name, std::string payload = {})
    {
        return stub_entry{.kind = UNITY_KIND_TEXT_RAW, .name = name, .payload = std::move(payload)};
    }

    // The additional resource kinds use their frozen external numeric ids (3..8) from
    // the canonical header; the first Font case used the literal 3 so it compiled
    // before the header gained the named constants.
    stub_entry font_entry(const std::u16string &name, std::string payload = {})
    {
        return stub_entry{.kind = UNITY_KIND_FONT, .name = name, .payload = std::move(payload)};
    }

    stub_entry video_entry(const std::u16string &name, std::string payload = {})
    {
        return stub_entry{.kind = UNITY_KIND_VIDEO_CLIP, .name = name, .payload = std::move(payload)};
    }

    stub_entry movie_entry(const std::u16string &name, std::string payload = {})
    {
        return stub_entry{.kind = UNITY_KIND_MOVIE_TEXTURE, .name = name, .payload = std::move(payload)};
    }

    stub_entry audio_entry(const std::u16string &name, std::string payload = {})
    {
        return stub_entry{.kind = UNITY_KIND_AUDIO_CLIP, .name = name, .payload = std::move(payload)};
    }

    stub_entry mesh_entry(const std::u16string &name, std::string payload = {})
    {
        return stub_entry{.kind = UNITY_KIND_MESH, .name = name, .payload = std::move(payload)};
    }

    stub_entry sprite_entry(const std::u16string &name, std::string payload = {})
    {
        return stub_entry{.kind = UNITY_KIND_SPRITE, .name = name, .payload = std::move(payload)};
    }

    // A distinct three-unit path component, base-62 coded, so no two entries of a synthetic
    // deep catalogue share an ancestor directory key.
    std::u16string encode_component(const std::uint32_t value)
    {
        constexpr char16_t alphabet[] = u"0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        std::u16string component(3, u'0');
        component[0] = alphabet[(value / (62U * 62U)) % 62U];
        component[1] = alphabet[(value / 62U) % 62U];
        component[2] = alphabet[value % 62U];
        return component;
    }

    // A relative original path of `depth` globally unique components, used to exercise the
    // cumulative catalogue metadata budget with many deep, fully distinct directory trees.
    std::u16string deep_original(const std::uint32_t seed, const std::size_t depth)
    {
        std::u16string path;
        for (std::size_t level = 0; level < depth; ++level) {
            if (level != 0) {
                path.push_back(u'/');
            }
            path += encode_component(seed * static_cast<std::uint32_t>(depth) + static_cast<std::uint32_t>(level));
        }
        return path;
    }

    // The preserved path the adapter must produce for deep_original(seed, depth) at `index`
    // when the entry kind is text and the basename is the final component.
    std::wstring deep_preserved(const std::uint32_t seed, const std::size_t depth, const std::int32_t index)
    {
        const auto component_at = [&](const std::size_t level) {
            return encode_component(seed * static_cast<std::uint32_t>(depth) + static_cast<std::uint32_t>(level));
        };
        std::wstring path;
        for (std::size_t level = 0; level + 1 < depth; ++level) {
            const std::u16string component = component_at(level);
            if (level != 0) {
                path.push_back(L'\\');
            }
            path.append(component.begin(), component.end());
        }
        path.push_back(L'\\');
        path += std::to_wstring(index);
        path.push_back(L'_');
        const std::u16string last = component_at(depth - 1);
        path.append(last.begin(), last.end());
        path += L".bin";
        return path;
    }

    const std::string fixture_payload = "unpackrat unity fixture\n";
} // namespace

// Replacement allocator for the test binary only. Everything routes through malloc/free as the
// default allocator does, with two test-only additions: a single allocation can be made to throw
// std::bad_alloc while `fault_armed` is set by a fault-injection test, and the actual malloc block
// size is added to and removed from the live-heap counter so a test can bound the incremental live
// heap. Both new[]/delete[] and the sized delete overloads stay symmetric with operator new.
namespace
{
    void account_free(void *const block) noexcept
    {
        if (block != nullptr) {
            g_live_bytes -= _msize(block);
        }
        std::free(block);
    }
} // namespace

void *operator new(const std::size_t size)
{
    if (fault_armed) {
        fault_armed = false;
        throw std::bad_alloc{};
    }
    void *const block = std::malloc(size == 0 ? 1 : size);
    if (block == nullptr) {
        throw std::bad_alloc{};
    }
    g_live_bytes += _msize(block);
    if (g_measuring_heap && g_live_bytes > g_peak_bytes) {
        g_peak_bytes = g_live_bytes;
    }
    return block;
}

void *operator new[](const std::size_t size)
{
    return ::operator new(size);
}

void operator delete(void *const block) noexcept
{
    account_free(block);
}

void operator delete[](void *const block) noexcept
{
    account_free(block);
}

void operator delete(void *const block, const std::size_t) noexcept
{
    account_free(block);
}

void operator delete[](void *const block, const std::size_t) noexcept
{
    account_free(block);
}

extern "C" int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_open(const std::uint16_t *path, std::int32_t path_length,
                                                              std::uint64_t *session_out, std::int32_t *count_out)
{
    return stub_open(path, path_length, session_out, count_out);
}

extern "C" int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_get_entry(std::uint64_t session, std::int32_t index,
                                                                   UnityEntry *entry, std::uint16_t *name,
                                                                   std::int32_t name_capacity)
{
    return stub_get_entry(session, index, entry, name, name_capacity);
}

extern "C" int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_export(std::uint64_t session, std::int32_t index,
                                                                unpackrat_unity_write_callback write, void *context)
{
    return stub_export(session, index, write, context);
}

extern "C" int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_get_source_size(std::uint64_t session, std::int32_t index,
                                                                        std::int64_t *size_out)
{
    return stub_get_source_size(session, index, size_out);
}

extern "C" int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_get_entry_path(std::uint64_t session, std::int32_t index,
                                                                        std::uint16_t *path,
                                                                        std::int32_t path_capacity,
                                                                        std::int32_t *path_length)
{
    return stub_get_entry_path(session, index, path, path_capacity, path_length);
}

extern "C" int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_get_entry_extension(
    std::uint64_t session, std::int32_t index, std::uint16_t *extension, std::int32_t extension_capacity,
    std::int32_t *extension_length)
{
    return stub_get_entry_extension(session, index, extension, extension_capacity, extension_length);
}

extern "C" int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_close(std::uint64_t session)
{
    return stub_close(session);
}

TEST_CASE("unity adapter: module identity, signature and identity decrypt", "[unity][unit]")
{
    const loaded_module mod;
    REQUIRE(mod.version == MAKEMODULEVERSION(0, 3));
    REQUIRE(mod.api_version == ACTUAL_API_VERSION);
    const GUID expected{0x4e91f7c6, 0x2a5d, 0x4973, {0x9b, 0x2c, 0x8f, 0x53, 0x71, 0xd6, 0x0a, 0x2e}};
    REQUIRE(std::memcmp(&mod.id, &expected, sizeof(GUID)) == 0);

    REQUIRE(extractor::extractor::get_signature().empty());

    extractor::configure(nullptr);
    extractor::configure(L"ignored");
    extractor::reset_settings();
    const extractor::version_info version = extractor::get_version_info();
    REQUIRE(version.major_version == 0);
    REQUIRE(version.minor_version == 3);

    extractor::extractor adapter;
    std::vector<char> bytes{1, 2, 3};
    REQUIRE(adapter.decrypt(0x11223344U, bytes) == 0x11223344U);
    REQUIRE(bytes == std::vector<char>({1, 2, 3}));
}

TEST_CASE("unity adapter: valid archive lists source-size entries and brace fallback folders before extract", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {
        texture_entry(u"fixture", "\x89PNG"),
        text_entry(u"fixture", fixture_payload),
    };
    stub->source_sizes = {4, static_cast<std::int64_t>(fixture_payload.size())};
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(storage != nullptr);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

    REQUIRE(stub->size_query_calls == 2);
    REQUIRE(stub->copy_calls == 2);
    REQUIRE(stub->export_calls == 0);

    REQUIRE(item_path(mod, storage, 0) == L"{Texture2D}\\0_fixture.png");
    REQUIRE(item_path(mod, storage, 1) == L"{TextAsset}\\1_fixture.bin");
    REQUIRE(item_size(mod, storage, 0) == 4);
    REQUIRE(item_size(mod, storage, 1) == static_cast<std::int64_t>(fixture_payload.size()));
    REQUIRE(item_packed_size(mod, storage, 0) == 4);
    REQUIRE(item_packed_size(mod, storage, 1) == static_cast<std::int64_t>(fixture_payload.size()));

    alignas(std::max_align_t) StorageItemInfo extra{};
    REQUIRE(mod.api.GetItem(storage, 2, &extra) == GET_ITEM_NOMOREITEMS);
    REQUIRE(stub->export_calls == 0);

    mod.api.CloseStorage(storage);
    REQUIRE(stub->close_calls == 1);
}

TEST_CASE("unity adapter: a Font entry lists with its braced folder and dynamic extension", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {font_entry(u"font", "FONTDATA")};
    stub->source_sizes = {8};
    stub->extensions = {u"ttf"};
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

    // Font is a first-class resource: it lands in its own braced fallback folder and
    // takes the dynamic output extension the additive ABI getter reports, not png/bin.
    REQUIRE(stub->entry_extension_query_calls == 1);
    REQUIRE(stub->entry_extension_copy_calls == 1);
    REQUIRE(item_path(mod, storage, 0) == L"{Font}\\0_font.ttf");
    REQUIRE(item_size(mod, storage, 0) == 8);
    REQUIRE(item_packed_size(mod, storage, 0) == 8);
    REQUIRE(stub->export_calls == 0);

    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: every resource kind lists with its own folder and output extension", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {
        texture_entry(u"tex"),  // 1 Texture2D  -> fixed png
        text_entry(u"txt"),     // 2 TextAsset  -> fixed bin
        font_entry(u"font"),    // 3 Font       -> dynamic
        video_entry(u"clip"),   // 4 VideoClip  -> dynamic
        movie_entry(u"movie"),  // 5 MovieTexture -> fixed ogv
        audio_entry(u"sfx"),    // 6 AudioClip  -> dynamic
        mesh_entry(u"mesh"),    // 7 Mesh       -> fixed glb
        sprite_entry(u"icon"),  // 8 Sprite     -> fixed png
    };
    stub->extensions = {u"", u"", u"ttf", u"mp4", u"", u"ogg", u"", u""};
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

    // Only the three dynamic kinds (Font, VideoClip, AudioClip) consult the getter; the
    // fixed kinds keep their frozen table extension and never query it.
    REQUIRE(stub->entry_extension_query_calls == 3);
    REQUIRE(stub->entry_extension_copy_calls == 3);
    REQUIRE(stub->export_calls == 0);

    REQUIRE(item_path(mod, storage, 0) == L"{Texture2D}\\0_tex.png");
    REQUIRE(item_path(mod, storage, 1) == L"{TextAsset}\\1_txt.bin");
    REQUIRE(item_path(mod, storage, 2) == L"{Font}\\2_font.ttf");
    REQUIRE(item_path(mod, storage, 3) == L"{VideoClip}\\3_clip.mp4");
    REQUIRE(item_path(mod, storage, 4) == L"{MovieTexture}\\4_movie.ogv");
    REQUIRE(item_path(mod, storage, 5) == L"{AudioClip}\\5_sfx.ogg");
    REQUIRE(item_path(mod, storage, 6) == L"{Mesh}\\6_mesh.glb");
    REQUIRE(item_path(mod, storage, 7) == L"{Sprite}\\7_icon.png");

    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: the dynamic extension getter is validated before use", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("query failure")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"font")};
        stub->extensions = {u"ttf"};
        stub->entry_extension_result = UNITY_DATA_FAILURE;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
    SECTION("an empty declared extension is rejected")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"font")};
        stub->extensions = {u""};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("an over-long extension is rejected")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"font")};
        stub->extensions = {u"aaaaaaaaaaaaaaaaa"};  // 17 units, one over the bound
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("an over-long declared length is rejected before the copy")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"font")};
        stub->extensions = {u"ttf"};
        stub->entry_extension_length_out = 17;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("copy failure")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"font")};
        stub->extensions = {u"ttf"};
        stub->entry_extension_copy_result = UNITY_DATA_FAILURE;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("copy length mismatch")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"font")};
        stub->extensions = {u"ttf"};
        stub->entry_extension_copy_length_out = 2;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("a dot inside the extension is rejected")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"font")};
        stub->extensions = {u"t.t"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("an upper-case extension is rejected")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"font")};
        stub->extensions = {u"TTF"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("a path separator inside the extension is rejected")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"font")};
        stub->extensions = {u"a\\b"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("a unit above the lower-case range is rejected")
    {
        // '{' is 0x7B, one past 'z': the lower-case test succeeds only up to the
        // final letter, so an extension unit that is not a letter or digit and
        // sits above the letters must still be rejected before it shapes a path.
        stub_binding stub;
        stub->entries = {font_entry(u"font")};
        stub->extensions = {u"a{b"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("a non-ASCII extension unit is rejected")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"font")};
        stub->extensions = {u"\u00E9x"};  // 'e' with acute, far above the ASCII grammar
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
}

TEST_CASE("unity adapter: dynamic extensions join preserved paths and reserve new fallback roots", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("a preserved hierarchy keeps the dynamic extension and strips the source one")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"Hero")};
        stub->extensions = {u"ttf"};
        stub->original_paths = {u"Assets/Fonts/Hero.ttf"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"Assets\\Fonts\\0_Hero.ttf");
        mod.api.CloseStorage(storage);
    }
    SECTION("the new braced fallback roots are reserved case-insensitively")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"f"), mesh_entry(u"m"), audio_entry(u"a")};
        stub->extensions = {u"ttf", u"", u"ogg"};
        stub->original_paths = {u"{Font}/x.ttf", u"{MESH}/x", u"{audioclip}/x"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"{Font}\\0_f.ttf");
        REQUIRE(item_path(mod, storage, 1) == L"{Mesh}\\1_m.glb");
        REQUIRE(item_path(mod, storage, 2) == L"{AudioClip}\\2_a.ogg");
        mod.api.CloseStorage(storage);
    }
    SECTION("a sixteen-unit extension is accepted and a digit-only extension is legal")
    {
        stub_binding stub;
        stub->entries = {font_entry(u"max"), font_entry(u"digits")};
        stub->extensions = {u"abcdefghijklmnop", u"007"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"{Font}\\0_max.abcdefghijklmnop");
        REQUIRE(item_path(mod, storage, 1) == L"{Font}\\1_digits.007");
        mod.api.CloseStorage(storage);
    }
    SECTION("the longest dynamic extension cannot push a preserved path past the Observer cap")
    {
        // Eleven 91-unit components build a preserved path of 1030 units with a 16-unit
        // extension (1030 > 1023) but only 1018 units with the fixed four-unit ".bin";
        // the over-cap entry therefore falls back while the shorter-extension one is kept.
        const std::u16string component(91, u'a');
        std::u16string original;
        for (int level = 0; level < 11; ++level) {
            if (level != 0) {
                original.push_back(u'/');
            }
            original += component;
        }
        std::wstring preserved;
        for (int level = 0; level < 10; ++level) {
            if (level != 0) {
                preserved.push_back(L'\\');
            }
            preserved.append(91, L'a');
        }
        preserved += L"\\0_";
        preserved.append(91, L'a');
        preserved += L".bin";
        REQUIRE(preserved.size() <= 1023);  // the shorter fixed extension still fits

        stub_binding stub;
        stub->entries = {text_entry(u"label0"), font_entry(u"label1")};
        stub->extensions = {u"", u"abcdefghijklmnop"};
        stub->original_paths = {original, original};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == preserved);
        REQUIRE(item_path(mod, storage, 1) == L"{Font}\\1_label1.abcdefghijklmnop");
        mod.api.CloseStorage(storage);
    }
}

TEST_CASE("unity adapter: item size and packed size both carry the declared source length", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {
        text_entry(u"first", "0123456789"),
        texture_entry(u"second", "ABCDE"),
    };
    stub->source_sizes = {10, 0};
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

    REQUIRE(stub->source_size_calls == 2);
    REQUIRE(stub->export_calls == 0);

    // The Size column shows the original source payload length, the same value as PackedSize.
    REQUIRE(item_size(mod, storage, 0) == 10);
    REQUIRE(item_size(mod, storage, 1) == 0);
    REQUIRE(item_packed_size(mod, storage, 0) == 10);
    REQUIRE(item_packed_size(mod, storage, 1) == 0);

    // The metadata listing never materialised anything, and extraction still streams the entry.
    const std::filesystem::path destination = archive.path.wstring() + L".packed";
    remove_file(destination);
    REQUIRE(module_extract(mod, storage, 0, destination) == SER_SUCCESS);
    REQUIRE(read_file(destination) == "0123456789");
    remove_file(destination);

    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: original asset paths are preserved and unsafe paths fall back", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {
        texture_entry(u"hero", "\x89PNG"),
        text_entry(u"notes", fixture_payload),
        texture_entry(u"evil"),
    };
    stub->source_sizes = {4, static_cast<std::int64_t>(fixture_payload.size()), 0};
    stub->original_paths = {
        u"Assets/Resources/Textures/hero.png",
        u"Assets/Scripts/notes.txt",
        u"../escape/evil.png",
    };
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

    // The adapter queries the original path once and copies it once per entry.
    REQUIRE(stub->entry_path_query_calls == 3);
    REQUIRE(stub->entry_path_copy_calls == 3);
    REQUIRE(stub->export_calls == 0);

    // The original parent hierarchy and the known basename are preserved; the file kind
    // extension still comes from the entry kind, and the numeric index keeps names unique.
    REQUIRE(item_path(mod, storage, 0) == L"Assets\\Resources\\Textures\\0_hero.png");
    REQUIRE(item_path(mod, storage, 1) == L"Assets\\Scripts\\1_notes.bin");
    // A traversal path is never followed: the entry falls back to the braced kind folder.
    REQUIRE(item_path(mod, storage, 2) == L"{Texture2D}\\2_evil.png");

    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: reserved device names with extensions fall back", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {
        text_entry(u"fixture"),
        text_entry(u"fixture"),
        text_entry(u"fixture"),
        text_entry(u"fixture"),
        text_entry(u"fixture"),
        text_entry(u"fixture"),
    };
    stub->original_paths = {
        u"Assets/NUL.txt/x",        // 0: reserved directory name with an extension
        u"Assets/nUl.txt/x",        // 1: reserved name, ASCII case-insensitive
        u"Assets/CON.any/x",        // 2: reserved name with an arbitrary extension
        u"Assets/COM\u00B9/x",      // 3: reserved COM superscript one
        u"Assets/LPT\u00B2.txt/x",  // 4: reserved LPT superscript two with an extension
        u"Assets/NULX/x",           // 5: reserved-like but legal directory, preserved
    };
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
    REQUIRE(stub->export_calls == 0);

    REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_fixture.bin");
    REQUIRE(item_path(mod, storage, 1) == L"{TextAsset}\\1_fixture.bin");
    REQUIRE(item_path(mod, storage, 2) == L"{TextAsset}\\2_fixture.bin");
    REQUIRE(item_path(mod, storage, 3) == L"{TextAsset}\\3_fixture.bin");
    REQUIRE(item_path(mod, storage, 4) == L"{TextAsset}\\4_fixture.bin");
    REQUIRE(item_path(mod, storage, 5) == L"Assets\\NULX\\5_x.bin");

    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: catalogue namespace collisions resolve deterministically", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("a synthesized file name used as another entry's directory falls back")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"x"), text_entry(u"y")};
        stub->original_paths = {u"Assets/x.txt", u"Assets/0_x.bin/y.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

        // The real directory "0_x.bin" keeps its hierarchy; the shallow synthesized file
        // name that collides with it falls back instead. Neither path conflicts.
        REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_x.bin");
        REQUIRE(item_path(mod, storage, 1) == L"Assets\\0_x.bin\\1_y.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("a preserved name equal to another entry's directory falls back")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"q"), text_entry(u"lab")};
        stub->original_paths = {u"Assets/1_lab.bin/q.txt", u"Assets/lab.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

        REQUIRE(item_path(mod, storage, 0) == L"Assets\\1_lab.bin\\0_q.bin");
        REQUIRE(item_path(mod, storage, 1) == L"{TextAsset}\\1_lab.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("the collision check is case-insensitive")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"q"), text_entry(u"lab")};
        stub->original_paths = {u"Assets/1_lab.bin/q.txt", u"ASSETS/LAB.BIN"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

        REQUIRE(item_path(mod, storage, 0) == L"Assets\\1_lab.bin\\0_q.bin");
        REQUIRE(item_path(mod, storage, 1) == L"{TextAsset}\\1_lab.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("a single-component original name is preserved with no directory")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"solo")};
        stub->original_paths = {u"solo.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

        REQUIRE(item_path(mod, storage, 0) == L"0_solo.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("a preserved name cannot enter the braced fallback namespace")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture"), text_entry(u"z")};
        stub->original_paths = {u"", u"{TextAsset}/0_fixture.bin/z.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

        // The original root is exactly the fallback folder name, so it is reserved and the
        // entry falls back; both entries stay extractable.
        REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_fixture.bin");
        REQUIRE(item_path(mod, storage, 1) == L"{TextAsset}\\1_z.bin");
        mod.api.CloseStorage(storage);
    }
}

TEST_CASE("unity adapter: an unpaired surrogate original path falls back instead of rejecting", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("a valid surrogate pair is preserved")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->original_paths = {u"Assets/\U0001F600.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

        REQUIRE(item_path(mod, storage, 0) == L"Assets\\0_\U0001F600.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("an unpaired high surrogate falls back")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->original_paths = {u"Assets/" + std::u16string(1, char16_t{0xD83D}) + u"/x.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);

        // The metadata label "fixture" is valid, so the entry uses the braced fallback
        // folder instead of rejecting the whole archive.
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_fixture.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("an unpaired low surrogate falls back")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->original_paths = {u"Assets/" + std::u16string(1, char16_t{0xDE00}) + u"/x.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_fixture.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("a high surrogate followed by a non-low unit falls back")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->original_paths = {u"Assets/" + std::u16string(1, char16_t{0xD83D}) + u"A/x.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_fixture.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("a high surrogate followed by a unit past the low range falls back")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->original_paths = {u"Assets/" + std::u16string(1, char16_t{0xD83D}) +
                                std::u16string(1, char16_t{0xE000}) + u"/x.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_fixture.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("a lone private-use unit is well formed and preserved")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->original_paths = {u"Assets/" + std::u16string(1, char16_t{0xE000}) + u"/x.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"Assets\\\xE000\\0_x.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("a trailing high surrogate falls back")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->original_paths = {u"Assets/" + std::u16string(1, char16_t{0xD83D})};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_fixture.bin");
        mod.api.CloseStorage(storage);
    }
}

TEST_CASE("unity adapter: rooted and single-component original paths are handled", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {text_entry(u"slash"), text_entry(u"one")};
    stub->original_paths = {u"/evil/x", u"x"};
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

    // A leading forward slash is a rooted path just like a leading backslash, so the entry
    // falls back; a one-unit original is relative, so its component name is preserved.
    REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_slash.bin");
    REQUIRE(item_path(mod, storage, 1) == L"1_x.bin");

    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: ordinary brace names keep their braces and only the fallback root is reserved", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("ordinary braces inside a directory are preserved")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"x")};
        stub->original_paths = {u"Assets/UI{HD}/x.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"Assets\\UI{HD}\\0_x.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("braces below the root are preserved")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"x")};
        stub->original_paths = {u"Assets/{Texture2D}/x.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"Assets\\{Texture2D}\\0_x.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("braces in a fallback label are preserved")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"a{b}c")};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_a{b}c.bin");
        mod.api.CloseStorage(storage);
    }
    SECTION("a reserved fallback root directory falls back")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"x"), text_entry(u"y"), texture_entry(u"z")};
        stub->original_paths = {u"{TextAsset}/x.txt", u"{textasset}/y.txt", u"{Texture2D}/z.txt"};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_x.bin");
        REQUIRE(item_path(mod, storage, 1) == L"{TextAsset}\\1_y.bin");
        REQUIRE(item_path(mod, storage, 2) == L"{Texture2D}\\2_z.png");
        mod.api.CloseStorage(storage);
    }
}

TEST_CASE("unity adapter: the catalogue metadata budget bounds a deep distinct namespace", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("a catalogue within the budget keeps its preserved hierarchy")
    {
        constexpr std::uint32_t count = 200;
        constexpr std::size_t depth = 40;
        stub_binding stub;
        stub->entries.reserve(count);
        stub->original_paths.reserve(count);
        for (std::uint32_t entry = 0; entry < count; ++entry) {
            stub->entries.push_back(text_entry(u"fixture"));
            stub->original_paths.push_back(deep_original(entry, depth));
        }
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == deep_preserved(0, depth, 0));
        REQUIRE(item_path(mod, storage, static_cast<int>(count) - 1) ==
                deep_preserved(count - 1, depth, static_cast<std::int32_t>(count) - 1));
        mod.api.CloseStorage(storage);
    }
    SECTION("a catalogue over the metadata budget fails cleanly and releases the session")
    {
        constexpr std::uint32_t count = 640;
        constexpr std::size_t depth = 250;
        stub_binding stub;
        stub->entries.reserve(count);
        stub->original_paths.reserve(count);
        for (std::uint32_t entry = 0; entry < count; ++entry) {
            stub->entries.push_back(text_entry(u"fixture"));
            stub->original_paths.push_back(deep_original(entry, depth));
        }
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);

        // The deep, fully distinct ancestor directories exceed the documented cumulative
        // metadata budget, so preparation stops with a bounded read failure and the backend
        // session is released rather than the listing growing without bound.
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
}

TEST_CASE("unity adapter: fallback catalogue scratch stays bounded while final files grow", "[unity][unit]")
{
    const auto measure = [](const std::uint32_t count) {
        stub_binding stub;
        stub->entries.reserve(count);
        stub->source_sizes.assign(count, 7);
        for (std::uint32_t index = 0; index < count; ++index) {
            stub->entries.push_back(text_entry(u"leaf", "payload"));
        }
        const temp_archive archive;
        const loaded_module mod;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);

        bool prepared = false;
        std::size_t peak = 0;
        std::size_t transient = 0;
        {
            const heap_window window;
            prepared = mod.api.PrepareFiles(storage) == TRUE;
            peak = window.peak_delta();
            transient = window.peak_over_current();
        }
        // Fixture setup, assertions and enumeration are outside the tracked window. The
        // final live catalogue is subtracted so the bound measures temporary duplication,
        // independent of file-object size and allocator rounding on x86 versus x64.
        std::printf("unity catalogue heap count=%u peak=%zu retained=%zu transient=%zu\n",
                    count, peak, peak - transient, transient);
        REQUIRE(prepared);
        REQUIRE(stub->export_calls == 0);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(stub->size_query_calls == static_cast<int>(count));
        REQUIRE(stub->copy_calls == static_cast<int>(count));
        REQUIRE(stub->entry_path_query_calls == static_cast<int>(count));
        REQUIRE(stub->entry_path_copy_calls == 0);
        REQUIRE(stub->source_size_calls == static_cast<int>(count));
        REQUIRE(stub->entry_extension_query_calls == 0);
        REQUIRE(stub->entry_extension_copy_calls == 0);
        for (std::uint32_t index = 0; index < count; ++index) {
            StorageItemInfo item{};
            REQUIRE(mod.api.GetItem(storage, static_cast<int>(index), &item) == GET_ITEM_OK);
            REQUIRE(std::wstring(item.Path) == L"{TextAsset}\\" + std::to_wstring(index) + L"_leaf.bin");
            REQUIRE(item.Size == 7);
            REQUIRE(item.PackedSize == 7);
        }
        StorageItemInfo end{};
        REQUIRE(mod.api.GetItem(storage, static_cast<int>(count), &end) == GET_ITEM_NOMOREITEMS);
        REQUIRE(stub->export_calls == 0);
        const std::filesystem::path destination = archive.path.wstring() + L".catalogue-out";
        for (const int index : {0, static_cast<int>(count) - 1}) {
            REQUIRE(module_extract(mod, storage, index, destination) == SER_SUCCESS);
            REQUIRE(read_file(destination) == "payload");
        }
        REQUIRE(stub->export_calls == 2);
        remove_file(destination);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
        return transient;
    };

    const std::size_t small = measure(1024);
    const std::size_t large = measure(8192);
    // The extra 7168 fallback rows add final files, not a second retained metadata table.
    // 64 KiB allows bounded conversion scratch without depending on an exact allocation count.
    REQUIRE(large <= small + 64U * 1024U);
}

TEST_CASE("unity adapter: preserved paths defer malformed fallback labels until collision resolution", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;
    stub_binding stub;
    stub->entries = {text_entry(std::u16string(1, char16_t{0xD83D})), text_entry(u"leaf")};
    HANDLE storage = nullptr;
    SECTION("a valid preserved path ignores the unusable metadata label")
    {
        stub->original_paths = {u"Assets/ok.txt", u""};
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(item_path(mod, storage, 0) == L"Assets\\0_ok.bin");
        REQUIRE(item_path(mod, storage, 1) == L"{TextAsset}\\1_leaf.bin");
        REQUIRE(stub->size_query_calls == 2);
        REQUIRE(stub->copy_calls == 2);
        REQUIRE(stub->entry_path_query_calls == 2);
        REQUIRE(stub->entry_path_copy_calls == 1);
        REQUIRE(stub->source_size_calls == 2);
        REQUIRE(stub->export_calls == 0);
    }
    SECTION("a later directory collision requires the malformed fallback and rejects preparation")
    {
        stub->original_paths = {u"Assets/ok.txt", u"Assets/0_ok.bin/nested.txt"};
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        REQUIRE(stub->size_query_calls == 2);
        REQUIRE(stub->entry_path_query_calls == 2);
        REQUIRE(stub->export_calls == 0);
    }
    mod.api.CloseStorage(storage);
    REQUIRE(stub->close_calls == 1);
}

TEST_CASE("unity adapter: a stripped extension cannot retain a large allocation", "[unity][unit]")
{
    // Each original is a short safe path whose extension is huge. The adapter strips the
    // extension, so the emitted path stays tiny while the raw metadata is about 65 KiB per entry.
    constexpr std::uint32_t count = 64;
    const std::u16string huge_extension(32700, u'z');

    stub_binding stub;
    stub->entries.reserve(count);
    stub->original_paths.reserve(count);
    for (std::uint32_t entry = 0; entry < count; ++entry) {
        stub->entries.push_back(text_entry(u"fixture"));
        stub->original_paths.push_back(u"Assets/x." + huge_extension);
    }

    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);

    std::size_t peak = 0;
    bool prepared = false;
    {
        const heap_window window;
        prepared = mod.api.PrepareFiles(storage) == TRUE;
        peak = window.peak_delta();
    }

    REQUIRE(prepared);
    REQUIRE(item_path(mod, storage, 0) == L"Assets\\0_x.bin");
    REQUIRE(item_path(mod, storage, static_cast<int>(count) - 1) == L"Assets\\63_x.bin");
    // The stripped extension must not be retained: the incremental live heap stays well under
    // this generous bound, far from the roughly 4 MiB a reserve-from-raw-size bug would hold.
    REQUIRE(peak < (1024U * 1024U));

    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: original path query failures reject preparation and release the session", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("query failure")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->original_paths = {u"Assets/x"};
        stub->entry_path_result = UNITY_DATA_FAILURE;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
    SECTION("negative declared path length")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->entry_path_length_out = -1;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
    SECTION("copy failure")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->original_paths = {u"Assets/x"};
        stub->entry_path_copy_result = UNITY_DATA_FAILURE;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
    SECTION("copy length mismatch")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->original_paths = {u"Assets/x"};
        stub->entry_path_copy_length_out = 7;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
    SECTION("over-long declared path is never copied")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->original_paths = {std::u16string(0x8000, u'a')};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        REQUIRE(stub->entry_path_query_calls == 1);
        REQUIRE(stub->entry_path_copy_calls == 0);
        REQUIRE(item_path(mod, storage, 0) == L"{TextAsset}\\0_fixture.bin");
        mod.api.CloseStorage(storage);
    }
}

TEST_CASE("unity adapter: original path policy preserves safe shapes and falls back on unsafe ones", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {
        texture_entry(u"label0"),
        text_entry(u"label1"),
        text_entry(u"label2"),
        text_entry(u"label3"),
        text_entry(u"label4"),
        text_entry(u"label5"),
        text_entry(u"label6"),
        text_entry(u"label7"),
        text_entry(u"label8"),
        text_entry(u"label9"),
        texture_entry(u"label10"),
        text_entry(u"label11"),
        text_entry(u"label12"),
        text_entry(u"label13"),
        text_entry(u"label14"),
        text_entry(u"label15"),
        text_entry(u"label16"),
        text_entry(u"label17"),
        text_entry(u"label18"),
        text_entry(u"a\\b/c"),
    };
    const std::u16string long_component(97, u'a');
    const std::u16string long_component_path = u"Assets/" + long_component + u"/x";
    const std::u16string overlong = u"Assets/" + std::u16string(95, u'a') + u"/" +
                                    std::u16string(95, u'b') + u"/" + std::u16string(95, u'c') + u"/" +
                                    std::u16string(95, u'd') + u"/" + std::u16string(95, u'e') + u"/" +
                                    std::u16string(95, u'f') + u"/" + std::u16string(95, u'g') + u"/" +
                                    std::u16string(95, u'h') + u"/" + std::u16string(95, u'i') + u"/" +
                                    std::u16string(95, u'j') + u"/" + std::u16string(95, u'k') + u"/" +
                                    std::u16string(95, u'l');
    stub->original_paths = {
        u"Assets/Resources/Textures/hero.png",        // 0: preserved hierarchy
        u"Assets\\Scripts\\notes",                    // 1: backslash separator, no extension
        u"Assets/Sub/A<B>C:D\"E|F?G*H",               // 2: every forbidden character is replaced
        u"Assets/Res./trimmed.",                      // 3: trailing dot/space trimming
        u"Assets/   /x",                              // 4: component sanitises to nothing
        u"Assets/CON/x",                              // 5: reserved directory
        u"Assets/com1",                               // 6: reserved lowercase basename
        u"Assets/CONX/data",                          // 7: reserved-like but legal
        long_component_path,                          // 8: over-long component
        u".png",                                      // 9: extension-only basename
        u"\\evil\\x",                                 // 10: rooted
        u"C:/evil/x",                                 // 11: drive-absolute
        u"Assets//x",                                 // 12: empty component
        u"Assets/./x",                                // 13: dot component
        u"Assets/../x",                               // 14: dotdot component
        overlong,                                     // 15: over-long total path
        u"Assets/\u042E\u043D\u0438\u043A\u043E\u0434/\u0444\u0430\u0439\u043B",  // 16: unicode
        u"Assets/Data/archive.tar.gz",                // 17: only the last extension is stripped
        u"Assets/a\u0001b/x",                         // 18: control unit replaced
    };
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
    REQUIRE(stub->entry_path_query_calls == 20);
    REQUIRE(stub->export_calls == 0);

    REQUIRE(item_path(mod, storage, 0) == L"Assets\\Resources\\Textures\\0_hero.png");
    REQUIRE(item_path(mod, storage, 1) == L"Assets\\Scripts\\1_notes.bin");
    REQUIRE(item_path(mod, storage, 2) == L"Assets\\Sub\\2_A_B_C_D_E_F_G_H.bin");
    REQUIRE(item_path(mod, storage, 3) == L"Assets\\Res\\3_trimmed.bin");
    REQUIRE(item_path(mod, storage, 4) == L"{TextAsset}\\4_label4.bin");
    REQUIRE(item_path(mod, storage, 5) == L"{TextAsset}\\5_label5.bin");
    REQUIRE(item_path(mod, storage, 6) == L"{TextAsset}\\6_label6.bin");
    REQUIRE(item_path(mod, storage, 7) == L"Assets\\CONX\\7_data.bin");
    REQUIRE(item_path(mod, storage, 8) == L"{TextAsset}\\8_label8.bin");
    REQUIRE(item_path(mod, storage, 9) == L"{TextAsset}\\9_label9.bin");
    REQUIRE(item_path(mod, storage, 10) == L"{Texture2D}\\10_label10.png");
    REQUIRE(item_path(mod, storage, 11) == L"{TextAsset}\\11_label11.bin");
    REQUIRE(item_path(mod, storage, 12) == L"{TextAsset}\\12_label12.bin");
    REQUIRE(item_path(mod, storage, 13) == L"{TextAsset}\\13_label13.bin");
    REQUIRE(item_path(mod, storage, 14) == L"{TextAsset}\\14_label14.bin");
    REQUIRE(item_path(mod, storage, 15) == L"{TextAsset}\\15_label15.bin");
    REQUIRE(item_path(mod, storage, 16) == L"Assets\\\u042E\u043D\u0438\u043A\u043E\u0434\\16_\u0444\u0430\u0439\u043B.bin");
    REQUIRE(item_path(mod, storage, 17) == L"Assets\\Data\\17_archive.tar.bin");
    REQUIRE(item_path(mod, storage, 18) == L"Assets\\a_b\\18_x.bin");
    REQUIRE(item_path(mod, storage, 19) == L"{TextAsset}\\19_a_b_c.bin");

    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: declared source-length failures reject preparation and release the session", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("query failure")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->source_size_result = UNITY_DATA_FAILURE;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
    SECTION("negative declared length")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture")};
        stub->source_sizes = {-1};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
}

TEST_CASE("unity adapter: custom extraction streams the selected entry", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {text_entry(u"fixture", fixture_payload)};
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

    const std::filesystem::path destination = archive.path.wstring() + L".out1";
    remove_file(destination);

    progress_recorder recorder;
    REQUIRE(module_extract(mod, storage, 0, destination, record_progress, &recorder) == SER_SUCCESS);
    REQUIRE(read_file(destination) == fixture_payload);
    REQUIRE(recorder.total == static_cast<std::int64_t>(fixture_payload.size()));
    REQUIRE(stub->export_calls == 1);

    remove_file(destination);
    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: backend open failures reject the archive", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("data failure")
    {
        stub_binding stub;
        stub->open_result = UNITY_DATA_FAILURE;
        REQUIRE(module_open(mod, archive.path) == SOR_INVALID_FILE);
        REQUIRE(stub->close_calls == 0);
    }
    SECTION("invalid argument")
    {
        stub_binding stub;
        stub->open_result = UNITY_INVALID_ARGUMENT;
        REQUIRE(module_open(mod, archive.path) == SOR_INVALID_FILE);
    }
    SECTION("busy")
    {
        stub_binding stub;
        stub->open_result = UNITY_BUSY;
        REQUIRE(module_open(mod, archive.path) == SOR_INVALID_FILE);
    }
    SECTION("zero token")
    {
        stub_binding stub;
        stub->token = 0;
        REQUIRE(module_open(mod, archive.path) == SOR_INVALID_FILE);
        REQUIRE(stub->close_calls == 0);
    }
    SECTION("negative count")
    {
        stub_binding stub;
        stub->count_override = -5;
        REQUIRE(module_open(mod, archive.path) == SOR_INVALID_FILE);
        REQUIRE(stub->close_calls == 1);
    }
    SECTION("count over the bound")
    {
        stub_binding stub;
        // The retired 200000 ceiling no longer rejects 200001; the over-bound
        // rejection is deliberately re-pinned to the current finite ceiling.
        stub->count_override = 400'001;
        REQUIRE(module_open(mod, archive.path) == SOR_INVALID_FILE);
        REQUIRE(stub->close_calls == 1);
    }
}

TEST_CASE("unity adapter: the open count bound accepts texture-plus-sprite catalogues", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    // A selected bundle exposes both the Texture2D and the Sprite view of a
    // catalogue, so the real shipping game reports 281151 entries and the finite
    // ceiling is 400000. Only the open-time count gate is exercised here: no
    // listing is requested, so no high-count fake entries are materialised.
    SECTION("the shipping game count 281151 opens")
    {
        stub_binding stub;
        stub->count_override = 281'151;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(stub->open_calls == 1);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
    SECTION("the exact new upper bound 400000 opens")
    {
        stub_binding stub;
        stub->count_override = 400'000;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
    SECTION("the previous 200000 bound stays accepted")
    {
        stub_binding stub;
        stub->count_override = 200'000;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        mod.api.CloseStorage(storage);
    }
    SECTION("one past the new bound 400001 is rejected and releases the session")
    {
        stub_binding stub;
        stub->count_override = 400'001;
        REQUIRE(module_open(mod, archive.path) == SOR_INVALID_FILE);
        REQUIRE(stub->close_calls == 1);
    }
    SECTION("a negative count is rejected and releases the session")
    {
        stub_binding stub;
        stub->count_override = -1;
        REQUIRE(module_open(mod, archive.path) == SOR_INVALID_FILE);
        REQUIRE(stub->close_calls == 1);
    }
}

TEST_CASE("unity adapter: get_archive_info validates the path directly", "[unity][unit]")
{
    stub_binding stub;
    extractor::extractor adapter;
    std::istringstream input;
    const std::vector<std::byte> data;

    SECTION("empty path")
    {
        REQUIRE_THROWS_AS(adapter.get_archive_info(input, data, std::filesystem::path{}, {}), extractor::read_error);
    }
    SECTION("over-long path")
    {
        const std::filesystem::path long_path(std::wstring(40'000, L'x'));
        REQUIRE_THROWS_AS(adapter.get_archive_info(input, data, long_path, {}), extractor::read_error);
    }
    SECTION("success reports the Unity format")
    {
        stub->token = 7;
        const extractor::archive_info info = adapter.get_archive_info(input, data, std::filesystem::path(L"x"), {});
        REQUIRE(info.format == L"Unity");
        REQUIRE(stub->open_calls == 1);
    }
}

TEST_CASE("unity adapter: zero-entry archive prepares with no items", "[unity][unit]")
{
    stub_binding stub;
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

    alignas(std::max_align_t) StorageItemInfo item{};
    REQUIRE(mod.api.GetItem(storage, 0, &item) == GET_ITEM_NOMOREITEMS);
    REQUIRE(stub->export_calls == 0);

    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: malformed entry metadata is rejected", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("unknown kind")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"x")};
        stub->query_kind = 99;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("wrong struct size")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"x")};
        stub->struct_size_out = 16;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("non-zero reserved field")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"x")};
        stub->reserved_out = 1;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("non-zero reserved2 field")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"x")};
        stub->reserved2_out = 1;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("name length over the bound")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"x")};
        stub->query_name_length = 0x8000U;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("size query error")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"x")};
        stub->query_result = UNITY_OUT_OF_RANGE;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("name copy error")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"name")};
        stub->copy_result = UNITY_BUFFER_TOO_SMALL;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
}

TEST_CASE("unity adapter: cancellation aborts with a clean destination", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {text_entry(u"fixture", fixture_payload)};
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

    const std::filesystem::path destination = archive.path.wstring() + L".cancel";
    remove_file(destination);
    REQUIRE(module_extract(mod, storage, 0, destination, cancel_progress) == SER_USERABORT);
    REQUIRE(!std::filesystem::exists(destination));

    REQUIRE(module_extract(mod, storage, 0, destination) == SER_SUCCESS);
    REQUIRE(read_file(destination) == fixture_payload);

    remove_file(destination);
    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: output and backend failures map to write/read codes", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("destination cannot be opened")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture", fixture_payload)};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        const std::filesystem::path destination = archive.path.wstring() + L".missing\\entry.bin";
        REQUIRE(module_extract(mod, storage, 0, destination) == SER_ERROR_WRITE);
        mod.api.CloseStorage(storage);
    }
    SECTION("backend reports a write failure after a chunk")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture", fixture_payload)};
        stub->export_result = UNITY_WRITE_FAILURE;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        const std::filesystem::path destination = archive.path.wstring() + L".wf";
        remove_file(destination);
        REQUIRE(module_extract(mod, storage, 0, destination) == SER_ERROR_WRITE);
        REQUIRE(!std::filesystem::exists(destination));
        mod.api.CloseStorage(storage);
    }
    SECTION("output write fails after a successful open")
    {
        stub_binding stub;
        // A payload larger than the stream buffer forces the failing write to surface from
        // inside the callback rather than only at close.
        const std::string big(65536, 'A');
        stub->entries = {text_entry(u"fixture", big)};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        const std::filesystem::path destination = archive.path.wstring() + L".locked";
        remove_file(destination);
        {
            std::ofstream seed(destination, std::ios::binary | std::ios::trunc);
            REQUIRE(seed.is_open());
            seed << big;
        }
        {
            const locked_destination lock(destination, static_cast<DWORD>(big.size()));
            REQUIRE(module_extract(mod, storage, 0, destination) == SER_ERROR_WRITE);
        }
        remove_file(destination);
        mod.api.CloseStorage(storage);
    }
    SECTION("backend reports a data failure without calling back")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture", fixture_payload)};
        stub->export_call_callback = false;
        stub->export_result = UNITY_DATA_FAILURE;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        const std::filesystem::path destination = archive.path.wstring() + L".df";
        remove_file(destination);
        REQUIRE(module_extract(mod, storage, 0, destination) == SER_ERROR_READ);
        REQUIRE(!std::filesystem::exists(destination));
        mod.api.CloseStorage(storage);
    }
    SECTION("backend cancels without calling back")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture", fixture_payload)};
        stub->export_call_callback = false;
        stub->export_result = UNITY_CANCELLED;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        const std::filesystem::path destination = archive.path.wstring() + L".cn";
        remove_file(destination);
        REQUIRE(module_extract(mod, storage, 0, destination) == SER_USERABORT);
        REQUIRE(!std::filesystem::exists(destination));
        mod.api.CloseStorage(storage);
    }
    SECTION("backend reports a data failure after streaming part of the entry")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture", fixture_payload)};
        // The callback still delivers the partial entry, then the backend reports a data
        // failure; the partial destination must be removed and the session stay usable.
        stub->export_result = UNITY_DATA_FAILURE;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        const std::filesystem::path destination = archive.path.wstring() + L".dfmid";
        remove_file(destination);
        REQUIRE(module_extract(mod, storage, 0, destination) == SER_ERROR_READ);
        REQUIRE(!std::filesystem::exists(destination));
        REQUIRE(module_extract(mod, storage, 0, destination) == SER_ERROR_READ);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
    SECTION("backend reports an unrecognised result code")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture", fixture_payload)};
        stub->export_call_callback = false;
        stub->export_result = UNITY_INVALID_HANDLE;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        const std::filesystem::path destination = archive.path.wstring() + L".unk";
        remove_file(destination);
        REQUIRE(module_extract(mod, storage, 0, destination) == SER_ERROR_READ);
        REQUIRE(!std::filesystem::exists(destination));
        mod.api.CloseStorage(storage);
    }
}

TEST_CASE("unity adapter: callback protocol violations become read errors", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("negative chunk length")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture", fixture_payload)};
        stub->export_bad_length = true;
        stub->export_bad_length_value = -1;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        const std::filesystem::path destination = archive.path.wstring() + L".neg";
        remove_file(destination);
        REQUIRE(module_extract(mod, storage, 0, destination) == SER_ERROR_READ);
        REQUIRE(!std::filesystem::exists(destination));
        mod.api.CloseStorage(storage);
    }
    SECTION("oversized chunk length")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture", fixture_payload)};
        stub->export_bad_length = true;
        stub->export_bad_length_value = 70'000;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        const std::filesystem::path destination = archive.path.wstring() + L".big";
        remove_file(destination);
        REQUIRE(module_extract(mod, storage, 0, destination) == SER_ERROR_READ);
        REQUIRE(!std::filesystem::exists(destination));
        mod.api.CloseStorage(storage);
    }
    SECTION("null pointer with a positive length")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture", fixture_payload)};
        stub->export_null_positive = true;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        const std::filesystem::path destination = archive.path.wstring() + L".null";
        remove_file(destination);
        REQUIRE(module_extract(mod, storage, 0, destination) == SER_ERROR_READ);
        REQUIRE(!std::filesystem::exists(destination));
        mod.api.CloseStorage(storage);
    }
    SECTION("an empty chunk before real data is accepted")
    {
        stub_binding stub;
        stub->entries = {text_entry(u"fixture", fixture_payload)};
        stub->export_prepend_empty = true;
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        const std::filesystem::path destination = archive.path.wstring() + L".empty";
        remove_file(destination);
        REQUIRE(module_extract(mod, storage, 0, destination) == SER_SUCCESS);
        REQUIRE(read_file(destination) == fixture_payload);
        remove_file(destination);
        mod.api.CloseStorage(storage);
    }
}

TEST_CASE("unity adapter: close and reopen own the session token", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {text_entry(u"fixture", fixture_payload)};
    const temp_archive archive;
    const loaded_module mod;

    {
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(stub->open_calls == 1);
        REQUIRE(mod.api.PrepareFiles(storage) == TRUE);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 1);
    }
    {
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(stub->open_calls == 2);
        mod.api.CloseStorage(storage);
        REQUIRE(stub->close_calls == 2);
    }
}

TEST_CASE("unity adapter: a failed allocation after a live open does not leak the session", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {text_entry(u"fixture", fixture_payload)};
    const open_allocation_fault fault;

    extractor::extractor adapter;
    std::istringstream input;
    const std::vector<std::byte> data;

    // The backend reports a live session, then the adapter's own next allocation (the
    // owning state) fails with std::bad_alloc. The session must be closed as that
    // exception propagates; it must never stay registered. The allocation failure must
    // also keep propagating unchanged rather than being swallowed.
    REQUIRE_THROWS_AS(adapter.get_archive_info(input, data, std::filesystem::path(L"x"), {}), std::bad_alloc);
    REQUIRE(stub->open_calls == 1);
    REQUIRE(stub->close_calls == 1);
}

TEST_CASE("unity adapter: virtual file names follow the sanitising decision table", "[unity][unit]")
{
    stub_binding stub;
    stub->entries = {
        texture_entry(u"fixture"),                              // 0
        text_entry(u"..\\evil/path"),                           // 1
        text_entry(u"a<b>c:d\"e/f\\g|h?i*j\tA"),                // 2
        text_entry(u""),                                        // 3
        texture_entry(u"  ..  "),                               // 4
        text_entry(u"CON"),                                     // 5
        text_entry(u"\u03A9_\u540D\u524D"),                     // 6
        text_entry(std::u16string(100, u'A')),                  // 7
        text_entry(std::u16string(95, u'A') + u"\U0001F600"),   // 8
    };
    const temp_archive archive;
    const loaded_module mod;

    HANDLE storage = nullptr;
    REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
    REQUIRE(mod.api.PrepareFiles(storage) == TRUE);

    REQUIRE(item_path(mod, storage, 0) == L"{Texture2D}\\0_fixture.png");
    REQUIRE(item_path(mod, storage, 1) == L"{TextAsset}\\1_.._evil_path.bin");
    REQUIRE(item_path(mod, storage, 2) == L"{TextAsset}\\2_a_b_c_d_e_f_g_h_i_j_A.bin");
    REQUIRE(item_path(mod, storage, 3) == L"{TextAsset}\\3_resource.bin");
    REQUIRE(item_path(mod, storage, 4) == L"{Texture2D}\\4_resource.png");
    REQUIRE(item_path(mod, storage, 5) == L"{TextAsset}\\5_CON.bin");
    REQUIRE(item_path(mod, storage, 6) == L"{TextAsset}\\6_\u03A9_\u540D\u524D.bin");
    REQUIRE(item_path(mod, storage, 7) == L"{TextAsset}\\7_" + std::wstring(96, L'A') + L".bin");
    REQUIRE(item_path(mod, storage, 8) == L"{TextAsset}\\8_" + std::wstring(95, L'A') + L".bin");

    mod.api.CloseStorage(storage);
}

TEST_CASE("unity adapter: an unpaired surrogate label is rejected", "[unity][unit]")
{
    const temp_archive archive;
    const loaded_module mod;

    SECTION("low surrogate at the cut")
    {
        stub_binding stub;
        stub->entries = {text_entry(std::u16string(95, u'A') + std::u16string(1, char16_t{0xDE00}) + u"\u0041")};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("high surrogate followed by a non-low unit")
    {
        stub_binding stub;
        stub->entries = {text_entry(std::u16string(95, u'A') + std::u16string(1, char16_t{0xD83D}) + u"\u0041")};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
    SECTION("high surrogate followed by a unit past the low range")
    {
        stub_binding stub;
        stub->entries = {text_entry(std::u16string(95, u'A') + std::u16string(1, char16_t{0xD83D}) + std::u16string(1, char16_t{0xE000}))};
        HANDLE storage = nullptr;
        REQUIRE(module_open(mod, archive.path, &storage) == SOR_SUCCESS);
        REQUIRE(mod.api.PrepareFiles(storage) == FALSE);
        mod.api.CloseStorage(storage);
    }
}
