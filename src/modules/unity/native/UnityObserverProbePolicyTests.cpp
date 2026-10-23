//
// TEST-ONLY focused policy tests for UnityObserverProbePolicy.h. They persist
// next to the host so the proof is reproducible from the tracked candidate set
// alone:
//
//     cl /std:c++20 /EHsc /MT /W4 UnityObserverProbePolicyTests.cpp
//
// The upper/lower include is a hook so historical evidence can run the same
// assertions against a frozen pre-correction copy of the policy, but the
// default is the tracked header.
//
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#ifndef UNITY_OBSERVER_POLICY_HEADER
#define UNITY_OBSERVER_POLICY_HEADER "UnityObserverProbePolicy.h"
#endif
#include UNITY_OBSERVER_POLICY_HEADER

namespace
{

    int g_failures = 0;
    int g_checks = 0;

    void Check(bool condition, const char *name)
    {
        ++g_checks;
        std::printf("%s %s\n", condition ? "ok  " : "FAIL", name);
        if (!condition) {
            ++g_failures;
        }
    }

    // Assert the observable outcome (accepted or rejected, and on acceptance the
    // resource kind the path implies) rather than any helper internals.
    void CheckPath(const wchar_t *path, bool expect_ok, probe::ResourceKind expect_kind, const char *name)
    {
        probe::ResourceKind kind = probe::kResourceNone;
        const bool ok = probe::ClassifyListingPath(path, &kind);
        Check(ok == expect_ok && (!ok || kind == expect_kind), name);
    }

    void CheckSize(std::int64_t size, std::int64_t packed, bool expect, const char *name)
    {
        Check(probe::ListingSizeOk(size, packed) == expect, name);
    }

} // namespace

