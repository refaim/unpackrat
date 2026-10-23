using System;
using System.IO;
using System.Linq;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.SpriteRenderData;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Structural duplicate suppression for visible Unity resources. A visible Sprite whose rendered
/// image is exactly its visible source texture is the canonical entry, and equivalent Sprites that
/// resolve the same actual source texture with the same effective render parameters collapse.
/// These fixtures use real serialized sprites and compare the surviving export pixels, never a
/// pixel hash at open.
/// </summary>
public sealed class UnitySpriteDedupTests
{
	private static byte[] Color(int index) =>
		[(byte)(1 + (index * 7)), (byte)(100 + (index * 5)), (byte)(250 - (index * 3)), 255];

	private static byte[] RawPixels(int width, int height)
	{
		byte[] data = new byte[width * height * 4];
		for (int index = 0; index < width * height; index++)
		{
			Color(index).CopyTo(data, index * 4);
		}

		return data;
	}

	/// <summary>The top-down pixels the pinned reader and PNG writer produce for a raw fixture.</summary>
	private static byte[] Flipped(byte[] raw, int width, int height)
	{
		byte[] result = new byte[raw.Length];
		for (int row = 0; row < height; row++)
		{
			System.Array.Copy(raw, row * width * 4, result, ((height - 1 - row) * width) * 4, width * 4);
		}

		return result;
	}

	private static string WriteSelected(byte[] contents)
	{
		string path = Path.Combine(Temp.NewDirectory(), "sprite.assets");
		File.WriteAllBytes(path, contents);
		return path;
	}

	private static byte[] Export(UnitySession session, UnityCatalogEntry entry)
	{
		using MemoryStream stream = new();
		Assert.True(session.Export(entry.Index, stream));
		return stream.ToArray();
	}

