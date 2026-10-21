// UnityExtractor.cpp
//
// Experimental Unity Observer adapter. It opens exactly one selected path through the
// frozen v1 UnitySession C ABI (native/unity_backend.h) by direct linkage, owns the
// returned non-zero session token in storage_state, and exposes the catalogue as a flat,
// bounded, metadata-only listing whose entries carry a custom extract handler. Opening
// and listing never export: both Size and PackedSize carry the declared source payload
// length the backend reports from catalogue metadata, never a converted PNG length, and
// the virtual path preserves the asset's original directory hierarchy when the backend
// reports one. Nothing here loads a library, spawns a process, or converts assets eagerly.

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <exception>
#include <filesystem>
#include <fstream>
#include <functional>
#include <ios>
#include <limits>
#include <memory>
#include <memory_resource>
#include <optional>
#include <set>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

// The catalogue namespace is compared case-insensitively with the Windows ordinal
// primitive, so lean out the platform header exactly as the Observer API layer does.
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "archive.h"
#include "modules/extractor.h"
#include "unity_backend.h"

namespace extractor
{
    namespace
    {
        // A non-zero wchar_t is required to reinterpret the path as UTF-16 units.
        static_assert(sizeof(wchar_t) == sizeof(std::uint16_t), "the Unity ABI takes UTF-16 units");

        // Upper bound on the entry count the backend may report. It keeps a corrupt count
        // from driving an unbounded listing allocation while leaving ample headroom. A
        // selected resource bundle exposes both the Texture2D and the Sprite view of a
        // catalogue, so a valid shipping catalogue reaches 281151 entries; the finite
        // ceiling is twice the former 200000 budget to cover that texture-plus-sprite
        // count without bounding every real catalogue.
        constexpr std::int32_t max_entries = 400'000;

        // Upper bound on the UTF-16 units copied for a single label. Only the leading
        // slice is ever kept, but the whole label is copied once so a truncated slice can
        // be cut on a surrogate boundary.
        constexpr std::uint32_t max_name_units = 0x7FFF;

        // Largest label slice kept in a virtual file name, measured before sanitising.
        constexpr std::size_t max_label_units = 96;

        // Largest single original-path component kept, measured before sanitising. A longer
        // component is not truncated: the whole original path falls back to the braced kind
        // folder instead of producing a confusing or unsafe name.
        constexpr std::size_t max_component_units = 96;

        // Hard cap on the UTF-16 units of a produced virtual path: Observer's fixed path
        // buffer holds 1024 units including the terminator.
        constexpr std::size_t max_path_units = 1023;

        // Conservative cumulative bound on the metadata the two-pass listing retains while it
        // resolves the whole catalogue namespace: the bounded fallback label, output extension
        // and preserved path of each entry plus every distinct ancestor directory key. It is
        // charged before the
        // matching retained allocation, so a catalogue with many long or deeply distinct
        // original paths fails cleanly with a read error instead of growing to gigabytes of
        // interior-prefix copies. A realistic catalogue stays far below it (the entry cap alone
        // bounds the per-entry part), and the guard is roughly a hundredfold below the raw
        // name retention it replaces.
        constexpr std::size_t max_catalogue_metadata_bytes = 128 * 1024 * 1024;

        // Frozen per-callback byte bound of the session ABI.
        constexpr std::int32_t max_chunk_bytes = 65536;

        // The frozen ABI extension contract for the dynamic kinds: a dotless lower-case
        // ASCII a-z0-9 string of 1..16 units. It bounds every extended path component and
        // the retained extension metadata.
        constexpr std::int32_t max_extension_units = 16;

        // Reusable per-entry read buffers. One fixed combined metadata call covers the common
        // small entry in a single ABI round trip; only a field longer than these bounds pays one
        // exact-size retry, so a normal entry costs exactly one call instead of one per field.
        constexpr std::size_t initial_name_units = 512;
        constexpr std::size_t initial_path_units = 2048;

        // Per-call stack scratch for lazy path materialisation. The healthy short entry
        // fits entirely in caller-frame storage, so a normal GetItem performs no heap
        // allocation; a field that outgrows the arena falls back to its upstream
        // allocator, keeping the existing bounded retry and long-field behaviour.
        constexpr std::size_t get_item_scratch_bytes = 16 * 1024;

        // Per-entry stack arena for Prepare validation. Healthy short metadata is rebuilt
        // entirely in caller-frame storage; a field that outgrows the arena spills to its
        // upstream allocator and is reclaimed by that iteration's arena destructor.
        constexpr std::size_t prepare_scratch_bytes = 16 * 1024;

        using scratch_string = std::pmr::wstring;
        using scratch_units = std::pmr::vector<std::uint16_t>;

        // The widest braced fallback folder ({MovieTexture}) fixes the widest fallback
        // path any bounded entry can produce (folder + separator + index + '_' + label +
        // '.' + extension), so the fallback needs no runtime length branch. A preserved
        // original path is checked against the same cap at runtime.
        constexpr std::wstring_view widest_fallback_folder = L"{MovieTexture}";
        static_assert(widest_fallback_folder.size() + 1 + 6 + 1 + max_label_units + 1 + max_extension_units <=
                          max_path_units,
                      "widest fallback virtual path fits the Observer path");

        // Closes one live backend session. This is the single close policy for the whole
        // adapter: a zero token means there is no session, so it is ignored, and closing
        // never throws across C.
        void close_session(const std::uint64_t token) noexcept
        {
            if (token != 0) {
                static_cast<void>(::unpackrat_unity_close(token));
            }
        }

