using System.IO;
using System.Linq;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe;

public sealed class UnityQuadUnusualSchemaTests
{
    [Fact]
    public void EmptyNoneRootChild_RetainsCapturedWidth()
    {
        AssetTypeTemplateField template = Root(
            Node("data", AssetValueType.None),
            Node("m_Width", AssetValueType.Int32));
        byte[] payload = [1, 1, 0, 0];
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);

        using MemoryStream input = new(payload, writable: false);
        UnityProjection fast = UnityProjection.ReadBorrowed(
            input, 0, input.Length, template, bigEndian: false, state);

        string[] keys = fast.Keys.ToArray();
        Assert.Equal(new[] { "m_Width" }, keys);
        Assert.Equal(257L, fast.Number("m_Width"));
    }

    [Fact]
    public void SlashInNames_RejectsQuadPlanAndMatchesMatureKeysAndValues()
    {
        AssetTypeTemplateField template = Root(
            Node("m_RD", AssetValueType.None,
                Node("m_VertexData", AssetValueType.None,
                    Node("m_Channels", AssetValueType.None,
                        Node("wrapper/part", AssetValueType.None,
                            ArrayOf("Array", Node("data", AssetValueType.None,
                                Node("stream", AssetValueType.Int32))))))));
        byte[] payload = [1, 0, 0, 0, 123, 0, 0, 0];
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);

        using MemoryStream input = new(payload, writable: false);
        UnityProjection fast = UnityProjection.ReadBorrowed(
            input, 0, input.Length, template, bigEndian: false, state);

        string[] keys = fast.Keys.ToArray();
        Assert.Contains(
            "m_RD/m_VertexData/m_Channels/wrapper/part/Array/data#0/stream",
            keys);
        Assert.Equal(123L, fast.Number(
            "m_RD/m_VertexData/m_Channels/wrapper/part/Array/data#0/stream"));
        long[] values = keys.Select(key => fast.Number(key)).ToArray();

        using MemoryStream matureInput = new(payload, writable: false);
        UnityProjection mature = UnityProjection.ReadBorrowed(
            matureInput, 0, matureInput.Length, template, bigEndian: false, state,
            validateLayout: true);
        Assert.Equal(mature.Keys.ToArray(), keys);
        Assert.Equal(values, keys.Select(key => mature.Number(key)).ToArray());
    }

    [Fact]
    public void UncapturedScalarWithCapturedChildAlias_DoesNotInventIgnoredKey()
    {
        AssetTypeTemplateField width = Node("m_Width", AssetValueType.Int32);
        AssetTypeTemplateField ignored = Node("ignored", AssetValueType.Int32, width);
        AssetTypeTemplateField template = Root(ignored, width);
        byte[] payload = [11, 0, 0, 0, 22, 0, 0, 0];
        UnityProjection.State state = new(template, UnityProjection.Profile.Quad);

        using MemoryStream input = new(payload, writable: false);
        UnityProjection fast = UnityProjection.ReadBorrowed(
            input, 0, input.Length, template, bigEndian: false, state);

        string[] keys = fast.Keys.ToArray();
        Assert.Equal(new[] { "m_Width" }, keys);
        Assert.DoesNotContain("ignored", keys);
        Assert.Equal(22L, fast.Number("m_Width"));
        long[] values = keys.Select(key => fast.Number(key)).ToArray();

        using MemoryStream matureInput = new(payload, writable: false);
        UnityProjection mature = UnityProjection.ReadBorrowed(
            matureInput, 0, matureInput.Length, template, bigEndian: false, state,
            validateLayout: true);
        Assert.Equal(mature.Keys.ToArray(), keys);
        Assert.Equal(values, keys.Select(key => mature.Number(key)).ToArray());
    }

    private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] children)
        => Node("Root", AssetValueType.None, children);

    private static AssetTypeTemplateField Node(string name, AssetValueType kind,
        params AssetTypeTemplateField[] children)
        => new()
        {
            Name = name,
            Type = kind switch
            {
                AssetValueType.None => "TypelessData",
                AssetValueType.Int32 => "int",
                _ => "int",
            },
            Version = 0,
            ValueType = kind,
            HasValue = kind != AssetValueType.None,
            IsArray = false,
            IsAligned = false,
            Children = [.. children],
        };

    private static AssetTypeTemplateField ArrayOf(string name,
        AssetTypeTemplateField element)
        => new()
        {
            Name = name,
            Type = "vector",
            Version = 0,
            ValueType = AssetValueType.Array,
            HasValue = true,
            IsArray = true,
            IsAligned = false,
            Children = [Node("size", AssetValueType.Int32), element],
        };
}
