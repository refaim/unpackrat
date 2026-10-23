using System;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityQuadSchemaRegressionTests
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

    private static UnityProjection ReadBorrowed(Stream input, AssetTypeTemplateField template,
        UnityProjection.State state)
        => UnityProjection.ReadBorrowed(input, 0, input.Length, template, false, state);

    private static UnityProjection ReadMature(Stream input, AssetTypeTemplateField template,
        UnityProjection.State state)
        => UnityProjection.ReadBorrowed(input, 0, input.Length, template, false, state,
            captureRawName: true);

    private static void AssertMatchesMature(UnityProjection mature, UnityProjection fallback,
        params string[] paths)
    {
        Assert.Equal(mature.Keys.ToArray(), fallback.Keys.ToArray());
        foreach (string path in paths)
        {
            bool matureNumber = mature.TryNumber(path, out long matureValue);
            bool fallbackNumber = fallback.TryNumber(path, out long fallbackValue);
            Assert.Equal(matureNumber, fallbackNumber);
            if (matureNumber) Assert.Equal(matureValue, fallbackValue);

            bool matureFloat = mature.TryFloat32(path, out uint matureFloatValue);
            bool fallbackFloat = fallback.TryFloat32(path, out uint fallbackFloatValue);
            Assert.Equal(matureFloat, fallbackFloat);
            if (matureFloat) Assert.Equal(matureFloatValue, fallbackFloatValue);

            bool matureBits = mature.TryBits(path, out long matureBitsValue);
            bool fallbackBits = fallback.TryBits(path, out long fallbackBitsValue);
            Assert.Equal(matureBits, fallbackBits);
            if (matureBits) Assert.Equal(matureBitsValue, fallbackBitsValue);

            Assert.Equal(mature.Text(path), fallback.Text(path));
            Assert.Equal(mature.Bytes(path), fallback.Bytes(path));
        }
    }

    [Fact]
    public void QuadPlanRequiresItsCompiledTemplateRootAcrossReusedState()
    {
        AssetTypeTemplateField width = Field("m_Width", AssetValueType.Int32);
        AssetTypeTemplateField original = Root(width);
        AssetTypeTemplateField wrapped = Root(Field("wrapper", AssetValueType.None, width));
        UnityProjection.State state = new(original, UnityProjection.Profile.Quad);
        UnityProjection.State matureState = new(original, UnityProjection.Profile.Quad);

        using MemoryStream firstInput = Data(writer => writer.Write(7));
        UnityProjection first = ReadBorrowed(firstInput, original, state);
        Assert.Equal(new[] { "m_Width" }, first.Keys);
        Assert.Equal(7, first.Number("m_Width"));

        using MemoryStream mismatchInput = Data(writer => writer.Write(9));
        UnityProjection fallback = ReadBorrowed(mismatchInput, wrapped, state);
        using MemoryStream matureInput = Data(writer => writer.Write(9));
        UnityProjection mature = ReadMature(matureInput, wrapped, matureState);
        Assert.Equal(new[] { "wrapper/m_Width" }, fallback.Keys);
        AssertMatchesMature(mature, fallback, "wrapper/m_Width");
        Assert.False(fallback.TryNumber("m_Width", out _));
        Assert.Equal(9, fallback.Number("wrapper/m_Width"));

        using MemoryStream recoveredInput = Data(writer => writer.Write(11));
        UnityProjection recovered = ReadBorrowed(recoveredInput, original, state);
        Assert.Equal(new[] { "m_Width" }, recovered.Keys);
        Assert.Equal(11, recovered.Number("m_Width"));
        Assert.DoesNotContain("wrapper/m_Width", recovered.Keys);
    }

    [Fact]
    public void QuadPlanDeclinesCompositeArrayDataAndMatchesMatureFallback()
    {
        AssetTypeTemplateField stream = Field("stream", AssetValueType.Int32);
        AssetTypeTemplateField compositeData = Field("data", AssetValueType.None, stream);
        AssetTypeTemplateField compositeArray = Field("Array", AssetValueType.None, compositeData);
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_VertexData", AssetValueType.None,
                Field("m_Channels", AssetValueType.None, compositeArray))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        UnityProjection.State matureState = new(template, UnityProjection.Profile.Quad);
        const string path = "m_RD/m_VertexData/m_Channels/Array/data#0/stream";

        using MemoryStream input = Data(writer => writer.Write(23));
        UnityProjection fallback = ReadBorrowed(input, template, state);
        using MemoryStream matureInput = Data(writer => writer.Write(23));
        UnityProjection mature = ReadMature(matureInput, template, matureState);
        Assert.Equal(new[] { path }, fallback.Keys);
        AssertMatchesMature(mature, fallback, path);
        Assert.Equal(23, fallback.Number(path));
        Assert.DoesNotContain("m_RD/m_VertexData/m_Channels/Array/data/stream", fallback.Keys);

        using MemoryStream repeatedInput = Data(writer => writer.Write(29));
        UnityProjection repeated = ReadBorrowed(repeatedInput, template, state);
        Assert.Equal(new[] { path }, repeated.Keys);
        Assert.Equal(29, repeated.Number(path));
    }
}