        // Owns the session token for the lifetime of one open archive and closes it on
        // destruction. It is only ever constructed with a token the backend has already
        // reported as non-zero, so it always owns a live session.
        // The catalogue retains only the indices whose preserved path collides with a
        // directory another entry needs; every valid path is materialised on demand into a
        // caller-owned buffer. That buffer is owned by the GetItem frame and the returned
        // path never outlives the call, so the catalogue exposes no retained path storage.
        class unity_state final : public storage_state, public indexed_catalogue
        {
          public:
            unity_state(const std::uint64_t token, const std::int32_t count) noexcept : token_(token), count_(count)
            {
            }

            ~unity_state() override
            {
                close_session(token_);
            }

            unity_state(const unity_state &) = delete;
            unity_state &operator=(const unity_state &) = delete;
            unity_state(unity_state &&) = delete;
            unity_state &operator=(unity_state &&) = delete;

            void prepare() override;

            [[nodiscard]] indexed_catalogue *indexed_files() noexcept override { return this; }

            [[nodiscard]] std::size_t count() const noexcept override
            {
                return prepared_ ? static_cast<std::size_t>(count_) : 0;
            }

            // Lazy materialisation: the retained conflict set plus the backend metadata are
            // enough to rebuild the exact path and sizes the retained catalogue used to hold.
            [[nodiscard]] std::optional<column> path_into(std::size_t index, std::span<char> buffer) const override;
            [[nodiscard]] std::optional<column> wide_path_into(std::size_t index,
                                                               std::span<wchar_t> buffer) const override;

            void extract(std::size_t index, std::ostream &output,
                         const std::function<void(std::int64_t)> &progress) const override;

          private:
            std::uint64_t token_;
            std::int32_t count_;
            bool prepared_ = false;
            // Sorted, so membership is a binary search and the common collision-free
            // catalogue retains an empty vector instead of a per-entry path table.
            std::vector<std::int32_t> conflicts_;
        };

        // Owns the raw session token from before the backend can produce it, and closes it
        // on any exception path unless ownership is released. Constructing the guard
        // allocates nothing and its destructor does not throw, so a live session is never
        // left unowned: a failing allocation that follows a successful open (for example
        // the std::bad_alloc raised while the long-lived unity_state is allocated) can no
        // longer leak it.
        class unity_session_guard final
        {
          public:
            explicit unity_session_guard(std::uint64_t &token) noexcept : token_(token)
            {
            }

            ~unity_session_guard()
            {
                close_session(token_);
            }

            unity_session_guard(const unity_session_guard &) = delete;
            unity_session_guard &operator=(const unity_session_guard &) = delete;
            unity_session_guard(unity_session_guard &&) = delete;
            unity_session_guard &operator=(unity_session_guard &&) = delete;

            // Transfers ownership to the caller; the guard then closes nothing.
            void release() noexcept
            {
                token_ = 0;
            }

          private:
            std::uint64_t &token_;
        };

        [[nodiscard]] bool is_forbidden(const wchar_t unit) noexcept
        {
            if (unit < 0x20) {
                // C0 controls and NUL are never legal in a Windows file name.
                return true;
            }
            switch (unit) {
            case L'<':
            case L'>':
            case L':':
            case L'"':
            case L'/':
            case L'\\':
            case L'|':
            case L'?':
            case L'*':
                return true;
            default:
                return false;
            }
        }

        // Ordinal ASCII case-insensitive equality for the reserved names below. Windows never
        // folds anything but ASCII letters when it matches these reserved tokens.
        [[nodiscard]] bool ascii_case_insensitive_equal(const std::wstring_view lhs,
                                                        const std::wstring_view rhs) noexcept
        {
            if (lhs.size() != rhs.size()) {
                return false;
            }
            for (std::size_t index = 0; index < lhs.size(); ++index) {
                wchar_t left = lhs[index];
                if (left >= L'a' && left <= L'z') {
                    left = static_cast<wchar_t>(left - L'a' + L'A');
                }
                wchar_t right = rhs[index];
                if (right >= L'a' && right <= L'z') {
                    right = static_cast<wchar_t>(right - L'a' + L'A');
                }
                if (left != right) {
                    return false;
                }
            }
            return true;
        }

        // Windows reserves a small set of device names in every directory. Only the portion of
        // a component before its first dot names the device, so "NUL.txt", "nUl.txt" and
        // "CON.any" are all the reserved device; the comparison is ASCII case-insensitive. The
        // list includes COM/LPT followed by superscript one/two/three, which Win32 also reserves.
        [[nodiscard]] bool is_reserved_device_name(const std::wstring_view name) noexcept
        {
            static constexpr std::wstring_view reserved[] = {
                L"CON",  L"PRN",  L"AUX",  L"NUL",  L"COM1", L"COM2", L"COM3", L"COM4",
                L"COM5", L"COM6", L"COM7", L"COM8", L"COM9", L"LPT1", L"LPT2", L"LPT3",
                L"LPT4", L"LPT5", L"LPT6", L"LPT7", L"LPT8", L"LPT9", L"COM\u00B9",
                L"COM\u00B2", L"COM\u00B3", L"LPT\u00B9", L"LPT\u00B2", L"LPT\u00B3",
            };
            const std::size_t dot = name.find(L'.');
            const std::wstring_view device =
                name.substr(0, dot == std::wstring_view::npos ? name.size() : dot);
            for (const std::wstring_view candidate : reserved) {
                if (ascii_case_insensitive_equal(device, candidate)) {
                    return true;
                }
            }
            return false;
        }

