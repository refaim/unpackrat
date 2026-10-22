/*
 * unity_backend.h - frozen v1 UnitySession C ABI.
 *
 * This header is the machine-checkable transcription of the frozen contract
 * and is deliberately dependency-light so it can be consumed by C and C++
 * (including the managed AOT side that emits these exports). It pulls in no
 * Windows, STL, or .NET types: only <stdint.h> and <stddef.h>.
 *
 * Calling convention is cdecl (explicit on Windows). All result codes are
 * int32_t (see UnityResult). UnityEntry is exactly 32 bytes with 8-byte
 * packing; the static_asserts below pin the size and every field offset.
 */
#ifndef UNPACKRAT_UNITY_BACKEND_H
#define UNPACKRAT_UNITY_BACKEND_H

#include <stddef.h>
#include <stdint.h>

/* Explicit cdecl on Windows; empty elsewhere. Overridable for exotic ABIs. */
#ifndef UNPACKRAT_UNITY_CDECL
#if defined(_WIN32)
#define UNPACKRAT_UNITY_CDECL __cdecl
#else
#define UNPACKRAT_UNITY_CDECL
#endif
#endif

/* Function declaration decoration. Defaults to a plain extern declaration. */
#ifndef UNPACKRAT_UNITY_API
#if defined(UNPACKRAT_UNITY_BUILD) && defined(_WIN32)
#define UNPACKRAT_UNITY_API __declspec(dllexport)
#else
#define UNPACKRAT_UNITY_API extern
#endif
#endif

/*
 * Size/offset static asserts that work as C++ and across C levels: real
 * static_assert in C++/C11, and a portable negative-array-size typedef
 * elsewhere (MSVC's default C mode has no _Static_assert keyword). The msg is
 * carried only by the keyword forms.
 */
#if defined(__cplusplus)
#define UNPACKRAT_UNITY_STATIC_ASSERT(cond, msg) static_assert(cond, msg)
#elif defined(__STDC_VERSION__) && (__STDC_VERSION__ >= 201112L)
#define UNPACKRAT_UNITY_STATIC_ASSERT(cond, msg) _Static_assert(cond, msg)
#else
#define UNPACKRAT_UNITY_CAT2_(a, b) a##b
#define UNPACKRAT_UNITY_CAT_(a, b) UNPACKRAT_UNITY_CAT2_(a, b)
#if defined(__COUNTER__)
#define UNPACKRAT_UNITY_SA_ID UNPACKRAT_UNITY_CAT_(unpackrat_unity_sa_, __COUNTER__)
#else
#define UNPACKRAT_UNITY_SA_ID UNPACKRAT_UNITY_CAT_(unpackrat_unity_sa_, __LINE__)
#endif
#define UNPACKRAT_UNITY_STATIC_ASSERT(cond, msg) typedef char UNPACKRAT_UNITY_SA_ID[(cond) ? 1 : -1]
#endif

