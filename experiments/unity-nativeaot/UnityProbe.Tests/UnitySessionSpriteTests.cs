using System;
using System.IO;
using System.Linq;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure;
using AssetRipper.IO.Files;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Sprite resource slice: a real serialized Sprite must surface as exactly one exportable catalogue
/// entry whose output is a standalone PNG of the sprite's own pixels, cropped from its texture,
/// uprighted for the packed orientation, restored onto its transparent canvas and trimmed to its
/// tight mesh. The fixture round-trip proof keeps the serialized sprite, its texture pointer and its
/// render-data rect honest, so a passing crop test cannot be an artifact of an invalid fixture.
/// </summary>
public sealed class UnitySessionSpriteTests
{
	private readonly ITestOutputHelper output;

	public UnitySessionSpriteTests(ITestOutputHelper output)
	{
		this.output = output;
	}

	/// <summary>
	/// A unique colour per pixel index: red rises and blue falls with the index, so a wrong crop,
	/// an accidental transpose or a stray flip cannot pass a per-pixel assertion.
	/// </summary>
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

	/// <summary>An RGBA buffer assembled from explicit pixel colour indices, row-major.</summary>
	private static byte[] Grid(int width, int height, params int[] indices)
	{
		byte[] data = new byte[width * height * 4];
		for (int index = 0; index < indices.Length; index++)
		{
			Color(indices[index]).CopyTo(data, index * 4);
		}

		return data;
	}

	/// <summary>
	/// The decoded crop content of a plain texture fixture. The upstream reader flips Y, so the raw
	/// buffer row <c>row</c> becomes the decoded top-down atlas row <c>height-1-row</c>. A texture
	/// rect is measured from the bottom-left, so an atlas rect (x, y, w, h) selects top-down columns
	/// <c>x..x+w</c> and top-down rows <c>height-y-h..height-y</c>.
	/// </summary>
	private static byte[] ExpectedCrop(int textureWidth, int textureHeight, int rectX, int rectY, int cropWidth, int cropHeight)
	{
		byte[] expected = new byte[cropWidth * cropHeight * 4];
		int topDownTop = textureHeight - rectY - cropHeight;
		for (int y = 0; y < cropHeight; y++)
		{
			int rawRow = textureHeight - 1 - (topDownTop + y);
			for (int x = 0; x < cropWidth; x++)
			{
				Color(((rawRow * textureWidth) + rectX + x)).CopyTo(expected, (((y * cropWidth) + x) * 4));
			}
		}

		return expected;
	}

	private static string WriteSelected(string fileName, byte[] contents)
	{
		string directory = Temp.NewDirectory();
		string path = Path.Combine(directory, fileName);
		File.WriteAllBytes(path, contents);
		return path;
	}