        // The braced kind folders are the fallback namespace: only unsafe entries ever land
        // there, so a preserved path must never enter it. An original path whose root directory
        // is exactly one of those names is therefore reserved and the entry falls back; every
        // other brace name is an ordinary directory and keeps its braces.
        [[nodiscard]] bool is_reserved_fallback_root(const std::wstring_view root) noexcept
        {
            static constexpr std::wstring_view reserved[] = {
                L"{Texture2D}", L"{TextAsset}",  L"{Font}",  L"{VideoClip}",
                L"{MovieTexture}", L"{AudioClip}", L"{Mesh}", L"{Sprite}",
            };
            for (const std::wstring_view candidate : reserved) {
                if (ascii_case_insensitive_equal(root, candidate)) {
                    return true;
                }
            }
            return false;
        }

        // The braced fallback folder of an entry kind, or null for an unknown kind. The
        // eight names are the frozen external kind table's directory fallback; a caller
        // rejects an entry whose kind has no folder before it is used.
        [[nodiscard]] const wchar_t *fallback_folder(const int kind) noexcept
        {
            switch (kind) {
            case UNITY_KIND_TEXTURE_PNG:
                return L"{Texture2D}";
            case UNITY_KIND_TEXT_RAW:
                return L"{TextAsset}";
            case UNITY_KIND_FONT:
                return L"{Font}";
            case UNITY_KIND_VIDEO_CLIP:
                return L"{VideoClip}";
            case UNITY_KIND_MOVIE_TEXTURE:
                return L"{MovieTexture}";
            case UNITY_KIND_AUDIO_CLIP:
                return L"{AudioClip}";
            case UNITY_KIND_MESH:
                return L"{Mesh}";
            case UNITY_KIND_SPRITE:
                return L"{Sprite}";
            default:
                return nullptr;
            }
        }

        // The output extension the frozen kind table pins for a kind, or null when the
        // kind carries a dynamic extension read from the additive ABI getter.
        [[nodiscard]] const wchar_t *fixed_extension(const int kind) noexcept
        {
            switch (kind) {
            case UNITY_KIND_TEXTURE_PNG:
            case UNITY_KIND_SPRITE:
                return L"png";
            case UNITY_KIND_TEXT_RAW:
                return L"bin";
            case UNITY_KIND_MOVIE_TEXTURE:
                return L"ogv";
            case UNITY_KIND_MESH:
                return L"glb";
            default:
                // Font, VideoClip and AudioClip report their extension as metadata.
                return nullptr;
            }
        }

        // One unit of a dynamic extension: a dotless lower-case ASCII a-z0-9 unit. A
        // separator, dot, space, digit-free punctuation or any case-mapped unit is
        // rejected before the extension can shape a virtual path.
        [[nodiscard]] bool is_extension_unit(const wchar_t unit) noexcept
        {
            return (unit >= L'a' && unit <= L'z') || (unit >= L'0' && unit <= L'9');
        }

        // Appends a non-negative catalogue index in decimal without a temporary owning
        // string. The entry bound keeps the value inside ten digits.
        void append_index(scratch_string &out, std::int32_t index)
        {
            wchar_t digits[10];
            std::size_t count = 0;
            do {
                digits[count++] = static_cast<wchar_t>(L'0' + index % 10);
                index /= 10;
            } while (index > 0);
            while (count > 0) {
                out.push_back(digits[--count]);
            }
        }

        // One bounded original-path component. It rejects an empty or over-long component, a
        // component that sanitises to nothing, and a reserved device name, so a single bad
        // component falls the whole original path back rather than producing a mangled name.
        [[nodiscard]] std::optional<scratch_string> sanitise_component(const std::wstring_view raw,
                                                                       std::pmr::memory_resource *const resource)
        {
            if (raw.empty() || raw.size() > max_component_units) {
                return std::nullopt;
            }
            scratch_string component(resource);
            component.reserve(raw.size());
            for (const wchar_t unit : raw) {
                component.push_back(is_forbidden(unit) ? L'_' : unit);
            }
            while (!component.empty() && (component.back() == L'.' || component.back() == L' ')) {
                component.pop_back();
            }
            if (component.empty() || is_reserved_device_name(component)) {
                return std::nullopt;
            }
            return component;
        }

        // True when the UTF-16 units are well formed: every high surrogate is followed by a
        // low surrogate, and no low surrogate is left unpaired. An original path that fails
        // this can never become a valid Windows name, so the entry falls back to the braced
        // kind folder instead of the whole archive being rejected. The metadata label is
        // validated separately by the UTF-8 conversion, which still rejects a bad label.
        template <typename Unit>
        [[nodiscard]] bool is_well_formed_utf16(const std::span<const Unit> units) noexcept
        {
            bool expecting_low = false;
            for (const Unit raw : units) {
                const auto unit = static_cast<std::uint32_t>(raw);
                if (expecting_low) {
                    if (unit < 0xDC00U || unit > 0xDFFFU) {
                        return false;
                    }
                    expecting_low = false;
                } else if (unit >= 0xD800U && unit <= 0xDBFFU) {
                    expecting_low = true;
                } else if (unit >= 0xDC00U && unit <= 0xDFFFU) {
                    return false;
                }
            }
            return !expecting_low;
        }

        [[nodiscard]] bool is_well_formed_wide(const std::wstring_view units) noexcept
        {
            return is_well_formed_utf16(std::span<const wchar_t>(units.data(), units.size()));
        }

