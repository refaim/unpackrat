using System;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityQuadCompilerContractsTests
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

    private static AssetTypeTemplateField ArrayOf(string name, AssetTypeTemplateField element)
        => Field(name, AssetValueType.Array, Field("size", AssetValueType.Int32), element);

    private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] children)
        => Field("Base", AssetValueType.None, children);

    private static MemoryStream Data(Action<BinaryWriter> write)
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) write(writer);
        stream.Position = 0;
        return stream;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);

        while (writer.BaseStream.Position % 4 != 0)
        {
            writer.Write((byte)0);
        }
    }

    private static UnityProjection Read(Stream input, AssetTypeTemplateField template,
        UnityProjection.State state)
        => UnityProjection.ReadBorrowed(input, 0, input.Length, template, false, state);

    [Fact]
    public void QuadPlanServesEveryScalarStorageKindAndReportsAbsentPaths()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Name", AssetValueType.String),
            Field("m_PixelsToUnits", AssetValueType.Int32),
            Field("m_Pivot", AssetValueType.None,
                Field("x", AssetValueType.Float),
                Field("y", AssetValueType.Double)),
            Field("m_Script", AssetValueType.String),
            Field("ignored", AssetValueType.Int32),
            Field("m_RD", AssetValueType.None,
                Field("m_VertexData", AssetValueType.None,
                    Field("m_Channels", AssetValueType.None,
                        ArrayOf("Array", Field("data", AssetValueType.None,
                            Field("stream", AssetValueType.Int32)))),
                    Field("ignored", AssetValueType.Int32))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        Assert.NotNull(state.QuadPlan);

        using MemoryStream input = Data(writer =>
        {
            WriteString(writer, "abc");
            writer.Write(42);
            writer.Write(1.5f);
            writer.Write(2.5d);
            WriteString(writer, "{}");
            writer.Write(99);
            writer.Write(2);
            writer.Write(7);
            writer.Write(8);
            writer.Write(123);
        });
        UnityProjection projection = Read(input, template, state);

        Assert.Equal("abc", projection.Text("m_Name"));
        Assert.Equal(string.Empty, projection.Text("m_PixelsToUnits"));
        Assert.Equal(42, projection.Number("m_PixelsToUnits"));
        Assert.False(projection.TryNumber("m_Name", out _));
        uint xBits = BitConverter.ToUInt32(BitConverter.GetBytes(1.5f), 0);
        Assert.True(projection.TryFloat32("m_Pivot/x", out uint storedX));
        Assert.Equal(xBits, storedX);
        Assert.False(projection.TryFloat32("m_Pivot/y", out _));
        Assert.True(projection.TryBits("m_Pivot/x", out long storedXBits));
        Assert.Equal(unchecked((long)xBits), storedXBits);
        Assert.True(projection.TryBits("m_Pivot/y", out long storedYBits));
        Assert.Equal(BitConverter.DoubleToInt64Bits(2.5d), storedYBits);
        Assert.False(projection.TryBits("m_Name", out _));
        Assert.Equal(2, projection.Bytes("m_Script").Length);
        Assert.False(projection.TryNumber("ignored", out _));
        Assert.False(projection.TryNumber("m_RD/m_VertexData/ignored", out _));
        Assert.Equal(7, projection.Number("m_RD/m_VertexData/m_Channels/Array/data#0/stream"));
        Assert.Equal(8, projection.Number("m_RD/m_VertexData/m_Channels/Array/data#1/stream"));
    }

    [Fact]
    public void QuadPlanUsesLastWriteForDuplicatePublishedPath()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Width", AssetValueType.Int32),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        Assert.NotNull(state.QuadPlan);

        using MemoryStream input = Data(writer =>
        {
            writer.Write(11);
            writer.Write(22);
        });
        UnityProjection projection = Read(input, template, state);

        Assert.Equal(new[] { "m_Width" }, projection.Keys);
        Assert.Equal(22, projection.Number("m_Width"));
    }

    [Fact]
    public void QuadPlanRejectsNestedIndexedArraysAndMatureFallbackKeepsTheValue()
    {
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_VertexData", AssetValueType.None,
                Field("m_Channels", AssetValueType.None,
                    ArrayOf("Array", Field("data", AssetValueType.None,
                        ArrayOf("Array", Field("data", AssetValueType.None,
                            Field("leaf", AssetValueType.Int32)))))))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        Assert.Null(state.QuadPlan);
        const string path = "m_RD/m_VertexData/m_Channels/Array/data#0/Array/data#0/leaf";

        using MemoryStream input = Data(writer =>
        {
            writer.Write(1);
            writer.Write(1);
            writer.Write(13);
        });
        UnityProjection projection = Read(input, template, state);

        Assert.Equal(new[] { path }, projection.Keys);
        Assert.Equal(13, projection.Number(path));
    }

    [Fact]
    public void QuadPlanRejectsSharedAliasedLeafAndMatureFallbackPreservesEveryOccurrence()
    {
        AssetTypeTemplateField leaf = Field("m_Width", AssetValueType.Int32);
        AssetTypeTemplateField template = Root(
            Field("wrapper", AssetValueType.None, leaf),
            leaf,
            Field("m_Height", AssetValueType.Int32));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        Assert.Null(state.QuadPlan);

        using MemoryStream input = Data(writer =>
        {
            writer.Write(5);
            writer.Write(6);
            writer.Write(7);
        });
        UnityProjection projection = Read(input, template, state);

        Assert.Equal(new[] { "wrapper/m_Width", "m_Width", "m_Height" }, projection.Keys);
        Assert.Equal(5, projection.Number("wrapper/m_Width"));
        Assert.Equal(6, projection.Number("m_Width"));
        Assert.Equal(7, projection.Number("m_Height"));
    }

    [Fact]
    public void QuadPlanRejectsConflictingKindsOnSamePathAndMatureFallbackKeepsLastValue()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Width", AssetValueType.Int32),
            Field("m_Width", AssetValueType.String));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        Assert.Null(state.QuadPlan);

        using MemoryStream input = Data(writer =>
        {
            writer.Write(3);
            WriteString(writer, "x");
        });
        UnityProjection projection = Read(input, template, state);

        Assert.Equal(new[] { "m_Width" }, projection.Keys);
        Assert.Equal("x", projection.Text("m_Width"));
        Assert.False(projection.TryNumber("m_Width", out _));
    }

    [Fact]
    public void QuadPlanRejectsDuplicateIndexedKeysAndMatureFallbackKeepsLastValue()
    {
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_VertexData", AssetValueType.None,
                Field("m_Channels", AssetValueType.None,
                    ArrayOf("Array", Field("data", AssetValueType.None,
                        Field("stream", AssetValueType.Int32))),
                    ArrayOf("Array", Field("data", AssetValueType.None,
                        Field("stream", AssetValueType.Int32)))))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        Assert.Null(state.QuadPlan);
        const string path = "m_RD/m_VertexData/m_Channels/Array/data#0/stream";

        using MemoryStream input = Data(writer =>
        {
            writer.Write(1);
            writer.Write(4);
            writer.Write(1);
            writer.Write(9);
        });
        UnityProjection projection = Read(input, template, state);

        Assert.Equal(new[] { path }, projection.Keys);
        Assert.Equal(9, projection.Number(path));
    }

    [Fact]
    public void QuadPlanFallsBackWhenIndexedArrayExceedsFixedSlots()
    {
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_VertexData", AssetValueType.None,
                Field("m_Channels", AssetValueType.None,
                    ArrayOf("Array", Field("data", AssetValueType.None,
                        Field("stream", AssetValueType.Int32)))))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        Assert.NotNull(state.QuadPlan);
        const string prefix = "m_RD/m_VertexData/m_Channels/Array/data#";

        using MemoryStream input = Data(writer =>
        {
            writer.Write(33);
            for (int i = 0; i < 33; i++) writer.Write(i);
        });
        UnityProjection projection = Read(input, template, state);

        Assert.Equal(33, projection.Keys.Count());
        Assert.Equal(0, projection.Number(prefix + "0/stream"));
        Assert.Equal(32, projection.Number(prefix + "32/stream"));
    }

    [Fact]
    public void QuadPlanUsesLastValueForNonIndexedArrayNaming()
    {
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_VertexData", AssetValueType.None,
                ArrayOf("m_Channels", Field("data", AssetValueType.None,
                    Field("stream", AssetValueType.Int32))))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        Assert.NotNull(state.QuadPlan);
        const string path = "m_RD/m_VertexData/m_Channels/data/stream";

        using MemoryStream input = Data(writer =>
        {
            writer.Write(2);
            writer.Write(6);
            writer.Write(8);
        });
        UnityProjection projection = Read(input, template, state);

        Assert.Equal(new[] { path }, projection.Keys);
        Assert.Equal(8, projection.Number(path));
    }

    [Fact]
    public void QuadPlanRejectsLargeIndexedSlotTableAndMatureFallbackKeepsValues()
    {
        AssetTypeTemplateField[] leaves = Enumerable.Range(0, 257)
            .Select(i => Field("leaf" + i, AssetValueType.Int32)).ToArray();
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("m_VertexData", AssetValueType.None,
                Field("m_Channels", AssetValueType.None,
                    ArrayOf("Array", Field("data", AssetValueType.None, leaves))))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        Assert.Null(state.QuadPlan);
        const string prefix = "m_RD/m_VertexData/m_Channels/Array/data#0/leaf";

        using MemoryStream input = Data(writer =>
        {
            writer.Write(1);
            for (int i = 0; i < 257; i++) writer.Write(i);
        });
        UnityProjection projection = Read(input, template, state);

        Assert.Equal(257, projection.Keys.Count());
        Assert.Equal(0, projection.Number(prefix + "0"));
        Assert.Equal(256, projection.Number(prefix + "256"));
    }

    [Fact]
    public void QuadPlanRejectsLargeDirectSlotTable()
    {
        AssetTypeTemplateField[] leaves = Enumerable.Range(0, 8193)
            .Select(i => Field("leaf" + i, AssetValueType.Int32)).ToArray();
        AssetTypeTemplateField template = Root(
            Field("m_Pivot", AssetValueType.None, leaves));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);

        Assert.Null(state.QuadPlan);
    }

    [Fact]
    public void QuadPlanIsAbsentWhenNoFieldIsPublished()
    {
        AssetTypeTemplateField template = Root(Field("unrelated", AssetValueType.Int32));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        Assert.Null(state.QuadPlan);

        using MemoryStream input = Data(writer => writer.Write(3));
        UnityProjection projection = Read(input, template, state);
        Assert.Empty(projection.Keys);
    }

    [Fact]
    public void StateRejectsDeepSchemaBeforeQuadCompilation()
    {
        AssetTypeTemplateField field = Field("m_Pivot", AssetValueType.Int32);
        for (int i = 0; i < 130; i++) field = Field("wrapper", AssetValueType.None, field);
        AssetTypeTemplateField template = Root(field);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
        {
            _ = new UnityProjection.State(template, UnityProjection.Profile.Quad);
        });
        Assert.Contains("too deep", error.Message);
    }
}
