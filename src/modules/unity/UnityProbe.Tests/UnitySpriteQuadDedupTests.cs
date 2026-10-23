using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using Xunit;
using MeshTopology = AssetRipper.SourceGenerated.Enums.MeshTopology;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnitySpriteQuadDedupTests
{
    private static byte[] Color(int index) =>
        [(byte)(1 + (index * 7)), (byte)(100 + (index * 5)), (byte)(250 - (index * 3)), 255];

    private static byte[] RawPixels(int width, int height)
    {
        byte[] data = new byte[width * height * 4];
        for (int index = 0; index < width * height; index++)
        {
            Color(index).CopyTo(data, index * 4);
        }
        return data;
    }

    private static byte[] Flipped(byte[] raw, int width, int height)
    {
        byte[] result = new byte[raw.Length];
        for (int row = 0; row < height; row++)
        {
            Array.Copy(raw, row * width * 4, result, (height - 1 - row) * width * 4, width * 4);
        }
        return result;
    }

    private static string WriteSelected(byte[] contents)
    {
        string path = Path.Combine(Temp.NewDirectory(), "sprite.assets");
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static byte[] Export(UnitySession session, UnityCatalogEntry entry)
    {
        using MemoryStream stream = new();
        Assert.True(session.Export(entry.Index, stream));
        return stream.ToArray();
    }

    private static UnityFixtureBuilder.TightMeshOptions Quad(
        float extent = 0.01f,
        float[]? points = null,
        ushort[]? indices = null,
        bool oddIndex = false,
        byte format = 0,
        byte dimension = 3,
        byte stream = 0,
        uint? vertexCount = null,
        bool includeIndexBuffer = true,
        int duplicateChannels = 0,
        bool unusedChannel = false)
    {
        ushort[] used = indices ?? [0, 1, 2, 0, 2, 3];
        return new UnityFixtureBuilder.TightMeshOptions(points ?? [-extent, -extent, extent, -extent, extent, extent, -extent, extent])
        {
            Indices = used,
            DeclaredVertexCount = vertexCount,
            ChannelDimension = dimension,
            ChannelFormat = format,
            ChannelOffset = 0,
            ChannelStream = stream,
            DuplicateChannelCount = duplicateChannels,
            AddUnusedChannel = unusedChannel,
            IncludeIndexBuffer = includeIndexBuffer,
            IncludeSubMesh = true,
            FirstByte = 0,
            IndexCount = (uint)used.Length,
            Topology = MeshTopology.Triangles,
            OddIndexBuffer = oddIndex,
        };
    }

    [Fact]
    public void TightFullRectQuadHidesTheWholeTextureAndExportsItRepeatably()
    {
        byte[] raw = RawPixels(2, 2);
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 64u, tightMesh: Quad()));

        using UnitySession session = UnitySession.Open(selected);
        UnityCatalogEntry entry = Assert.Single(session.Entries);
        Assert.Equal(UnityEntryKind.Sprite, entry.Kind);
        Assert.Equal("hero", entry.Name);
        for (int pass = 0; pass < 2; pass++)
        {
            Assert.Equal(Flipped(raw, 2, 2), PngDecoder.Decode(Export(session, entry)).Rgba);
        }
    }

    [Fact]
    public void InactiveZeroDimensionChannelDoesNotBlockTheQuadProof()
    {
        byte[] raw = RawPixels(2, 2);
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 64u, tightMesh: Quad(unusedChannel: true)));

        using UnitySession session = UnitySession.Open(selected);
        UnityCatalogEntry entry = Assert.Single(session.Entries);
        Assert.Equal(UnityEntryKind.Sprite, entry.Kind);
        Assert.Equal(Flipped(raw, 2, 2), PngDecoder.Decode(Export(session, entry)).Rgba);
    }

    [Fact]
    public void AsymmetricFullQuadExportsEveryPixelExactly()
    {
        byte[] raw = RawPixels(3, 2);
        float[] points = [-0.015f, -0.01f, 0.015f, -0.01f, 0.015f, 0.01f, -0.015f, 0.01f];
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite("hero", raw, 3, 2,
            0, 0, 3, 2, 0, 0, 3, 2, 0, 0, settingsRaw: 64u, tightMesh: Quad(points: points)));

        using UnitySession session = UnitySession.Open(selected);
        UnityCatalogEntry entry = Assert.Single(session.Entries);
        Assert.Equal(UnityEntryKind.Sprite, entry.Kind);
        Assert.Equal(Flipped(raw, 3, 2), PngDecoder.Decode(Export(session, entry)).Rgba);
    }

    [Fact]
    public void QuadEdgeJustOutsideTheExactCornerIsNotProven()
    {
        byte[] raw = RawPixels(2, 2);
        float left = MathF.BitIncrement(-0.01f);
        float[] points = [left, -0.01f, 0.01f, -0.01f, 0.01f, 0.01f, left, 0.01f];
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 64u, tightMesh: Quad(points: points)));

        using UnitySession session = UnitySession.Open(selected);
        Assert.Equal(2, session.Entries.Count);
        Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
        Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
    }

    [Fact]
    public void QuadEdgeAtThePixelCentreIsNotProven()
    {
        byte[] raw = RawPixels(2, 2);
        float left = MathF.BitIncrement(-0.005f);
        float[] points = [left, -0.01f, 0.01f, -0.01f, 0.01f, 0.01f, left, 0.01f];
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 64u, tightMesh: Quad(points: points)));

        using UnitySession session = UnitySession.Open(selected);
        Assert.Equal(2, session.Entries.Count);
        Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
        Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
    }

    [Fact]
    public void TightQuadOpenUsesNoFullAssetReaderAndDoesNotTouchTexturePixels()
    {
        byte[] raw = RawPixels(2, 2);
        byte[] bytes = UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 64u, tightMesh: Quad());
        int start = bytes.AsSpan().IndexOf(raw);
        Assert.True(start > 0);
        using PayloadGuard guard = new(bytes, start, raw.Length);
        using SmartStream input = SmartStream.CreateOwned(guard);
        FileBase selected = SchemeReader.ReadFile(input, "selected.assets", "selected.assets", FileReadMode.MetadataOnly);
        CountingReader reader = new();

        IUnityGraph graph = UnitySource.OpenGraph("selected.assets", _ => selected, reader, compact: true);
        using UnitySession session = UnitySession.FromGraph(graph);
        Assert.Equal(UnityEntryKind.Sprite, Assert.Single(session.Entries).Kind);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public void TightQuadProofKeepsDistinctSourceIdentitiesAndDoesNotCollapseByPayload()
    {
        byte[] raw = RawPixels(2, 2);
        byte[] first = UnityFixtureBuilder.SerializedSprite("first", raw, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 64u, tightMesh: Quad());
        byte[] second = UnityFixtureBuilder.SerializedSprite("second", raw, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 64u, tightMesh: Quad());
        string path = Path.Combine(Temp.NewDirectory(), "selected.unity3d");
        File.WriteAllBytes(path, UnityFixtureBuilder.CompressedBundle(
            ("first.assets", first), ("second.assets", second)));

        using UnitySession session = UnitySession.Open(path);
        Assert.Equal(new[] { "first", "second" }, session.Entries.Select(entry => entry.Name));
        Assert.All(session.Entries, entry => Assert.Equal(UnityEntryKind.Sprite, entry.Kind));
        foreach (UnityCatalogEntry entry in session.Entries)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                Assert.Equal(Flipped(raw, 2, 2), PngDecoder.Decode(Export(session, entry)).Rgba);
            }
        }
    }

    [Fact]
    public void ProvenFullQuadsSharingOneSourceCollapseToTheFirstSprite()
    {
        byte[] raw = RawPixels(2, 2);
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSharedSourceSprites(
            "first", "second", raw, 2, 2, Quad(), Quad()));

        using UnitySession session = UnitySession.Open(selected);
        UnityCatalogEntry entry = Assert.Single(session.Entries);
        Assert.Equal("first", entry.Name);
        Assert.Equal(UnityEntryKind.Sprite, entry.Kind);
        Assert.Equal(Flipped(raw, 2, 2), PngDecoder.Decode(Export(session, entry)).Rgba);
    }

    [Fact]
    public void PartialSecondQuadKeepsBothSpritesAndHidesOnlyTheTexture()
    {
        byte[] raw = RawPixels(2, 2);
        UnityFixtureBuilder.TightMeshOptions partial = Quad(
            points: [-0.01f, -0.01f, 0.01f, -0.01f, 0.01f, 0.01f], indices: [0, 1, 2]);
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSharedSourceSprites(
            "first", "second", raw, 2, 2, Quad(), partial));

        using UnitySession session = UnitySession.Open(selected);
        Assert.Equal(new[] { "first", "second" }, session.Entries.Select(entry => entry.Name));
        Assert.All(session.Entries, entry => Assert.Equal(UnityEntryKind.Sprite, entry.Kind));
    }

    [Fact]
    public void RectangleAndProvenQuadSharingOneSourceCollapseAcrossDownscale()
    {
        byte[] raw = RawPixels(2, 2);
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSharedSourceSprites(
            "rectangle", "quad", raw, 2, 2, null, Quad(),
            firstSettingsRaw: 2u, secondSettingsRaw: 64u, firstDownscale: 1f));

        using UnitySession session = UnitySession.Open(selected);
        UnityCatalogEntry entry = Assert.Single(session.Entries);
        Assert.Equal("rectangle", entry.Name);
        Assert.Equal(UnityEntryKind.Sprite, entry.Kind);
        Assert.Equal(Flipped(raw, 2, 2), PngDecoder.Decode(Export(session, entry)).Rgba);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("duplicateTriangles")]
    [InlineData("wrongDiagonal")]
    [InlineData("skew")]
    [InlineData("degenerate")]
    [InlineData("outOfRange")]
    [InlineData("oversizedIndex")]
    [InlineData("unsupportedChannel")]
    [InlineData("dimension")]
    [InlineData("stream")]
    [InlineData("duplicateChannels")]
    [InlineData("shortData")]
    [InlineData("oversizedVertexCount")]
    [InlineData("noIndexBuffer")]
    [InlineData("pivotMismatch")]
    [InlineData("croppedX")]
    [InlineData("croppedY")]
    [InlineData("canvasMismatch")]
    [InlineData("downscale")]
    [InlineData("rotation")]
    [InlineData("unknownSettings")]
    [InlineData("packed")]
    [InlineData("mode")]
    public void UnprovenTightGeometryAndUnknownSettingsKeepTheTexture(string kind)
    {
        byte[] raw = RawPixels(2, 2);
        float x = 0, y = 0, textureWidth = 2, textureHeight = 2, canvasWidth = 2, downscale = 0;
        uint settings = 64u;
        UnityFixtureBuilder.TightMeshOptions? mesh = kind switch
        {
            "missing" => null,
            "partial" => Quad(points: [-0.01f, -0.01f, 0.01f, -0.01f, 0.01f, 0.01f], indices: [0, 1, 2]),
            "duplicateTriangles" => Quad(indices: [0, 1, 2, 0, 1, 2]),
            "wrongDiagonal" => Quad(indices: [0, 1, 2, 1, 2, 3]),
            "skew" => Quad(points: [-0.01f, -0.01f, 0.01f, -0.01f, 0f, 0.01f, -0.01f, 0.01f]),
            "degenerate" => Quad(points: [0f, 0f, 0f, 0f, 0.01f, 0f, 0f, 0.01f], indices: [0, 1, 2, 0, 2, 3]),
            "outOfRange" => Quad(indices: [0, 1, 4, 0, 2, 3]),
            "oversizedIndex" => Quad(oddIndex: true),
            "unsupportedChannel" => Quad(format: 7),
            "dimension" => Quad(dimension: 2),
            "stream" => Quad(stream: 8),
            "duplicateChannels" => Quad(duplicateChannels: 1),
            "shortData" => Quad(dimension: 4),
            "oversizedVertexCount" => Quad(vertexCount: 5),
            "noIndexBuffer" => Quad(includeIndexBuffer: false),
            "pivotMismatch" => Quad(extent: 0.004f),
            _ => Quad(),
        };
        switch (kind)
        {
            case "croppedX": x = 1; textureWidth = 1; break;
            case "croppedY": y = 1; textureHeight = 1; break;
            case "canvasMismatch": canvasWidth = 1; break;
            case "downscale": downscale = 0.5f; break;
            case "rotation": settings = 64u | 4u; break;
            case "unknownSettings": settings = 64u | 128u; break;
            case "packed": settings = 65u; break;
            case "mode": settings = 66u; break;
        }

        string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2,
            x, y, textureWidth, textureHeight, 0, 0, canvasWidth, 2, 0, 0,
            settingsRaw: settings, tightMesh: mesh, downscale: downscale));
        using UnitySession session = UnitySession.Open(selected);
        if (kind == "mode")
        {
            Assert.Equal(UnityEntryKind.Sprite, Assert.Single(session.Entries).Kind);
        }
        else
        {
            Assert.Equal(2, session.Entries.Count);
            Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
            Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
        }
    }

    [Theory]
    [InlineData(12, 0)]
    [InlineData(12, 6)]
    [InlineData(13, 0)]
    [InlineData(13, 6)]
    public void ActiveBlendChannelsKeepBothEntriesWithoutThrowing(int channel, int format)
    {
        byte[] raw = RawPixels(2, 2);
        UnityFixtureBuilder.TightMeshOptions mesh = Quad() with
        {
            ActiveExtraChannelIndex = channel,
            ActiveExtraChannelFormat = (byte)format,
        };
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 64u, tightMesh: mesh));

        using UnitySession session = UnitySession.Open(selected);
        Assert.Equal(2, session.Entries.Count);
        Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
        Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
    }

    private sealed class CountingReader : AssetFactoryBase
    {
        public int Reads { get; private set; }

        public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type,
            IReadOnlyList<SerializedTypeReference> references)
        {
            Reads++;
            return null;
        }
    }

    private sealed class PayloadGuard(byte[] bytes, int start, int count) : MemoryStream(bytes, false)
    {
        public bool Allow { get; set; }
        public int Attempts { get; private set; }

        private void Check(int requested)
        {
            if (requested > 0 && Position < start + count && Position + requested > start)
            {
                Attempts++;
                if (!Allow) throw new InvalidOperationException("Inline resource body was read before export.");
            }
        }

        public override int Read(byte[] buffer, int offset, int requested)
        {
            Check(requested);
            return base.Read(buffer, offset, requested);
        }

        public override int Read(Span<byte> buffer)
        {
            Check(buffer.Length);
            return base.Read(buffer);
        }

        public override int ReadByte()
        {
            Check(1);
            return base.ReadByte();
        }
    }
}