        // Builds the preserved virtual path from an original asset path: the original parent
        // directories, the unique numeric catalogue index, the original base name without its
        // extension, and the entry kind's own extension. Returns nullopt for every rooted,
        // traversal, reserved, unnameable or over-long path so the caller uses the safe braced
        // fallback; it never truncates a component or emits a path over the Observer cap.
        [[nodiscard]] std::optional<scratch_string> original_virtual_path(
            const std::span<const std::uint16_t> raw, const std::int32_t index,
            const std::wstring_view extension, std::pmr::memory_resource *const resource)
        {
            if (raw.empty()) {
                return std::nullopt;
            }
            if (!is_well_formed_utf16(raw)) {
                // An unpaired original is unusable, so the entry falls back rather than the
                // UTF-8 conversion later rejecting the whole archive.
                return std::nullopt;
            }
            const scratch_string original(raw.begin(), raw.end(), resource);
            if (original[0] == L'\\' || original[0] == L'/' ||
                (original.size() >= 2 && original[1] == L':')) {
                return std::nullopt;
            }

            std::pmr::vector<std::wstring_view> components(resource);
            std::size_t start = 0;
            for (std::size_t position = 0; position <= original.size(); ++position) {
                if (position == original.size() || original[position] == L'/' || original[position] == L'\\') {
                    components.emplace_back(std::wstring_view(original).substr(start, position - start));
                    start = position + 1;
                }
            }
            for (const std::wstring_view component : components) {
                if (component.empty() || component == L"." || component == L"..") {
                    return std::nullopt;
                }
            }

            const std::wstring_view basename = components.back();
            const std::size_t dot = basename.find_last_of(L'.');
            const std::wstring_view stem =
                (dot == std::wstring_view::npos) ? basename : basename.substr(0, dot);

            // The output is built from bounded sanitised components only; its capacity must
            // never be reserved from the stripped raw original, or a huge ignored extension
            // would inflate the retained allocation despite the short emitted path.
            scratch_string path(resource);
            for (std::size_t component_index = 0; component_index + 1 < components.size(); ++component_index) {
                const std::optional<scratch_string> directory =
                    sanitise_component(components[component_index], resource);
                if (!directory) {
                    return std::nullopt;
                }
                if (component_index == 0 && is_reserved_fallback_root(*directory)) {
                    // The root cannot share the braced fallback namespace.
                    return std::nullopt;
                }
                path += *directory;
                path += L'\\';
            }
            const std::optional<scratch_string> label = sanitise_component(stem, resource);
            if (!label) {
                return std::nullopt;
            }
            append_index(path, index);
            path += L'_';
            path += *label;
            path += L'.';
            path += extension;
            if (path.size() > max_path_units) {
                return std::nullopt;
            }
            return path;
        }

        // Copies at most max_label_units units of a metadata label, replacing every
        // Windows path-illegal unit with '_', never splitting a surrogate pair at the cut,
        // trimming trailing dots and spaces, and falling back to "resource" when the
        // result is empty. Unicode is preserved.
        [[nodiscard]] scratch_string sanitise_label(const std::span<const std::uint16_t> raw,
                                                    std::pmr::memory_resource *const resource)
        {
            std::size_t limit = std::min(raw.size(), max_label_units);
            if (limit < raw.size() && raw[limit - 1] >= 0xD800U && raw[limit - 1] <= 0xDBFFU && raw[limit] >= 0xDC00U &&
                raw[limit] <= 0xDFFFU) {
                // The cut would orphan the high surrogate; drop it so the pair stays whole.
                --limit;
            }

            scratch_string label(resource);
            label.reserve(limit);
            for (std::size_t index = 0; index < limit; ++index) {
                const wchar_t unit = static_cast<wchar_t>(raw[index]);
                label.push_back(is_forbidden(unit) ? L'_' : unit);
            }
            while (!label.empty() && (label.back() == L'.' || label.back() == L' ')) {
                label.pop_back();
            }
            if (label.empty()) {
                return scratch_string(L"resource", resource);
            }
            return label;
        }

        // Case-insensitive ordinal key for the catalogue-wide virtual namespace. Windows
        // resolves a file name case-insensitively with a Unicode ordinal fold, and
        // CompareStringOrdinal is the documented primitive for exactly that, so two names the
        // file system cannot tell apart collapse to a single key here. The key is transparent so
        // an ancestor prefix can be looked up without first allocating an owning string.
        struct caseless_less
        {
            using is_transparent = void;

            [[nodiscard]] bool operator()(const std::wstring_view lhs, const std::wstring_view rhs) const noexcept
            {
                return ::CompareStringOrdinal(lhs.data(), static_cast<int>(lhs.size()), rhs.data(),
                                              static_cast<int>(rhs.size()), TRUE) == CSTR_LESS_THAN;
            }
        };

        using name_set = std::set<std::wstring, caseless_less>;

        // Charges `bytes` of retained catalogue metadata against the cumulative budget before the
        // matching allocation happens, so a catalogue with many long or deeply distinct original
        // paths fails with a bounded read error instead of growing without bound.
        void charge_metadata(std::size_t &retained, const std::size_t bytes)
        {
            if (retained + bytes > max_catalogue_metadata_bytes) {
                throw read_error();
            }
            retained += bytes;
        }

        // The bytes an owned string keeps alive: its allocation is its capacity plus the
        // terminator slot, so a retained string is charged by capacity rather than by its
        // logical length. This is the character payload; the small fixed per-entry and per-node
        // container overhead is separately bounded by the entry cap and the key count.
        [[nodiscard]] std::size_t retained_string_bytes(const std::wstring &value) noexcept
        {
            return (value.capacity() + 1) * sizeof(wchar_t);
        }

