using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityQuadProofCoverageTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly UnityVersion Version = UnityVersion.Parse("2021.3.0f1");
    private static readonly Type ProofType = typeof(UnitySource).GetNestedType("SpriteQuadProof", BindingFlags.NonPublic)!;

    private static AssetTypeTemplateField Field(string name, AssetValueType kind, params AssetTypeTemplateField[] children)
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

    private static AssetTypeTemplateField ListField(string name, AssetTypeTemplateField element)
        => Field(name, AssetValueType.Array, Field("size", AssetValueType.Int32), element);

    private static AssetTypeTemplateField BytesField(string name)
        => Field(name, AssetValueType.ByteArray, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));

    private static MemoryStream Data(Action<BinaryWriter> write)
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) write(writer);
        stream.Position = 0;
        return stream;
    }

    private static AssetTypeTemplateField QuadTemplate()
    {
        AssetTypeTemplateField subMesh = Field("data", AssetValueType.None,
            Field("firstByte", AssetValueType.Int32),
            Field("indexCount", AssetValueType.Int32),
            Field("topology", AssetValueType.Int32),
            Field("firstVertex", AssetValueType.Int32),
            Field("vertexCount", AssetValueType.Int32));
        AssetTypeTemplateField channel = Field("data", AssetValueType.None,
            Field("stream", AssetValueType.Int32),
            Field("offset", AssetValueType.Int32),
            Field("format", AssetValueType.Int32),
            Field("dimension", AssetValueType.Int32));
        return Root(
            Field("m_PixelsToUnits", AssetValueType.Float),
            Field("m_Rect", AssetValueType.None, Field("width", AssetValueType.Float), Field("height", AssetValueType.Float)),
            Field("m_Pivot", AssetValueType.None, Field("x", AssetValueType.Float), Field("y", AssetValueType.Float)),
            Field("m_Offset", AssetValueType.None, Field("x", AssetValueType.Float), Field("y", AssetValueType.Float)),
            Field("m_RD", AssetValueType.None,
                Field("settingsRaw", AssetValueType.UInt32),
                Field("downscaleMultiplier", AssetValueType.Float),
                BytesField("m_IndexBuffer"),
                Field("m_SubMeshes", AssetValueType.None, ListField("Array", subMesh)),
                Field("m_VertexData", AssetValueType.None,
                    Field("m_VertexCount", AssetValueType.Int32),
                    BytesField("m_DataSize"),
                    Field("m_Channels", AssetValueType.None, ListField("Array", channel))),
                Field("unrelated", AssetValueType.Int32)));
    }

    private static (UnityProjection Projection, UnityProjection.State State, MemoryStream Source) QuadProjection()
    {
        ushort[] indices = [0, 1, 2, 0, 2, 3];
        float[] points = [-1f, -1f, 1f, -1f, 1f, 1f, -1f, 1f];
        byte[] indexBytes = new byte[indices.Length * sizeof(ushort)];
        for (int i = 0; i < indices.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(indexBytes.AsSpan(i * sizeof(ushort)), indices[i]);
        byte[] vertexBytes = new byte[(points.Length / 2) * 3 * sizeof(float)];
        for (int i = 0; i < points.Length / 2; i++)
        {
            Span<byte> vertex = vertexBytes.AsSpan(i * 3 * sizeof(float), 3 * sizeof(float));
            BinaryPrimitives.WriteInt32LittleEndian(vertex, BitConverter.SingleToInt32Bits(points[i * 2]));
            BinaryPrimitives.WriteInt32LittleEndian(vertex[sizeof(float)..], BitConverter.SingleToInt32Bits(points[(i * 2) + 1]));
            BinaryPrimitives.WriteInt32LittleEndian(vertex[(2 * sizeof(float))..], BitConverter.SingleToInt32Bits(0f));
        }
        MemoryStream source = Data(writer =>
        {
            writer.Write(1f);
            writer.Write(2f);
            writer.Write(2f);
            writer.Write(0.5f);
            writer.Write(0.5f);
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(64u);
            writer.Write(0f);
            writer.Write(indexBytes.Length);
            writer.Write(indexBytes);
            writer.Write(1);
            writer.Write(0);
            writer.Write(6);
            writer.Write(0);
            writer.Write(0);
            writer.Write(4);
            writer.Write(4);
            writer.Write(vertexBytes.Length);
            writer.Write(vertexBytes);
            writer.Write(1);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(3);
            writer.Write(99);
        });
        AssetTypeTemplateField template = QuadTemplate();
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        UnityProjection projection = UnityProjection.Read(source, template, false, state);
        return (projection, state, source);
    }

    private static bool Prove(UnityProjection projection, Stream source, long objectOffset = 0L, int width = 2, int height = 2)
        => (bool)ProofType.GetMethod("TryProve", Static)!.Invoke(null,
            new object?[] { projection, source, objectOffset, Version, EndianType.LittleEndian, width, height })!;

    private static bool Covers(float[] points, ushort[] indices, int width, int height)
        => (bool)ProofType.GetMethod("Covers", Static)!.Invoke(null, new object?[] { points, indices, width, height })!;

    private static bool TryChannels(UnityProjection projection)
        => (bool)ProofType.GetMethod("TryChannels", Static)!.Invoke(null, new object?[] { projection, null })!;

    private static Dictionary<string, object> Values(UnityProjection projection)
    {
        FieldInfo field = typeof(UnityProjection).GetField("values", Instance)!;
        return Assert.IsType<Dictionary<string, object>>(field.GetValue(projection));
    }

    private static uint B(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));

    private static void SetChannel(Dictionary<string, object> values, int index, long stream, long offset, long format, long dimension)
    {
        string prefix = $"m_RD/m_VertexData/m_Channels/Array/data#{index}/";
        values[prefix + "stream"] = stream;
        values[prefix + "offset"] = offset;
        values[prefix + "format"] = format;
        values[prefix + "dimension"] = dimension;
    }

    private static void AssertRejected(Action<Dictionary<string, object>> mutate)
    {
        var built = QuadProjection();
        using (built.Source)
        {
            Assert.True(Prove(built.Projection, built.Source), "The seed projection must prove before a single-field mutation.");
            mutate(Values(built.Projection));
            Assert.False(Prove(built.Projection, built.Source), "A single invalid Quad field must refuse the proof.");
        }
    }

    [Fact]
    public void QuadProfileSeedsARealProjectionAndCapturesOnlyProofMetadata()
    {
        var built = QuadProjection();
        using (built.Source)
        {
            Assert.True(built.State.Quad);
            Assert.True(built.State.DynamicPaths);
            Assert.True(built.State.DictionaryStorage);
            Assert.Equal(512, built.State.VisitBudget);
            Assert.False(Prove(built.Projection, built.Source, width: 0));
            Assert.False(Prove(built.Projection, built.Source, height: -1));
            Assert.True(Prove(built.Projection, built.Source));
            Dictionary<string, object> values = Values(built.Projection);
            Assert.Equal(64L, Assert.IsType<long>(values["m_RD/settingsRaw"]));
            Assert.Equal(B(0.5f), Assert.IsType<uint>(values["m_Pivot/x"]));
            Assert.Equal(B(1f), Assert.IsType<uint>(values["m_PixelsToUnits"]));
            Assert.IsType<UnityProjection.ByteRange>(values["m_RD/m_IndexBuffer"]);
            Assert.IsType<UnityProjection.ByteRange>(values["m_RD/m_VertexData/m_DataSize"]);
            Assert.Contains("m_RD/m_SubMeshes/Array/data#0/firstByte", built.Projection.Keys);
            Assert.Contains("m_RD/m_VertexData/m_Channels/Array/data#0/dimension", built.Projection.Keys);
            Assert.DoesNotContain("m_RD/m_SubMeshes/Array/data#1/firstByte", built.Projection.Keys);
            Assert.DoesNotContain("m_RD/unrelated", built.Projection.Keys);
            Assert.DoesNotContain(built.Projection.Keys, key => key.EndsWith("/unrelated", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void QuadProofUsesOffsetFallbackAndNonZeroObjectOffsets()
    {
        var built = QuadProjection();
        using (built.Source)
        {
            Dictionary<string, object> values = Values(built.Projection);
            values.Remove("m_Pivot/x");
            values.Remove("m_Pivot/y");
            Assert.True(Prove(built.Projection, built.Source));
            byte[] body = built.Source.ToArray();
            byte[] framed = new byte[64 + body.Length];
            Buffer.BlockCopy(body, 0, framed, 64, body.Length);
            using MemoryStream prefixed = new(framed, false);
            Assert.True(Prove(built.Projection, prefixed, objectOffset: 64L));
        }
    }

    [Fact]
    public void QuadProofRejectsMissingOrInvalidCanvasMetadata()
    {
        foreach (string path in new[]
        {
            "m_RD/settingsRaw", "m_RD/downscaleMultiplier", "m_PixelsToUnits",
            "m_Rect/width", "m_Rect/height",
        })
        {
            AssertRejected(values => values.Remove(path));
        }
        AssertRejected(values => values["m_RD/settingsRaw"] = 65L);
        AssertRejected(values => values["m_RD/downscaleMultiplier"] = B(0.5f));
        AssertRejected(values => values["m_RD/downscaleMultiplier"] = B(float.NaN));
        AssertRejected(values => values["m_PixelsToUnits"] = B(0f));
        AssertRejected(values => values["m_PixelsToUnits"] = B(-1f));
        AssertRejected(values => values["m_PixelsToUnits"] = B(float.NaN));
        AssertRejected(values => values["m_PixelsToUnits"] = B(float.PositiveInfinity));
        AssertRejected(values => values["m_Rect/width"] = B(3f));
        AssertRejected(values => values["m_Rect/height"] = B(1f));
        var one = QuadProjection();
        using (one.Source)
        {
            Values(one.Projection)["m_RD/downscaleMultiplier"] = B(1f);
            Assert.True(Prove(one.Projection, one.Source));
        }
    }

    [Fact]
    public void QuadProofRequiresCompleteFinitePivotOrACompleteOffsetFallback()
    {
        AssertRejected(values => values.Remove("m_Pivot/x"));
        AssertRejected(values => values.Remove("m_Pivot/y"));
        AssertRejected(values =>
        {
            values.Remove("m_Pivot/x");
            values.Remove("m_Pivot/y");
            values.Remove("m_Offset/x");
            values.Remove("m_Offset/y");
        });
        AssertRejected(values =>
        {
            values.Remove("m_Pivot/x");
            values.Remove("m_Pivot/y");
            values.Remove("m_Offset/y");
        });
        AssertRejected(values => values["m_Pivot/x"] = B(float.NaN));
        AssertRejected(values => values["m_Pivot/y"] = B(float.PositiveInfinity));
        AssertRejected(values =>
        {
            values.Remove("m_Pivot/x");
            values.Remove("m_Pivot/y");
            values["m_Offset/x"] = B(float.NaN);
        });
    }

    [Fact]
    public void QuadProofRejectsIncompleteSubMeshAndVertexCountDescriptions()
    {
        string[] missing =
        [
            "m_RD/m_SubMeshes/Array/data#0/firstByte",
            "m_RD/m_SubMeshes/Array/data#0/indexCount",
            "m_RD/m_SubMeshes/Array/data#0/topology",
            "m_RD/m_SubMeshes/Array/data#0/firstVertex",
            "m_RD/m_SubMeshes/Array/data#0/vertexCount",
            "m_RD/m_VertexData/m_VertexCount",
        ];
        foreach (string path in missing) AssertRejected(values => values.Remove(path));
        (string Path, long Value)[] wrong =
        [
            ("m_RD/m_SubMeshes/Array/data#0/firstByte", 1L),
            ("m_RD/m_SubMeshes/Array/data#0/indexCount", 5L),
            ("m_RD/m_SubMeshes/Array/data#0/topology", 1L),
            ("m_RD/m_SubMeshes/Array/data#0/firstVertex", 1L),
            ("m_RD/m_SubMeshes/Array/data#0/vertexCount", 5L),
            ("m_RD/m_VertexData/m_VertexCount", 5L),
        ];
        foreach ((string path, long value) in wrong) AssertRejected(values => values[path] = value);
        AssertRejected(values => values["m_RD/m_SubMeshes/Array/data#1/firstByte"] = 0L);
    }

    [Fact]
    public void QuadProofRejectsMissingTruncatedAndOutOfBoundsIndexAndVertexBuffers()
    {
        AssertRejected(values => values.Remove("m_RD/m_IndexBuffer"));
        AssertRejected(values => values["m_RD/m_IndexBuffer"] = new UnityProjection.ByteRange(0, 11));
        AssertRejected(values => values["m_RD/m_IndexBuffer"] = new UnityProjection.ByteRange(-1, 12));
        AssertRejected(values => values["m_RD/m_IndexBuffer"] = new UnityProjection.ByteRange(1_000_000, 12));
        AssertRejected(values => values.Remove("m_RD/m_VertexData/m_DataSize"));
        AssertRejected(values => values["m_RD/m_VertexData/m_DataSize"] = new UnityProjection.ByteRange(0, 0));
        AssertRejected(values => values["m_RD/m_VertexData/m_DataSize"] = new UnityProjection.ByteRange(0, 4097));
        AssertRejected(values => values["m_RD/m_VertexData/m_DataSize"] = new UnityProjection.ByteRange(-1, 48));
        AssertRejected(values => values["m_RD/m_VertexData/m_DataSize"] = new UnityProjection.ByteRange(1_000_000, 48));

        var built = QuadProjection();
        using (built.Source)
        {
            Assert.True(Prove(built.Projection, built.Source));
            Dictionary<string, object> values = Values(built.Projection);
            UnityProjection.ByteRange index = Assert.IsType<UnityProjection.ByteRange>(values["m_RD/m_IndexBuffer"]);
            UnityProjection.ByteRange data = Assert.IsType<UnityProjection.ByteRange>(values["m_RD/m_VertexData/m_DataSize"]);
            values["m_RD/m_VertexData/m_DataSize"] = new UnityProjection.ByteRange(data.Offset, 47);
            Assert.False(Prove(built.Projection, built.Source));
            values["m_RD/m_VertexData/m_DataSize"] = data;
            values["m_RD/m_IndexBuffer"] = new UnityProjection.ByteRange(built.Source.Length - 5, 12);
            Assert.False(Prove(built.Projection, built.Source));
            values["m_RD/m_IndexBuffer"] = index;
            Assert.False(Prove(built.Projection, built.Source, objectOffset: -1_000L));
        }
    }

    [Fact]
    public void QuadProofTreatsShortSourceReadsAsRefusalAndKeepsTheCursorBounded()
    {
        var built = QuadProjection();
        using (built.Source)
        {
            Assert.True(Prove(built.Projection, built.Source));
            Dictionary<string, object> values = Values(built.Projection);
            UnityProjection.ByteRange index = Assert.IsType<UnityProjection.ByteRange>(values["m_RD/m_IndexBuffer"]);
            UnityProjection.ByteRange data = Assert.IsType<UnityProjection.ByteRange>(values["m_RD/m_VertexData/m_DataSize"]);
            byte[] bytes = built.Source.ToArray();
            using ShortReadStream indexShort = new(bytes, index.Offset);
            Assert.False(Prove(built.Projection, indexShort));
            Assert.Equal(index.Offset, indexShort.Position);
            Assert.InRange(indexShort.Position, 0, indexShort.Length);
            using ShortReadStream dataShort = new(bytes, data.Offset);
            Assert.False(Prove(built.Projection, dataShort));
            Assert.Equal(data.Offset, dataShort.Position);
            Assert.InRange(dataShort.Position, 0, dataShort.Length);
        }
    }

    [Fact]
    public void QuadProofRejectsAbsentPartialAndMalformedChannelDescriptions()
    {
        const string prefix = "m_RD/m_VertexData/m_Channels/Array/data#0/";
        AssertRejected(values =>
        {
            values.Remove(prefix + "stream");
            values.Remove(prefix + "offset");
            values.Remove(prefix + "format");
            values.Remove(prefix + "dimension");
        });
        AssertRejected(values => values.Remove(prefix + "stream"));
        AssertRejected(values => values.Remove(prefix + "offset"));
        AssertRejected(values => values.Remove(prefix + "format"));
        AssertRejected(values => values.Remove(prefix + "dimension"));
        AssertRejected(values =>
        {
            values.Remove(prefix + "stream");
            values.Remove(prefix + "offset");
        });
        AssertRejected(values =>
        {
            values.Remove(prefix + "stream");
            values.Remove(prefix + "offset");
            values.Remove(prefix + "format");
        });
        AssertRejected(values => values[prefix + "stream"] = -1L);
        AssertRejected(values => values[prefix + "stream"] = 8L);
        AssertRejected(values => values[prefix + "offset"] = -1L);
        AssertRejected(values => values[prefix + "offset"] = 256L);
        AssertRejected(values => values[prefix + "format"] = -1L);
        AssertRejected(values => values[prefix + "format"] = 7L);
        AssertRejected(values => values[prefix + "format"] = 256L);
        AssertRejected(values => values[prefix + "dimension"] = -1L);
        AssertRejected(values => values[prefix + "dimension"] = 5L);
        AssertRejected(values => values[prefix + "dimension"] = 2L);
        AssertRejected(values => values[prefix + "dimension"] = 0L);
        AssertRejected(values => SetChannel(values, 1, 0L, 0L, 0L, 3L));
        AssertRejected(values => values["m_RD/m_VertexData/m_Channels/Array/data#32/stream"] = 0L);
        AssertRejected(values => values["m_RD/m_VertexData/m_Channels/Array/data#32/offset"] = 0L);
        AssertRejected(values => values["m_RD/m_VertexData/m_Channels/Array/data#32/format"] = 0L);
        AssertRejected(values => values["m_RD/m_VertexData/m_Channels/Array/data#32/dimension"] = 0L);
    }

    [Fact]
    public void QuadProofAcceptsInactiveChannelSlots()
    {
        var built = QuadProjection();
        using (built.Source)
        {
            Dictionary<string, object> values = Values(built.Projection);
            for (int index = 1; index <= 13; index++) SetChannel(values, index, 0L, index, 0L, 0L);
            Assert.True(Prove(built.Projection, built.Source));
        }
    }

    [Fact]
    public void ChannelGuardRejectsActiveBlendChannelsWhileInactiveOccurrencesRemainAccepted()
    {
        var built = QuadProjection();
        using (built.Source)
        {
            Dictionary<string, object> values = Values(built.Projection);
            for (int index = 1; index <= 13; index++) SetChannel(values, index, 0L, index, 0L, 0L);
            Assert.True(TryChannels(built.Projection));
            SetChannel(values, 12, 0L, 12L, 0L, 1L);
            Assert.False(TryChannels(built.Projection));
            SetChannel(values, 12, 0L, 12L, 0L, 0L);
            SetChannel(values, 13, 0L, 13L, 0L, 1L);
            Assert.False(TryChannels(built.Projection));
        }
    }

    [Fact]
    public void ChannelGuardAcceptsAllThirtyTwoSlotsAndRejectsTheFirstBeyondThem()
    {
        var built = QuadProjection();
        using (built.Source)
        {
            Dictionary<string, object> values = Values(built.Projection);
            for (int index = 1; index < 32; index++) SetChannel(values, index, 0L, index, 0L, 0L);
            Assert.True(TryChannels(built.Projection));
            SetChannel(values, 32, 0L, 32L, 0L, 0L);
            Assert.False(TryChannels(built.Projection));
        }
    }

    [Fact]
    public void CoversRejectsMalformedCardinalityCornersAndTriangleMasks()
    {
        float[] corners = [0f, 0f, 2f, 0f, 2f, 2f, 0f, 2f];
        ushort[] faces = [0, 1, 2, 0, 2, 3];
        Assert.True(Covers(corners, faces, 2, 2));
        Assert.False(Covers(new float[] { 0f, 0f, 2f, 0f, 2f, 2f }, faces, 2, 2));
        Assert.False(Covers(corners, new ushort[] { 0, 1, 2, 0, 2 }, 2, 2));
        Assert.False(Covers(corners, faces, 0, 2));
        Assert.False(Covers(corners, faces, 2, 0));
        Assert.False(Covers(new float[] { 1f, 0f, 2f, 0f, 2f, 2f, 0f, 2f }, faces, 2, 2));
        Assert.False(Covers(new float[] { 0f, 0f, 2f, 0f, 1f, 2f, 0f, 2f }, faces, 2, 2));
        Assert.False(Covers(new float[] { 0f, 0f, 2f, 0f, 2f, 1f, 0f, 2f }, faces, 2, 2));
        Assert.False(Covers(new float[] { 0f, 0f, 0f, 0f, 2f, 2f, 0f, 2f }, faces, 2, 2));
        Assert.False(Covers(corners, new ushort[] { 0, 1, 4, 0, 2, 3 }, 2, 2));
        Assert.False(Covers(corners, new ushort[] { 0, 0, 1, 0, 2, 3 }, 2, 2));
        Assert.False(Covers(corners, new ushort[] { 0, 1, 2, 0, 1, 2 }, 2, 2));
        Assert.False(Covers(corners, new ushort[] { 0, 1, 2, 0, 0, 1 }, 2, 2));
        Assert.False(Covers(corners, new ushort[] { 0, 1, 2, 1, 2, 3 }, 2, 2));
        Assert.True(Covers(corners, new ushort[] { 0, 1, 3, 1, 2, 3 }, 2, 2));
    }

    [Fact]
    public void CoversAllowsTheExactPackedAreaLimitButRefusesOnePixelMore()
    {
        float[] large = [0f, 0f, 2048f, 0f, 2048f, 4096f, 0f, 4096f];
        ushort[] faces = [0, 1, 2, 0, 2, 3];
        Assert.True(Covers(large, faces, 2048, 4096));
        Assert.False(Covers(large, faces, 2048, 4097));
    }

    [Fact]
    public void QuadProfileUsesTheTightVisitBudgetAndRefusesFieldsBeyondIt()
    {
        AssetTypeTemplateField template = Root(Enumerable.Range(0, 513)
            .Select(_ => Field("m_Width", AssetValueType.Int32)).ToArray());
        using MemoryStream source = Data(writer =>
        {
            for (int i = 0; i < 513; i++) writer.Write(i);
        });
        UnityProjection.State catalogue = new(template);
        Assert.Equal(16_000_000, catalogue.VisitBudget);
        Assert.False(catalogue.Quad);
        UnityProjection.State quad = new(template, UnityProjection.Profile.Quad);
        Assert.Equal(512, quad.VisitBudget);
        Assert.True(quad.Quad);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => UnityProjection.Read(source, template, false, quad));
        Assert.Contains("field budget", error.Message);
    }

    private sealed class ShortReadStream(byte[] bytes, long cutoff) : MemoryStream(bytes, false)
    {
        public override int Read(byte[] buffer, int offset, int count)
            => Position >= cutoff ? 0 : base.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer)
            => Position >= cutoff ? 0 : base.Read(buffer);

        public override int ReadByte()
            => Position >= cutoff ? -1 : base.ReadByte();
    }
}
