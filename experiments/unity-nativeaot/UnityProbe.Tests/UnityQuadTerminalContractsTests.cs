using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityQuadTerminalContractsTests
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
            Assert.Equal(mature.Text(path), fallback.Text(path));
            Assert.Equal(mature.Bytes(path), fallback.Bytes(path));
        }
    }

    [Fact]
    public void QuadPlanThrowsFieldBudgetBeforeCapturedWidthBeyond512Visits()
    {
        List<AssetTypeTemplateField> children = new();
        for (int i = 0; i < 513; i++) children.Add(Field("ignored" + i, AssetValueType.Int32));
        children.Add(Field("m_Width", AssetValueType.Int32));
        AssetTypeTemplateField template = Root(children.ToArray());
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        Assert.Single(state.Paths);

        using MemoryStream input = Data(writer =>
        {
            for (int i = 0; i < 514; i++) writer.Write(i);
        });
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => { ReadBorrowed(input, template, state); });
        Assert.Equal("Unity projection exceeded its field budget.", error.Message);
    }

    [Fact]
    public void QuadPlanSkipsUncapturedStringBeforeCapturedWidth()
    {
        AssetTypeTemplateField template = Root(
            Field("note", AssetValueType.String),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);

        using MemoryStream input = Data(writer =>
        {
            writer.Write(4);
            writer.Write(Encoding.UTF8.GetBytes("note"));
            writer.Write(42);
        });
        UnityProjection projection = ReadBorrowed(input, template, state);

        Assert.Equal(new[] { "m_Width" }, projection.Keys);
        Assert.Equal(42, projection.Number("m_Width"));
        Assert.Equal(string.Empty, projection.Text("note"));
        Assert.DoesNotContain("note", projection.Keys);
    }

    [Fact]
    public void QuadPlanStopsAtLastCapturedNameWithoutRewind()
    {
        AssetTypeTemplateField template = Root(Field("m_Name", AssetValueType.String));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);

        using MemoryStream input = Data(writer =>
        {
            writer.Write(4);
            writer.Write(Encoding.UTF8.GetBytes("hero"));
        });
        UnityProjection projection = ReadBorrowed(input, template, state);

        Assert.Equal(new[] { "m_Name" }, projection.Keys);
        Assert.Equal("hero", projection.Text("m_Name"));
        Assert.Empty(projection.RawName.ToArray());
        Assert.Equal(default(UnityProjection.ByteRange), projection.Bytes("m_Name"));
        Assert.False(projection.TryNumber("m_Name", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WantedQuadVertexDataLookalikeIsNeverCaptured(bool primitive)
    {
        AssetTypeTemplateField vertexData = primitive
            ? Field("m_VertexData", AssetValueType.UInt32)
            : Field("m_VertexData", AssetValueType.None, Field("unrecognized", AssetValueType.UInt32));
        AssetTypeTemplateField template = Root(
            Field("m_RD", AssetValueType.None, vertexData),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);

        using MemoryStream input = Data(writer =>
        {
            writer.Write(5u);
            writer.Write(42);
        });
        UnityProjection projection = ReadBorrowed(input, template, state);

        Assert.Equal(new[] { "m_Width" }, projection.Keys);
        Assert.Equal(42, projection.Number("m_Width"));
        Assert.DoesNotContain("m_RD/m_VertexData", projection.Keys);
    }

    [Fact]
    public void MatureQuadReadStoresNumberAndRejectsTextKindLookup()
    {
        AssetTypeTemplateField template = Root(Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);

        using MemoryStream input = Data(writer => writer.Write(42));
        UnityProjection projection = UnityProjection.Read(input, template, bigEndian: false, state: state);

        Assert.Equal(new[] { "m_Width" }, projection.Keys);
        Assert.Equal(string.Empty, projection.Text("m_Width"));
        Assert.Equal(42, projection.Number("m_Width"));
    }

    [Fact]
    public void QuadPlanFallsBackWhenRuntimeArrayElementsExceedIndexedLimit()
    {
        List<AssetTypeTemplateField> data = new();
        for (int i = 0; i < 33; i++) data.Add(Field("data", AssetValueType.UInt8));
        AssetTypeTemplateField template = Root(
            Field("Array", AssetValueType.None, data.ToArray()),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        UnityProjection.State matureState = new(template, UnityProjection.Profile.Quad);

        using MemoryStream input = Data(writer =>
        {
            for (int i = 0; i < 33; i++) writer.Write((byte)i);
            writer.Write(42);
        });
        UnityProjection fallback = ReadBorrowed(input, template, state);
        using MemoryStream matureInput = Data(writer =>
        {
            for (int i = 0; i < 33; i++) writer.Write((byte)i);
            writer.Write(42);
        });
        UnityProjection mature = ReadMature(matureInput, template, matureState);

        Assert.Equal(new[] { "m_Width" }, fallback.Keys);
        Assert.Equal(42, fallback.Number("m_Width"));
        AssertMatchesMature(mature, fallback, "m_Width");
    }
}