        // Parses the leading catalogue index of a preserved file-name component of the form
        // "<digits>_<label>.<ext>". Returns -1 when the component is not a preserved file name
        // or its index is out of range, so a plain directory never becomes a candidate.
        // The sole caller invokes this only for a component that already contains '.', so the
        // digit scan always stops before the end and the index is safe to read.
        [[nodiscard]] std::int32_t parse_leading_index(const std::wstring_view component, const std::int32_t count)
        {
            std::int64_t value = 0;
            std::size_t digits = 0;
            while (component[digits] >= L'0' && component[digits] <= L'9') {
                value = value * 10 + (component[digits] - L'0');
                if (value >= count) {
                    return -1;
                }
                ++digits;
            }
            if (digits == 0 || component[digits] != L'_') {
                return -1;
            }
            return static_cast<std::int32_t>(value);
        }

        // Records every proper ancestor directory of a preserved path and charges each new key
        // against the budget by its retained capacity. A prefix whose own last component is a
        // preserved file name is recorded as a conflict candidate: only such a prefix can equal
        // another entry's preserved path, so the candidate set stays sparse.
        void record_required_directories(const std::wstring_view path, name_set &dest, std::size_t &retained,
                                         std::set<std::int32_t> &candidates, const std::int32_t count)
        {
            std::size_t component_start = 0;
            for (std::size_t position = path.find(L'\\'); position != std::wstring_view::npos;
                 position = path.find(L'\\', position + 1)) {
                const std::wstring_view component = path.substr(component_start, position - component_start);
                if (component.find(L'.') != std::wstring_view::npos) {
                    const std::int32_t candidate = parse_leading_index(component, count);
                    if (candidate >= 0) {
                        candidates.insert(candidate);
                    }
                }
                component_start = position + 1;
                const std::wstring_view prefix = path.substr(0, position);
                if (dest.find(prefix) != dest.end()) {
                    continue;
                }
                std::wstring key(prefix);
                charge_metadata(retained, retained_string_bytes(key));
                dest.emplace(std::move(key));
            }
        }

        // Copies one validated wide path directly into the caller-owned UTF-8 buffer. An
        // invalid unit and an undersized buffer are the same read error the filesystem
        // conversion used to report, and no intermediate owning string is created.
        [[nodiscard]] std::size_t to_utf8_into(const std::wstring_view wide, const std::span<char> buffer)
        {
            if (buffer.empty()) {
                throw read_error();
            }
            const std::size_t capacity =
                std::min(buffer.size(), static_cast<std::size_t>(std::numeric_limits<int>::max()));
            const int written = ::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(),
                                                      static_cast<int>(wide.size()), buffer.data(),
                                                      static_cast<int>(capacity), nullptr, nullptr);
            if (written <= 0) {
                throw read_error();
            }
            return static_cast<std::size_t>(written);
        }

        // The braced fallback virtual path of one entry: the safe kind folder, the unique
        // numeric index, the bounded label and the output extension.
        [[nodiscard]] scratch_string fallback_path(const std::int32_t index, const int kind,
                                                   const std::wstring_view label,
                                                   const std::wstring_view extension,
                                                   std::pmr::memory_resource *const resource)
        {
            scratch_string wide(fallback_folder(kind), resource);
            wide += L'\\';
            append_index(wide, index);
            wide += L'_';
            wide += label;
            wide += L'.';
            wide += extension;
            return wide;
        }

        // One entry's validated, bounded metadata as read from the backend. Nothing here is
        // retained beyond the call: prepare reuses it to build the conflict set, and path_into
        // rebuilds the final path from it on demand.
        struct entry_metadata final
        {
            int kind = 0;
            scratch_string label;
            scratch_string extension;
            std::optional<scratch_string> preserved;
            std::int64_t source_size = 0;

            explicit entry_metadata(std::pmr::memory_resource *const resource)
                : label(resource), extension(resource)
            {
            }
        };

