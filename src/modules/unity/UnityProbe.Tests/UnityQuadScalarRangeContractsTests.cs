using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityQuadScalarRangeContractsTests
{
    private const string RepeatedPath = "m_RD/m_SubMeshes/Array/data#0/value";

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

    private static byte[] IntegerBytes(long value, int size, bool bigEndian)
    {
        byte[] bytes = new byte[size];
        switch (size)
        {
            case 1:
                bytes[0] = unchecked((byte)value);
                break;
            case 2:
                if (bigEndian) BinaryPrimitives.WriteInt16BigEndian(bytes, unchecked((short)value));
                else BinaryPrimitives.WriteInt16LittleEndian(bytes, unchecked((short)value));
                break;
            case 4:
                if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(bytes, unchecked((int)value));
                else BinaryPrimitives.WriteInt32LittleEndian(bytes, unchecked((int)value));
                break;
            default:
                if (bigEndian) BinaryPrimitives.WriteInt64BigEndian(bytes, value);
                else BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
                break;
        }
        return bytes;
    }

    private static object[] Integral(string name, AssetValueType kind, int size, long number, bool bigEndian)
        => new object[]
        {
            $"{name} ({(bigEndian ? "big" : "little")} endian)", kind, bigEndian,
            IntegerBytes(number, size, bigEndian), number, unchecked((ulong)number), 0L, true, false,
        };

    private static object[] Floating(string name, AssetValueType kind, int size, ulong bits, bool bigEndian)
        => new object[]
        {
            $"{name} ({(bigEndian ? "big" : "little")} endian)", kind, bigEndian,
            IntegerBytes(unchecked((long)bits), size, bigEndian), 0L, 0UL,
            unchecked((long)bits), false, kind == AssetValueType.Float,
        };

    public static IEnumerable<object[]> ScalarCases()
    {
        foreach (bool big in new[] { false, true })
        {
            yield return Integral("bool", AssetValueType.Bool, 1, 1, big);
            yield return Integral("int8", AssetValueType.Int8, 1, -5, big);
            yield return Integral("uint8", AssetValueType.UInt8, 1, 0xE7, big);
            yield return Integral("int16", AssetValueType.Int16, 2, -12345, big);
            yield return Integral("uint16", AssetValueType.UInt16, 2, 0xFEDC, big);
            yield return Integral("int32", AssetValueType.Int32, 4, -123456789, big);
            yield return Integral("uint32", AssetValueType.UInt32, 4, 0x89ABCDEF, big);
            yield return Integral("int64", AssetValueType.Int64, 8, -1234567890123456789L, big);
            yield return Integral("uint64", AssetValueType.UInt64, 8, unchecked((long)0xFEDCBA9876543210UL), big);
            yield return Floating("float", AssetValueType.Float, 4, 0x7FC00123UL, big);
            yield return Floating("double", AssetValueType.Double, 8, 0xFFF0000000000042UL, big);
        }
    }

    [Theory]
    [MemberData(nameof(ScalarCases))]
    public void QuadPlanReadsScalarsWithEndianAwareTypedBits(string name, AssetValueType kind,
        bool bigEndian, byte[] scalar, long number, ulong unsigned, long bits,
        bool integral, bool float32)
    {
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_SubMeshes", AssetValueType.None,
                ArrayField("Array", Field("data", AssetValueType.None, Field("value", kind))))));
        byte[] body = new byte[4 + scalar.Length];
        if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(body, 1);
        else BinaryPrimitives.WriteInt32LittleEndian(body, 1);
        scalar.CopyTo(body, 4);
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        using MemoryStream input = new(body, false);
        UnityProjection projection = ReadBorrowed(input, template, state, bigEndian);

        Assert.Equal(string.Empty, projection.Text(RepeatedPath));
        Assert.Equal(default, projection.Bytes(RepeatedPath));
        if (integral)
        {
            Assert.Equal(number, projection.Number(RepeatedPath));
            Assert.True(projection.TryNumber(RepeatedPath, out long actual));
            Assert.Equal(number, actual);
            Assert.True(projection.TryUnsigned(RepeatedPath, out ulong actualUnsigned));
            Assert.Equal(unsigned, actualUnsigned);
            Assert.False(projection.TryBits(RepeatedPath, out _));
            Assert.False(projection.TryFloat32(RepeatedPath, out _));
        }
        else
        {
            Assert.Equal(0, projection.Number(RepeatedPath));
            Assert.False(projection.TryNumber(RepeatedPath, out _));
            Assert.False(projection.TryUnsigned(RepeatedPath, out _));
            Assert.True(projection.TryBits(RepeatedPath, out long actualBits));
            Assert.Equal(bits, actualBits);
            if (float32)
            {
                Assert.True(projection.TryFloat32(RepeatedPath, out uint actual32));
                Assert.Equal(unchecked((uint)bits), actual32);
            }
            else Assert.False(projection.TryFloat32(RepeatedPath, out _));
        }
    }

    [Fact]
    public void QuadPlanGettersDistinguishMissingAndWrongKinds()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Width", AssetValueType.Int32),
            Field("m_Rect", AssetValueType.None,
                Field("width", AssetValueType.Float), Field("height", AssetValueType.Double)),
            Field("m_RD", AssetValueType.None,
                Field("settingsRaw", AssetValueType.UInt32),
                Field("m_SubMeshes", AssetValueType.None,
                    ArrayField("Array", Field("data", AssetValueType.None,
                        Field("name", AssetValueType.String))))));
        using MemoryStream input = Data(writer =>
        {
            writer.Write(7);
            writer.Write(BitConverter.SingleToUInt32Bits(2f));
            writer.Write(BitConverter.DoubleToUInt64Bits(3.5));
            writer.Write(64u);
            writer.Write(1);
            writer.Write(5);
            writer.Write(Encoding.UTF8.GetBytes("hello"));
            writer.Write(new byte[3]);
        });
        UnityProjection projection = ReadBorrowed(input, template,
            new UnityProjection.State(template, UnityProjection.Profile.Quad));

        Assert.Equal(string.Empty, projection.Text("m_Width"));
        Assert.Equal(7, projection.Number("m_Width"));
        Assert.False(projection.TryBits("m_Width", out _));
        Assert.False(projection.TryFloat32("m_Width", out _));
        Assert.Equal(default, projection.Bytes("m_Width"));

        Assert.Equal(string.Empty, projection.Text("m_Rect/width"));
        Assert.Equal(0, projection.Number("m_Rect/width"));
        Assert.False(projection.TryNumber("m_Rect/width", out _));
        Assert.True(projection.TryBits("m_Rect/width", out long widthBits));
        Assert.Equal(BitConverter.SingleToUInt32Bits(2f), unchecked((uint)widthBits));
        Assert.True(projection.TryFloat32("m_Rect/width", out uint width));
        Assert.Equal(BitConverter.SingleToUInt32Bits(2f), width);

        Assert.Equal(0, projection.Number("m_Rect/height"));
        Assert.True(projection.TryBits("m_Rect/height", out long heightBits));
        Assert.Equal(unchecked((long)BitConverter.DoubleToUInt64Bits(3.5)), heightBits);
        Assert.False(projection.TryFloat32("m_Rect/height", out _));

        Assert.Equal(64, projection.Number("m_RD/settingsRaw"));
        Assert.Equal("hello", projection.Text("m_RD/m_SubMeshes/Array/data#0/name"));
        Assert.Equal(0, projection.Number("m_RD/m_SubMeshes/Array/data#0/name"));
        Assert.False(projection.TryNumber("m_RD/m_SubMeshes/Array/data#0/name", out _));
        Assert.False(projection.TryBits("m_RD/m_SubMeshes/Array/data#0/name", out _));
        Assert.False(projection.TryFloat32("m_RD/m_SubMeshes/Array/data#0/name", out _));
        Assert.Equal(default, projection.Bytes("m_RD/m_SubMeshes/Array/data#0/name"));

        Assert.Equal(string.Empty, projection.Text("m_Rect/x"));
        Assert.Equal(0, projection.Number("m_Rect/x"));
        Assert.False(projection.TryNumber("m_Rect/x", out _));
        Assert.False(projection.TryBits("m_Rect/x", out _));
        Assert.False(projection.TryFloat32("m_Rect/x", out _));
        Assert.Equal(default, projection.Bytes("m_Rect/x"));
        Assert.DoesNotContain("m_Rect/x", projection.Keys);
    }

    private static AssetTypeTemplateField StringRangeTemplate()
        => Root(Field("m_RD", AssetValueType.None,
            Field("m_SubMeshes", AssetValueType.None,
                ArrayField("Array", Field("data", AssetValueType.None,
                    Field("name", AssetValueType.String))))));

    private static AssetTypeTemplateField ByteRangeTemplate()
        => Root(Field("m_RD", AssetValueType.None,
            Field("m_VertexData", AssetValueType.None,
                Field("m_DataSize", AssetValueType.ByteArray))));

    [Theory]
    [InlineData(AssetValueType.String, -1)]
    [InlineData(AssetValueType.String, 1)]
    [InlineData(AssetValueType.ByteArray, -1)]
    [InlineData(AssetValueType.ByteArray, 1)]
    public void QuadPlanRejectsInvalidStringAndByteRangeLengths(AssetValueType kind, int length)
    {
        bool text = kind == AssetValueType.String;
        AssetTypeTemplateField template = text ? StringRangeTemplate() : ByteRangeTemplate();
        int prefix = text ? 4 : 0;
        byte[] body = new byte[prefix + 4];
        if (text) BinaryPrimitives.WriteInt32LittleEndian(body, 1);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(prefix), length);
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
        {
            using MemoryStream input = new(body, false);
            _ = ReadBorrowed(input, template, state);
        });
        Assert.Equal("Invalid Unity byte range.", error.Message);
    }

    [Fact]
    public void QuadPlanRejectsCapturedTextAboveMetadataLimit()
    {
        const int length = 1_048_577;
        byte[] body = new byte[8 + length];
        BinaryPrimitives.WriteInt32LittleEndian(body, 1);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), length);
        AssetTypeTemplateField template = StringRangeTemplate();
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
        {
            using MemoryStream input = new(body, false);
            _ = ReadBorrowed(input, template, state);
        });
        Assert.Equal("Unity metadata string is too long.", error.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(513)]
    public void QuadPlanRejectsNegativeAndOverBudgetArrayCounts(int count)
    {
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_SubMeshes", AssetValueType.None,
                ArrayField("Array", Field("data", AssetValueType.None,
                    Field("value", AssetValueType.Int32))))));
        byte[] body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, count);
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
        {
            using MemoryStream input = new(body, false);
            _ = ReadBorrowed(input, template, state);
        });
        Assert.Equal("Invalid Unity array count.", error.Message);
    }

    [Fact]
    public void QuadPlanRejectsManagedReferencesBeforeFinalFieldCompletion()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Width", AssetValueType.Int32),
            Field("m_RD", AssetValueType.None,
                Field("settingsRaw", AssetValueType.ManagedReferencesRegistry)));
        byte[] body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, 7);
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
        {
            using MemoryStream input = new(body, false);
            _ = ReadBorrowed(input, template, state);
        });
        Assert.Equal("Managed-reference projection is unsupported.", error.Message);
    }
}