	private sealed class CountingReader : AssetFactoryBase
	{
		public int Reads { get; private set; }

		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type,
			System.Collections.Generic.IReadOnlyList<SerializedTypeReference> references)
		{
			Reads++;
			return null;
		}
	}

	[Fact]
	public void FullTextureSpriteIsTheOnlyVisibleEntryAndExportsTheWholeImage()
	{
		byte[] raw = RawPixels(2, 2);
		string selected = WriteSelected(
			UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 2u));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Sprite, entry.Kind);
		Assert.Equal("hero", entry.Name);
		Assert.Equal(Flipped(raw, 2, 2), PngDecoder.Decode(Export(session, entry)).Rgba);
	}

	[Fact]
	public void CroppedSpriteAndItsAtlasTextureBothRemain()
	{
		byte[] raw = RawPixels(4, 4);
		string selected = WriteSelected(
			UnityFixtureBuilder.SerializedSprite("hero", raw, 4, 4, 1, 1, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 2u));

		using UnitySession session = UnitySession.Open(selected);
		Assert.Equal(2, session.Entries.Count);
		Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
		UnityCatalogEntry sprite = Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
		(int width, int height, byte[] rgba) = PngDecoder.Decode(Export(session, sprite));
		Assert.Equal(2, width);
		Assert.Equal(2, height);
		Assert.NotEqual(0, rgba.Length);
	}

	[Fact]
	public void EquivalentRepeatedSpritesCollapseToOneDeterministicEntry()
	{
		string selected = WriteSelected(UnityFixtureBuilder.SerializedRepeatedSprites(2));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Sprite, entry.Kind);
		Assert.Equal("hero", entry.Name);
		Assert.Equal(2L, entry.PathId);
		Assert.Equal(Flipped([10, 20, 30, 255], 1, 1), PngDecoder.Decode(Export(session, entry)).Rgba);
	}

	[Fact]
	public void CatalogueConstructionNeverReadsTheFullAssetOrDecodesPixels()
	{
		byte[] raw = RawPixels(2, 2);
		byte[] bytes = UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 2u);
		string selected = WriteSelected(bytes);
		CountingReader reader = new();

		using UnitySession session = UnitySession.FromGraph(
			UnitySource.OpenGraph(selected, readFile: null, fullReader: reader, compact: true));
		Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
		Assert.Equal(0, reader.Reads);
	}

	[Fact]
	public void SelectedCollectionsResolveEverySharedSourceBeforeOneGlobalSuppressionPass()
	{
		byte[] raw = RawPixels(2, 2);
		byte[] full = UnityFixtureBuilder.SerializedSprite("full", raw, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0);
		byte[] crop = UnityFixtureBuilder.SerializedSprite("crop", raw, 2, 2, 1, 1, 1, 1, 0, 0, 1, 1,
			0, 0, externalTexture: true, includeLocalTexture: false, dependencies: ["first.assets"]);
		byte[] duplicate = UnityFixtureBuilder.SerializedSprite("full", raw, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2,
			0, 0, externalTexture: true, includeLocalTexture: false, dependencies: ["first.assets"]);
		string path = Path.Combine(Temp.NewDirectory(), "selected.unity3d");
		File.WriteAllBytes(path, UnityFixtureBuilder.CompressedBundle(
			("first.assets", full), ("second.assets", crop), ("third.assets", duplicate)));

		using UnitySession session = UnitySession.Open(path);
		Assert.DoesNotContain(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
		Assert.Equal(new[] { "full", "crop" }, session.Entries.Select(entry => entry.Name));
		Assert.Equal(new[] { 0, 1 }, session.Entries.Select(entry => entry.CollectionIndex));
		Assert.Equal(Flipped(raw, 2, 2), PngDecoder.Decode(Export(session, session.Entries[0])).Rgba);
		(int width, int height, byte[] rgba) = PngDecoder.Decode(Export(session, session.Entries[1]));
		Assert.Equal(1, width);
		Assert.Equal(1, height);
		Assert.Equal(Color(3), rgba);
	}

	[Fact]
	public void DependencyOnlyCatalogueCannotHideTheTextureACroppedSelectedSpriteStillNeeds()
	{
		byte[] raw = RawPixels(2, 2);
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "sprite.assets");
		string dependency = Path.Combine(directory, "texture.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedSprite("selected-crop", raw, 2, 2,
			1, 1, 1, 1, 0, 0, 1, 1, 0, 0, externalTexture: true, includeLocalTexture: false,
			dependencies: ["texture.assets"]));
		File.WriteAllBytes(dependency, UnityFixtureBuilder.SerializedSprite("dependency-full", raw,
			2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry sprite = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Sprite, sprite.Kind);
		Assert.Equal("selected-crop", sprite.Name);
		(int width, int height, byte[] rgba) = PngDecoder.Decode(Export(session, sprite));
		Assert.Equal(1, width);
		Assert.Equal(1, height);
		Assert.Equal(Color(3), rgba);
	}

	[Theory]
	[InlineData("packed-horizontal", (1u << 2) | 3u)]
	[InlineData("packed-vertical", (2u << 2) | 3u)]
	[InlineData("packed-180", (3u << 2) | 3u)]
	[InlineData("packed-90", (4u << 2) | 3u)]
	[InlineData("unpacked-rotation-ignored", (1u << 2) | 2u)]
	[InlineData("unsupported-settings-bit", 2u | (1u << 9))]
	public void NonIdentityOrUnsupportedPackingKeepsTheWholeTexture(string setting, uint settingsRaw)
	{
		byte[] raw = RawPixels(2, 2);
		string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2,
			0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: settingsRaw));

		using UnitySession session = UnitySession.Open(selected);
		Assert.True(setting.Length != 0);
		Assert.Equal(2, session.Entries.Count);
		Assert.Contains(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
		Assert.Contains(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
	}

	[Fact]
	public void RawPackingBitsAgreeWithThePinnedMatureSdk()
	{
		System.Type contract = typeof(ISpriteRenderData);
		System.Type implementation = contract.Assembly.GetTypes().First(type =>
			type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters && contract.IsAssignableFrom(type));
		System.Reflection.PropertyInfo settings = contract.GetProperty("SettingsRaw")
			?? implementation.GetProperty("SettingsRaw", System.Reflection.BindingFlags.Instance
				| System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
			?? throw new InvalidOperationException("SpriteRenderData has no SettingsRaw property.");
		System.Type factsReader = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "SpriteFactsReader");
		System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public
			| System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
		System.Reflection.MethodInfo knownRectangle = factsReader.GetMethod("IsKnownRectangleSettings", flags)!;
		System.Reflection.MethodInfo identityRotation = factsReader.GetMethod("IsIdentityRotation", flags)!;
		foreach (uint raw in new uint[] { 2u, 3u, 6u, 7u, 10u, 11u, 14u, 15u, 18u, 19u, 66u, 67u, 130u, 258u, 514u })
		{
			ISpriteRenderData render = (ISpriteRenderData)System.Runtime.CompilerServices.RuntimeHelpers
				.GetUninitializedObject(implementation);
			settings.SetValue(render, raw);
			bool expectedKnown = (raw & ~0x7Fu) == 0
				&& render.PackingMode == SpritePackingMode.Rectangle
				&& (int)render.PackingRotation <= (int)SpritePackingRotation.Rotate90;
			Assert.Equal(expectedKnown, (bool)knownRectangle.Invoke(null, [raw])!);
			Assert.Equal(render.PackingRotation == SpritePackingRotation.None,
				(bool)identityRotation.Invoke(null, [raw])!);
		}
	}

	[Theory]
	[InlineData("source-x-nan", 0)]
	[InlineData("source-width-infinity", 1)]
	[InlineData("source-offset-nan", 2)]
	[InlineData("canvas-width-negative", 3)]
	[InlineData("downscale-fractional", 4)]
	[InlineData("downscale-negative", 5)]
	[InlineData("downscale-nan", 6)]
	public void InvalidGeometryCannotCanonicaliseOrHideAValidTexture(string invalidCase, int kind)
	{
		byte[] raw = RawPixels(2, 2);
		float x = 0, y = 0, width = 2, height = 2, offsetX = 0, offsetY = 0, canvasWidth = 2, downscale = 0;
		switch (kind)
		{
			case 0: x = float.NaN; break;
			case 1: width = float.PositiveInfinity; break;
			case 2: offsetX = float.NaN; break;
			case 3: canvasWidth = -1; break;
			case 4: downscale = 0.5f; break;
			case 5: downscale = -1; break;
			case 6: downscale = float.NaN; break;
			default: throw new ArgumentOutOfRangeException(nameof(kind));
		}
		string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2,
			x, y, width, height, 0, 0, canvasWidth, 2, offsetX, offsetY, downscale: downscale));

		using UnitySession session = UnitySession.Open(selected);
		Assert.True(invalidCase.Length != 0);
		Assert.Equal(2, session.Entries.Count);
		Assert.Contains(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
		Assert.Contains(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
	}

	[Fact]
	public void EqualInvalidMetadataIsNotAProofThatTwoSpritesCanShareOneEntry()
	{
		string selected = WriteSelected(UnityFixtureBuilder.SerializedRepeatedSprites(2,
			settingsRaw: 2u | (1u << 9)));

		using UnitySession session = UnitySession.Open(selected);
		Assert.Equal(3, session.Entries.Count);
		Assert.Equal(2, session.Entries.Count(entry => entry.Kind == UnityEntryKind.Sprite));
		Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
	}

	[Fact]
	public void ExternalTextureReferenceKeepsCompactedOwnerTexture()
	{
		byte[] sourcePixels = { 10, 20, 30, 255 };
		byte[] compactedPixels = { 80, 90, 100, 255 };
		byte[] first = UnityFixtureBuilder.SerializedInlineTexture("source", sourcePixels);
		byte[] second = UnityFixtureBuilder.SerializedSprite(
			"hero", compactedPixels, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0,
			externalTexture: true,
			includeLocalTexture: true,
			dependencies: new[] { "first.assets" });
		string path = WriteSelected(UnityFixtureBuilder.CompressedBundle(
			("first.assets", first),
			("second.assets", second)));
		using UnitySession session = UnitySession.Open(path);

		Assert.Equal(2, session.Entries.Count);
		Assert.DoesNotContain(session.Entries, entry => entry.Name == "source");
		UnityCatalogEntry heroTexture = Assert.Single(session.Entries.Where(entry => entry.Kind == UnityEntryKind.Texture));
		UnityCatalogEntry hero = Assert.Single(session.Entries.Where(entry => entry.Kind == UnityEntryKind.Sprite));
		Assert.Equal("hero_texture", heroTexture.Name);
		Assert.Equal("hero", hero.Name);

		(int textureWidth, int textureHeight, byte[] texturePixels) = PngDecoder.Decode(Export(session, heroTexture));
		Assert.Equal(1, textureWidth);
		Assert.Equal(1, textureHeight);
		Assert.Equal(compactedPixels, texturePixels);

		(int spriteWidth, int spriteHeight, byte[] spritePixels) = PngDecoder.Decode(Export(session, hero));
		Assert.Equal(1, spriteWidth);
		Assert.Equal(1, spriteHeight);
		Assert.Equal(sourcePixels, spritePixels);
	}

	[Fact]
	public void PackedRectangleWithoutOrientationPreservesPixelIdentity()
	{
		byte[] raw = RawPixels(2, 2);
		byte[] assets = UnityFixtureBuilder.SerializedSprite(
			"packed", raw, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0,
			settingsRaw: 3u);
		string path = WriteSelected(UnityFixtureBuilder.CompressedBundle(("packed.assets", assets)));
		using UnitySession session = UnitySession.Open(path);

		UnityCatalogEntry sprite = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Sprite, sprite.Kind);
		Assert.Equal("packed", sprite.Name);
		(int width, int height, byte[] pixels) = PngDecoder.Decode(Export(session, sprite));
		Assert.Equal(2, width);
		Assert.Equal(2, height);
		Assert.Equal(Flipped(raw, 2, 2), pixels);
	}

	[Theory]
	[InlineData(66u)]
	[InlineData(67u)]
	public void RectangleModeWithMeshTypeBitSetAndZeroRotationPreservesFullTexturePixelIdentity(uint settingsRaw)
	{
		byte[] raw = RawPixels(2, 2);
		string selected = WriteSelected(UnityFixtureBuilder.SerializedSprite("hero", raw, 2, 2,
			0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: settingsRaw));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry sprite = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Sprite, sprite.Kind);
		Assert.Equal("hero", sprite.Name);
		Assert.Equal(Flipped(raw, 2, 2), PngDecoder.Decode(Export(session, sprite)).Rgba);
	}

	[Fact]
	public void IdenticalGeometryOnDistinctTextureSourcesKeepsBothCanonicalSprites()
	{
		byte[] raw = RawPixels(2, 2);
		byte[] first = UnityFixtureBuilder.SerializedSprite("first", raw, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0);
		byte[] second = UnityFixtureBuilder.SerializedSprite("second", raw, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0);
		string path = Path.Combine(Temp.NewDirectory(), "selected.unity3d");
		File.WriteAllBytes(path, UnityFixtureBuilder.CompressedBundle(
			("first.assets", first), ("second.assets", second)));

		using UnitySession session = UnitySession.Open(path);
		Assert.Equal(new[] { "first", "second" }, session.Entries.Select(entry => entry.Name));
		Assert.All(session.Entries, entry => Assert.Equal(UnityEntryKind.Sprite, entry.Kind));
	}

	[Fact]
	public void DoubleBitPatternsCannotMasqueradeAsFloatGeometry()
	{
		AssetsTools.NET.AssetTypeTemplateField Field(
			string name, AssetsTools.NET.AssetValueType kind,
			params AssetsTools.NET.AssetTypeTemplateField[] children)
			=> new()
			{
				Name = name, Type = name, ValueType = kind, HasValue = kind != AssetsTools.NET.AssetValueType.None,
				IsArray = kind is AssetsTools.NET.AssetValueType.Array or AssetsTools.NET.AssetValueType.ByteArray,
				Children = children.ToList(),
			};
		const long doubleBits = 0x000000003F800000L;
		using MemoryStream canvasInput = new();
		using (BinaryWriter writer = new(canvasInput, System.Text.Encoding.UTF8, true))
		{
			writer.Write(BitConverter.Int64BitsToDouble(doubleBits));
			writer.Write(BitConverter.Int64BitsToDouble(doubleBits));
		}
		canvasInput.Position = 0;
		AssetsTools.NET.AssetTypeTemplateField canvasTemplate = Field(
			"Base", AssetsTools.NET.AssetValueType.None,
			Field("m_Rect", AssetsTools.NET.AssetValueType.None,
				Field("width", AssetsTools.NET.AssetValueType.Double),
				Field("height", AssetsTools.NET.AssetValueType.Double)));
		UnityProjection canvas = UnityProjection.Read(canvasInput, canvasTemplate, false);

		using MemoryStream sourceInput = new();
		using (BinaryWriter writer = new(sourceInput, System.Text.Encoding.UTF8, true))
		{
			for (int index = 0; index < 7; index++)
				writer.Write(BitConverter.Int64BitsToDouble(doubleBits));
			writer.Write(2u);
		}
		sourceInput.Position = 0;
		AssetsTools.NET.AssetTypeTemplateField sourceTemplate = Field(
			"Base", AssetsTools.NET.AssetValueType.None,
			Field("m_RD", AssetsTools.NET.AssetValueType.None,
				Field("textureRect", AssetsTools.NET.AssetValueType.None,
					Field("x", AssetsTools.NET.AssetValueType.Double),
					Field("y", AssetsTools.NET.AssetValueType.Double),
					Field("width", AssetsTools.NET.AssetValueType.Double),
					Field("height", AssetsTools.NET.AssetValueType.Double)),
				Field("textureRectOffset", AssetsTools.NET.AssetValueType.None,
					Field("x", AssetsTools.NET.AssetValueType.Double),
					Field("y", AssetsTools.NET.AssetValueType.Double)),
				Field("downscaleMultiplier", AssetsTools.NET.AssetValueType.Double),
				Field("settingsRaw", AssetsTools.NET.AssetValueType.UInt32)));
		UnityProjection source = UnityProjection.Read(sourceInput, sourceTemplate, false);

		System.Type factsReader = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "SpriteFactsReader");
		System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public
			| System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
		static bool Present(object facts) =>
			(bool)facts.GetType().GetProperty("Present")!.GetValue(facts)!;
		Assert.False(Present(factsReader.GetMethod("ReadCanvas", flags)!.Invoke(null, [canvas])!));
		System.Reflection.MethodInfo sourceReader = factsReader.GetMethod("ReadLocalSource", flags)
			?? factsReader.GetMethod("ReadSource", flags)
			?? throw new InvalidOperationException("SpriteFactsReader has no local source reader.");
		object[] sourceArguments = sourceReader.GetParameters().Length == 1 ? [source] : [source, "m_RD/"];
		Assert.False(Present(sourceReader.Invoke(null, sourceArguments)!));
	}
}