        // Reads and validates exactly one entry. Every rejection path throws read_error so a
        // corrupt catalogue tears the session down. The name and original buffers are
        // caller-owned scratch, reused across entries and never retained. One combined getter
        // reports the descriptor, the declared source length and all three field lengths, and
        // copies the name and original path it fits, so a normal entry costs one ABI call.
        [[nodiscard]] entry_metadata read_entry(const std::uint64_t token, const std::int32_t index,
                                                scratch_units &name, scratch_units &original,
                                                std::pmr::memory_resource *const resource)
        {
            // Fixed reusable capacity makes the common small entry fit in the first call. A
            // field longer than this bound is reported by the same call and retried exactly once.
            if (name.size() < initial_name_units) {
                name.resize(initial_name_units);
            }
            if (original.size() < initial_path_units) {
                original.resize(initial_path_units);
            }
            // The dynamic kind's output extension is a bounded 1..max_extension_units token, so
            // one fixed staged buffer covers it with no per-entry allocation. The combined call
            // reports and copies it too, so a Font/VideoClip/AudioClip entry is one call as well.
            std::uint16_t extension[max_extension_units]{};

            UnityMetadata meta{};
            meta.struct_size = sizeof(UnityMetadata);
            const auto read = [&](std::uint16_t *const name_buffer, const std::int32_t name_capacity,
                                  std::uint16_t *const original_buffer, const std::int32_t original_capacity,
                                  std::uint16_t *const extension_buffer, const std::int32_t extension_capacity) {
                return ::unpackrat_unity_get_metadata(token, index, &meta, name_buffer, name_capacity,
                                                      original_buffer, original_capacity, extension_buffer,
                                                      extension_capacity);
            };
            std::int32_t status = read(name.data(), static_cast<std::int32_t>(name.size()), original.data(),
                                       static_cast<std::int32_t>(original.size()), extension,
                                       static_cast<std::int32_t>(max_extension_units));

            if (status != UNITY_OK && status != UNITY_BUFFER_TOO_SMALL) {
                throw read_error();
            }
            if (meta.struct_size != sizeof(UnityMetadata) || meta.reserved != 0 || meta.reserved2 != 0) {
                throw read_error();
            }
            if (meta.name_length < 0 || meta.original_length < 0 || meta.extension_length < 0 ||
                meta.source_size < 0) {
                throw read_error();
            }
            // Only the eight known resource kinds are listed; an unknown id is corrupt
            // metadata and tears the session down rather than sliding into a fallback.
            if (fallback_folder(meta.kind) == nullptr) {
                throw read_error();
            }
            // A name longer than the copy ceiling is never allocated or truncated: it is
            // corrupt metadata and rejects the entry.
            if (static_cast<std::uint32_t>(meta.name_length) > max_name_units) {
                throw read_error();
            }
            // A fixed kind takes its output extension from the frozen table and ignores the
            // reported field; a dynamic kind must report a bounded 1..16 unit token, and
            // anything else is corrupt metadata that rejects the entry before the extension
            // can shape a virtual path.
            const wchar_t *const fixed = fixed_extension(meta.kind);
            const bool dynamic = fixed == nullptr;
            if (dynamic && (meta.extension_length < 1 || meta.extension_length > max_extension_units)) {
                throw read_error();
            }

            // The original asset path is metadata only and may be absent. An over-long declared
            // path is never copied or truncated: it is treated as absent so the entry falls back
            // to the safe braced kind folder, which keeps a hostile catalogue from driving a
            // large allocation or an over-long Observer path.
            const bool original_present =
                meta.original_length > 0 && static_cast<std::uint32_t>(meta.original_length) <= max_name_units;

            if (status == UNITY_BUFFER_TOO_SMALL) {
                // Every length is reported on a short-buffer call, so the exact buffers are
                // already known and one sized retry finishes the read. An absent path is passed
                // as a size query so the retry cannot report the same field too small again; a
                // fixed kind drops the extension it never uses.
                //
                // The retry reports into the same descriptor and must fill the buffers this call
                // sized from one entry's truth. Remember those immutable fields so a second call
                // that answers with changed truth is rejected before any of it is used.
                const std::int32_t first_kind = meta.kind;
                const std::int32_t first_name_length = meta.name_length;
                const std::int32_t first_original_length = meta.original_length;
                const std::int32_t first_extension_length = meta.extension_length;
                const std::int64_t first_source_size = meta.source_size;
                name.assign(static_cast<std::size_t>(meta.name_length), 0);
                original.clear();
                if (original_present) {
                    original.assign(static_cast<std::size_t>(meta.original_length), 0);
                }
                status = read(name.data(), static_cast<std::int32_t>(name.size()),
                              original_present ? original.data() : nullptr,
                              original_present ? static_cast<std::int32_t>(original.size()) : 0,
                              dynamic ? extension : nullptr,
                              dynamic ? static_cast<std::int32_t>(max_extension_units) : 0);
                if (status != UNITY_OK) {
                    throw read_error();
                }
                if (meta.struct_size != sizeof(UnityMetadata) || meta.reserved != 0 || meta.reserved2 != 0) {
                    throw read_error();
                }
                // The retry buffers were sized from the first call, so the second call must report
                // the same immutable descriptor: a changed kind, length or source size would let it
                // escape those bounds (a negative length a huge allocation, a wider dynamic
                // extension a stack read). Unchanged fields keep every copied length within the
                // exact buffer capacities the retry was handed.
                if (meta.kind != first_kind || meta.name_length != first_name_length ||
                    meta.original_length != first_original_length ||
                    meta.extension_length != first_extension_length ||
                    meta.source_size != first_source_size) {
                    throw read_error();
                }
            }

            // Trim the reusable buffers to exactly the units the call copied, so the label and
            // path helpers see the same spans the scalar reads used to produce.
            name.resize(static_cast<std::size_t>(meta.name_length));
            if (original_present) {
                original.resize(static_cast<std::size_t>(meta.original_length));
            } else {
                original.clear();
            }

            entry_metadata result(resource);
            result.kind = meta.kind;
            result.label = sanitise_label(name, resource);
            // The extension is the frozen table value for a fixed kind and the combined getter's
            // validated dynamic token otherwise, so it is bounded and checked either way.
            if (!dynamic) {
                result.extension = scratch_string(fixed, resource);
            } else {
                result.extension.reserve(static_cast<std::size_t>(meta.extension_length));
                for (std::int32_t unit = 0; unit < meta.extension_length; ++unit) {
                    const wchar_t wide = static_cast<wchar_t>(extension[static_cast<std::size_t>(unit)]);
                    if (!is_extension_unit(wide)) {
                        throw read_error();
                    }
                    result.extension.push_back(wide);
                }
            }
            result.preserved = original_virtual_path(original, index, result.extension, resource);
            result.source_size = meta.source_size;
            return result;
        }

        [[nodiscard]] const scratch_string &materialise_path(const entry_metadata &entry, const bool conflict,
                                                             const std::int32_t index, scratch_string &fallback,
                                                             std::pmr::memory_resource *const resource)
        {
            if (entry.preserved && !conflict) {
                return *entry.preserved;
            }
            fallback = fallback_path(index, entry.kind, entry.label, entry.extension, resource);
            return fallback;
        }

