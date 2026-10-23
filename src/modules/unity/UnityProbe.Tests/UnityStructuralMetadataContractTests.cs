using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

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
    private static Type Nested(string name) => typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == name);
    private static object Construct(Type type, params object?[] arguments)
        => Activator.CreateInstance(type, Instance, null, arguments, null)!;
    private static uint B(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));
    private static AssetCollection Collection()
        => AssetCreator.CreateCollection(UnityVersion.Parse("2021.3.0f1"));

    private static object Source(bool present = true, float x = 0f, float y = 0f, float width = 2f, float height = 2f,
        float offsetX = 0f, float offsetY = 0f, float downscale = 0f, uint settings = 2u)
        => Construct(Nested("SpriteSourceFacts"), present, B(x), B(y), B(width), B(height), B(offsetX), B(offsetY), B(downscale), settings);
    private static object CanvasFacts(bool present = true, float width = 2f, float height = 2f)
        => Construct(Nested("SpriteCanvasFacts"), present, B(width), B(height));
    private static object SpriteFacts(object source, object canvas, AssetCollection? collection = null, int width = 2, int height = 2)
    {
        Type type = Nested("SpriteFacts");
        object facts = Activator.CreateInstance(type)!;
        type.GetField("Source")!.SetValue(facts, source);
        type.GetField("Canvas")!.SetValue(facts, canvas);
        type.GetField("SourceCollection")!.SetValue(facts, collection);
        type.GetField("SourceWidth")!.SetValue(facts, width);
        type.GetField("SourceHeight")!.SetValue(facts, height);
        type.GetField("Resolved")!.SetValue(facts, collection is not null);
        return facts;
    }

    private static Type FactsReader() => Nested("SpriteFactsReader");
    private static object ReadFacts(UnityProjection projection, bool local)
    {
        MethodInfo method = FactsReader().GetMethod(local ? "ReadLocalSource" : "ReadSource", Static)!;
        return method.Invoke(null, local ? new object?[] { projection } : new object?[] { projection, "m_RD/" })!;
    }
    private static bool Present(object facts) => (bool)facts.GetType().GetProperty("Present")!.GetValue(facts)!;
    private static uint Raw(object facts) => (uint)facts.GetType().GetProperty("SettingsRaw")!.GetValue(facts)!;
    private static object ReadCanvasFacts(UnityProjection projection)
        => FactsReader().GetMethod("ReadCanvas", Static)!.Invoke(null, new object?[] { projection })!;
    private static bool IsFullTexture(object facts, int width, int height)
        => (bool)Nested("SpriteFacts").GetMethod("IsFullTexture", Instance)!.Invoke(facts, new object?[] { width, height })!;
    private static bool Exportable(object source, object canvas, int textureWidth, int textureHeight)
        => (bool)FactsReader().GetMethod("IsExportableRectangle", Static)!.Invoke(null,
            new object?[] { source, canvas, textureWidth, textureHeight })!;

    private static UnityProjection SourceProjection(string omit = "", string settings = "raw", uint raw = 0,
        int mode = 1, int rotation = 1, bool packed = true, bool includePacked = true)
    {
        List<AssetTypeTemplateField> rectangle = new();
        List<AssetTypeTemplateField> offset = new();
        List<Action<BinaryWriter>> writes = new();
        void FloatField(List<AssetTypeTemplateField> fields, string path, string name, float value)
        {
            if (omit == path) return;
            fields.Add(Field(name, AssetValueType.Float));
            writes.Add(writer => writer.Write(value));
        }
        FloatField(rectangle, "textureRect/x", "x", 0f);
        FloatField(rectangle, "textureRect/y", "y", 0f);
        FloatField(rectangle, "textureRect/width", "width", 2f);
        FloatField(rectangle, "textureRect/height", "height", 2f);
        FloatField(offset, "textureRectOffset/x", "x", 0f);
        FloatField(offset, "textureRectOffset/y", "y", 0f);
        List<AssetTypeTemplateField> renderData = new()
        {
            Field("textureRect", AssetValueType.None, rectangle.ToArray()),
            Field("textureRectOffset", AssetValueType.None, offset.ToArray()),
        };
        if (omit != "downscaleMultiplier")
        {
            renderData.Add(Field("downscaleMultiplier", AssetValueType.Float));
            writes.Add(writer => writer.Write(0f));
        }
        if (settings == "raw")
        {
            renderData.Add(Field("settingsRaw", AssetValueType.UInt32));
            writes.Add(writer => writer.Write(raw));
        }
        else
        {
            if (settings is not ("missingMode" or "missingAll"))
            {
                renderData.Add(Field("packingMode", AssetValueType.Int32));
                writes.Add(writer => writer.Write(mode));
            }
            if (settings is not ("missingRotation" or "missingAll"))
            {
                renderData.Add(Field("packingRotation", AssetValueType.Int32));
                writes.Add(writer => writer.Write(rotation));
            }
            if (includePacked && settings != "missingPacked")
            {
                renderData.Add(Field("packed", AssetValueType.Bool));
                writes.Add(writer => writer.Write(packed));
            }
        }
        return Read(Root(Field("m_RD", AssetValueType.None, renderData.ToArray())), writer =>
        {
            foreach (Action<BinaryWriter> write in writes) write(writer);
        });
    }
    private static UnityProjection CanvasProjection(bool width, bool height)
    {
        List<AssetTypeTemplateField> fields = new();
        List<Action<BinaryWriter>> writes = new();
        if (width) { fields.Add(Field("width", AssetValueType.Float)); writes.Add(writer => writer.Write(2f)); }
        if (height) { fields.Add(Field("height", AssetValueType.Float)); writes.Add(writer => writer.Write(3f)); }
        return Read(Root(Field("m_Rect", AssetValueType.None, fields.ToArray())), writer =>
        {
            foreach (Action<BinaryWriter> write in writes) write(writer);
        });
    }

    [Fact]
    public void SourceReadersRequireEveryGeometryPathInsteadOfDefaultingZero()
    {
        string[] paths =
        [
            "textureRect/x", "textureRect/y", "textureRect/width", "textureRect/height",
            "textureRectOffset/x", "textureRectOffset/y", "downscaleMultiplier",
        ];
        UnityProjection complete = SourceProjection(settings: "raw");
        Assert.True(Present(ReadFacts(complete, false)));
        Assert.True(Present(ReadFacts(complete, true)));
        foreach (string path in paths)
        {
            UnityProjection missing = SourceProjection(omit: path, settings: "raw");
            Assert.False(Present(ReadFacts(missing, false)));
            Assert.False(Present(ReadFacts(missing, true)));
        }
        UnityProjection noSettings = SourceProjection(settings: "missingAll");
        Assert.False(Present(ReadFacts(noSettings, false)));
        Assert.False(Present(ReadFacts(noSettings, true)));
    }

    [Fact]
    public void SourceAndLocalSettingsUseRawBitsOrKnownPackedModeRotationFallback()
    {
        foreach (bool local in new[] { false, true })
        {
            Assert.Equal(0xDEADBEEFu, Raw(ReadFacts(SourceProjection(settings: "raw", raw: 0xDEADBEEFu), local)));
            Assert.Equal(19u, Raw(ReadFacts(SourceProjection(settings: "fallback", mode: 1, rotation: 4, packed: true), local)));
            Assert.Equal(18u, Raw(ReadFacts(SourceProjection(settings: "fallback", mode: 1, rotation: 4, packed: false), local)));
            Assert.Equal(18u, Raw(ReadFacts(SourceProjection(settings: "missingPacked", mode: 1, rotation: 4), local)));
            Assert.False(Present(ReadFacts(SourceProjection(settings: "missingMode", mode: 1, rotation: 4, packed: true), local)));
            Assert.False(Present(ReadFacts(SourceProjection(settings: "missingRotation", mode: 1, rotation: 4, packed: true), local)));
        }
    }

    [Fact]
    public void CanvasReaderRequiresBothDeclaredDimensions()
    {
        object valid = ReadCanvasFacts(CanvasProjection(true, true));
        Assert.True(Present(valid));
        Assert.Equal(B(2f), (uint)valid.GetType().GetProperty("RectWidth")!.GetValue(valid)!);
        Assert.Equal(B(3f), (uint)valid.GetType().GetProperty("RectHeight")!.GetValue(valid)!);
        Assert.False(Present(ReadCanvasFacts(CanvasProjection(false, true))));
        Assert.False(Present(ReadCanvasFacts(CanvasProjection(true, false))));
    }

    [Fact]
    public void FullTextureNeedsIdentityRotationExactTextureAndExactCanvas()
    {
        AssetCollection collection = Collection();
        object full = SpriteFacts(Source(), CanvasFacts(), collection);
        Assert.True(IsFullTexture(full, 2, 2));
        Assert.False(IsFullTexture(full, 0, 2));
        Assert.False(IsFullTexture(SpriteFacts(Source(settings: 6u), CanvasFacts(), collection), 2, 2));
        Assert.False(IsFullTexture(SpriteFacts(Source(width: 1f), CanvasFacts(), collection), 2, 2));
        Assert.False(IsFullTexture(SpriteFacts(Source(height: 1f), CanvasFacts(), collection), 2, 2));
        Assert.False(IsFullTexture(SpriteFacts(Source(), CanvasFacts(width: 3f), collection), 2, 2));
        Assert.False(IsFullTexture(SpriteFacts(Source(), CanvasFacts(height: 3f), collection), 2, 2));
    }

    [Fact]
    public void ExportableRectangleRefusesInvalidGeometryAndHonoursPackingRotation()
    {
        object valid = Source();
        object canvas = CanvasFacts();
        Assert.True(Exportable(valid, canvas, 2, 2));
        Assert.False(Exportable(Source(present: false), canvas, 2, 2));
        Assert.False(Exportable(valid, CanvasFacts(present: false), 2, 2));
        Assert.False(Exportable(valid, canvas, 0, 2));
        Assert.False(Exportable(valid, canvas, 2, 0));
        Assert.False(Exportable(Source(settings: 2u | 0x80u), canvas, 2, 2));
        Assert.False(Exportable(Source(settings: 0u), canvas, 2, 2));
        Assert.False(Exportable(Source(settings: 2u | (5u << 2)), canvas, 2, 2));
        Assert.False(Exportable(Source(downscale: 0.5f), canvas, 2, 2));
        Assert.False(Exportable(Source(x: 3f, width: 3f), canvas, 4, 4));
        Assert.False(Exportable(Source(width: float.NaN), canvas, 4, 4));
        Assert.False(Exportable(Source(offsetX: float.NaN), canvas, 2, 2));
        Assert.False(Exportable(Source(offsetY: -1f), canvas, 2, 2));
        Assert.False(Exportable(valid, CanvasFacts(width: 1_000_000_000f, height: 1_000_000_000f), 2, 2));
        object rotated = Source(width: 2f, height: 1f, settings: 19u);
        object narrow = CanvasFacts(width: 1f, height: 2f);
        Assert.True(Exportable(rotated, narrow, 4, 4));
        Assert.False(Exportable(Source(width: 2f, height: 1f, settings: 18u), narrow, 4, 4));
    }

    [Fact]
    public void SpriteKeyEqualityUsesSourceIdentityAndTypedGeometry()
    {
        AssetCollection first = Collection();
        AssetCollection second = Collection();
        object source = Source();
        object canvas = CanvasFacts();
        object baseline = Construct(Nested("SpriteKey"), first, 7L, source, canvas);
        object same = Construct(Nested("SpriteKey"), first, 7L, source, canvas);
        Type keyType = Nested("SpriteKey");
        MethodInfo typedEquals = keyType.GetMethod("Equals", Instance, null, new[] { keyType }, null)!;
        bool Typed(object other) => (bool)typedEquals.Invoke(baseline, new[] { other })!;
        Assert.True(Typed(same));
        Assert.True(object.Equals(baseline, same));
        Assert.Equal((int)keyType.GetMethod("GetHashCode", Instance)!.Invoke(baseline, null)!,
            (int)keyType.GetMethod("GetHashCode", Instance)!.Invoke(same, null)!);
        Assert.False(Typed(Construct(keyType, second, 7L, source, canvas)));
        Assert.False(Typed(Construct(keyType, first, 8L, source, canvas)));
        Assert.False(Typed(Construct(keyType, first, 7L, Source(x: 1f), canvas)));
        Assert.False(Typed(Construct(keyType, first, 7L, source, CanvasFacts(width: 3f))));
        Assert.False(object.Equals(baseline, null));
        Assert.False(object.Equals(baseline, new object()));
        Assert.False(((object)baseline).Equals(new object()));
    }

    [Fact]
    public void DictionaryStoragePreservesDoubleAndFloatBitsWithoutCrossTyping()
    {
        AssetTypeTemplateField template = Root(Field("m_RD", AssetValueType.None,
            Field("textureRect", AssetValueType.None, Field("x", AssetValueType.Float), Field("y", AssetValueType.Double)),
            Field("downscaleMultiplier", AssetValueType.Double), Field("settingsRaw", AssetValueType.UInt32)));
        template.Type = "SpriteAtlas";
        UnityProjection projection = Read(template, writer =>
        {
            writer.Write(0x7FC00123u);
            writer.Write(0x000000003F800000UL);
            writer.Write(0x7FF8000000000001UL);
            writer.Write(7u);
        });
        Assert.True(projection.TryFloat32("m_RD/textureRect/x", out uint single));
        Assert.Equal(0x7FC00123u, single);
        Assert.True(projection.TryBits("m_RD/textureRect/y", out long wide));
        Assert.Equal(unchecked((long)0x000000003F800000UL), wide);
        Assert.False(projection.TryFloat32("m_RD/textureRect/y", out _));
        Assert.True(projection.TryBits("m_RD/downscaleMultiplier", out long nanBits));
        Assert.Equal(unchecked((long)0x7FF8000000000001UL), nanBits);
        Assert.False(projection.TryNumber("m_RD/downscaleMultiplier", out _));
        Assert.True(projection.TryNumber("m_RD/settingsRaw", out long settings));
        Assert.Equal(7L, settings);
        Assert.Equal(new[] { "m_RD/textureRect/x", "m_RD/textureRect/y", "m_RD/downscaleMultiplier", "m_RD/settingsRaw" }, projection.Keys);
    }

    [Fact]
    public void CatalogueRootWhitelistCapturesKnownMetadataAndSkipsOtherRoots()
    {
        string[] roots =
        [
            "m_Name", "m_Width", "m_Height", "image data", "m_StreamData", "m_Script", "m_FontData",
            "m_MovieData", "m_OriginalPath", "m_ExternalResources", "m_AudioData", "m_Type",
            "m_Resource", "m_IndexBuffer", "m_SpriteAtlas", "m_RenderDataKey", "m_Rect", "m_Offset",
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
            Field("m_RD", AssetValueType.None,
                Field("texture", AssetValueType.None, Field("m_FileID", AssetValueType.Int32), Field("m_PathID", AssetValueType.Int64)),
                Field("textureRect", AssetValueType.None, Field("x", AssetValueType.Float), Field("y", AssetValueType.Float),
                    Field("width", AssetValueType.Float), Field("height", AssetValueType.Float)),
                Field("textureRectOffset", AssetValueType.None, Field("x", AssetValueType.Float), Field("y", AssetValueType.Float)),
                Field("downscaleMultiplier", AssetValueType.Float), Field("settingsRaw", AssetValueType.UInt32),
                Field("packed", AssetValueType.Bool), Field("packingMode", AssetValueType.Int32),
                Field("packingRotation", AssetValueType.Int32), Field("other", AssetValueType.Int32)));
        UnityProjection projection = Read(template, writer =>
        {
            writer.Write(3); writer.Write(4); writer.Write(99);
            writer.Write(5); writer.Write(99);
            writer.Write(1); writer.Write(2L);
            writer.Write(0f); writer.Write(0f); writer.Write(2f); writer.Write(2f);
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(2u);
            writer.Write(true); writer.Write(1); writer.Write(0); writer.Write(99);
        });
        Assert.Equal(new[] { "m_VertexData/m_VertexCount", "m_VertexData/m_DataSize" }, projection.Keys.Where(key => key.StartsWith("m_VertexData/", StringComparison.Ordinal)));
        Assert.Equal(new[] { "m_CompressedMesh/m_Vertices/m_NumItems" }, projection.Keys.Where(key => key.StartsWith("m_CompressedMesh/", StringComparison.Ordinal)));
        Assert.Contains("m_RD/texture/m_PathID", projection.Keys);
        Assert.Contains("m_RD/textureRect/width", projection.Keys);
        Assert.Contains("m_RD/textureRectOffset/y", projection.Keys);
        Assert.Contains("m_RD/downscaleMultiplier", projection.Keys);
        Assert.Contains("m_RD/settingsRaw", projection.Keys);
        Assert.Contains("m_RD/packed", projection.Keys);
        Assert.Contains("m_RD/packingMode", projection.Keys);
        Assert.Contains("m_RD/packingRotation", projection.Keys);
        Assert.DoesNotContain("m_RD/other", projection.Keys);
        Assert.DoesNotContain(projection.Keys, key => key.EndsWith("/other", StringComparison.Ordinal));
    }

    [Fact]
    public void AtlasMapCapturesDirectFirstTextureAndSecondMetadataButNotUnknownFields()
    {
        AssetTypeTemplateField template = Root(Field("m_RenderDataMap", AssetValueType.None,
            Field("direct", AssetValueType.Int32),
            Field("Array", AssetValueType.Array, Field("size", AssetValueType.Int32),
                Field("data", AssetValueType.None,
                    Field("first", AssetValueType.None, Field("a", AssetValueType.Int32)),
                    Field("second", AssetValueType.None,
                        Field("texture", AssetValueType.None, Field("m_PathID", AssetValueType.Int64)),
                        Field("textureRect", AssetValueType.None, Field("x", AssetValueType.Float)),
                        Field("other", AssetValueType.Int32))))));
        UnityProjection projection = Read(template, writer =>
        {
            writer.Write(5); writer.Write(1); writer.Write(6); writer.Write(7L); writer.Write(1.5f); writer.Write(99);
        });
        Assert.Contains("m_RenderDataMap/direct", projection.Keys);
        Assert.Contains("m_RenderDataMap/Array/data/first/a", projection.Keys);
        Assert.Contains("m_RenderDataMap/Array/data/second/texture/m_PathID", projection.Keys);
        Assert.Contains("m_RenderDataMap/Array/data/second/textureRect/x", projection.Keys);
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
    public void MissingTexturePathIdDropsTheSpriteButKeepsTheTexture()
    {
        byte[] pixels = { 10, 20, 30, 255 };
        string selected = Path.Combine(Temp.NewDirectory(), "missing.assets");
        File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedSprite(
            "missing", pixels, 1, 1, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, texturePathId: 7));

        using UnitySession session = UnitySession.Open(selected);

        UnityCatalogEntry texture = Assert.Single(session.Entries);
        Assert.Equal(UnityEntryKind.Texture, texture.Kind);
        Assert.Equal("missing_texture", texture.Name);
        using MemoryStream png = new();
        Assert.True(session.Export(texture.Index, png));
        Assert.Equal(pixels, PngDecoder.Decode(png.ToArray()).Rgba);
    }

    [Fact]
    public void EagerTextureAndMetadataOnlySpriteShareOneLazyFactory()
    {
        byte[] pixels = { 10, 20, 30, 255 };
        byte[] firstBytes = UnityFixtureBuilder.SerializedInlineTexture("texture", pixels);
        byte[] secondBytes = UnityFixtureBuilder.SerializedSprite(
            "hero", pixels, 1, 1, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 0f,
            externalTexture: true, includeLocalTexture: false, dependencies: ["first.assets"]);

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
        SerializedAssetCollection spriteCollection = root.AddCollectionFromSerializedFile(second, factory);
        root.InitializeAllDependencyLists();

        IUnityGraph graph = factory.BuildCompactGraph(root, first,
            [textureCollection, spriteCollection], [second], roots);
        using UnitySession session = UnitySession.FromGraph(graph);

        UnityCatalogEntry texture = Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
        UnityCatalogEntry sprite = Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
        Assert.Equal("texture", texture.Name);
        Assert.Equal("hero", sprite.Name);
        using MemoryStream texturePng = new();
        Assert.True(session.Export(texture.Index, texturePng));
        Assert.Equal(pixels, PngDecoder.Decode(texturePng.ToArray()).Rgba);
        using MemoryStream spritePng = new();
        Assert.True(session.Export(sprite.Index, spritePng));
        Assert.Equal(pixels, PngDecoder.Decode(spritePng.ToArray()).Rgba);
    }
}
