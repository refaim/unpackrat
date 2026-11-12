using System;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityProjectionRemainingContractTests
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

    private static MemoryStream Data(Action<BinaryWriter> write)
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) write(writer);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void SpriteOnlyFieldsAreIgnoredWhileResourceMetadataRemainsCaptured()
    {
        AssetTypeTemplateField template = Root(
            Field("m_RD", AssetValueType.None, Field("texture", AssetValueType.Int64)),
            Field("m_RenderDataKey", AssetValueType.Int64),
            Field("m_RenderDataMap", AssetValueType.None, Field("first", AssetValueType.Int64)),
            Field("m_Rect", AssetValueType.None, Field("width", AssetValueType.Float)),
            Field("m_Offset", AssetValueType.None, Field("x", AssetValueType.Float)),
            Field("m_SpriteAtlas", AssetValueType.None, Field("m_PathID", AssetValueType.Int64)),
            Field("m_Width", AssetValueType.Int32));
        using MemoryStream input = Data(writer =>
        {
            writer.Write(11L); writer.Write(12L); writer.Write(13L);
            writer.Write(14f); writer.Write(15f); writer.Write(16L);
            writer.Write(17);
        });

        UnityProjection projection = UnityProjection.Read(input, template, false);

        Assert.Equal(new[] { "m_Width" }, projection.Keys);
        Assert.Equal(17, projection.Number("m_Width"));
    }

    [Fact]
    public void ConflictingScalarKindsKeepTheLastTypedValue()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Width", AssetValueType.UInt32),
            Field("m_Width", AssetValueType.Double));
        using MemoryStream input = Data(writer =>
        {
            writer.Write(0x11223344u);
            writer.Write(0x7FF8000000000001UL);
        });

        UnityProjection projection = UnityProjection.Read(input, template, false);

        Assert.True(projection.TryBits("m_Width", out long bits));
        Assert.Equal(unchecked((long)0x7FF8000000000001UL), bits);
        Assert.False(projection.TryNumber("m_Width", out _));
        Assert.False(projection.TryUnsigned("m_Width", out _));
        Assert.Equal(new[] { "m_Width" }, projection.Keys);
    }

    [Fact]
    public void RepeatedFloatingMetadataKeepsTheLastValuesAndTheirExactTypes()
    {
        AssetTypeTemplateField template = Root(
            Field("m_StreamData", AssetValueType.Array, Field("size", AssetValueType.Int32),
                Field("data", AssetValueType.None,
                    Field("width", AssetValueType.Double), Field("height", AssetValueType.Float))));
        using MemoryStream input = Data(writer =>
        {
            writer.Write(2);
            writer.Write(0x3FF0000000000000UL); writer.Write(0x3F800000u);
            writer.Write(0xFFF8000000000042UL); writer.Write(0xBF800000u);
        });

        UnityProjection projection = UnityProjection.Read(input, template, false);

        Assert.True(projection.TryBits("m_StreamData/data/width", out long widthBits));
        Assert.Equal(unchecked((long)0xFFF8000000000042UL), widthBits);
        Assert.True(projection.TryBits("m_StreamData/data/height", out long heightBits));
        Assert.Equal(0xBF800000L, heightBits);
        Assert.False(projection.TryNumber("m_StreamData/data/width", out _));
        Assert.False(projection.TryUnsigned("m_StreamData/data/height", out _));
        Assert.Equal(new[] { "m_StreamData/data/width", "m_StreamData/data/height" }, projection.Keys);
    }

    [Fact]
    public void IgnoredArraysDoNotPublishElementNames()
    {
        AssetTypeTemplateField template = Root(
            Field("unrelated", AssetValueType.Array, Field("size", AssetValueType.Int32),
                Field("data", AssetValueType.Int32)),
            Field("m_Width", AssetValueType.Int32));
        using MemoryStream input = Data(writer =>
        {
            writer.Write(1); writer.Write(42); writer.Write(7);
        });

        UnityProjection projection = UnityProjection.Read(input, template, false);

        Assert.Equal(new[] { "m_Width" }, projection.Keys);
        Assert.Equal(7, projection.Number("m_Width"));
    }
}