#ifdef __cplusplus
extern "C"
{
#endif

    /* All operations return one of these int32_t codes. */
    typedef enum UnityResult
    {
        UNITY_OK = 0,
        UNITY_INVALID_ARGUMENT = -1,
        UNITY_DATA_FAILURE = -2,
        UNITY_INVALID_HANDLE = -3,
        UNITY_OUT_OF_RANGE = -4,
        UNITY_BUFFER_TOO_SMALL = -5,
        UNITY_CANCELLED = -6,
        UNITY_WRITE_FAILURE = -7,
        UNITY_BUSY = -8
    } UnityResult;

    /*
     * Entry kind discriminator. The numeric ids are the frozen external contract:
     * 1 Texture2D/png, 2 TextAsset/bin, 3 Font/dynamic, 4 VideoClip/dynamic,
     * 5 MovieTexture/ogv, 6 AudioClip/dynamic, 7 Mesh/glb, 8 Sprite/png. A managed
     * enum may stay 0-based and convert explicitly with +1.
     */
    typedef enum UnityEntryKind
    {
        UNITY_KIND_TEXTURE_PNG = 1,
        UNITY_KIND_TEXT_RAW = 2,
        UNITY_KIND_FONT = 3,
        UNITY_KIND_VIDEO_CLIP = 4,
        UNITY_KIND_MOVIE_TEXTURE = 5,
        UNITY_KIND_AUDIO_CLIP = 6,
        UNITY_KIND_MESH = 7,
        UNITY_KIND_SPRITE = 8
    } UnityEntryKind;

#pragma pack(push, 8)
    /* Exactly 32 bytes; field order and offsets are frozen. */
    typedef struct UnityEntry
    {
        uint32_t struct_size;     /* offset  0: caller sets 32 on input */
        int32_t kind;             /* offset  4: UnityEntryKind value */
        int32_t collection_index; /* offset  8 */
        uint32_t reserved;        /* offset 12: zero */
        int64_t path_id;          /* offset 16: signed catalogue id */
        uint32_t name_length;     /* offset 24: UTF-16 units, no terminator */
        uint32_t reserved2;       /* offset 28: zero */
    } UnityEntry;
#pragma pack(pop)

#pragma pack(push, 8)
    /*
     * Combined metadata descriptor for one catalogue entry, exactly 48 bytes; field order
     * and offsets are frozen. It is the single-call form of the scalar getters: one
     * unpackrat_unity_get_metadata call reports the entry descriptor, the declared source
     * payload length and the three UTF-16 field lengths, and copies each field whose caller
     * buffer is large enough. The caller sets struct_size = 48 on input.
     */
    typedef struct UnityMetadata
    {
        uint32_t struct_size;     /* offset  0: caller sets 48 on input */
        int32_t kind;             /* offset  4: UnityEntryKind value */
        int32_t collection_index; /* offset  8 */
        uint32_t reserved;        /* offset 12: zero */
        int64_t path_id;          /* offset 16: signed catalogue id */
        int64_t source_size;      /* offset 24: declared source payload length (>= 0) */
        int32_t name_length;      /* offset 32: UTF-16 units, no terminator (>= 0) */
        int32_t original_length;  /* offset 36: original asset path units (>= 0) */
        int32_t extension_length; /* offset 40: output extension units (>= 0) */
        uint32_t reserved2;       /* offset 44: zero */
    } UnityMetadata;
#pragma pack(pop)

    /*
     * Export write callback. Returning 0 accepts the whole chunk, 1 requests
     * cancellation, any other value is a write failure. The byte pointer is
     * borrowed only for the duration of the call; chunks are <= 65536 bytes and
     * no empty callback is required. It must not let an exception cross C.
     */
    typedef int32_t(UNPACKRAT_UNITY_CDECL *unpackrat_unity_write_callback)(void *context, const uint8_t *bytes,
                                                                           int32_t length);

    UNPACKRAT_UNITY_API int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_open(const uint16_t *path, int32_t path_length,
                                                                           uint64_t *session_out, int32_t *count_out);

    UNPACKRAT_UNITY_API int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_get_entry(uint64_t session, int32_t index,
                                                                                UnityEntry *entry, uint16_t *name,
                                                                                int32_t name_capacity);

    UNPACKRAT_UNITY_API int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_export(uint64_t session, int32_t index,
                                                                             unpackrat_unity_write_callback write,
                                                                             void *context);

    /*
     * Reports the declared source payload length of catalogue entry `index` into
     * `size_out` as an immutable catalogue value, without converting or materialising
     * the entry. `size_out` must be non-null and is zeroed before any failure; a
     * successful call writes the declared length (>= 0), or 0 when the length is
     * unknown or unrepresentable as int64. This is the declared source resource
     * payload length before conversion, not a PNG/RGBA length and not a physical
     * attribution of shared compressed UnityFS blocks.
     */
    UNPACKRAT_UNITY_API int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_get_source_size(uint64_t session, int32_t index,
                                                                                      int64_t *size_out);

    /*
     * Reports the original asset path of catalogue entry `index` as UTF-16 code
     * units: the resource path AssetRipper's own original-path processor recorded
     * for the asset, or empty when the asset has no original path. `path_length`
     * must be non-null and is zeroed before any failure. A null `path` is a size
     * query: the required unit count is written to `path_length` and UNITY_OK is
     * returned. A non-null `path` with `path_capacity` smaller than the required
     * count returns UNITY_BUFFER_TOO_SMALL and still writes the required count; a
     * negative capacity, or a null path with a non-zero capacity, is
     * UNITY_INVALID_ARGUMENT. No terminator is written and the path is metadata
     * only, never a file-system path.
     */
    UNPACKRAT_UNITY_API int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_get_entry_path(uint64_t session, int32_t index,
                                                                                     uint16_t *path,
                                                                                     int32_t path_capacity,
                                                                                     int32_t *path_length);

    /*
     * Reports the output extension of catalogue entry `index` as UTF-16 code units:
     * a dotless lower-case ASCII a-z0-9 string of 1..16 units, immutable per entry and
     * known before extraction. Same query/copy/zeroing/no-partial-copy/Busy/stale-handle
     * rules as unpackrat_unity_get_entry_path: a null `extension` is a size query; a
     * negative capacity, or a null extension with non-zero capacity, is
     * UNITY_INVALID_ARGUMENT; a capacity smaller than the required count returns
     * UNITY_BUFFER_TOO_SMALL and still writes the required count. `length` must be
     * non-null and is zeroed before any failure. No terminator is written. The value is
     * metadata only and never a file-system path.
     */
    UNPACKRAT_UNITY_API int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_get_entry_extension(
        uint64_t session, int32_t index, uint16_t *extension, int32_t capacity, int32_t *length);

    /*
     * Reads the whole metadata of catalogue entry `index` in ONE serialised operation: the
     * descriptor, declared source length and the three UTF-16 field lengths land in
     * `metadata`, and each field is copied into its caller buffer when that buffer is large
     * enough. It is purely additive: the scalar getters above are unchanged.
     *
     * `metadata` must be non-null with struct_size == 48 and is fully initialised before any
     * other failure. A negative capacity, or a null buffer with a positive capacity, is
     * UNITY_INVALID_ARGUMENT. A null buffer with zero capacity is a size query for that field:
     * its length is still reported and nothing is copied. A field whose buffer is smaller than
     * its length is not copied; such a call returns UNITY_BUFFER_TOO_SMALL after writing every
     * length, so a bounded caller can retry with exact buffers. UNITY_OK means every requested
     * field was copied. Out-of-range is UNITY_OUT_OF_RANGE. Every reported length is
     * non-negative and a negative length never appears on a successful read. Nothing STL,
     * managed or owned crosses the boundary: only fixed structs and caller-owned UTF-16 buffers.
     */
    UNPACKRAT_UNITY_API int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_get_metadata(
        uint64_t session, int32_t index, UnityMetadata *metadata, uint16_t *name, int32_t name_capacity,
        uint16_t *original, int32_t original_capacity, uint16_t *extension, int32_t extension_capacity);

    UNPACKRAT_UNITY_API int32_t UNPACKRAT_UNITY_CDECL unpackrat_unity_close(uint64_t session);

    /* Function-pointer typedefs for dynamic (GetProcAddress) resolution. */
    typedef int32_t(UNPACKRAT_UNITY_CDECL *unpackrat_unity_open_fn)(const uint16_t *path, int32_t path_length,
                                                                    uint64_t *session_out, int32_t *count_out);
    typedef int32_t(UNPACKRAT_UNITY_CDECL *unpackrat_unity_get_entry_fn)(uint64_t session, int32_t index,
                                                                         UnityEntry *entry, uint16_t *name,
                                                                         int32_t name_capacity);
    typedef int32_t(UNPACKRAT_UNITY_CDECL *unpackrat_unity_export_fn)(uint64_t session, int32_t index,
                                                                      unpackrat_unity_write_callback write,
                                                                      void *context);
    typedef int32_t(UNPACKRAT_UNITY_CDECL *unpackrat_unity_get_source_size_fn)(uint64_t session, int32_t index,
                                                                               int64_t *size_out);
    typedef int32_t(UNPACKRAT_UNITY_CDECL *unpackrat_unity_get_entry_path_fn)(uint64_t session, int32_t index,
                                                                              uint16_t *path, int32_t path_capacity,
                                                                              int32_t *path_length);
    typedef int32_t(UNPACKRAT_UNITY_CDECL *unpackrat_unity_get_entry_extension_fn)(uint64_t session, int32_t index,
                                                                                   uint16_t *extension,
                                                                                   int32_t capacity, int32_t *length);
    typedef int32_t(UNPACKRAT_UNITY_CDECL *unpackrat_unity_get_metadata_fn)(
        uint64_t session, int32_t index, UnityMetadata *metadata, uint16_t *name, int32_t name_capacity,
        uint16_t *original, int32_t original_capacity, uint16_t *extension, int32_t extension_capacity);
    typedef int32_t(UNPACKRAT_UNITY_CDECL *unpackrat_unity_close_fn)(uint64_t session);

#ifdef __cplusplus
} /* extern "C" */
#endif

