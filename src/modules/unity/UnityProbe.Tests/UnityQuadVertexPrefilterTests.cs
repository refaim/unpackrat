using System;
using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetsTools.NET;
using Xunit;
using MeshTopology = AssetRipper.SourceGenerated.Enums.MeshTopology;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityQuadVertexPrefilterTests
{
    private const BindingFlags Instance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static byte[] RawPixels(int width, int height)
    {
        byte[] data = new byte[width * height * 4];
        for (int index = 0; index < width * height; index++)
        {
            data[index * 4] = (byte)(1 + (index * 7));
            data[(index * 4) + 1] = (byte)(100 + (index * 5));
            data[(index * 4) + 2] = (byte)(250 - (index * 3));
            data[(index * 4) + 3] = 255;
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

    private static UnityFixtureBuilder.TightMeshOptions Quad(uint? vertexCount = null)
        => new([-0.01f, -0.01f, 0.01f, -0.01f, 0.01f, 0.01f, -0.01f, 0.01f])
        {
            Indices = [0, 1, 2, 0, 2, 3],
            DeclaredVertexCount = vertexCount,
            ChannelDimension = 3,
            ChannelFormat = 0,
            ChannelOffset = 0,
            ChannelStream = 0,
            IncludeIndexBuffer = true,
            IncludeSubMesh = true,
            FirstByte = 0,
            IndexCount = 6,
            Topology = MeshTopology.Triangles,
        };

    private static AssetTypeTemplateField Field(string name, AssetValueType kind,
        params AssetTypeTemplateField[] children)
        => new()
        {
            Name = name,
            Type = name,
            ValueType = kind,
            HasValue = kind != AssetValueType.None,
            IsArray = kind is AssetValueType.Array or AssetValueType.ByteArray,
            Children = children.ToList(),
        };

    private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] children)
        => Field("Base", AssetValueType.None, children);

    private static MemoryStream Data(Action<BinaryWriter> write)
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) write(writer);
        stream.Position = 0;
        return stream;
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(5u)]
    [InlineData(uint.MaxValue)]
    public void IneligibleCountsKeepRetainedPngEntriesWithoutAQuadTraversal(uint declared)
    {
        byte[] raw = RawPixels(2, 2);
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite(
            "hero", raw, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0,
            settingsRaw: 64u, tightMesh: Quad(declared)));

        using UnitySession session = UnitySession.Open(selected);
        Assert.Equal(2, session.Entries.Count);

        UnityCatalogEntry texture = Assert.Single(session.Entries,
            entry => entry.Kind == UnityEntryKind.Texture);
        Assert.Equal("hero_texture", texture.Name);
        Assert.Equal("png", texture.OutputExtension);
        Assert.Equal(raw.Length, texture.SourceSize);

        UnityCatalogEntry sprite = Assert.Single(session.Entries,
            entry => entry.Kind == UnityEntryKind.Sprite);
        Assert.Equal("hero", sprite.Name);
        Assert.Equal("png", sprite.OutputExtension);

        // Before the prefilter this fails: opening creates Shape.QuadTraversal for the
        // rejected proof even though the declared count can never equal four.
        AssertNoQuadTraversal(session);
        Assert.Equal(Flipped(raw, 2, 2), PngDecoder.Decode(Export(session, texture)).Rgba);
    }

    [Fact]
    public void NoTightMeshMetadataKeepsBothEntriesWithoutAQuadTraversal()
    {
        byte[] raw = RawPixels(2, 2);
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite(
            "hero", raw, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0,
            settingsRaw: 64u));

        using UnitySession session = UnitySession.Open(selected);
        Assert.Equal(2, session.Entries.Count);
        Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
        Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
        AssertNoQuadTraversal(session);
    }

    [Fact]
    public void DeclaredFourVertexQuadStillCollapsesAndExportsExactPixels()
    {
        byte[] raw = RawPixels(2, 2);
        string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite(
            "hero", raw, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0,
            settingsRaw: 64u, tightMesh: Quad()));

        using UnitySession session = UnitySession.Open(selected);
        UnityCatalogEntry entry = Assert.Single(session.Entries);
        Assert.Equal("hero", entry.Name);
        Assert.Equal(UnityEntryKind.Sprite, entry.Kind);
        Assert.Equal(Flipped(raw, 2, 2), PngDecoder.Decode(Export(session, entry)).Rgba);
    }

    [Fact]
    public void SpriteLinkRetainsTwentyFourByteSparePaddingLayout()
    {
        Type factory = typeof(UnitySource).GetNestedType(
            "LazyAssetFactory", BindingFlags.NonPublic)!;
        Type spriteLink = factory.GetNestedType("SpriteLink", BindingFlags.NonPublic)!;
        Assert.Equal(24, Marshal.SizeOf(spriteLink));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WantedCapturesOnlyTheExactSpriteVertexCountLeaf(bool bigEndian)
    {
        AssetTypeTemplateField template = Root(
            Field("m_RD", AssetValueType.None,
                Field("m_VertexData", AssetValueType.None,
                    Field("m_VertexCount", AssetValueType.Int32)),
                Field("m_VertexCount", AssetValueType.Int32)),
            Field("allVertexData", AssetValueType.None,
                Field("m_VertexCount", AssetValueType.Int32)));

        using MemoryStream stream = Data(writer =>
        {
            void Integer(int value) =>
                writer.Write(bigEndian
                    ? BinaryPrimitives.ReverseEndianness(value)
                    : value);
            Integer(4);
            Integer(9);
            Integer(7);
        });

        UnityProjection projection = UnityProjection.Read(stream, template, bigEndian);
        const string path = "m_RD/m_VertexData/m_VertexCount";
        Assert.True(projection.TryNumber(path, out long count));
        Assert.Equal(4, count);
        Assert.False(projection.TryNumber("m_RD/m_VertexCount", out _));
        Assert.False(projection.TryNumber("allVertexData/m_VertexCount", out _));
    }

    [Theory]
    [InlineData(AssetValueType.Float)]
    [InlineData(AssetValueType.Double)]
    public void FloatingVertexCountCannotBeReadAsTheIntegerFour(AssetValueType kind)
    {
        AssetTypeTemplateField template = Root(
            Field("m_RD", AssetValueType.None,
                Field("m_VertexData", AssetValueType.None,
                    Field("m_VertexCount", kind))));

        using MemoryStream stream = Data(writer =>
        {
            if (kind == AssetValueType.Float) writer.Write(4f);
            else writer.Write(4d);
        });

        UnityProjection projection = UnityProjection.Read(stream, template, false);
        const string path = "m_RD/m_VertexData/m_VertexCount";
        Assert.False(projection.TryNumber(path, out _));

        if (kind == AssetValueType.Float)
        {
            Assert.True(projection.TryFloat32(path, out uint bits));
            Assert.Equal(unchecked((uint)BitConverter.SingleToInt32Bits(4f)), bits);
        }
        else
        {
            Assert.True(projection.TryBits(path, out long bits));
            Assert.Equal(BitConverter.DoubleToInt64Bits(4d), bits);
        }
    }

    [Theory]
    [InlineData(0x0000000100000004UL)]
    [InlineData(0xFFFFFFFF00000004UL)]
    public void WideUnsignedVertexCountNeverNarrowsToFour(ulong declared)
    {
        AssetTypeTemplateField template = Root(
            Field("m_RD", AssetValueType.None,
                Field("m_VertexData", AssetValueType.None,
                    Field("m_VertexCount", AssetValueType.UInt64))));

        using MemoryStream stream = Data(writer => writer.Write(declared));
        UnityProjection projection = UnityProjection.Read(stream, template, false);
        const string path = "m_RD/m_VertexData/m_VertexCount";
        Assert.True(projection.TryNumber(path, out long value));
        Assert.NotEqual(4L, value);
    }

    private static void AssertNoQuadTraversal(UnitySession session)
    {
        object graph = typeof(UnitySession).GetField("graph", Instance)!.GetValue(session)!;
        object factory = graph.GetType().GetField("factory", Instance)!.GetValue(graph)!;
        IDictionary shapes = (IDictionary)factory.GetType()
            .GetField("shapes", Instance)!.GetValue(factory)!;
        Type shapeType = typeof(UnitySource).GetNestedType(
            "Shape", BindingFlags.NonPublic)!;
        FieldInfo traversal = shapeType.GetField("quadTraversal", Instance)!;

        int inspected = 0;
        foreach (object shape in shapes.Values)
        {
            if (!shapeType.IsInstanceOfType(shape)) continue;
            inspected++;
            Assert.Null(traversal.GetValue(shape));
        }
        Assert.True(inspected > 0);
    }
}
