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

#include <cstddef>
#include <cstdint>
#include <exception>
#include <filesystem>
#include <fstream>
#include <functional>
#include <ios>
#include <memory>
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
        class unity_state final : public storage_state
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

            [[nodiscard]] std::uint64_t token() const noexcept
            {
                return token_;
            }

            [[nodiscard]] std::int32_t count() const noexcept
            {
                return count_;
            }

          private:
            std::uint64_t token_;
            std::int32_t count_;
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

        // Reads and validates one entry's dynamic output extension. The getter must
        // report 1..16 units and the copy must return exactly the queried count; anything
        // else is corrupt metadata and rejects the entry before the extension is used.
        [[nodiscard]] std::wstring dynamic_extension(const std::uint64_t token, const std::int32_t index)
        {
            std::int32_t units = 0;
            if (::unpackrat_unity_get_entry_extension(token, index, nullptr, 0, &units) != UNITY_OK) {
                throw read_error();
            }
            if (units < 1 || units > max_extension_units) {
                throw read_error();
            }
            std::vector<std::uint16_t> raw(static_cast<std::size_t>(units));
            std::int32_t copied = 0;
            if (::unpackrat_unity_get_entry_extension(token, index, raw.data(), units, &copied) != UNITY_OK ||
                copied != units) {
                throw read_error();
            }
            std::wstring extension;
            extension.reserve(raw.size());
            for (const std::uint16_t unit : raw) {
                const wchar_t wide = static_cast<wchar_t>(unit);
                if (!is_extension_unit(wide)) {
                    throw read_error();
                }
                extension.push_back(wide);
            }
            return extension;
        }

        // One bounded original-path component. It rejects an empty or over-long component, a
        // component that sanitises to nothing, and a reserved device name, so a single bad
        // component falls the whole original path back rather than producing a mangled name.
        [[nodiscard]] std::optional<std::wstring> sanitise_component(const std::wstring_view raw)
        {
            if (raw.empty() || raw.size() > max_component_units) {
                return std::nullopt;
            }
            std::wstring component;
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
        [[nodiscard]] bool is_well_formed_utf16(const std::span<const std::uint16_t> units) noexcept
        {
            bool expecting_low = false;
            for (const std::uint16_t unit : units) {
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

        // Builds the preserved virtual path from an original asset path: the original parent
        // directories, the unique numeric catalogue index, the original base name without its
        // extension, and the entry kind's own extension. Returns nullopt for every rooted,
        // traversal, reserved, unnameable or over-long path so the caller uses the safe braced
        // fallback; it never truncates a component or emits a path over the Observer cap.
        [[nodiscard]] std::optional<std::wstring> original_virtual_path(const std::span<const std::uint16_t> raw,
                                                                        const std::int32_t index,
                                                                        const std::wstring_view extension)
        {
            if (raw.empty()) {
                return std::nullopt;
            }
            if (!is_well_formed_utf16(raw)) {
                // An unpaired original is unusable, so the entry falls back rather than the
                // UTF-8 conversion later rejecting the whole archive.
                return std::nullopt;
            }
            const std::wstring original(raw.begin(), raw.end());
            if (original[0] == L'\\' || original[0] == L'/' ||
                (original.size() >= 2 && original[1] == L':')) {
                return std::nullopt;
            }

            std::vector<std::wstring_view> components;
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
            std::wstring path;
            for (std::size_t component_index = 0; component_index + 1 < components.size(); ++component_index) {
                const std::optional<std::wstring> directory = sanitise_component(components[component_index]);
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
            const std::optional<std::wstring> label = sanitise_component(stem);
            if (!label) {
                return std::nullopt;
            }
            path += std::to_wstring(index);
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
        [[nodiscard]] std::wstring sanitise_label(const std::span<const std::uint16_t> raw)
        {
            std::size_t limit = std::min(raw.size(), max_label_units);
            if (limit < raw.size() && raw[limit - 1] >= 0xD800U && raw[limit - 1] <= 0xDBFFU && raw[limit] >= 0xDC00U &&
                raw[limit] <= 0xDFFFU) {
                // The cut would orphan the high surrogate; drop it so the pair stays whole.
                --limit;
            }

            std::wstring label;
            label.reserve(limit);
            for (std::size_t index = 0; index < limit; ++index) {
                const wchar_t unit = static_cast<wchar_t>(raw[index]);
                label.push_back(is_forbidden(unit) ? L'_' : unit);
            }
            while (!label.empty() && (label.back() == L'.' || label.back() == L' ')) {
                label.pop_back();
            }
            if (label.empty()) {
                return L"resource";
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

        // Records every proper ancestor directory of a virtual path and charges each newly
        // inserted key against the budget, by its retained capacity, before it is stored. One
        // catalogue-wide pass over these keys tells which file names another entry needs as a
        // directory, so no pairwise scan of the catalogue is ever required. A deep, fully
        // distinct tree costs O(depth^2) units of interior-prefix copies, which the budget
        // bounds explicitly.
        void record_ancestor_directories(const std::wstring_view path, name_set &dest, std::size_t &retained)
        {
            for (std::size_t position = path.find(L'\\'); position != std::wstring_view::npos;
                 position = path.find(L'\\', position + 1)) {
                const std::wstring_view prefix = path.substr(0, position);
                if (dest.find(prefix) != dest.end()) {
                    continue;
                }
                std::wstring key(prefix);
                charge_metadata(retained, retained_string_bytes(key));
                dest.emplace(std::move(key));
            }
        }

        // Builds the virtual path for one entry: the preserved original directory hierarchy
        // when the backend reports a safe one, otherwise the fixed braced kind folder. A
        // preserved path is kept unless its file name is a directory another preserved path
        // needs, in which case this entry falls back so the real directory keeps its hierarchy.
        // The fallback label was already sanitised and bounded, and a preserved root can never be
        // the braced folder itself, so the fallback is always unique and the whole catalogue
        // stays collectively extractable.
        [[nodiscard]] std::string virtual_path(const std::int32_t index, const int kind,
                                               const std::wstring_view label,
                                               const std::optional<std::wstring> &preserved,
                                               const name_set &required_directories,
                                               const std::wstring_view extension)
        {
            std::wstring wide;
            if (preserved && !required_directories.contains(*preserved)) {
                wide = *preserved;
            } else {
                wide = fallback_folder(kind);
                wide += L'\\';
                wide += std::to_wstring(index);
                wide += L'_';
                wide += label;
                wide += L'.';
                wide += extension;
            }
            try {
                const std::u8string utf8 = std::filesystem::path(wide).u8string();
                return std::string(reinterpret_cast<const char *>(utf8.data()), utf8.size());
            } catch (const std::system_error &) {
                // std::filesystem::filesystem_error derives from std::system_error, and the
                // MSVC conversion throws the base type for an unpaired UTF-16 unit: a label
                // that cannot round-trip to UTF-8 is a corrupt input, not a valid name.
                throw read_error();
            }
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

        // Builds the custom extract handler for one entry. It borrows the archive's output
        // stream and progress callback for the duration of the call and rethrows any saved
        // failure after the C ABI has returned, preserving the archive's IO, cancel and
        // allocation semantics. A streaming index keeps the state alive until the archive
        // destroys its file list.
        [[nodiscard]] std::function<void(std::istream &, std::ostream &, const std::function<void(std::int64_t)> &)>
        make_extract(const std::uint64_t token, const std::int32_t index)
        {
            return [token, index](std::istream &, std::ostream &output,
                                  const std::function<void(std::int64_t)> &progress) {
                export_bridge bridge;
                bridge.output = &output;
                bridge.progress = &progress;
                const std::int32_t result = ::unpackrat_unity_export(token, index, &bridge_write, &bridge);
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
            };
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

    std::vector<std::unique_ptr<file>> extractor::list_files([[maybe_unused]] std::istream &stream)
    {
        // archive::prepare_files calls this only after a successful get_archive_info, so
        // the session this adapter installed is always present here.
        const unity_state &session = static_cast<const unity_state &>(*state_);
        const std::uint64_t token = session.token();
        const std::int32_t count = session.count();

        // Read each entry's metadata once. Fallback paths use reserved braced roots, so they
        // cannot collide with directories discovered later and can become final files now.
        // Only preserved paths need retained metadata until the full namespace is known.
        // Raw buffers remain transient; the cumulative metadata charge is unchanged even
        // when a fallback entry's bounded metadata is released immediately.
        struct listed_entry
        {
            std::int32_t index = 0;
            int kind = 0;
            std::wstring label;
            std::wstring extension;
            std::optional<std::wstring> preserved;
        };

        std::vector<listed_entry> pending;
        std::vector<std::unique_ptr<file>> files;
        files.reserve(static_cast<std::size_t>(count));
        name_set required_directories;
        std::size_t retained_bytes = 0;
        for (std::int32_t index = 0; index < count; ++index) {
            UnityEntry entry{};
            entry.struct_size = sizeof(UnityEntry);
            if (::unpackrat_unity_get_entry(token, index, &entry, nullptr, 0) != UNITY_OK) {
                throw read_error();
            }
            if (entry.struct_size != sizeof(UnityEntry) || entry.reserved != 0 || entry.reserved2 != 0) {
                throw read_error();
            }
            // Only the eight known resource kinds are listed; an unknown id is corrupt
            // metadata and tears the session down rather than sliding into a fallback.
            if (fallback_folder(entry.kind) == nullptr) {
                throw read_error();
            }
            const std::uint32_t name_units = entry.name_length;
            if (name_units > max_name_units) {
                throw read_error();
            }

            std::vector<std::uint16_t> name(name_units);
            if (name_units > 0) {
                UnityEntry copy{};
                copy.struct_size = sizeof(UnityEntry);
                if (::unpackrat_unity_get_entry(token, index, &copy, name.data(),
                                                static_cast<std::int32_t>(name_units)) != UNITY_OK) {
                    throw read_error();
                }
            }

            // The original asset path is metadata only and may be absent. An over-long declared
            // path is never copied or truncated: it is treated as absent so the entry falls back
            // to the safe braced kind folder, which keeps a hostile catalogue from driving a
            // large allocation or an over-long Observer path.
            std::int32_t path_units = 0;
            if (::unpackrat_unity_get_entry_path(token, index, nullptr, 0, &path_units) != UNITY_OK ||
                path_units < 0) {
                throw read_error();
            }
            std::vector<std::uint16_t> original;
            if (path_units > 0 && static_cast<std::uint32_t>(path_units) <= max_name_units) {
                original.resize(static_cast<std::size_t>(path_units));
                std::int32_t copied_units = 0;
                if (::unpackrat_unity_get_entry_path(token, index, original.data(), path_units, &copied_units) !=
                        UNITY_OK ||
                    copied_units != path_units) {
                    throw read_error();
                }
            }

            // The declared source payload length comes from catalogue metadata only, so listing
            // never reads a resS body or materialises an export. A rejected or negative length is
            // a corrupt catalogue entry, and the enclosing archive tears the session down.
            std::int64_t source_size = 0;
            if (::unpackrat_unity_get_source_size(token, index, &source_size) != UNITY_OK || source_size < 0) {
                throw read_error();
            }

            listed_entry current;
            current.index = index;
            current.kind = entry.kind;
            current.label = sanitise_label(name);
            // The output extension comes from the frozen kind table for the fixed kinds and
            // from the additive ABI getter for the dynamic ones; either way it is bounded
            // and validated before it can shape a virtual path.
            const wchar_t *const fixed = fixed_extension(entry.kind);
            current.extension = (fixed != nullptr) ? std::wstring(fixed) : dynamic_extension(token, index);
            current.preserved = original_virtual_path(original, index, current.extension);
            // Charge the retained capacity of the bounded outputs this entry keeps, before they
            // are stored, then the ancestor directory keys it needs. The raw name/original
            // buffers above are transient and released each iteration.
            std::size_t projected = retained_string_bytes(current.label);
            projected += retained_string_bytes(current.extension);
            if (current.preserved) {
                projected += retained_string_bytes(*current.preserved);
            }
            charge_metadata(retained_bytes, projected);
            if (current.preserved) {
                record_ancestor_directories(*current.preserved, required_directories, retained_bytes);
            }
            auto new_file = std::make_unique<file>();
            // The Size column shows the original source payload length, exactly the same value
            // as PackedSize (unknown0); it is never the predicted PNG length and no conversion
            // runs at listing time.
            new_file->uncompressed_body_size_in_bytes = source_size;
            new_file->compressed_body_size_in_bytes = source_size;
            new_file->extract = make_extract(token, index);
            if (current.preserved) {
                // Do not convert an unused fallback label: a valid preserved path can ignore
                // malformed UTF-16 in that label unless a later directory forces fallback.
                pending.push_back(std::move(current));
            } else {
                new_file->path = virtual_path(current.index, current.kind, current.label, current.preserved,
                                              required_directories, current.extension);
            }
            files.push_back(std::move(new_file));
        }

        for (const listed_entry &current : pending) {
            files[static_cast<std::size_t>(current.index)]->path =
                virtual_path(current.index, current.kind, current.label, current.preserved,
                             required_directories, current.extension);
        }
        return files;
    }

    uint32_t extractor::decrypt([[maybe_unused]] uint32_t magic, [[maybe_unused]] std::vector<char> &data) const
    {
        // The custom extract handler owns the whole body; the default path never runs.
        return magic;
    }
} // namespace extractor
