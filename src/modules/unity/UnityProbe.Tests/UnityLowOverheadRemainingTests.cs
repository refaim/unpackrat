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