int main()
{
    using namespace probe;

    // 1. Cancel bookkeeping: a real cancel callback happened, and no callback
    // is accepted afterwards.
    {
        SinkState sink;
        sink.mode = 1;
        const int first = SinkInvoke(sink, 100);
        Check(first == 0, "cancel: first data callback returns 0");
        Check(sink.cancellation_requested, "cancel: cancellation requested on first callback");
        const int second = SinkInvoke(sink, 50);
        Check(second == 0, "cancel: no callback accepted after cancel");
        Check(sink.calls_after_cancel == 1, "cancel: post-cancel callback counted");
        Check(sink.nonzero_returns_after_cancel == 0, "cancel: no nonzero return after cancel");
        Check(sink.post_cancel_violation, "cancel: post-cancel callback flagged");
    }

    // 2. Invalid deltas are protocol violations.
    {
        SinkState zero;
        Check(SinkInvoke(zero, 0) == 0 && zero.protocol_violation, "delta: zero is a protocol violation");
        SinkState negative;
        Check(SinkInvoke(negative, -7) == 0 && negative.protocol_violation, "delta: negative is a protocol violation");
    }

    // 3. Cap enforcement: exactly the cap passes, one byte over fails.
    {
        SinkState sink;
        Check(SinkInvoke(sink, kMaxBytesPerFile) == 1 && !sink.over_cap, "cap: exactly the cap is accepted");
        Check(SinkInvoke(sink, 1) == 0 && sink.over_cap, "cap: over the cap is rejected");
    }

    // 4. Candidate selection: skip a read failure and an empty success, pick a
    // later non-empty success; report none when there is no such entry.
    {
        struct CandidateCase
        {
            std::vector<int> results;
            std::vector<std::int64_t> bytes;
            int expected = -1;
            const char *name = "";
        };
        const CandidateCase cases[] = {
            {{kSerRead, kSerSuccess, kSerSuccess}, {-1, 0, 42}, 2, "candidate: picks later non-empty success"},
            {{kSerRead, kSerSuccess}, {-1, 0}, -1, "candidate: none when nothing non-empty succeeded"},
            {{}, {}, -1, "candidate: none for an empty catalog"},
            {{}, {-1, 0, 42}, -1, "candidate: no result cannot select an unpaired byte count"},
            {{kSerRead, kSerSuccess, kSerSuccess}, {}, -1, "candidate: no byte count cannot select an unpaired result"},
            {{kSerSuccess, kSerSuccess},
             {0, 42, 99},
             1,
             "candidate: shorter results select the first matched non-empty success and ignore the unpaired byte"},
            {{kSerSuccess, kSerSuccess, kSerSuccess},
             {0, 42},
             1,
             "candidate: shorter bytes select the first matched non-empty success and ignore the unpaired result"},
        };
        for (const CandidateCase &test : cases) {
            Check(SelectProbeTarget(test.results, test.bytes) == test.expected, test.name);
        }
    }

    // 5. Per-item error policy: only SER_ERROR_READ is tolerated.
    {
        Check(IsAllowedPerItemError(kSerRead), "policy: READ allowed");
        Check(!IsAllowedPerItemError(kSerWrite), "policy: WRITE rejected");
        Check(!IsAllowedPerItemError(kSerSystem), "policy: SYSTEM rejected");
        Check(!IsAllowedPerItemError(kSerUserAbort), "policy: USERABORT rejected");
        Check(!IsAllowedPerItemError(kSerPassword), "policy: PASSWORD rejected");
    }

    // 6. Enumeration terminal policy: NOMOREITEMS exactly at the count.
    {
        Check(TerminalEnumerationOk(kGetItemNoMore, 48, 48), "enum: NOMOREITEMS at the count");
        Check(!TerminalEnumerationOk(kGetItemError, 48, 48), "enum: error terminal rejected");
        Check(!TerminalEnumerationOk(kGetItemNoMore, 47, 48), "enum: count mismatch rejected");
    }

    // 7. Path classification: the leaf's output extension alone decides the
    //    kind. Natural asset names, including literal numeric-leading names, are
    //    accepted under any ordinary directory hierarchy or braced fallback
    //    folder; ".png" is the converted image kind, ".bin" the raw-bin kind,
    //    and every other ABI-safe extension (font/video/audio/mesh/movie) the
    //    generic transformed binary kind.
    {
        CheckPath(L"Assets\\Textures\\hero.png", true, kResourceTexture, "path: natural nested texture accepted");
        CheckPath(L"Assets\\Textures\\0_hero.png", true, kResourceTexture,
                  "path: numeric-leading nested texture accepted");
        CheckPath(L"{Sprite}\\MistArcher_3.png", true, kResourceTexture,
                  "path: natural sprite name with a numeric segment accepted");
        CheckPath(L"{Sprite}\\MistArcher_3_1.png", true, kResourceTexture,
                  "path: duplicate natural sprite name accepted");
        CheckPath(L"Assets\\Textures\\hero_idle_01.png", true, kResourceTexture,
                  "path: natural name with underscores accepted");
        CheckPath(L"Assets/Textures/\u0433\u0435\u0440\u043e\u0439_idle_01.png", true, kResourceTexture,
                  "path: Unicode natural name with underscores accepted");
        CheckPath(L"Assets\\_0.png", true, kResourceTexture, "path: underscore-leading numeric stem accepted");
        Check(ClassifyListingPath(L"Assets\\Textures\\hero.png", nullptr),
              "path: natural texture accepted without a kind output");
        CheckPath(L"Assets/Scripts/3_scene.bin", true, kResourceRawBin, "path: nested raw .bin accepted");
        CheckPath(L"{Texture2D}\\0_hero.png", true, kResourceTexture, "path: braced texture fallback accepted");
        CheckPath(L"{TextAsset}\\3_scene.bin", true, kResourceRawBin, "path: braced raw .bin fallback accepted");
        CheckPath(L"0_hero.png", true, kResourceTexture, "path: bare leaf accepted");
        CheckPath(L"{Sprite}\\7_icon.png", true, kResourceTexture, "path: sprite fallback is a converted image");
        CheckPath(L"{Font}\\2_font.ttf", true, kResourceBinary, "path: font fallback accepted as binary");
        CheckPath(L"{VideoClip}\\3_clip.mp4", true, kResourceBinary, "path: video fallback accepted as binary");
        CheckPath(L"{MovieTexture}\\4_movie.ogv", true, kResourceBinary, "path: movie fallback accepted as binary");
        CheckPath(L"{AudioClip}\\5_sfx.ogg", true, kResourceBinary, "path: audio fallback accepted as binary");
        CheckPath(L"{Mesh}\\6_mesh.glb", true, kResourceBinary, "path: mesh fallback accepted as binary");
        CheckPath(L"Assets\\Fonts\\0_Hero.ttf", true, kResourceBinary,
                  "path: preserved hierarchy with a dynamic extension accepted");
        CheckPath(L"0_x.abcdefghijklmnop", true, kResourceBinary, "path: a sixteen-unit extension is accepted");
        CheckPath(L"12_Music.007", true, kResourceBinary, "path: a digit-only extension is accepted");
    }

    // 8. Path rejection: an extension outside the ABI grammar (empty, too long,
    //    non-alnum, upper-case), an empty filename stem, a missing extension,
    //    and an empty trailing extension.
    {
        CheckPath(L"", false, kResourceNone, "path: empty rejected");
        CheckPath(L"Assets\\Textures\\", false, kResourceNone, "path: empty leaf rejected");
        CheckPath(L"Assets\\Textures\\.png", false, kResourceNone, "path: empty filename stem rejected");
        CheckPath(L"Assets\\Textures\\hero", false, kResourceNone,
                  "path: a natural leaf with a missing extension is rejected");
        CheckPath(L"Assets\\Textures\\0_hero", false, kResourceNone, "path: missing extension rejected");
        CheckPath(L"Assets\\Textures\\hero.", false, kResourceNone,
                  "path: a natural leaf with an empty trailing extension is rejected");
        CheckPath(L"Assets\\Textures\\0_hero.PNG", false, kResourceNone,
                  "path: an upper-case extension is outside the grammar");
        CheckPath(L"Assets\\Textures\\0_hero.t-t", false, kResourceNone,
                  "path: a non-alnum extension is outside the grammar");
        CheckPath(L"Assets\\Textures\\0_hero.", false, kResourceNone, "path: an empty trailing extension is rejected");
        CheckPath(L"Assets\\Textures\\0_hero.abcdefghijklmnopq", false, kResourceNone,
                  "path: a seventeen-unit extension is rejected");
    }

    // 9. Path rejection: traversal, root, drive, UNC and empty components.
    {
        CheckPath(L"..\\0_hero.png", false, kResourceNone, "path: leading traversal rejected");
        CheckPath(L"Assets\\..\\0_hero.png", false, kResourceNone, "path: inner traversal rejected");
        CheckPath(L"Assets\\.\\0_hero.png", false, kResourceNone, "path: single-dot component rejected");
        CheckPath(L"{Texture2D}\\..\\0_hero.png", false, kResourceNone, "path: braced traversal rejected");
        CheckPath(L"\\0_hero.png", false, kResourceNone, "path: rooted backslash rejected");
        CheckPath(L"/0_hero.png", false, kResourceNone, "path: rooted slash rejected");
        CheckPath(L"C:\\assets\\0_hero.png", false, kResourceNone, "path: drive-absolute rejected");
        CheckPath(L"\\\\srv\\share\\0_hero.png", false, kResourceNone, "path: UNC rejected");
        CheckPath(L"Assets\\\\0_hero.png", false, kResourceNone, "path: empty component rejected");
    }

    // 10. Source-size metadata: Size must equal PackedSize and both be >= 0
    //     (0 means unknown source length).
    {
        CheckSize(24, 24, true, "size: Size==PackedSize>0 accepted");
        CheckSize(0, 0, true, "size: Size==PackedSize==0 accepted");
        CheckSize(5, 0, false, "size: Size>PackedSize rejected");
        CheckSize(0, 5, false, "size: Size<PackedSize rejected");
        CheckSize(-1, -1, false, "size: negative pair rejected");
        CheckSize(24, -1, false, "size: negative packed rejected");
    }

    // 11. Whole-catalogue source-size stability: a later entry that changes its
    //     Size/PackedSize (for example to the converted PNG length) must be
    //     rejected even when entry 0 is unchanged.
    {
        const std::vector<SourceSize> stable = {{0, 0}, {24, 24}, {4096, 4096}};
        Check(CatalogueSourceSizeUnchanged(stable, stable), "catalogue: unchanged entries accepted");

        const std::vector<SourceSize> entry0_invalid = {{-1, -1}, {24, 24}, {4096, 4096}};
        Check(!CatalogueSourceSizeUnchanged(stable, entry0_invalid), "catalogue: invalid entry 0 rejected");

        const std::vector<SourceSize> entry1_changed = {{0, 0}, {512, 512}, {4096, 4096}};
        Check(!CatalogueSourceSizeUnchanged(stable, entry1_changed),
              "catalogue: changed entry 1 rejected while entry 0 stable");

        const std::vector<SourceSize> invalid_listing = {{0, 0}, {2, 9}, {4096, 4096}};
        const std::vector<SourceSize> valid_current = {{0, 0}, {2, 2}, {4096, 4096}};
        Check(!CatalogueSourceSizeUnchanged(invalid_listing, valid_current),
              "catalogue: changed PackedSize rejected when Size is unchanged and the listing pair was invalid");

        const std::vector<SourceSize> entry1_invalid = {{0, 0}, {-1, -1}, {4096, 4096}};
        Check(!CatalogueSourceSizeUnchanged(stable, entry1_invalid), "catalogue: invalid entry 1 rejected");

        const std::vector<SourceSize> entry2_changed = {{0, 0}, {24, 24}, {999, 999}};
        Check(!CatalogueSourceSizeUnchanged(stable, entry2_changed), "catalogue: changed last entry rejected");

        const std::vector<SourceSize> shorter = {{0, 0}, {24, 24}};
        Check(!CatalogueSourceSizeUnchanged(stable, shorter), "catalogue: fewer entries rejected");
        Check(!CatalogueSourceSizeUnchanged(std::vector<SourceSize>{}, std::vector<SourceSize>{}),
              "catalogue: empty catalog rejected");
    }

    // 12. Per-kind output policy: only the raw-bin kind is byte-identical to its
    //     declared source length, and only the image kind needs the structural
    //     PNG check; every transformed kind is exempt from both.
    {
        Check(RequiresPackedEqualsExported(kResourceRawBin), "policy: raw .bin PackedSize equals exported bytes");
        Check(!RequiresPackedEqualsExported(kResourceTexture), "policy: converted image need not match source size");
        Check(!RequiresPackedEqualsExported(kResourceBinary), "policy: transformed binary need not match source size");
        Check(!RequiresPackedEqualsExported(kResourceNone), "policy: no kind never requires equality");
        Check(IsPngPayloadKind(kResourceTexture), "policy: image kind needs the PNG check");
        Check(!IsPngPayloadKind(kResourceRawBin), "policy: raw-bin kind never needs the PNG check");
        Check(!IsPngPayloadKind(kResourceBinary), "policy: binary kind never needs the PNG check");
        Check(std::string(ResourceKindName(kResourceTexture)) == "png", "name: image kind");
        Check(std::string(ResourceKindName(kResourceRawBin)) == "raw-bin", "name: raw-bin kind");
        Check(std::string(ResourceKindName(kResourceBinary)) == "binary", "name: binary kind");
        Check(std::string(ResourceKindName(kResourceNone)) == "none", "name: unknown kind");
    }

    // 13. ABI extension grammar: a dotless lower-case ASCII a-z0-9 string of
    //     1..16 units, and nothing else.
    {
        Check(IsSafeExtension(L"png"), "grammar: png safe");
        Check(IsSafeExtension(L"ttf"), "grammar: ttf safe");
        Check(IsSafeExtension(L"abcdefghijklmnop"), "grammar: sixteen units safe");
        Check(IsSafeExtension(L"007"), "grammar: digits safe");
        Check(!IsSafeExtension(L""), "grammar: empty rejected");
        Check(!IsSafeExtension(L"abcdefghijklmnopq"), "grammar: seventeen units rejected");
        Check(!IsSafeExtension(L"PNG"), "grammar: upper-case rejected");
        Check(!IsSafeExtension(L"t-t"), "grammar: punctuation rejected");
        Check(!IsSafeExtension(L"a b"), "grammar: space rejected");
        Check(!IsSafeExtension(L"a.b"), "grammar: dot rejected");
        Check(!IsSafeExtension(L"a{b"), "grammar: a unit above the letters rejected");
    }

    // 14. Mixed listing regression table. An Observer listing cannot tell which
    //     asset type produced a raw ".bin" (a TextAsset's own bytes, or a video /
    //     audio source that fell back to raw bytes). Every raw ".bin" entry is
    //     therefore the single raw-bin category, and the reported kind name must
    //     say so; only ".bin" is that category, and the image and transformed
    //     binary kinds are excluded from it.
    {
        CheckPath(L"{VideoClip}\\3_VideoRaw.bin", true, kResourceRawBin, "mixed: a video raw .bin fallback is raw-bin");
        CheckPath(L"{TextAsset}\\3_TextPayload.bin", true, kResourceRawBin, "mixed: a text payload .bin is raw-bin");
        CheckPath(L"{AudioClip}\\9_Sound.bin", true, kResourceRawBin, "mixed: an audio raw .bin fallback is raw-bin");
        CheckPath(L"Assets\\Media\\7_Clip.bin", true, kResourceRawBin, "mixed: a preserved raw .bin is raw-bin");
        Check(std::string(ResourceKindName(kResourceRawBin)) == "raw-bin",
              "mixed: the raw-bin category reports the name raw-bin");
        Check(std::string(ResourceKindName(kResourceTexture)) != "raw-bin", "mixed: the image kind is not raw-bin");
        Check(std::string(ResourceKindName(kResourceBinary)) != "raw-bin",
              "mixed: a transformed binary is not raw-bin");
        CheckPath(L"{Texture2D}\\0_hero.png", true, kResourceTexture, "mixed: .png is excluded from raw-bin");
        CheckPath(L"{Mesh}\\6_mesh.glb", true, kResourceBinary, "mixed: .glb is excluded from raw-bin");
        CheckPath(L"{Font}\\2_font.ttf", true, kResourceBinary, "mixed: .ttf is excluded from raw-bin");
    }

    std::printf("policy cases=%d failures=%d\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
