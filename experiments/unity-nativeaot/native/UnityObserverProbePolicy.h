#ifndef UNITY_OBSERVER_PROBE_POLICY_H
#define UNITY_OBSERVER_PROBE_POLICY_H
//
// TEST-ONLY host policy for UnityObserverProbe.cpp. It persists next to the
// host so the host and its focused policy tests build from the tracked
// candidate set alone; it is not shipped production code.
//
// Pure integer logic with no Windows dependency, so the native host and the
// standalone policy test compile exactly the same code.
//
#include <cstddef>
#include <cstdint>
#include <string_view>
#include <vector>

namespace probe {

// Observer result constants (mirror of src/api.h); local so this header stays
// dependency-light and never drags in windows.h.
enum : int {
    kSorSuccess = 1,
    kSerSuccess = 0,
    kSerWrite = 1,
    kSerRead = 2,
    kSerSystem = 3,
    kSerUserAbort = 4,
    kSerPassword = 5,
    kGetItemError = 0,
    kGetItemOk = 1,
    kGetItemNoMore = 2,
};

constexpr std::int64_t kMaxBytesPerFile = 256LL * 1024 * 1024;

// ---- progress callback model --------------------------------------------

// `mode` selects accept(0) or cancel-on-first-callback(1). The bookkeeping is
// independent of the host so the policy test can drive it directly.
struct SinkState {
    int mode = 0;
    int calls = 0;
    int data_calls = 0;
    bool cancellation_requested = false;
    int calls_after_cancel = 0;
    int nonzero_returns_after_cancel = 0;
    bool post_cancel_violation = false;
    bool protocol_violation = false;
    bool over_cap = false;
    std::int64_t total_bytes = 0;
    std::int64_t max_delta = 0;
};

// One progress-callback invocation; returns the BOOL handed back to the module.
// A non-positive delta is a protocol violation. Once cancellation has been
// requested every later call returns 0 and is flagged, so a module that keeps
// calling after a cancel is detected instead of silently accepted.
inline int SinkInvoke(SinkState& sink, std::int64_t delta) {
    ++sink.calls;
    if (sink.cancellation_requested) {
        ++sink.calls_after_cancel;
        sink.post_cancel_violation = true;
        return 0;
    }
    if (delta <= 0) {
        sink.protocol_violation = true;
        return 0;
    }
    ++sink.data_calls;
    if (delta > sink.max_delta) {
        sink.max_delta = delta;
    }
    sink.total_bytes += delta;
    if (sink.total_bytes > kMaxBytesPerFile) {
        sink.over_cap = true;
        return 0;
    }
    if (sink.mode == 1) {
        sink.cancellation_requested = true;
        return 0;
    }
    return 1;
}

// ---- probe candidate selection ------------------------------------------

// The cancel/retry/write-error probes must run against an entry that already
// exported successfully with a non-empty body. Returns its index, or -1 when
// no such entry exists (the probes are then not exercised at all).
inline int SelectProbeTarget(const std::vector<int>& result_codes, const std::vector<std::int64_t>& byte_counts) {
    const std::size_t count = result_codes.size() < byte_counts.size() ? result_codes.size() : byte_counts.size();
    for (std::size_t i = 0; i < count; ++i) {
        if (result_codes[i] == kSerSuccess && byte_counts[i] > 0) {
            return static_cast<int>(i);
        }
    }
    return -1;
}

// ---- per-item error policy ----------------------------------------------

// In game mode only a per-item SER_ERROR_READ (the known empty placeholder
// texture) is tolerated; every other code aborts the session.
inline bool IsAllowedPerItemError(int result_code) { return result_code == kSerRead; }

// ---- enumeration terminal policy ----------------------------------------

// A stable catalog must terminate with GET_ITEM_NOMOREITEMS exactly at the
// expected count; a terminal error or a count mismatch both fail.
inline bool TerminalEnumerationOk(int terminal_code, int observed_count, int expected_count) {
    return terminal_code == kGetItemNoMore && observed_count == expected_count;
}

// ---- listing path classification ----------------------------------------

// Resource kind implied by an accepted listing path's output extension. The
// directory hierarchy never identifies the kind: the kind is decided only by the
// leaf's output extension, which the module pins from the frozen kind table
// (".png", ".bin", ".ogv", ".glb") or from the additive per-entry extension
// metadata (font, video, audio). ".png" is the converted image payload that a
// caller still checks structurally. ".bin" is the raw-bin payload whose bytes
// are identical to its declared source length; the listing cannot tell which
// asset type produced it, because a TextAsset's own bytes and a video / audio
// source that fell back to raw bytes both land there, so the category is named
// for the bytes and makes no asset-type claim. Every other ABI-safe extension is
// a transformed binary resource (font, video, audio, mesh, movie) whose export
// bytes are a conversion of the source and need not match it.
enum ResourceKind : int {
    kResourceNone = 0,
    kResourceTexture = 1,  // ".png" -> converted Texture2D/Sprite
    kResourceRawBin = 2,   // ".bin" -> raw payload, byte-identical to its declared source
    kResourceBinary = 3,   // any other ABI-safe extension -> transformed binary resource
};

// The additive-ABI extension grammar: a dotless lower-case ASCII a-z0-9 string
// of 1..16 units. The module only ever emits an extension in this grammar (a
// frozen fixed extension or a validated dynamic one), so a listing path whose
// extension is outside it is a module contract violation, not a resource shape.
inline bool IsSafeExtension(std::wstring_view ext) {
    if (ext.empty() || ext.size() > 16) {
        return false;
    }
    for (wchar_t c : ext) {
        const bool lower = c >= L'a' && c <= L'z';
        const bool digit = c >= L'0' && c <= L'9';
        if (!lower && !digit) {
            return false;
        }
    }
    return true;
}

// Whether a successfully exported entry of this kind must report a PackedSize
// equal to its exported byte count. Only the raw ".bin" payload is
// byte-identical to its declared source; the image kind and every transformed
// binary kind are conversions, so the two counts must never be equated.
inline bool RequiresPackedEqualsExported(ResourceKind kind) { return kind == kResourceRawBin; }

// Whether the extracted payload of this kind must pass the structural PNG check.
inline bool IsPngPayloadKind(ResourceKind kind) { return kind == kResourceTexture; }

// Stable report name for a kind; fixed once a kind exists.
inline const char* ResourceKindName(ResourceKind kind) {
    switch (kind) {
        case kResourceTexture:
            return "png";
        case kResourceRawBin:
            return "raw-bin";
        case kResourceBinary:
            return "binary";
        default:
            return "none";
    }
}

inline bool IsPathSeparator(wchar_t c) { return c == L'\\' || c == L'/'; }

// The frozen leaf shape: the text before the first '_' must be a non-empty run
// of ASCII digits (for example "0_hero.png"). A leaf with no '_' is rejected
// because the whole name then includes the extension and cannot be numeric.
inline bool NumericPrefixLeafOk(std::wstring_view leaf) {
    const std::size_t underscore = leaf.find(L'_');
    const std::wstring_view prefix = (underscore == std::wstring_view::npos) ? leaf : leaf.substr(0, underscore);
    if (prefix.empty()) {
        return false;
    }
    for (wchar_t c : prefix) {
        if (c < L'0' || c > L'9') {
            return false;
        }
    }
    return true;
}

// Classify a module-provided listing path. On success returns true and sets
// *kind_out (when non-null) to the resource kind implied by the extension.
//
// Accepted shape (feature contract): any ordinary directory hierarchy or a
// literal braced fallback folder ({Texture2D}, {TextAsset}, {Font}, {VideoClip},
// {MovieTexture}, {AudioClip}, {Mesh}, {Sprite}) is allowed; the kind is decided
// only by the output extension, never by a directory name. The path must be
// relative and must not traverse out of the tree: rooted, drive-qualified, UNC,
// empty-component and ".."/"." names are rejected, as is a leaf that is not the
// frozen numeric-prefix shape. The extension must obey the ABI grammar (a
// dotless lower-case a-z0-9 string of 1..16 units): ".png" is the converted
// image kind, ".bin" the raw-bin kind, and every other safe extension the
// transformed binary kind.
inline bool ClassifyListingPath(std::wstring_view path, ResourceKind* kind_out) {
    if (kind_out != nullptr) {
        *kind_out = kResourceNone;
    }
    if (path.empty()) {
        return false;
    }
    if (IsPathSeparator(path.front())) {
        return false;  // rooted
    }
    if (path.find(L':') != std::wstring_view::npos) {
        return false;  // drive-qualified or alternate data stream
    }
    std::wstring_view leaf;
    for (std::size_t start = 0;;) {
        const std::size_t next = path.find_first_of(L"\\/", start);
        const std::wstring_view component =
            (next == std::wstring_view::npos) ? path.substr(start) : path.substr(start, next - start);
        if (component.empty() || component == L".." || component == L".") {
            return false;  // empty component or traversal
        }
        leaf = component;
        if (next == std::wstring_view::npos) {
            break;
        }
        start = next + 1;
    }
    if (!NumericPrefixLeafOk(leaf)) {
        return false;
    }
    const std::size_t dot = leaf.find_last_of(L'.');
    const std::wstring_view ext = (dot == std::wstring_view::npos) ? std::wstring_view() : leaf.substr(dot + 1);
    if (!IsSafeExtension(ext)) {
        return false;
    }
    ResourceKind kind = kResourceBinary;
    if (ext == L"png") {
        kind = kResourceTexture;
    } else if (ext == L"bin") {
        kind = kResourceRawBin;
    }
    if (kind_out != nullptr) {
        *kind_out = kind;
    }
    return true;
}

// ---- listing source-size metadata ---------------------------------------

// Metadata contract: FileSize and PackedSize both carry the declared source
// payload length before conversion (0 means unknown), so they must be equal and
// non-negative. PackedSize is never the encoded PNG length.
inline bool ListingSizeOk(std::int64_t size, std::int64_t packed) {
    return size >= 0 && packed >= 0 && size == packed;
}

// One entry's source-size pair as captured at listing / observed after
// extraction.
struct SourceSize {
    std::int64_t size = 0;
    std::int64_t packed = 0;
};

// Whole-catalogue metadata stability: every entry returned after extraction
// must carry a valid source-size pair AND match the value saved at listing, so
// a single mutated later entry (for example a texture whose PackedSize became
// the PNG length) is rejected. A different entry count is also rejected.
inline bool CatalogueSourceSizeUnchanged(const std::vector<SourceSize>& listing,
                                         const std::vector<SourceSize>& current) {
    if (listing.empty() || listing.size() != current.size()) {
        return false;
    }
    for (std::size_t i = 0; i < listing.size(); ++i) {
        if (!ListingSizeOk(current[i].size, current[i].packed)) {
            return false;
        }
        if (current[i].size != listing[i].size || current[i].packed != listing[i].packed) {
            return false;
        }
    }
    return true;
}

}  // namespace probe

#endif  // UNITY_OBSERVER_PROBE_POLICY_H
