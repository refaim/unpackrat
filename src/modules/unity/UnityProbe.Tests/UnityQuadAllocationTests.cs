using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.Streams.Smart;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityQuadAllocationTests
{
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

    private static AssetTypeTemplateField ArrayField(string name, AssetTypeTemplateField element)
        => Field(name, AssetValueType.Array, Field("size", AssetValueType.Int32), element);

    private static MemoryStream Data(Action<BinaryWriter> write)
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) write(writer);
        stream.Position = 0;
        return stream;
    }

    private static UnityProjection ReadBorrowed(Stream input, AssetTypeTemplateField template,
        UnityProjection.State state, bool bigEndian = false)
        => UnityProjection.ReadBorrowed(input, 0, input.Length, template, bigEndian, state);

    private static UnityFixtureBuilder.TightMeshOptions Quad()
        => new([-0.01f, -0.01f, 0.01f, -0.01f, 0.01f, 0.01f, -0.01f, 0.01f])
        {
            Indices = [0, 1, 2, 0, 2, 3],
            ChannelDimension = 3,
            ChannelFormat = 0,
            ChannelOffset = 0,
            ChannelStream = 0,
            IncludeIndexBuffer = true,
            IncludeSubMesh = true,
            FirstByte = 0,
            IndexCount = 6,
            Topology = AssetRipper.SourceGenerated.Enums.MeshTopology.Triangles,
        };

    private static void WriteChannel(BinaryWriter writer, int stream, int offset, int format, int dimension)
    {
        writer.Write(stream);
        writer.Write(offset);
        writer.Write(format);
        writer.Write(dimension);
    }

    private static void AssertQuad(UnityProjection projection)
    {
        Assert.Equal("hero", projection.Text("m_Name"));
        Assert.Equal(64, projection.Number("m_RD/settingsRaw"));
        Assert.True(projection.TryFloat32("m_PixelsToUnits", out uint pixels));
        Assert.Equal(BitConverter.SingleToUInt32Bits(100f), pixels);
        Assert.True(projection.TryFloat32("m_Rect/width", out uint width));
        Assert.Equal(BitConverter.SingleToUInt32Bits(2f), width);
        Assert.Equal(4, projection.Number("m_RD/m_VertexData/m_VertexCount"));
        Assert.Equal(6, projection.Number("m_RD/m_SubMeshes/Array/data#0/indexCount"));
        Assert.Equal(4, projection.Number("m_RD/m_SubMeshes/Array/data#0/vertexCount"));
        Assert.Equal(0, projection.Number("m_RD/m_SubMeshes/Array/data#0/topology"));
        Assert.Equal(0, projection.Number("m_RD/m_VertexData/m_Channels/Array/data#0/stream"));
        Assert.Equal(3, projection.Number("m_RD/m_VertexData/m_Channels/Array/data#0/dimension"));
        Assert.Equal(48, projection.Bytes("m_RD/m_VertexData/m_DataSize").Length);
        Assert.Equal(12, projection.Bytes("m_RD/m_IndexBuffer/Array").Length);
        Assert.Contains("m_RD/m_SubMeshes/Array/data#0/firstByte", projection.Keys);
        Assert.DoesNotContain("m_RD/m_SubMeshes/Array/data#1/firstByte", projection.Keys);
        Assert.DoesNotContain("m_RD/m_VertexData/m_Channels/Array/data#1/stream", projection.Keys);
    }

    private static int QuadMismatches(UnityProjection projection)
    {
        int mismatches = 0;
        if (projection.Number("m_RD/settingsRaw") != 64) mismatches++;
        if (projection.Number("m_RD/m_VertexData/m_VertexCount") != 4) mismatches++;
        if (projection.Number("m_RD/m_SubMeshes/Array/data#0/indexCount") != 6) mismatches++;
        if (projection.Bytes("m_RD/m_VertexData/m_DataSize").Length != 48) mismatches++;
        if (projection.Bytes("m_RD/m_IndexBuffer/Array").Length != 12) mismatches++;
        if (!projection.TryFloat32("m_Rect/width", out uint width)
            || width != BitConverter.SingleToUInt32Bits(2f)) mismatches++;
        return mismatches;
    }

    [Fact]
    public void RepeatedQuadProjectionUsesTypedScratchWithoutPathStringsOrBoxing()
    {
        byte[] raw = [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];
        byte[] bytes = UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2,
            0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 64u, tightMesh: Quad());
        using SmartStream encoded = SmartStream.CreateMemory(bytes);
        using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(
            encoded, "sprite.assets", "sprite.assets");
        Assert.True(TypeTreeNodeStruct.TryMakeFromTpk(
            (AssetRipper.SourceGenerated.ClassIDType)213, file.Version, out var release, out _));
        AssetTypeTemplateField template = UnityProjection.Template(release);
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        byte[] body = file.Objects[1].ObjectData;
        using MemoryStream input = new(body, false);
        UnityProjection warm = ReadBorrowed(input, template, state);
        AssertQuad(warm);
        UnityProjection.ByteRange index = warm.Bytes("m_RD/m_IndexBuffer/Array");
        UnityProjection.ByteRange vertex = warm.Bytes("m_RD/m_VertexData/m_DataSize");

        // Keep the measurement window free of xUnit calls; validate every read with primitive counters.
        int mismatches = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            UnityProjection projection = ReadBorrowed(input, template, state);
            mismatches += QuadMismatches(projection);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, mismatches);
        Assert.True(allocated < 512L * 1024,
            $"512 reused Quad projections allocated {allocated} bytes; typed scratch must avoid per-object paths and boxed values.");

        using PayloadGuard guard = new(body, index, vertex);
        UnityProjection guarded = ReadBorrowed(guard, template, state);
        AssertQuad(guarded);
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public void QuadPlanClearsIndexedPresenceAndValuesAcrossArrayCounts()
    {
        AssetTypeTemplateField element = Field("data", AssetValueType.None,
            Field("stream", AssetValueType.Int32), Field("offset", AssetValueType.Int32),
            Field("format", AssetValueType.Int32), Field("dimension", AssetValueType.Int32));
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_VertexData", AssetValueType.None,
                Field("m_Channels", AssetValueType.None, ArrayField("Array", element)))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        const string basePath = "m_RD/m_VertexData/m_Channels/Array/data";

        using MemoryStream one = Data(writer => { writer.Write(1); WriteChannel(writer, 0, 0, 0, 3); });
        UnityProjection first = ReadBorrowed(one, template, state);
        Assert.Equal(3, first.Number(basePath + "#0/dimension"));
        Assert.DoesNotContain(basePath + "#1/dimension", first.Keys);

        using MemoryStream two = Data(writer =>
        {
            writer.Write(2);
            WriteChannel(writer, 0, 0, 0, 3);
            WriteChannel(writer, 1, 0, 0, 4);
        });
        UnityProjection second = ReadBorrowed(two, template, state);
        Assert.Equal(3, second.Number(basePath + "#0/dimension"));
        Assert.Equal(4, second.Number(basePath + "#1/dimension"));
        Assert.DoesNotContain(basePath + "#2/dimension", second.Keys);

        using MemoryStream again = Data(writer => { writer.Write(1); WriteChannel(writer, 0, 0, 0, 3); });
        UnityProjection third = ReadBorrowed(again, template, state);
        Assert.Equal(3, third.Number(basePath + "#0/dimension"));
        Assert.DoesNotContain(basePath + "#1/dimension", third.Keys);
    }

    [Fact]
    public void QuadPlanFallsBackForNestedArraysInsideRepeatedData()
    {
        AssetTypeTemplateField nested = Field("nested", AssetValueType.None,
            ArrayField("Array", Field("data", AssetValueType.None, Field("value", AssetValueType.Int32))));
        AssetTypeTemplateField element = Field("data", AssetValueType.None,
            Field("stream", AssetValueType.Int32), nested);
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_VertexData", AssetValueType.None,
                Field("m_Channels", AssetValueType.None, ArrayField("Array", element)))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        using MemoryStream input = Data(writer =>
        {
            writer.Write(1);
            writer.Write(7);
            writer.Write(2);
            writer.Write(11);
            writer.Write(12);
        });
        UnityProjection projection = ReadBorrowed(input, template, state);
        Assert.Equal(7, projection.Number("m_RD/m_VertexData/m_Channels/Array/data#0/stream"));
        Assert.Equal(11, projection.Number("m_RD/m_VertexData/m_Channels/Array/data#0/nested/Array/data#0/value"));
        Assert.Equal(12, projection.Number("m_RD/m_VertexData/m_Channels/Array/data#0/nested/Array/data#1/value"));
    }

    [Fact]
    public void QuadPlanPreservesSignedWideFloatAndDoubleBitsInBigEndian()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Width", AssetValueType.Int32),
            Field("m_RD", AssetValueType.None,
                Field("textureRect", AssetValueType.None,
                    Field("x", AssetValueType.Float), Field("y", AssetValueType.Double)),
                Field("settingsRaw", AssetValueType.UInt32)),
            Field("m_SpriteAtlas", AssetValueType.None, Field("m_PathID", AssetValueType.UInt64)),
            Field("m_PixelsToUnits", AssetValueType.Int64));
        byte[] body = new byte[36];
        BinaryPrimitives.WriteInt32BigEndian(body, -123);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), 0x7FC00123u);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(8), 0xFFF0000000000042UL);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16), 0x89ABCDEFu);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(20), 0xFEDCBA9876543210UL);
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(28), -5L);
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        using MemoryStream input = new(body, false);
        UnityProjection projection = ReadBorrowed(input, template, state, bigEndian: true);
        Assert.True(projection.TryNumber("m_Width", out long width));
        Assert.Equal(-123, width);
        Assert.True(projection.TryFloat32("m_RD/textureRect/x", out uint floatBits));
        Assert.Equal(0x7FC00123u, floatBits);
        Assert.True(projection.TryBits("m_RD/textureRect/y", out long doubleBits));
        Assert.Equal(unchecked((long)0xFFF0000000000042UL), doubleBits);
        Assert.False(projection.TryNumber("m_RD/textureRect/y", out _));
        Assert.True(projection.TryUnsigned("m_RD/settingsRaw", out ulong settings));
        Assert.Equal(0x89ABCDEFUL, settings);
        Assert.True(projection.TryUnsigned("m_SpriteAtlas/m_PathID", out ulong path));
        Assert.Equal(0xFEDCBA9876543210UL, path);
        Assert.True(projection.TryNumber("m_PixelsToUnits", out long pixels));
        Assert.Equal(-5, pixels);
    }

    [Fact]
    public void QuadPlanKeepsMissingFieldsAbsentAndOnlyReportsCapturedKeys()
    {
        AssetTypeTemplateField template = Root(Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        using MemoryStream input = Data(writer => writer.Write(7));
        UnityProjection projection = ReadBorrowed(input, template, state);
        Assert.Equal(7, projection.Number("m_Width"));
        Assert.False(projection.TryNumber("m_RD/settingsRaw", out _));
        Assert.False(projection.TryFloat32("m_Rect/width", out _));
        Assert.False(projection.TryBits("m_RD/textureRect/x", out _));
        Assert.Equal(default, projection.Bytes("m_RD/m_IndexBuffer"));
        Assert.Equal(string.Empty, projection.Text("m_Name"));
        Assert.Equal(new[] { "m_Width" }, projection.Keys);
    }

    [Fact]
    public void QuadPlanFallsBackInsteadOfTruncatingArraysBeyondItsIndexedBound()
    {
        AssetTypeTemplateField element = Field("data", AssetValueType.None,
            Field("stream", AssetValueType.Int32), Field("offset", AssetValueType.Int32),
            Field("format", AssetValueType.Int32), Field("dimension", AssetValueType.Int32));
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_VertexData", AssetValueType.None,
                Field("m_Channels", AssetValueType.None, ArrayField("Array", element)))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        using MemoryStream input = Data(writer =>
        {
            writer.Write(33);
            for (int i = 0; i < 33; i++) WriteChannel(writer, i, 0, 0, 3);
        });
        UnityProjection projection = ReadBorrowed(input, template, state);
        Assert.Equal(3, projection.Number("m_RD/m_VertexData/m_Channels/Array/data#32/dimension"));
        Assert.Contains("m_RD/m_VertexData/m_Channels/Array/data#32/stream", projection.Keys);
    }

    [Fact]
    public void QuadPlanStorageTracksFastPathFallbackAndBypassReads()
    {
        AssetTypeTemplateField element = Field("data", AssetValueType.None,
            Field("stream", AssetValueType.Int32), Field("offset", AssetValueType.Int32),
            Field("format", AssetValueType.Int32), Field("dimension", AssetValueType.Int32));
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_VertexData", AssetValueType.None,
                Field("m_Channels", AssetValueType.None, ArrayField("Array", element)))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        const string basePath = "m_RD/m_VertexData/m_Channels/Array/data";

        using MemoryStream fastInput = Data(writer =>
        {
            writer.Write(1);
            WriteChannel(writer, 0, 0, 0, 3);
        });
        UnityProjection fast = ReadBorrowed(fastInput, template, state);
        Assert.Equal(3, fast.Number(basePath + "#0/dimension"));
        Assert.Contains(basePath + "#0/stream", fast.Keys);

        using MemoryStream overflow = Data(writer =>
        {
            writer.Write(33);
            for (int i = 0; i < 33; i++) WriteChannel(writer, i, 0, 0, i + 3);
        });
        UnityProjection fallback = ReadBorrowed(overflow, template, state);
        Assert.Equal(3, fallback.Number(basePath + "#0/dimension"));
        Assert.Equal(35, fallback.Number(basePath + "#32/dimension"));
        Assert.Contains(basePath + "#32/stream", fallback.Keys);

        using MemoryStream recoveredInput = Data(writer =>
        {
            writer.Write(1);
            WriteChannel(writer, 0, 0, 0, 6);
        });
        UnityProjection recovered = ReadBorrowed(recoveredInput, template, state);
        Assert.Equal(6, recovered.Number(basePath + "#0/dimension"));
        Assert.DoesNotContain(basePath + "#32/dimension", recovered.Keys);

        using MemoryStream captured = Data(writer =>
        {
            writer.Write(1);
            WriteChannel(writer, 0, 0, 0, 7);
        });
        UnityProjection captureFallback = UnityProjection.ReadBorrowed(
            captured, 0, captured.Length, template, false, state, captureRawName: true);
        Assert.Equal(7, captureFallback.Number(basePath + "#0/dimension"));

        using MemoryStream validated = Data(writer =>
        {
            writer.Write(1);
            WriteChannel(writer, 0, 0, 0, 8);
        });
        UnityProjection layoutFallback = UnityProjection.ReadBorrowed(
            validated, 0, validated.Length, template, false, state, validateLayout: true);
        Assert.Equal(8, layoutFallback.Number(basePath + "#0/dimension"));

        using MemoryStream directInput = Data(writer =>
        {
            writer.Write(1);
            WriteChannel(writer, 0, 0, 0, 9);
        });
        UnityProjection direct = UnityProjection.Read(directInput, template, false, state);
        Assert.Equal(9, direct.Number(basePath + "#0/dimension"));

        using MemoryStream finalInput = Data(writer =>
        {
            writer.Write(1);
            WriteChannel(writer, 0, 0, 0, 10);
        });
        UnityProjection final = ReadBorrowed(finalInput, template, state);
        Assert.Equal(10, final.Number(basePath + "#0/dimension"));
        Assert.DoesNotContain(basePath + "#32/dimension", final.Keys);
    }

    private sealed class PayloadGuard : MemoryStream
    {
        private readonly UnityProjection.ByteRange[] payloads;
        public int Attempts { get; private set; }

        public PayloadGuard(byte[] bytes, params UnityProjection.ByteRange[] ranges) : base(bytes, false)
            => payloads = ranges;

        private void Check(int count)
        {
            foreach (UnityProjection.ByteRange payload in payloads)
            {
                if (count > 0 && Position < payload.Offset + payload.Length && Position + count > payload.Offset)
                {
                    Attempts++;
                    throw new InvalidOperationException("A borrowed Quad payload was read during projection.");
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) { Check(count); return base.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { Check(buffer.Length); return base.Read(buffer); }
        public override int ReadByte() { Check(1); return base.ReadByte(); }
    }
}
