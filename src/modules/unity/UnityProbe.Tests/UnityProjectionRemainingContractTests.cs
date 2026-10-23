using System;
using System.IO;
using System.Linq;
using System.Text;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
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

    private static AssetTypeTemplateField Array(string name, AssetTypeTemplateField element)
        => Field(name, AssetValueType.Array, Field("size", AssetValueType.Int32), element);

    private static MemoryStream Data(Action<BinaryWriter> write)
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) write(writer);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void DictionaryBitStorageKeepsFullStaticAndDynamicDoublePatterns()
    {
        AssetTypeTemplateField duplicate = Root(
            Field("m_Width", AssetValueType.UInt32),
            Field("m_Width", AssetValueType.Double));
        using (MemoryStream input = Data(writer =>
        {
            writer.Write(0x11223344u);
            writer.Write(0x7FF8000000000001UL);
        }))
        {
            UnityProjection projection = UnityProjection.Read(input, duplicate, false);
            Assert.True(projection.TryBits("m_Width", out long bits));
            Assert.Equal(unchecked((long)0x7FF8000000000001UL), bits);
            Assert.False(projection.TryNumber("m_Width", out _));
            Assert.False(projection.TryUnsigned("m_Width", out _));
            Assert.Equal(new[] { "m_Width" }, projection.Keys);
        }

        AssetTypeTemplateField textureRect = Field("textureRect", AssetValueType.None,
            Field("width", AssetValueType.Double),
            Field("height", AssetValueType.Float));
        AssetTypeTemplateField second = Field("second", AssetValueType.None, textureRect);
        AssetTypeTemplateField data = Field("data", AssetValueType.None, second);
        AssetTypeTemplateField atlas = Root(
            Field("m_RenderDataMap", AssetValueType.None, Array("Array", data)));
        atlas.Type = "SpriteAtlas";
        using (MemoryStream input = Data(writer =>
        {
            writer.Write(1);
            writer.Write(0xFFF8000000000042UL);
            writer.Write(0xBF800000u);
        }))
        {
            UnityProjection projection = UnityProjection.Read(input, atlas, false);
            Assert.True(projection.TryBits(
                "m_RenderDataMap/Array/data#0/second/textureRect/width", out long widthBits));
            Assert.Equal(unchecked((long)0xFFF8000000000042UL), widthBits);
            Assert.True(projection.TryBits(
                "m_RenderDataMap/Array/data#0/second/textureRect/height", out long heightBits));
            Assert.Equal(0xBF800000L, heightBits);
            Assert.False(projection.TryNumber(
                "m_RenderDataMap/Array/data#0/second/textureRect/width", out _));
            Assert.False(projection.TryUnsigned(
                "m_RenderDataMap/Array/data#0/second/textureRect/height", out _));
        }
    }

    [Fact]
    public void DynamicAtlasPathsDoNotIndexDataOutsideRenderDataMap()
    {
        AssetTypeTemplateField template = Root(
            Field("unrelated", AssetValueType.None,
                Array("Array", Field("data", AssetValueType.Int32))),
            Field("m_Width", AssetValueType.Int32));
        template.Type = "SpriteAtlas";
        using MemoryStream input = Data(writer =>
        {
            writer.Write(1);
            writer.Write(42);
            writer.Write(7);
        });
        UnityProjection projection = UnityProjection.Read(input, template, false);
        Assert.Equal(new[] { "m_Width" }, projection.Keys);
        Assert.Equal(7, projection.Number("m_Width"));
        Assert.DoesNotContain(projection.Keys, key => key.Contains("data#", StringComparison.Ordinal));
        Assert.DoesNotContain(projection.Keys, key => key.Contains("unrelated", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacySpritePackingFieldsKeepTheirTypedValues()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Rect", AssetValueType.None,
                Field("width", AssetValueType.Float),
                Field("height", AssetValueType.Float)),
            Field("m_Offset", AssetValueType.None,
                Field("x", AssetValueType.Float),
                Field("y", AssetValueType.Float)),
            Field("m_RD", AssetValueType.None,
                Field("textureRect", AssetValueType.None,
                    Field("x", AssetValueType.Float),
                    Field("y", AssetValueType.Float),
                    Field("width", AssetValueType.Float),
                    Field("height", AssetValueType.Float)),
                Field("textureRectOffset", AssetValueType.None,
                    Field("x", AssetValueType.Float),
                    Field("y", AssetValueType.Float)),
                Field("downscaleMultiplier", AssetValueType.Float),
                Field("settingsRaw", AssetValueType.UInt32),
                Field("packed", AssetValueType.Bool),
                Field("packingMode", AssetValueType.Int32),
                Field("packingRotation", AssetValueType.Int32)));
        using MemoryStream input = Data(writer =>
        {
            writer.Write(1920f); writer.Write(1080f);
            writer.Write(-3f); writer.Write(4f);
            writer.Write(0.5f); writer.Write(0.25f);
            writer.Write(1.5f); writer.Write(2.5f);
            writer.Write(0.125f); writer.Write(-0.5f);
            writer.Write(0.75f);
            writer.Write(0x80000001u);
            writer.Write(true);
            writer.Write(2); writer.Write(3);
        });
        UnityProjection projection = UnityProjection.Read(input, template, false);
        void Bits(string path, float expected)
        {
            Assert.True(projection.TryBits(path, out long bits));
            Assert.Equal((long)(uint)BitConverter.SingleToInt32Bits(expected), bits);
        }
        Bits("m_Rect/width", 1920f);
        Bits("m_Rect/height", 1080f);
        Bits("m_Offset/x", -3f);
        Bits("m_Offset/y", 4f);
        Bits("m_RD/textureRect/x", 0.5f);
        Bits("m_RD/textureRect/y", 0.25f);
        Bits("m_RD/textureRect/width", 1.5f);
        Bits("m_RD/textureRect/height", 2.5f);
        Bits("m_RD/textureRectOffset/x", 0.125f);
        Bits("m_RD/textureRectOffset/y", -0.5f);
        Bits("m_RD/downscaleMultiplier", 0.75f);
        Assert.Equal(0x80000001UL, projection.Unsigned("m_RD/settingsRaw"));
        Assert.Equal(1, projection.Number("m_RD/packed"));
        Assert.Equal(2, projection.Number("m_RD/packingMode"));
        Assert.Equal(3, projection.Number("m_RD/packingRotation"));
    }

    [Fact]
    public void QuadVertexDataCapturesRequiredFieldsAndRejectsUnrelatedSiblings()
    {
        AssetTypeTemplateField channel = Field("data", AssetValueType.None,
            Field("channel", AssetValueType.UInt8),
            Field("dimension", AssetValueType.UInt8),
            Field("format", AssetValueType.UInt8));
        AssetTypeTemplateField channels = Field("m_Channels", AssetValueType.None,
            Array("Array", channel));
        AssetTypeTemplateField vertexData = Field("m_VertexData", AssetValueType.None,
            Field("m_VertexCount", AssetValueType.UInt32),
            Field("m_DataSize", AssetValueType.UInt32),
            channels,
            Field("unrelated", AssetValueType.Int32));
        AssetTypeTemplateField template = Root(
            Field("unrelatedRoot", AssetValueType.Int32),
            Field("m_PixelsToUnits", AssetValueType.Float),
            Field("m_Pivot", AssetValueType.None,
                Field("x", AssetValueType.Float),
                Field("y", AssetValueType.Float)),
            Field("m_RD", AssetValueType.None,
                vertexData,
                Field("nonVertexUnrelated", AssetValueType.Int32)));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        using MemoryStream input = Data(writer =>
        {
            writer.Write(555);
            writer.Write(0.01f);
            writer.Write(0.5f); writer.Write(0.5f);
            writer.Write(4u); writer.Write(12u);
            writer.Write(1);
            writer.Write((byte)0); writer.Write((byte)3); writer.Write((byte)0);
            writer.Write(999);
            writer.Write(888);
        });
        UnityProjection projection = UnityProjection.Read(input, template, false, state);
        Assert.True(projection.TryBits("m_PixelsToUnits", out long pixels));
        Assert.Equal((long)BitConverter.SingleToInt32Bits(0.01f), pixels);
        Assert.Equal(4, projection.Number("m_RD/m_VertexData/m_VertexCount"));
        Assert.Equal(12, projection.Number("m_RD/m_VertexData/m_DataSize"));
        Assert.Equal(0, projection.Number(
            "m_RD/m_VertexData/m_Channels/Array/data#0/channel"));
        Assert.Equal(3, projection.Number(
            "m_RD/m_VertexData/m_Channels/Array/data#0/dimension"));
        Assert.Equal(0, projection.Number(
            "m_RD/m_VertexData/m_Channels/Array/data#0/format"));
        Assert.DoesNotContain(projection.Keys, key => key.EndsWith("/unrelated", StringComparison.Ordinal));
        Assert.DoesNotContain(projection.Keys, key => key.Contains("nonVertexUnrelated", StringComparison.Ordinal));
        Assert.DoesNotContain(projection.Keys, key => key.Contains("unrelatedRoot", StringComparison.Ordinal));
    }
}