        struct export_bridge final
        {
            std::ostream *output = nullptr;
            const std::function<void(std::int64_t)> *progress = nullptr;
            std::exception_ptr saved;
            bool protocol_violation = false;
        };

        // The session ABI write callback. It never lets an exception cross C: an output IO
        // or cancellation failure is captured in the bridge and rethrown only after the
        // export call returns. A malformed chunk is flagged without allocating anything.
        std::int32_t UNPACKRAT_UNITY_CDECL bridge_write(void *context, const std::uint8_t *bytes,
                                                        const std::int32_t length) noexcept
        {
            auto *bridge = static_cast<export_bridge *>(context);
            if (length < 0 || length > max_chunk_bytes) {
                bridge->protocol_violation = true;
                return 2;
            }
            if (length == 0) {
                return 0;
            }
            if (bytes == nullptr) {
                bridge->protocol_violation = true;
                return 2;
            }
            try {
                bridge->output->write(reinterpret_cast<const char *>(bytes), static_cast<std::streamsize>(length));
                (*bridge->progress)(length);
            } catch (const archive::user_interrupt &) {
                bridge->saved = std::current_exception();
                return 1;
            } catch (...) {
                bridge->saved = std::current_exception();
                return 2;
            }
            return 0;
        }

        // Dispatches one entry through the session owner. It borrows the archive's output
        // stream and progress callback for the duration of the call and rethrows any saved
        // failure after the C ABI has returned, preserving the archive's IO, cancel and
        // allocation semantics. The archive owns this state through every export call.
        void unity_state::extract(const std::size_t index, std::ostream &output,
                                  const std::function<void(std::int64_t)> &progress) const
        {
            if (index >= count()) throw std::out_of_range("Index out of range");
            const std::uint64_t token = token_;
            export_bridge bridge;
            bridge.output = &output;
            bridge.progress = &progress;
            const std::int32_t result = ::unpackrat_unity_export(token, static_cast<std::int32_t>(index), &bridge_write, &bridge);
            if (bridge.saved) {
                std::rethrow_exception(bridge.saved);
            }
            if (bridge.protocol_violation) {
                throw read_error();
            }
            switch (result) {
            case UNITY_OK:
                return;
            case UNITY_CANCELLED:
                throw archive::user_interrupt();
            case UNITY_WRITE_FAILURE:
                throw std::ios_base::failure("unity export write failure");
            case UNITY_DATA_FAILURE:
            default:
                throw read_error();
            }
        }
    } // namespace

    void configure([[maybe_unused]] const wchar_t *settings) noexcept
    {
    }

    void reset_settings() noexcept
    {
    }

    version_info get_version_info() noexcept
    {
        return {
            {0x4e91f7c6, 0x2a5d, 0x4973, {0x9b, 0x2c, 0x8f, 0x53, 0x71, 0xd6, 0x0a, 0x2e}},
            0,
            3,
        };
    }

    std::vector<std::byte> extractor::get_signature()
    {
        // An empty signature accepts every input, so the real backend decides whether the
        // selected file is a serialized Unity file or a bundle.
        return {};
    }

    archive_info extractor::get_archive_info([[maybe_unused]] std::istream &stream,
                                             [[maybe_unused]] const std::span<const std::byte> &data,
                                             const std::filesystem::path &path,
                                             [[maybe_unused]] const std::string_view password)
    {
        const std::wstring wide = path.wstring();
        if (wide.empty() || wide.size() > 0x7FFF) {
            throw read_error();
        }

        std::uint64_t token = 0;
        std::int32_t count = 0;
        // The guard owns any session the backend creates, from before the call, so the
        // token is owned the instant it exists and every failure path below releases it.
        unity_session_guard session(token);
        const std::int32_t result = ::unpackrat_unity_open(reinterpret_cast<const std::uint16_t *>(wide.data()),
                                                           static_cast<std::int32_t>(wide.size()), &token, &count);
        if (result != UNITY_OK || token == 0) {
            throw read_error();
        }
        if (count < 0 || count > max_entries) {
            // The guard closes the freshly opened session as this failure unwinds.
            throw read_error();
        }

        // Allocate the long-lived owner while the guard still owns the session, then hand
        // ownership over. If the allocation throws, the guard closes the session; once it
        // succeeds, the transfer is noexcept.
        auto owned = std::make_unique<unity_state>(token, count);
        session.release();
        state_ = std::move(owned);
        return archive_info{L"Unity", L"", L""};
    }

    void unity_state::prepare()
    {
        if (prepared_) return;
        const std::uint64_t token = token_;
        const std::int32_t count = count_;

        // One metadata pass: validate every entry, build the transient required-directory set
        // and collect the indices whose preserved path could be one of those directories. The
        // raw name and original buffers reuse bounded heap capacity across the whole pass;
        // only the sparse confirmation below can retain an index.
        name_set required_directories;
        std::size_t retained_bytes = 0;
        std::set<std::int32_t> candidates;

        // The raw ABI buffers stay on the standard heap so their capacity survives every
        // iteration. Per-entry metadata uses an automatic stack arena constructed inside each
        // iteration: its destructor runs after the entry and temporary path, reclaiming any
        // upstream spill, and the next constructor starts again from the caller-owned stack
        // block. The arena must not live across iterations because release() does not reattach
        // the initial buffer after an upstream chunk has been allocated on this toolchain.
        std::pmr::memory_resource *const raw_resource = std::pmr::new_delete_resource();
        scratch_units name(raw_resource);
        scratch_units original(raw_resource);
        alignas(std::max_align_t) std::array<std::byte, prepare_scratch_bytes> prepare_scratch;
        // The existing Observer cap allows four UTF-8 bytes per usable UTF-16 unit.
        std::array<char, max_path_units * 4> fallback_utf8{};

        for (std::int32_t index = 0; index < count; ++index) {
            std::pmr::monotonic_buffer_resource arena(prepare_scratch.data(), prepare_scratch.size(),
                                                      std::pmr::new_delete_resource());
            const entry_metadata entry = read_entry(token, index, name, original, &arena);
            if (entry.preserved) {
                record_required_directories(*entry.preserved, required_directories, retained_bytes, candidates,
                                            count);
            } else {
                // A fallback entry's label must convert into a valid UTF-8 path now: an
                // unusable label is corrupt metadata and fails preparation atomically, exactly
                // as the retained catalogue did when it materialised the fallback.
                const scratch_string wide =
                    fallback_path(index, entry.kind, entry.label, entry.extension, &arena);
                static_cast<void>(to_utf8_into(wide, std::span<char>(fallback_utf8)));
            }
        }

        // Sparse confirmation: only the candidate indices are re-read, so the collision-free
        // catalogue pays no extra reads. A candidate is a real conflict exactly when its own
        // preserved path is one of the required directories.
        std::vector<std::int32_t> conflicts;
        for (const std::int32_t candidate : candidates) {
            std::pmr::monotonic_buffer_resource arena(prepare_scratch.data(), prepare_scratch.size(),
                                                      std::pmr::new_delete_resource());
            const entry_metadata entry = read_entry(token, candidate, name, original, &arena);
            if (entry.preserved && required_directories.contains(*entry.preserved)) {
                // This entry is forced to its braced fallback, so an unusable label must fail
                // preparation atomically here, exactly as the retained form did.
                const scratch_string wide =
                    fallback_path(candidate, entry.kind, entry.label, entry.extension, &arena);
                static_cast<void>(to_utf8_into(wide, std::span<char>(fallback_utf8)));
                conflicts.push_back(candidate);
            }
        }

        // Commit only a complete index. Every failure above destroys the local conflict set and
        // leaves the source session open for a clean retry or one final close.
        conflicts_ = std::move(conflicts);
        prepared_ = true;
    }

    std::optional<indexed_catalogue::column> unity_state::path_into(const std::size_t index,
                                                                    const std::span<char> buffer) const
    {
        if (index >= count()) {
            throw std::out_of_range("Index out of range");
        }
        // Re-read the entry through the same validated reader prepare used, so the path and the
        // two sizes below are byte-for-byte the values the retained catalogue used to store.
        // The stack arena keeps the healthy short entry off the heap; a field that outgrows it
        // falls back to the arena's upstream allocator, which keeps the bounded retry intact.
        alignas(std::max_align_t) std::array<std::byte, get_item_scratch_bytes> scratch;
        std::pmr::monotonic_buffer_resource arena(scratch.data(), scratch.size(), std::pmr::new_delete_resource());
        std::pmr::memory_resource *const resource = &arena;
        scratch_units name(resource);
        scratch_units original(resource);
        const entry_metadata entry = read_entry(token_, static_cast<std::int32_t>(index), name, original, resource);
        const bool conflict = std::binary_search(conflicts_.begin(), conflicts_.end(),
                                                 static_cast<std::int32_t>(index));
        scratch_string fallback(resource);
        const scratch_string &path =
            materialise_path(entry, conflict, static_cast<std::int32_t>(index), fallback, resource);
        const std::size_t written = to_utf8_into(path, buffer);
        return column{written, entry.source_size, entry.source_size};
    }

    std::optional<indexed_catalogue::column> unity_state::wide_path_into(
        const std::size_t index, const std::span<wchar_t> buffer) const
    {
        if (index >= count()) {
            throw std::out_of_range("Index out of range");
        }
        alignas(std::max_align_t) std::array<std::byte, get_item_scratch_bytes> scratch;
        std::pmr::monotonic_buffer_resource arena(scratch.data(), scratch.size(), std::pmr::new_delete_resource());
        std::pmr::memory_resource *const resource = &arena;
        scratch_units name(resource);
        scratch_units original(resource);
        const entry_metadata entry = read_entry(token_, static_cast<std::int32_t>(index), name, original, resource);
        const bool conflict = std::binary_search(conflicts_.begin(), conflicts_.end(),
                                                 static_cast<std::int32_t>(index));
        scratch_string fallback(resource);
        const scratch_string &path =
            materialise_path(entry, conflict, static_cast<std::int32_t>(index), fallback, resource);
        if (!is_well_formed_wide(std::wstring_view(path.data(), path.size())) || buffer.size() < path.size()) {
            throw read_error();
        }
        std::copy(path.begin(), path.end(), buffer.begin());
        return column{path.size(), entry.source_size, entry.source_size};
    }

    std::vector<std::unique_ptr<file>> extractor::list_files([[maybe_unused]] std::istream &stream)
    {
        // Unity uses the indexed route; accidentally requesting legacy rows is an error.
        throw read_error();
    }

    uint32_t extractor::decrypt([[maybe_unused]] uint32_t magic, [[maybe_unused]] std::vector<char> &data) const
    {
        // The custom extract handler owns the whole body; the default path never runs.
        return magic;
    }
} // namespace extractor
