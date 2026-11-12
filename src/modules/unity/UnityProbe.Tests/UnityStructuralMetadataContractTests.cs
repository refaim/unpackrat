using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.Streams.Smart;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Extensions;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityStructuralMetadataContractTests
{
    private static AssetTypeTemplateField Field(string name, AssetValueType kind, params AssetTypeTemplateField[] children)
        => new() { Name = name, Type = name, ValueType = kind, HasValue = kind != AssetValueType.None,
            IsArray = kind is AssetValueType.Array or AssetValueType.ByteArray, Children = children.ToList() };
    private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] children) => Field("Base", AssetValueType.None, children);
    private static MemoryStream Data(Action<BinaryWriter> write)
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) write(writer);
        stream.Position = 0;
        return stream;
    }
    private static UnityProjection Read(AssetTypeTemplateField template, Action<BinaryWriter> write)
    {
        using MemoryStream stream = Data(write);
        return UnityProjection.Read(stream, template, false);
    }

    [Fact]
    public void DictionaryStoragePreservesDoubleAndFloatBitsWithoutCrossTyping()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Width", AssetValueType.UInt32), Field("m_Width", AssetValueType.Float),
            Field("m_Height", AssetValueType.Double),
            Field("m_StreamData", AssetValueType.None, Field("offset", AssetValueType.Double), Field("size", AssetValueType.UInt32)));
        UnityProjection.State state = new(template);
        Assert.True(state.DictionaryStorage);
        UnityProjection projection = Read(template, writer =>
        {
            writer.Write(123u); writer.Write(0x7FC00123u);
            writer.Write(0x000000003F800000UL);
            writer.Write(0x7FF8000000000001UL);
            writer.Write(7u);
        });
        Assert.True(projection.TryFloat32("m_Width", out uint single));
        Assert.Equal(0x7FC00123u, single);
        Assert.False(projection.TryNumber("m_Width", out _));
        Assert.True(projection.TryBits("m_Height", out long wide));
        Assert.Equal(unchecked((long)0x000000003F800000UL), wide);
        Assert.False(projection.TryFloat32("m_Height", out _));
        Assert.True(projection.TryBits("m_StreamData/offset", out long nanBits));
        Assert.Equal(unchecked((long)0x7FF8000000000001UL), nanBits);
        Assert.False(projection.TryNumber("m_StreamData/offset", out _));
        Assert.True(projection.TryNumber("m_StreamData/size", out long size));
        Assert.Equal(7L, size);
        Assert.Equal(new[] { "m_Width", "m_Height", "m_StreamData/offset", "m_StreamData/size" }, projection.Keys);
    }

    [Fact]
    public void CatalogueRootWhitelistCapturesKnownMetadataAndSkipsOtherRoots()
    {
        string[] roots =
        [
            "m_Name", "m_Width", "m_Height", "image data", "m_StreamData", "m_Script", "m_FontData",
            "m_MovieData", "m_OriginalPath", "m_ExternalResources", "m_AudioData", "m_Type",
            "m_Resource", "m_IndexBuffer",
        ];
        foreach (string root in roots)
        {
            UnityProjection projection = Read(Root(Field(root, AssetValueType.Int32)), writer => writer.Write(7));
            Assert.Contains(root, projection.Keys);
            Assert.Equal(7, projection.Number(root));
        }
        UnityProjection unrelated = Read(Root(Field("unrelated", AssetValueType.Int32)), writer => writer.Write(7));
        Assert.Empty(unrelated.Keys);
        Assert.Equal(0, unrelated.Number("unrelated"));
    }

    [Fact]
    public void NestedCatalogueFieldsRequireTheirKnownScopeAndName()
    {
        AssetTypeTemplateField template = Root(
            Field("m_VertexData", AssetValueType.None, Field("m_VertexCount", AssetValueType.Int32),
                Field("m_DataSize", AssetValueType.Int32), Field("other", AssetValueType.Int32)),
            Field("m_CompressedMesh", AssetValueType.None, Field("m_Vertices", AssetValueType.None,
                Field("m_NumItems", AssetValueType.Int32), Field("other", AssetValueType.Int32))),
            Field("unrelated", AssetValueType.None, Field("m_VertexCount", AssetValueType.Int32), Field("m_NumItems", AssetValueType.Int64)));
        UnityProjection projection = Read(template, writer =>
        {
            writer.Write(3); writer.Write(4); writer.Write(99);
            writer.Write(5); writer.Write(99);
            writer.Write(1); writer.Write(2L);
        });
        Assert.Equal(new[] { "m_VertexData/m_VertexCount", "m_VertexData/m_DataSize" }, projection.Keys.Where(key => key.StartsWith("m_VertexData/", StringComparison.Ordinal)));
        Assert.Equal(new[] { "m_CompressedMesh/m_Vertices/m_NumItems" }, projection.Keys.Where(key => key.StartsWith("m_CompressedMesh/", StringComparison.Ordinal)));
        Assert.Equal(3, projection.Number("m_VertexData/m_VertexCount"));
        Assert.Equal(4, projection.Number("m_VertexData/m_DataSize"));
        Assert.Equal(5, projection.Number("m_CompressedMesh/m_Vertices/m_NumItems"));
        Assert.DoesNotContain(projection.Keys, key => key.StartsWith("unrelated/", StringComparison.Ordinal));
        Assert.DoesNotContain(projection.Keys, key => key.EndsWith("/other", StringComparison.Ordinal));
    }


    [Fact]
    public void ScriptStringIsBorrowedAsBytesRatherThanDecodedText()
    {
        UnityProjection projection = Read(Root(Field("m_Script", AssetValueType.String)), writer =>
        {
            writer.Write(3); writer.Write(new byte[] { 1, 2, 3 });
        });
        Assert.Equal(new UnityProjection.ByteRange(4, 3), projection.Bytes("m_Script"));
        Assert.Equal(string.Empty, projection.Text("m_Script"));
        Assert.Equal(0, projection.Number("m_Script"));
    }

    [Fact]
    public void UnsupportedCapturedScalarSchemaIsRefusedInsteadOfDefaulted()
    {
        using MemoryStream stream = Data(writer => writer.Write(1));
        Assert.Throws<InvalidDataException>(() => UnityProjection.Read(stream,
            Root(Field("m_Width", (AssetValueType)0x7F)), false));
    }

    [Fact]
    public void MalformedTextureHeaderEmitsNoTextureInsteadOfInventingDimensions()
    {
        const string sentinel = "meta-sentinel-5c1f8a0d";
        byte[] pixels = { 10, 20, 30, 255 };
        byte[] serialized = UnityFixtureBuilder.SerializedInlineTexture(sentinel, pixels);
        byte[] name = Encoding.UTF8.GetBytes(sentinel);
        int nameOffset = serialized.AsSpan().IndexOf(name);
        Assert.True(nameOffset >= 4);
        Assert.Equal(nameOffset, serialized.AsSpan().LastIndexOf(name));
        Assert.Equal(name.Length, BinaryPrimitives.ReadInt32LittleEndian(serialized.AsSpan(nameOffset - 4, 4)));
        byte[] malformed = serialized.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(nameOffset - 4, 4), -1);
        string selected = Path.Combine(Temp.NewDirectory(), "malformed.assets");
        File.WriteAllBytes(selected, malformed);

        using UnitySession session = UnitySession.Open(selected);

        Assert.Empty(session.Entries);
    }


    [Fact]
    public void EagerTextureAndMetadataOnlyTextShareOneLazyFactory()
    {
        byte[] pixels = { 10, 20, 30, 255 };
        byte[] firstBytes = UnityFixtureBuilder.SerializedInlineTexture("texture", pixels);
        byte[] secondBytes = UnityFixtureBuilder.SerializedTextAsset("notes", "payload");

        List<SmartStream> roots =
        [
            SmartStream.CreateMemory(firstBytes),
            SmartStream.CreateMemory(secondBytes),
        ];
        SerializedFile first = (SerializedFile)SchemeReader.ReadFile(roots[0], "first.assets", "first.assets", FileReadMode.Eager);
        SerializedFile second = (SerializedFile)SchemeReader.ReadFile(roots[1], "second.assets", "second.assets", FileReadMode.MetadataOnly);

        GameBundle root = new();
        UnitySource.LazyAssetFactory factory = new(
            new GameAssetFactory(new BaseManager(static _ => { })), compact: true);
        SerializedAssetCollection textureCollection = root.AddCollectionFromSerializedFile(first, factory);
        SerializedAssetCollection textCollection = root.AddCollectionFromSerializedFile(second, factory);
        root.InitializeAllDependencyLists();

        IUnityGraph graph = factory.BuildCompactGraph(root, first,
            [textureCollection, textCollection], [second], roots);
        using UnitySession session = UnitySession.FromGraph(graph);

        UnityCatalogEntry texture = Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
        UnityCatalogEntry text = Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Text);
        Assert.Equal("texture", texture.Name);
        Assert.Equal("notes", text.Name);
        using MemoryStream texturePng = new();
        Assert.True(session.Export(texture.Index, texturePng));
        Assert.Equal(pixels, PngDecoder.Decode(texturePng.ToArray()).Rgba);
        using MemoryStream textBytes = new();
        Assert.True(session.Export(text.Index, textBytes));
        Assert.Equal(Encoding.UTF8.GetBytes("payload"), textBytes.ToArray());
    }
}
