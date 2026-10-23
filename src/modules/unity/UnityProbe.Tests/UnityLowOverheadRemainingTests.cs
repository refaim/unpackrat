using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityLowOverheadRemainingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuadCapturesExactNamingAndRejectsLookalikes(bool bigEndian)
    {
        AssetTypeTemplateField indexBuffer = Field("m_IndexBuffer", AssetValueType.ByteArray);
        indexBuffer.IsAligned = true;
        AssetTypeTemplateField template = Root(
            Field("m_PixelsToUnits", AssetValueType.Float),
            Field("m_RD", AssetValueType.None,
                Field("m_VertexData", AssetValueType.None,
                    Field("m_VertexCount", AssetValueType.UInt32),
                    Field("m_DataSize", AssetValueType.UInt32),
                    Field("m_Channels", AssetValueType.UInt8),
                    Field("m_VertexCountLookalike", AssetValueType.UInt32)),
                Field("m_VertexDataLookalike", AssetValueType.None,
                    Field("m_VertexCount", AssetValueType.UInt32)),
                Field("m_SubMeshesLookalike", AssetValueType.None,
                    Field("m_VertexData", AssetValueType.None,
                        Field("m_VertexCount", AssetValueType.UInt32))),
                indexBuffer),
            Field("m_RDLookalike", AssetValueType.None,
                Field("m_VertexData", AssetValueType.None,
                    Field("m_VertexCount", AssetValueType.UInt32))),
            Field("m_Pivot", AssetValueType.None,
                Field("x", AssetValueType.Float),
                Field("y", AssetValueType.Float)));

        List<byte> body = [];
        AddFloat(body, bigEndian, 2.5f);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 7);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 8);
        AddScalar(body, bigEndian, AssetValueType.UInt8, 3);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 99);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 100);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 101);
        int bufferStart = AddBytes(body, bigEndian, new byte[] { 9, 8, 7 });
        Align4(body);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 102);
        AddFloat(body, bigEndian, -1.5f);
        AddFloat(body, bigEndian, 4.25f);

        UnityProjection result = ReadQuad(template, body, bigEndian, out UnityProjection.State state);
        Assert.NotNull(state.QuadPlan);
        AssertKeys(result,
            "m_PixelsToUnits",
            "m_RD/m_VertexData/m_VertexCount",
            "m_RD/m_VertexData/m_DataSize",
            "m_RD/m_VertexData/m_Channels",
            "m_RD/m_IndexBuffer",
            "m_Pivot/x",
            "m_Pivot/y");
        Assert.True(result.TryFloat32("m_PixelsToUnits", out uint pixels));
        Assert.Equal(2.5f, BitConverter.UInt32BitsToSingle(pixels));
        Assert.Equal(7UL, result.Unsigned("m_RD/m_VertexData/m_VertexCount"));
        Assert.Equal(8UL, result.Unsigned("m_RD/m_VertexData/m_DataSize"));
        Assert.Equal(3UL, result.Unsigned("m_RD/m_VertexData/m_Channels"));
        UnityProjection.ByteRange buffer = result.Bytes("m_RD/m_IndexBuffer");
        Assert.Equal((long)bufferStart, buffer.Offset);
        Assert.Equal(3, buffer.Length);
        Assert.True(result.TryFloat32("m_Pivot/x", out uint xBits));
        Assert.Equal(-1.5f, BitConverter.UInt32BitsToSingle(xBits));
        Assert.True(result.TryFloat32("m_Pivot/y", out uint yBits));
        Assert.Equal(4.25f, BitConverter.UInt32BitsToSingle(yBits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuadArrayDataNamingUsesIndexedKeysWithoutIndexingNakedData(bool bigEndian)
    {
        AssetTypeTemplateField subMesh = Field("data", AssetValueType.None,
            Field("firstByte", AssetValueType.UInt32),
            Field("indexCount", AssetValueType.UInt32));
        AssetTypeTemplateField array = Field("Array", AssetValueType.Array,
            Field("size", AssetValueType.Int32),
            subMesh);
        AssetTypeTemplateField nakedData = Field("data", AssetValueType.None,
            Field("firstByte", AssetValueType.UInt32));
        AssetTypeTemplateField indexBuffer = Field("m_IndexBuffer", AssetValueType.ByteArray);
        indexBuffer.IsAligned = true;
        AssetTypeTemplateField template = Root(
            Field("m_RD", AssetValueType.None,
                Field("m_SubMeshes", AssetValueType.None, array, nakedData),
                indexBuffer,
                Field("m_VertexData", AssetValueType.None,
                    Field("m_VertexCount", AssetValueType.UInt32),
                    Field("m_DataSize", AssetValueType.UInt32),
                    Field("m_Channels", AssetValueType.UInt8))),
            Field("m_Pivot", AssetValueType.None,
                Field("x", AssetValueType.Float),
                Field("y", AssetValueType.Float)));

        List<byte> body = [];
        AddScalar(body, bigEndian, AssetValueType.Int32, 2);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 10);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 20);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 11);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 21);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 99);
        int bufferStart = AddBytes(body, bigEndian, new byte[] { 1, 2, 3 });
        Align4(body);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 7);
        AddScalar(body, bigEndian, AssetValueType.UInt32, 8);
        AddScalar(body, bigEndian, AssetValueType.UInt8, 3);
        AddFloat(body, bigEndian, 1.25f);
        AddFloat(body, bigEndian, -2.5f);

        UnityProjection result = ReadQuad(template, body, bigEndian, out UnityProjection.State state);
        Assert.NotNull(state.QuadPlan);
        AssertKeys(result,
            "m_RD/m_SubMeshes/Array/data#0/firstByte",
            "m_RD/m_SubMeshes/Array/data#0/indexCount",
            "m_RD/m_SubMeshes/Array/data#1/firstByte",
            "m_RD/m_SubMeshes/Array/data#1/indexCount",
            "m_RD/m_SubMeshes/data/firstByte",
            "m_RD/m_IndexBuffer",
            "m_RD/m_VertexData/m_VertexCount",
            "m_RD/m_VertexData/m_DataSize",
            "m_RD/m_VertexData/m_Channels",
            "m_Pivot/x",
            "m_Pivot/y");
        Assert.Equal(10UL, result.Unsigned("m_RD/m_SubMeshes/Array/data#0/firstByte"));
        Assert.Equal(20UL, result.Unsigned("m_RD/m_SubMeshes/Array/data#0/indexCount"));
        Assert.Equal(11UL, result.Unsigned("m_RD/m_SubMeshes/Array/data#1/firstByte"));
        Assert.Equal(21UL, result.Unsigned("m_RD/m_SubMeshes/Array/data#1/indexCount"));
        Assert.Equal(99UL, result.Unsigned("m_RD/m_SubMeshes/data/firstByte"));
        UnityProjection.ByteRange buffer = result.Bytes("m_RD/m_IndexBuffer");
        Assert.Equal((long)bufferStart, buffer.Offset);
        Assert.Equal(3, buffer.Length);
        Assert.Equal(7UL, result.Unsigned("m_RD/m_VertexData/m_VertexCount"));
        Assert.Equal(8UL, result.Unsigned("m_RD/m_VertexData/m_DataSize"));
        Assert.Equal(3UL, result.Unsigned("m_RD/m_VertexData/m_Channels"));
        Assert.True(result.TryFloat32("m_Pivot/x", out uint xBits));
        Assert.Equal(1.25f, BitConverter.UInt32BitsToSingle(xBits));
        Assert.True(result.TryFloat32("m_Pivot/y", out uint yBits));
        Assert.Equal(-2.5f, BitConverter.UInt32BitsToSingle(yBits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyVariableArrayRouteReadsTheFollowingField(bool bigEndian)
    {
        AssetTypeTemplateField template = Root(
            Field("m_Name", AssetValueType.String),
            ArrayField("tags", Field("data", AssetValueType.String)),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template);
        Assert.NotNull(state.Route);

        List<byte> body = [];
        AddString(body, bigEndian, "hero");
        AddScalar(body, bigEndian, AssetValueType.Int32, 0);
        AddScalar(body, bigEndian, AssetValueType.Int32, 42);
        const int offset = 5;
        byte[] source = Embed(body.ToArray(), offset, 3);
        using MemoryStream input = new(source, writable: false);
        UnityProjection result = UnityProjection.ReadBorrowed(input, offset, body.Count, template, bigEndian, state);

        Assert.Equal("hero", result.Text("m_Name"));
        Assert.Equal(42L, result.Number("m_Width"));
    }

    private static UnityProjection ReadQuad(AssetTypeTemplateField template, List<byte> body, bool bigEndian,
        out UnityProjection.State state)
    {
        state = new(template, UnityProjection.Profile.Quad);
        const int offset = 7;
        byte[] source = Embed(body.ToArray(), offset, 5);
        using MemoryStream input = new(source, writable: false);
        return UnityProjection.ReadBorrowed(input, offset, body.Count, template, bigEndian, state);
    }

    private static void AssertKeys(UnityProjection projection, params string[] expected)
    {
        Assert.Equal(
            expected.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
            projection.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] fields)
        => Field("Base", AssetValueType.None, fields);

    private static AssetTypeTemplateField ArrayField(string name, AssetTypeTemplateField element)
        => Field(name, AssetValueType.Array, Field("size", AssetValueType.Int32), element);

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

    private static void AddString(List<byte> bytes, bool bigEndian, string value)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        AddScalar(bytes, bigEndian, AssetValueType.Int32, encoded.Length);
        bytes.AddRange(encoded);
        Align4(bytes);
    }

    private static int AddBytes(List<byte> bytes, bool bigEndian, byte[] value)
    {
        AddScalar(bytes, bigEndian, AssetValueType.Int32, value.Length);
        int start = bytes.Count;
        bytes.AddRange(value);
        return start;
    }

    private static void AddFloat(List<byte> bytes, bool bigEndian, float value)
        => AddScalar(bytes, bigEndian, AssetValueType.UInt32, BitConverter.SingleToUInt32Bits(value));

    private static void AddScalar(List<byte> bytes, bool bigEndian, AssetValueType kind, long value)
    {
        Span<byte> encoded = stackalloc byte[8];
        int width = kind switch
        {
            AssetValueType.UInt8 => 1,
            AssetValueType.Int32 or AssetValueType.UInt32 => 4,
            _ => throw new InvalidOperationException($"Unsupported scalar {kind}."),
        };
        switch (kind)
        {
            case AssetValueType.UInt8:
                encoded[0] = (byte)value;
                break;
            case AssetValueType.Int32 when bigEndian:
                BinaryPrimitives.WriteInt32BigEndian(encoded, (int)value);
                break;
            case AssetValueType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(encoded, (int)value);
                break;
            case AssetValueType.UInt32 when bigEndian:
                BinaryPrimitives.WriteUInt32BigEndian(encoded, (uint)value);
                break;
            case AssetValueType.UInt32:
                BinaryPrimitives.WriteUInt32LittleEndian(encoded, (uint)value);
                break;
            default:
                throw new InvalidOperationException($"Unsupported scalar {kind}.");
        }
        bytes.AddRange(encoded[..width].ToArray());
    }

    private static void Align4(List<byte> bytes)
    {
        while ((bytes.Count & 3) != 0) bytes.Add(0);
    }

    private static byte[] Embed(byte[] body, int offset, int suffix)
    {
        byte[] source = new byte[checked(offset + body.Length + suffix)];
        Array.Fill(source, (byte)0xA5, 0, offset);
        body.CopyTo(source, offset);
        Array.Fill(source, (byte)0x5A, offset + body.Length, suffix);
        return source;
    }
}