	/// <summary>Opens the sprite file and returns its single Sprite-kind catalogue entry.</summary>
	private static (UnitySession Session, UnityCatalogEntry Entry) OpenSprite(string selected)
	{
		UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);
		return (session, entry);
	}

	private static byte[] Export(UnitySession session, UnityCatalogEntry entry)
	{
		using MemoryStream exported = new();
		Assert.True(session.Export(entry.Index, exported));
		return exported.ToArray();
	}

	private static (int Width, int Height, byte[] Rgba) ExportSpriteBytes(byte[] file)
	{
		string selected = WriteSelected("sprite.assets", file);
		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);
		return PngDecoder.Decode(Export(session, entry));
	}

	private static bool TryExportSpriteBytes(byte[] file)
	{
		string selected = WriteSelected("sprite.assets", file);
		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);
		using MemoryStream stream = new();
		return session.Export(entry.Index, stream);
	}

	/// <summary>The right-triangle mesh used by the tight fixtures, in sprite-local units.</summary>
	private static readonly float[] TightTriangle = [-1.5f, -1.5f, 1.5f, -1.5f, -1.5f, 1.5f];

	private static byte[] TightFile(UnityFixtureBuilder.TightMeshOptions options) =>
		UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 0, 0, 3, 3, 0, 0, 3, 3, 0, 0, settingsRaw: 0u, pixelsToUnits: 1f, tightMesh: options);

	/// <summary>
	/// The three-by-three bounding box of the tight triangle with the lower-left half retained and
	/// the upper-right neighbours cleared, restored onto the transparent rect canvas.
	/// </summary>
	private static readonly int[] TightKeep = [0, 3, 4, 6, 7, 8];

	private static byte[] TightExpected()
	{
		byte[] crop = ExpectedCrop(4, 4, 0, 0, 3, 3);
		byte[] expected = new byte[3 * 3 * 4];
		foreach (int index in TightKeep)
		{
			System.Array.Copy(crop, index * 4, expected, index * 4, 4);
		}

		return expected;
	}

	[Fact]
	public void FixtureCarriesARealDeserializedSpriteWithItsTextureLinkAndRect()
	{
		byte[] texture = RawPixels(4, 4);
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite("hero", texture, 4, 4, 1, 1, 2, 3, 1, 1, 2, 3, 0, 0));

		CoreConfiguration configuration = new()
		{
			ImportSettings = new ImportSettings { ScriptContentLevel = ScriptContentLevel.Level0 },
		};
		using GameStructure structure = GameStructure.Load(new[] { selected }, LocalFileSystem.Instance, configuration);

		// The pipeline also loads Unity's builtin resources, which carry their own sprites; the
		// fixture's sprite is the one this file authored.
		ISprite sprite = Assert.Single(structure.FileCollection.FetchAssets().OfType<ISprite>(), s => s.Name.ToString() == "hero");

		ITexture2D? resolved = sprite.RD.Texture.TryGetAsset(sprite.Collection);
		Assert.NotNull(resolved);
		Assert.Equal(4, resolved!.Width_C28);
		Assert.Equal(4, resolved.Height_C28);
		Assert.Equal(texture, resolved.ImageData_C28);

		Assert.Equal(1f, sprite.RD.TextureRect.X);
		Assert.Equal(1f, sprite.RD.TextureRect.Y);
		Assert.Equal(2f, sprite.RD.TextureRect.Width);
		Assert.Equal(3f, sprite.RD.TextureRect.Height);
		Assert.Equal(2u, sprite.RD.SettingsRaw);
	}

	[Fact]
	public void SpriteCropExportsItsSelectedRegionAsAStandalonePng()
	{
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 1, 1, 2, 3, 1, 1, 2, 3, 0, 0));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);
		Assert.Equal("png", entry.OutputExtension);
		Assert.Equal("hero", entry.Name);
		// The sprite's own texture is dedicated, so its declared source length is honest.
		Assert.Equal(64L, entry.SourceSize);

		(int width, int height, byte[] rgba) = PngDecoder.Decode(Export(session, entry));
		output.WriteLine($"crop={width}x{height}");

		Assert.Equal(2, width);
		Assert.Equal(3, height);
		Assert.Equal(ExpectedCrop(4, 4, 1, 1, 2, 3), rgba);
	}

	[Fact]
	public void TextureEntryStillShowsTheWholeSourceTexture()
	{
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 1, 1, 2, 3, 1, 1, 2, 3, 0, 0));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry texture = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Texture);

		(int width, int height, byte[] rgba) = PngDecoder.Decode(Export(session, texture));
		Assert.Equal(4, width);
		Assert.Equal(4, height);
		// An ordinary Texture2D entry still renders the whole texture, top-down after the reader's
		// Y-flip; only the Sprite entry is cropped.
		byte[] raw = RawPixels(4, 4);
		byte[] flipped = new byte[raw.Length];
		for (int row = 0; row < 4; row++)
		{
			System.Array.Copy(raw, row * 4 * 4, flipped, ((3 - row) * 4) * 4, 4 * 4);
		}

		Assert.Equal(flipped, rgba);
	}

	[Fact]
	public void PackedRotate90UprightsTheCropClockwise()
	{
		// A packed 90-degree sprite stores its atlas region transposed. The top-down atlas crop is
		// DEF / ABC (bottom-up ABC / DEF flipped), and a clockwise turn scans it as AD / BE / CF,
		// exactly the independent reference oracle. The packed rectangle settings keep the packing
		// mode rectangular, so the rotation alone drives the geometry.
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite("hero", RawPixels(3, 2), 3, 2, 0, 0, 3, 2, 0, 0, 2, 3, 0, 0, settingsRaw: 19u));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);

		(int width, int height, byte[] rgba) = PngDecoder.Decode(Export(session, entry));
		Assert.Equal(2, width);
		Assert.Equal(3, height);
		Assert.Equal(Grid(2, 3, 0, 3, 1, 4, 2, 5), rgba);
	}

	[Fact]
	public void PackedFlipHorizontalUprightsTheCrop()
	{
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite("hero", RawPixels(3, 2), 3, 2, 0, 0, 3, 2, 0, 0, 3, 2, 0, 0, settingsRaw: 7u));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);

		Assert.Equal(Grid(3, 2, 5, 4, 3, 2, 1, 0), PngDecoder.Decode(Export(session, entry)).Rgba);
	}

	[Fact]
	public void PackedFlipVerticalUprightsTheCrop()
	{
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite("hero", RawPixels(3, 2), 3, 2, 0, 0, 3, 2, 0, 0, 3, 2, 0, 0, settingsRaw: 11u));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);

		Assert.Equal(Grid(3, 2, 0, 1, 2, 3, 4, 5), PngDecoder.Decode(Export(session, entry)).Rgba);
	}

	[Fact]
	public void PackedRotate180UprightsTheCrop()
	{
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite("hero", RawPixels(3, 2), 3, 2, 0, 0, 3, 2, 0, 0, 3, 2, 0, 0, settingsRaw: 15u));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);

		Assert.Equal(Grid(3, 2, 2, 1, 0, 5, 4, 3), PngDecoder.Decode(Export(session, entry)).Rgba);
	}

	[Fact]
	public void UnpackedSettingsIgnoreTheRotationField()
	{
		// The packed bit is clear, so the rotation field (a non-zero ninety-degree value here) must
		// be ignored entirely.
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite("hero", RawPixels(3, 2), 3, 2, 0, 0, 3, 2, 0, 0, 3, 2, 0, 0, settingsRaw: 18u));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);

		Assert.Equal(Grid(3, 2, 3, 4, 5, 0, 1, 2), PngDecoder.Decode(Export(session, entry)).Rgba);
	}

	[Theory]
	[InlineData(1f, 1f)]
	[InlineData(1f, 2f)]
	[InlineData(2f, 1f)]
	public void TrimRestoresTheTransparentOriginalCanvas(float offsetX, float offsetY)
	{
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 1, 1, 2, 2, 0, 0, 4, 4, offsetX, offsetY));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);

		(int width, int height, byte[] rgba) = PngDecoder.Decode(Export(session, entry));
		Assert.Equal(4, width);
		Assert.Equal(4, height);

		byte[] expected = new byte[4 * 4 * 4];
		byte[] crop = ExpectedCrop(4, 4, 1, 1, 2, 2);
		int placedX = (int)offsetX;
		int placedY = 4 - (int)offsetY - 2;
		for (int row = 0; row < 2; row++)
		{
			System.Array.Copy(crop, row * 2 * 4, expected, ((((placedY + row) * 4) + placedX) * 4), 2 * 4);
		}

		Assert.Equal(expected, rgba);
	}

	[Fact]
	public void TightSpriteDropsColouredNeighboursOutsideItsMesh()
	{
		// A right triangle over the lower-left half of the 3x3 bounding box. The deliberately
		// coloured pixels of the rectangular crop that fall outside the mesh must be transparent.
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite(
				"hero",
				RawPixels(4, 4),
				4,
				4,
				0,
				0,
				3,
				3,
				0,
				0,
				3,
				3,
				0,
				0,
				settingsRaw: 0u,
				pixelsToUnits: 1f,
				tightMesh: new UnityFixtureBuilder.TightMeshOptions([-1.5f, -1.5f, 1.5f, -1.5f, -1.5f, 1.5f])));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);

		(int width, int height, byte[] rgba) = PngDecoder.Decode(Export(session, entry));
		Assert.Equal(3, width);
		Assert.Equal(3, height);

		byte[] crop = ExpectedCrop(4, 4, 0, 0, 3, 3);
		bool[] keep = [true, false, false, true, true, false, true, true, true];
		byte[] expected = new byte[3 * 3 * 4];
		for (int index = 0; index < keep.Length; index++)
		{
			if (keep[index])
			{
				System.Array.Copy(crop, index * 4, expected, index * 4, 4);
			}
		}

		Assert.Equal(expected, rgba);
	}

	[Theory]
	[InlineData(0f, true)]
	[InlineData(1f, true)]
	[InlineData(2f, false)]
	[InlineData(-1f, false)]
	[InlineData(float.NegativeInfinity, false)]
	[InlineData(float.PositiveInfinity, false)]
	[InlineData(float.NaN, false)]
	public void SpriteDownscaleMultiplierMustBeUnit(float downscale, bool exports)
	{
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 1, 1, 2, 3, 1, 1, 2, 3, 0, 0, downscale: downscale));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);

		using MemoryStream exported = new();
		Assert.Equal(exports, session.Export(entry.Index, exported));
	}

	[Fact]
	public void SpriteWithoutAResolvableTextureIsNotCatalogued()
	{
		string selected = WriteSelected("sprite.assets",
			UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 1, 1, 2, 3, 1, 1, 2, 3, 0, 0, linksTexture: false));

		using UnitySession session = UnitySession.Open(selected);
		Assert.DoesNotContain(session.Entries, e => e.Kind == UnityEntryKind.Sprite);
	}

	[Fact]
	public void AtlasSpriteResolvesItsKeyedDependencyRegionAndExcludesDependencyEntries()
	{
		(byte[] spriteFile, byte[] atlasFile) = UnityFixtureBuilder.SerializedAtlasSprite(
			"hero", RawPixels(2, 2), 2, 2, RawPixels(4, 4), 4, 4, 1, 1, 2, 3, 0, 0, 2u, spriteKeyValue: 7, atlasKeyValue: 7);

		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "sprite.assets");
		File.WriteAllBytes(selected, spriteFile);
		File.WriteAllBytes(Path.Combine(directory, UnityFixtureBuilder.AtlasDependencyName), atlasFile);

		using UnitySession session = UnitySession.Open(selected);

		// Only the selected file's own local texture and the sprite are catalogued; the atlas and
		// its texture live in the dependency file and must stay out.
		Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Texture);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);
		// An atlas texture is shared, so the sprite's own contribution is unknown.
		Assert.Equal(0L, entry.SourceSize);

		(int width, int height, byte[] rgba) = PngDecoder.Decode(Export(session, entry));
		Assert.Equal(2, width);
		Assert.Equal(3, height);
		// The keyed atlas record wins over the deliberately different local render data.
		Assert.Equal(ExpectedCrop(4, 4, 1, 1, 2, 3), rgba);
	}

	[Fact]
	public void AtlasSpriteWithAMissingKeyIsNotCatalogued()
	{
		(byte[] spriteFile, byte[] atlasFile) = UnityFixtureBuilder.SerializedAtlasSprite(
			"hero", RawPixels(2, 2), 2, 2, RawPixels(4, 4), 4, 4, 1, 1, 2, 3, 0, 0, 2u, spriteKeyValue: 7, atlasKeyValue: 8);

		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "sprite.assets");
		File.WriteAllBytes(selected, spriteFile);
		File.WriteAllBytes(Path.Combine(directory, UnityFixtureBuilder.AtlasDependencyName), atlasFile);

		using UnitySession session = UnitySession.Open(selected);
		Assert.DoesNotContain(session.Entries, e => e.Kind == UnityEntryKind.Sprite);
	}

	[Fact]
	public void AtlasSpriteWithAMissingDependencyIsNotCatalogued()
	{
		(byte[] spriteFile, byte[] _) = UnityFixtureBuilder.SerializedAtlasSprite(
			"hero", RawPixels(2, 2), 2, 2, RawPixels(4, 4), 4, 4, 1, 1, 2, 3, 0, 0, 2u, spriteKeyValue: 7, atlasKeyValue: 7);

		// The declared atlas dependency is deliberately not written beside the sprite file.
		string selected = WriteSelected("sprite.assets", spriteFile);

		using UnitySession session = UnitySession.Open(selected);
		Assert.DoesNotContain(session.Entries, e => e.Kind == UnityEntryKind.Sprite);
	}

	[Fact]
	public void LegacyTightSpriteDropsColouredNeighboursOutsideItsMesh()
	{
		// A 4.3 sprite carries its mesh as per-vertex positions and an index list, and serializes no
		// pivot; the mesh is centred on the rect with the default pivot.
		byte[] file = UnityFixtureBuilder.SerializedLegacySprite(
			"hero", RawPixels(4, 4), 4, 4, 0, 0, 3, 3, 0, 0, 3, 3, 0, 0, 0u, 1f, TightTriangle);

		(int width, int height, byte[] rgba) = ExportSpriteBytes(file);
		Assert.Equal(3, width);
		Assert.Equal(3, height);
		Assert.Equal(TightExpected(), rgba);
	}

	[Fact]
	public void LegacyFixtureCarriesItsDeserializedOffsetAndNoSerializedPivot()
	{
		// The validity check for the legacy-pivot defect: the 4.3 fixture really serializes no pivot
		// and a nonzero sprite offset, so a later crop test cannot pass on an invalid fixture.
		byte[] file = UnityFixtureBuilder.SerializedLegacySprite(
			"hero", RawPixels(4, 4), 4, 4, 0, 0, 4, 4, 0, 0, 4, 4, 0, 0, 0u, 1f,
			legacyVertices: [-3f, -3f, 1f, -3f, -3f, 1f], spriteOffsetX: 1f, spriteOffsetY: 1f);

		string selected = WriteSelected("sprite.assets", file);
		CoreConfiguration configuration = new()
		{
			ImportSettings = new ImportSettings { ScriptContentLevel = ScriptContentLevel.Level0 },
		};
		using GameStructure structure = GameStructure.Load(new[] { selected }, LocalFileSystem.Instance, configuration);

		ISprite sprite = Assert.Single(structure.FileCollection.FetchAssets().OfType<ISprite>(), s => s.Name.ToString() == "hero");
		Assert.False(sprite.Has_Pivot());
		Assert.Equal(1f, sprite.Offset.X);
		Assert.Equal(1f, sprite.Offset.Y);
	}

	[Fact]
	public void LegacySpriteWithoutASerializedPivotDerivesItsPivotFromTheRectAndOffset()
	{
		// The pinned SpriteExtensions contract for a sprite with no serialized pivot is
		// pivot = (Rect.Size / 2 + Offset) / Rect.Size. With a 4x4 rect and offset (1,1) that is
		// (0.75, 0.75), so the mesh's three local vertices (-3,-3), (1,-3), (-3,1) land on the canvas
		// at pixels (0,0), (4,0), (0,4). The pixel-centre mask keeps that lower-left triangle, so
		// from the bottom canvas row upward the retained pixel counts are four, three, two and one.
		byte[] file = UnityFixtureBuilder.SerializedLegacySprite(
			"hero", RawPixels(4, 4), 4, 4, 0, 0, 4, 4, 0, 0, 4, 4, 0, 0, 0u, 1f,
			legacyVertices: [-3f, -3f, 1f, -3f, -3f, 1f], spriteOffsetX: 1f, spriteOffsetY: 1f);

		(int width, int height, byte[] rgba) = ExportSpriteBytes(file);
		Assert.Equal(4, width);
		Assert.Equal(4, height);

		byte[] crop = ExpectedCrop(4, 4, 0, 0, 4, 4);
		bool[] keep =
		[
			true, false, false, false,
			true, true, false, false,
			true, true, true, false,
			true, true, true, true,
		];
		byte[] expected = new byte[4 * 4 * 4];
		for (int index = 0; index < keep.Length; index++)
		{
			if (keep[index])
			{
				System.Array.Copy(crop, index * 4, expected, index * 4, 4);
			}
		}

		Assert.Equal(expected, rgba);
	}

	[Fact]
	public void TightSpriteWithoutAMeshFailsExport()
	{
		byte[] file = UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 0, 0, 3, 3, 0, 0, 3, 3, 0, 0, settingsRaw: 0u, pixelsToUnits: 1f);
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithoutASubMeshMasksTheWholeIndexBuffer()
	{
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { IncludeSubMesh = false });
		Assert.Equal(TightExpected(), ExportSpriteBytes(file).Rgba);
	}

	[Fact]
	public void TightSpriteWithAnEmptyIndexBufferFailsExport()
	{
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { IncludeIndexBuffer = false });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithAnOddIndexBufferFailsExport()
	{
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { OddIndexBuffer = true });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithANonTriangleTopologyFailsExport()
	{
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { Topology = MeshTopology.TriangleStrip });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithAnIncompleteTriangleFailsExport()
	{
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { IndexCount = 2u });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithAnOutOfRangeIndexFailsExport()
	{
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { Indices = [0, 1, 9] });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithASubMeshRangeBeyondTheIndexBufferFailsExport()
	{
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { FirstByte = 0xFFFFFFF0u });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithANonFiniteVertexFailsExport()
	{
		float[] points = [float.NaN, -1.5f, 1.5f, -1.5f, -1.5f, 1.5f];
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(points));
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithAnImplausiblyLargeDeclaredVertexCountFailsExport()
	{
		// Two declared points in a twenty-four byte body, but the metadata claims a vertex count past
		// the explicit practical limit: the preflight refuses before the vertex decoder is asked to
		// materialise a buffer for it, so no oversized allocation is attempted.
		byte[] file = TightFile(
			new UnityFixtureBuilder.TightMeshOptions([-1.5f, -1.5f, 0f, 0f]) { DeclaredVertexCount = SpriteGeometry.MaxMeshVertexCount + 1u });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWhoseDeclaredVertexCountExceedsItsBodyFailsExport()
	{
		// The declared count is modest but larger than the written body: the declared source range
		// does not fit, so the preflight refuses before the decoder reads past the end.
		byte[] file = TightFile(
			new UnityFixtureBuilder.TightMeshOptions([-1.5f, -1.5f, 0f, 0f]) { DeclaredVertexCount = 100u });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithAnImplausiblyLargeDeclaredIndexCountFailsExport()
	{
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { IndexCount = 5_000_000u });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithAnOversizedBackingIndexBufferFailsExport()
	{
		// The selected submesh declares only three indices, but the backing index buffer that the
		// decoder copies and iterates in full is one entry past the explicit practical cap. The
		// selected count keeps the triangle work tiny, so only the whole-buffer bound can refuse
		// this descriptor before the oversized copy is materialised.
		ushort[] indices = new ushort[SpriteGeometry.MaxMeshIndexCount + 1];
		indices[0] = 0;
		indices[1] = 1;
		indices[2] = 2;
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { Indices = indices, IndexCount = 3u });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWhoseChannelOffsetExceedsTheBodyFailsExport()
	{
		// Three float3 vertices in a thirty-six byte body with a twelve byte stride: the raw
		// stream metadata looks consistent, but the channel's own offset of 255 places its first
		// component far past the end of the body, so the decoder must be refused from metadata
		// rather than allowed to copy out of range.
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { ChannelOffset = 255 });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithAnInvalidChannelFormatFailsExport()
	{
		// A channel format outside every pinned vertex format cannot be widened to a byte size, so
		// the descriptor is refused instead of surfacing the format helper's argument error.
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { ChannelFormat = 200 });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithAnUnsupportedChannelDimensionFailsExport()
	{
		// Dimension five is wider than any Vector2/3/4 the decoder can expand into, so the
		// descriptor is refused rather than allowed to fail mid-decode.
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { ChannelDimension = 5 });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWhoseChannelSelectsTooManyStreamsFailsExport()
	{
		// A single channel with stream index two hundred would materialise two hundred and one
		// streams; the derived stream count is bounded, so the descriptor is refused.
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { ChannelStream = 200 });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithTooManyChannelsFailsExport()
	{
		// Sixty-five channels are past the practical channel limit; the raw list is refused before
		// the channel-to-stream conversion walks it.
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { DuplicateChannelCount = 64 });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithANonDecodedChannelFailsExport()
	{
		// A zero-dimension channel is never decoded, so no vertex array can be materialised and the
		// preflight refuses rather than asking the decoder for the positions it cannot produce.
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { ChannelDimension = 0 });
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void TightSpriteWithAnUnusedChannelStillExports()
	{
		// A second, zero-dimension channel is skipped, so it neither contributes bytes nor changes
		// the exported mask.
		byte[] file = TightFile(new UnityFixtureBuilder.TightMeshOptions(TightTriangle) { AddUnusedChannel = true });
		Assert.Equal(TightExpected(), ExportSpriteBytes(file).Rgba);
	}

	[Fact]
	public void LegacyTightSpriteWithVerticesButNoIndicesFailsExport()
	{
		// The older shapes can carry vertices without any index list, so no triangle can be formed
		// and no valid mask exists; a tight sprite is refused rather than exported as its
		// unpixel-accurate rectangular bounding box.
		byte[] file = UnityFixtureBuilder.SerializedLegacySprite(
			"hero", RawPixels(4, 4), 4, 4, 0, 0, 3, 3, 0, 0, 3, 3, 0, 0, 0u, 1f, TightTriangle, withIndices: false);

		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void SpriteWithAnAbsurdTextureIsCataloguedButExportsNothing()
	{
		// 100000x100000 RGBA with only four bytes: the dimension guard refuses the decode.
		byte[] file = UnityFixtureBuilder.SerializedSprite("hero", [0, 0, 0, 0], 100000, 100000, 0, 0, 4, 4, 0, 0, 4, 4, 0, 0);
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void SpriteWithAnOutOfBoundsTextureRectFailsExport()
	{
		byte[] file = UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 0, 0, 5, 5, 0, 0, 5, 5, 0, 0);
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void SpriteWithAnUnknownPackedRotationFailsExport()
	{
		// Packed with the reserved "Any" rotation value: the orientation cannot be undone.
		byte[] file = UnityFixtureBuilder.SerializedSprite("hero", RawPixels(3, 2), 3, 2, 0, 0, 3, 2, 0, 0, 3, 2, 0, 0, settingsRaw: 61u);
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void SpriteWithAZeroSizedRectFailsExport()
	{
		byte[] file = UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 1, 1, 2, 2, 0, 0, 0, 0, 0, 0);
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void SpriteWithANonFiniteOffsetFailsExport()
	{
		byte[] file = UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 1, 1, 2, 2, 0, 0, 3, 3, float.NaN, 0f);
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void SpriteWithAnOffsetThatWouldNotFitFailsExport()
	{
		byte[] file = UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 0, 0, 2, 3, 0, 0, 3, 3, 2f, 0f);
		Assert.False(TryExportSpriteBytes(file));
	}

	[Fact]
	public void SpriteWithAProvablyEmptyTextureIsNotCatalogued()
	{
		byte[] file = UnityFixtureBuilder.SerializedSprite("hero", [], 0, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0);
		string selected = WriteSelected("sprite.assets", file);
		using UnitySession session = UnitySession.Open(selected);
		Assert.DoesNotContain(session.Entries, e => e.Kind == UnityEntryKind.Sprite);
	}

	[Fact]
	public void AtlasSpriteWithAnUnresolvableAtlasTextureIsNotCatalogued()
	{
		(byte[] spriteFile, byte[] atlasFile) = UnityFixtureBuilder.SerializedAtlasSprite(
			"hero", RawPixels(2, 2), 2, 2, RawPixels(4, 4), 4, 4, 1, 1, 2, 3, 0, 0, 2u, 7, 7, linkAtlasTexture: false);

		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "sprite.assets");
		File.WriteAllBytes(selected, spriteFile);
		File.WriteAllBytes(Path.Combine(directory, UnityFixtureBuilder.AtlasDependencyName), atlasFile);

		using UnitySession session = UnitySession.Open(selected);
		Assert.DoesNotContain(session.Entries, e => e.Kind == UnityEntryKind.Sprite);
	}

	[Theory]
	[InlineData(SpritePackingMode.Tight, SpriteGeometry.ModeTight)]
	[InlineData(SpritePackingMode.Rectangle, SpriteGeometry.ModeRectangle)]
	public void PackingModeConstantsMatchThePinnedEnum(SpritePackingMode pinned, int expected)
		=> Assert.Equal(expected, (int)pinned);

	[Theory]
	[InlineData(SpritePackingRotation.None, SpriteGeometry.RotationNone)]
	[InlineData(SpritePackingRotation.FlipHorizontal, SpriteGeometry.RotationFlipHorizontal)]
	[InlineData(SpritePackingRotation.FlipVertical, SpriteGeometry.RotationFlipVertical)]
	[InlineData(SpritePackingRotation.Rotate180, SpriteGeometry.RotationRotate180)]
	[InlineData(SpritePackingRotation.Rotate90, SpriteGeometry.RotationRotate90)]
	public void PackingRotationConstantsMatchThePinnedEnum(SpritePackingRotation pinned, int expected)
		=> Assert.Equal(expected, (int)pinned);
}
