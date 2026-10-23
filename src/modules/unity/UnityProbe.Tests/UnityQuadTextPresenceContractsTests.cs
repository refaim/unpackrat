using System;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityQuadTextPresenceContractsTests
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

    private static AssetTypeTemplateField Aligned(AssetTypeTemplateField field)
    {
        field.IsAligned = true;
        return field;
    }

    private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] children)
        => Field("Base", AssetValueType.None, children);

    private static MemoryStream Data(int count, string? value)
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true))
        {
            writer.Write(count);
            if (value is not null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                writer.Write(bytes.Length);
                writer.Write(bytes);
                while ((writer.BaseStream.Position & 3) != 0) writer.Write((byte)0);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream Data(uint value)
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) writer.Write(value);
        stream.Position = 0;
        return stream;
    }

    private static UnityProjection Borrowed(Stream input, AssetTypeTemplateField template,
        UnityProjection.State state)
        => UnityProjection.ReadBorrowed(input, 0, input.Length, template, false, state);

    [Fact]
    public void TextPresenceTracksRepeatedArrayCountAndClearsOnAbsentRead()
    {
        AssetTypeTemplateField name = Aligned(Field("name", AssetValueType.String));
        AssetTypeTemplateField data = Field("data", AssetValueType.None, name);
        AssetTypeTemplateField array = Field("Array", AssetValueType.Array,
            Field("size", AssetValueType.Int32), data);
        AssetTypeTemplateField template = Root(
            Field("m_RD", AssetValueType.None,
                Field("m_SubMeshes", AssetValueType.None, array)));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        string key = "m_RD/m_SubMeshes/Array/data#0/name";

        using (MemoryStream input = Data(0, null))
        {
            UnityProjection absent = Borrowed(input, template, state);
            Assert.Empty(absent.Keys);
            Assert.DoesNotContain(key, absent.Keys);
            Assert.Equal(string.Empty, absent.Text(key));
        }

        using (MemoryStream input = Data(1, "hello"))
        {
            UnityProjection present = Borrowed(input, template, state);
            Assert.Equal(new[] { key }, present.Keys);
            Assert.Equal("hello", present.Text(key));
        }

        using (MemoryStream input = Data(0, null))
        {
            UnityProjection cleared = Borrowed(input, template, state);
            Assert.Empty(cleared.Keys);
            Assert.DoesNotContain(key, cleared.Keys);
            Assert.Equal(string.Empty, cleared.Text(key));
        }
    }

    [Fact]
    public void QuadPlanCapturesDeclaredValueInsideNestedVertexCountContainer()
    {
        AssetTypeTemplateField template = Root(
            Field("m_RD", AssetValueType.None,
                Field("m_VertexData", AssetValueType.None,
                    Field("m_VertexCount", AssetValueType.None,
                        Field("declared", AssetValueType.UInt32)))));
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);
        string parent = "m_RD/m_VertexData/m_VertexCount";
        string key = parent + "/declared";

        using (MemoryStream input = Data(4u))
        {
            UnityProjection fast = Borrowed(input, template, state);
            Assert.Equal(new[] { key }, fast.Keys);
            Assert.True(fast.TryNumber(key, out long captured));
            Assert.Equal(4L, captured);
            Assert.Equal(4, fast.Number(key));
            Assert.False(fast.TryNumber(parent, out _));
            Assert.Equal(0, fast.Number(parent));
        }

        using (MemoryStream input = Data(4u))
        {
            UnityProjection mature = UnityProjection.Read(input, template, bigEndian: false, state: state);
            Assert.Equal(new[] { key }, mature.Keys);
            Assert.Equal(4, mature.Number(key));
            Assert.False(mature.TryNumber(parent, out _));
        }
    }
}