UNPACKRAT_UNITY_STATIC_ASSERT(sizeof(UnityEntry) == 32, "UnityEntry must be exactly 32 bytes");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityEntry, struct_size) == 0, "UnityEntry.struct_size offset");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityEntry, kind) == 4, "UnityEntry.kind offset");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityEntry, collection_index) == 8, "UnityEntry.collection_index offset");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityEntry, reserved) == 12, "UnityEntry.reserved offset");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityEntry, path_id) == 16, "UnityEntry.path_id offset");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityEntry, name_length) == 24, "UnityEntry.name_length offset");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityEntry, reserved2) == 28, "UnityEntry.reserved2 offset");
UNPACKRAT_UNITY_STATIC_ASSERT(sizeof(UnityMetadata) == 48, "UnityMetadata must be exactly 48 bytes");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityMetadata, kind) == 4, "UnityMetadata.kind offset");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityMetadata, path_id) == 16, "UnityMetadata.path_id offset");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityMetadata, source_size) == 24, "UnityMetadata.source_size offset");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityMetadata, name_length) == 32, "UnityMetadata.name_length offset");
UNPACKRAT_UNITY_STATIC_ASSERT(offsetof(UnityMetadata, extension_length) == 40, "UnityMetadata.extension_length offset");

#endif /* UNPACKRAT_UNITY_BACKEND_H */
