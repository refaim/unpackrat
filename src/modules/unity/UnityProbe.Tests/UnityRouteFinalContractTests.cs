using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityRouteFinalContractTests
{
    private const int FieldBudget = 16_000_000;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void SimpleWantedCatalogueRootsAreProjected()
    {
        string[] roots =
        [
            "m_Name", "m_Width", "m_Height", "image data", "m_StreamData",
            "m_Script", "m_FontData", "m_MovieData", "m_OriginalPath", "m_ExternalResources",
            "m_AudioData", "m_Type", "m_Resource", "m_IndexBuffer", "m_SpriteAtlas", "m_RenderDataKey",
            "m_Rect", "m_Offset",
        ];
        AssetTypeTemplateField[] fields = roots.Select(name => Field(name, AssetValueType.Int32)).ToArray();
        List<byte> body = [];
        for (int index = 0; index < fields.Length; index++) AddInt32(body, 101 + index);
        using MemoryStream input = new(body.ToArray(), writable: false);
        UnityProjection projection = UnityProjection.Read(input, Root(fields), false);
        for (int index = 0; index < roots.Length; index++)
            Assert.Equal(101 + index, projection.Number(roots[index]));
    }

    [Fact]
    public void LookalikeCatalogueRootsAreIgnoredBeforeTheTrailingKnownField()
    {
        string[] simple =
        [
            "m_Name", "m_Width", "m_Height", "image data", "m_StreamData",
            "m_Script", "m_FontData", "m_MovieData", "m_OriginalPath", "m_ExternalResources",
            "m_AudioData", "m_Type", "m_Resource", "m_IndexBuffer", "m_SpriteAtlas", "m_RenderDataKey",
            "m_Rect", "m_Offset",
        ];
        string[] complex = ["m_VertexData", "m_CompressedMesh", "m_RD", "m_RenderDataMap"];
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_";
        HashSet<string> known = new(simple, StringComparer.Ordinal);
        known.UnionWith(complex);
        HashSet<string> candidates = new(StringComparer.Ordinal);

        foreach (string root in simple.Concat(complex))
        {
            for (int index = 0; index < root.Length; index++)
                foreach (char replacement in alphabet)
                {
                    if (replacement == root[index]) continue;
                    candidates.Add(root[..index] + replacement + root[(index + 1)..]);
                }

            foreach (char edge in alphabet)
            {
                candidates.Add(edge + root);
                candidates.Add(root + edge);
            }

            candidates.Add(root[1..]);
            candidates.Add(root[..^1]);
            candidates.Add(root[1..^1]);
        }

        candidates.ExceptWith(known);
        string[] unknown = candidates.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(unknown);

        AssetTypeTemplateField[] fields = unknown.Select(name => Field(name, AssetValueType.Int32))
            .Append(Field("m_Width", AssetValueType.Int32))
            .ToArray();
        List<byte> body = [];
        for (int index = 0; index < unknown.Length; index++) AddInt32(body, index + 1);
        AddInt32(body, 777);

        using MemoryStream input = new(body.ToArray(), writable: false);
        UnityProjection projection = UnityProjection.Read(input, Root(fields), false);

        foreach (string name in unknown)
            Assert.False(projection.Keys.Contains(name, StringComparer.Ordinal), $"Unexpected projection for '{name}'.");
        Assert.Single(projection.Keys);
        Assert.Contains("m_Width", projection.Keys);
        Assert.Equal(777, projection.Number("m_Width"));
    }

    [Fact]
    public void NullRootNameIsSkippedWithoutMisaligningTheFollowingField()
    {
        AssetTypeTemplateField[] fields =
        [
            Field(null!, AssetValueType.Int32),
            Field("m_Width", AssetValueType.Int32),
        ];
        List<byte> body = [];
        AddInt32(body, 12345);
        AddInt32(body, 777);

        using MemoryStream input = new(body.ToArray(), writable: false);
        UnityProjection projection = UnityProjection.Read(input, Root(fields), false);

        string[] keys = projection.Keys.ToArray();
        Assert.Single(keys);
        Assert.Equal("m_Width", keys[0]);
        Assert.Equal(777, projection.Number("m_Width"));
    }

    [Theory]
    [InlineData("second", true)]
    [InlineData("third", false)]
    public void UnknownRenderDataMapBranchDoesNotCaptureKnownTextureFact(string branch, bool captured)
    {
        string path = $"m_RenderDataMap/Array/data/{branch}/textureRect/x";
        var template = Root(
            Field("m_RenderDataMap", AssetValueType.None,
                Field("Array", AssetValueType.None,
                    Field("data", AssetValueType.None,
                        Field(branch, AssetValueType.None,
                            Field("textureRect", AssetValueType.None,
                                Field("x", AssetValueType.Int32)
                            )
                        )
                    )
                )
            ),
            Field("m_Width", AssetValueType.Int32)
        );
        var bytes = new List<byte>();
        AddInt32(bytes, 7);
        AddInt32(bytes, 777);
        using MemoryStream input = new(bytes.ToArray(), writable: false);

        var projection = UnityProjection.Read(input, template, false);

        Assert.Equal(captured, projection.Keys.Contains(path, StringComparer.Ordinal));
        if (captured)
        {
            Assert.Equal(7L, projection.Number(path));
        }
        Assert.Equal(777L, projection.Number("m_Width"));
    }

    [Fact]
    public void ScalarReplayStoresOneCapturedValueThenFailsOnTheNextBudgetedField()
    {
        AssetTypeTemplateField template = Root(
            ArrayField("ignored", Field("data", AssetValueType.None)),
            Field("m_Width", AssetValueType.Int32),
            Field("m_Height", AssetValueType.Int32));
        UnityProjection.State routeState = new(template);
        UnityProjection.State matureState = new(template);
        Assert.NotNull(routeState.Route);

        List<byte> body = [];
        AddInt32(body, FieldBudget - 2);
        AddInt32(body, 77);
        AddInt32(body, 88);
        const int offset = 7;
        byte[] source = Embed(body.ToArray(), offset, 3);
        using MemoryStream routeInput = new(source, writable: false);
        using MemoryStream matureInput = new(source, writable: false);
        ReadOutcome route = FailingRead(template, routeState, routeInput, offset, body.Count, validateLayout: false);
        ReadOutcome mature = FailingRead(template, matureState, matureInput, offset, body.Count, validateLayout: true);

        Assert.NotNull(route.Failure);
        Assert.NotNull(mature.Failure);
        Assert.IsType<InvalidDataException>(route.Failure);
        Assert.Equal(mature.Failure.GetType(), route.Failure.GetType());
        Assert.Equal(mature.Logical, route.Logical);
        Assert.Equal(mature.Physical, route.Physical);
        UnityProjection scratch = Assert.IsType<UnityProjection>(routeState.Scratch);
        Assert.Equal(77, scratch.Number("m_Width"));
        Assert.Equal(0, scratch.Number("m_Height"));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(5, false)]
    public void OverlappingArrayBudgetAndTruncationKeepTheEarlierFailure(int availableBytes, bool budgetWins)
    {
        AssetTypeTemplateField template = Root(
            ArrayField("ignored", Field("data", AssetValueType.None)),
            ArrayField("target", Field("data", AssetValueType.None,
                Field("value", AssetValueType.Int32),
                Field("tail", AssetValueType.UInt16))),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State routeState = new(template);
        UnityProjection.State matureState = new(template);
        Assert.NotNull(routeState.Route);

        List<byte> body = [];
        AddInt32(body, FieldBudget - 5);
        AddInt32(body, 2);
        AddInt32(body, 1);
        AddUInt16(body, 2);
        if (availableBytes == 10) AddInt32(body, 3);
        else body.RemoveAt(body.Count - 1);

        const int offset = 3;
        byte[] source = Embed(body.ToArray(), offset, 2);
        using MemoryStream routeInput = new(source, writable: false);
        using MemoryStream matureInput = new(source, writable: false);
        ReadOutcome route = FailingRead(template, routeState, routeInput, offset, body.Count, validateLayout: false);
        ReadOutcome mature = FailingRead(template, matureState, matureInput, offset, body.Count, validateLayout: true);

        Assert.NotNull(route.Failure);
        Assert.NotNull(mature.Failure);
        Assert.Equal(mature.Failure.GetType(), route.Failure.GetType());
        Assert.Equal(budgetWins ? typeof(InvalidDataException) : typeof(EndOfStreamException),
            route.Failure.GetType());
        if (budgetWins)
            Assert.Equal("Unity projection exceeded its field budget.", route.Failure.Message);
        Assert.Equal(mature.Logical, route.Logical);
    }

    [Fact]
    public void NestedAndAlignedElementLayoutsUseGuardedArrays()
    {
        AssetTypeTemplateField nested = Field("data", AssetValueType.None,
            ArrayField("inner", Field("data", AssetValueType.Int32)));
        AssetTypeTemplateField aligned = Field("data", AssetValueType.None,
            Field("value", AssetValueType.Int32));
        aligned.IsAligned = true;
        AssetTypeTemplateField template = Root(
            ArrayField("nested", nested),
            ArrayField("aligned", aligned),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template);
        Assert.NotNull(state.Route);

        List<byte> body = [];
        AddInt32(body, 0);
        AddInt32(body, 0);
        AddInt32(body, 44);
        using MemoryStream input = new(body.ToArray(), writable: false);
        UnityProjection projection = UnityProjection.ReadBorrowed(input, 0, body.Count, template, false, state);
        Assert.Equal(44, projection.Number("m_Width"));
    }

    private sealed record ReadOutcome(Exception? Failure, long Logical, long Physical);

    private static ReadOutcome FailingRead(AssetTypeTemplateField template, UnityProjection.State state,
        Stream input, long offset, long length, bool validateLayout)
    {
        object bounded = state.Reader.BaseStream;
        Type boundedType = bounded.GetType();
        MethodInfo attach = boundedType.GetMethod("AttachRange", Instance)!;
        MethodInfo detach = boundedType.GetMethod("Detach", Instance)!;
        MethodInfo read = typeof(UnityProjection).GetMethod("Read", Static, null,
            [typeof(AssetTypeTemplateField), typeof(UnityProjection.State), typeof(bool), typeof(bool),
                typeof(bool), typeof(bool)], null)!;
        attach.Invoke(bounded, [input, offset, length]);
        try
        {
            Exception? failure = null;
            try
            {
                read.Invoke(null, [template, state, validateLayout, false, true, false]);
            }
            catch (TargetInvocationException error)
            {
                failure = error.InnerException;
            }
            return new ReadOutcome(failure, state.Reader.Position, input.Position);
        }
        finally
        {
            detach.Invoke(bounded, null);
        }
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

    private static void AddInt32(List<byte> bytes, int value)
    {
        Span<byte> encoded = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(encoded, value);
        bytes.AddRange(encoded.ToArray());
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        Span<byte> encoded = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(encoded, value);
        bytes.AddRange(encoded.ToArray());
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
