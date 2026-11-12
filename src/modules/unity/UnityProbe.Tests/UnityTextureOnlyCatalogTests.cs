using System;
using System.IO;
using System.Linq;
using AssetRipper.Assets.Bundles;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.Streams.Smart;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Sprite records describe views of texture pixels, but only selected Texture2D objects belong in
/// the export catalogue. A full-image sprite must never replace or hide its source texture.
/// </summary>
public sealed class UnityTextureOnlyCatalogTests : IDisposable
{
	private static readonly byte[] Pixels =
	[
		10, 20, 30, 255, 40, 50, 60, 255,
		70, 80, 90, 255, 100, 110, 120, 255,
	];
	private static readonly byte[] TopDownPixels =
	[
		70, 80, 90, 255, 100, 110, 120, 255,
		10, 20, 30, 255, 40, 50, 60, 255,
	];
	private readonly string directory = Temp.NewDirectory();

	public void Dispose() => Directory.Delete(directory, recursive: true);

	private string Write(string name, byte[] bytes)
	{
		string path = Path.Combine(directory, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	private static UnitySession Open(string path, bool compact)
		=> UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null, compact: compact));

	private static void AssertTexture(UnitySession session, UnityCatalogEntry entry, int collection,
		string name, int width, int height, byte[] expected)
	{
		Assert.Equal(UnityEntryKind.Texture, entry.Kind);
		Assert.Equal(collection, entry.CollectionIndex);
		Assert.Equal(1L, entry.PathId);
		Assert.Equal(name, entry.Name);
		Assert.Equal("png", entry.OutputExtension);
		Assert.Equal(expected.LongLength, entry.SourceSize);
		using MemoryStream destination = new();
		Assert.True(session.Export(entry.Index, destination));
		(int actualWidth, int actualHeight, byte[] rgba) = PngDecoder.Decode(destination.ToArray());
		Assert.Equal(width, actualWidth);
		Assert.Equal(height, actualHeight);
		Assert.Equal(expected, rgba);
	}

	private static byte[] TextureWithSprite(bool fullImage)
	{
		int spriteSize = fullImage ? 2 : 1;
		return UnityFixtureBuilder.SerializedSprite("hero", Pixels, 2, 2,
			0, 0, spriteSize, spriteSize, 0, 0, spriteSize, spriteSize, 0, 0, settingsRaw: 2u);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void FullImageAndCroppedSpritesLeaveOnlyTheCompleteSelectedTexture(bool compact, bool fullImage)
	{
		string path = Write("selected.assets", TextureWithSprite(fullImage));
		using UnitySession session = Open(path, compact);

		UnityCatalogEntry texture = Assert.Single(session.Entries);
		Assert.Equal(0, texture.Index);
		AssertTexture(session, texture, 0, "hero_texture", 2, 2, TopDownPixels);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AMalformedSpriteCannotHideItsValidTexture(bool compact)
	{
		string path = Write("malformed-sprite.assets",
			UnityFixtureBuilder.SerializedRepeatedSprites(2, malformedLast: true));
		using UnitySession session = Open(path, compact);

		UnityCatalogEntry texture = Assert.Single(session.Entries);
		AssertTexture(session, texture, 0, "hero_texture", 1, 1, [10, 20, 30, 255]);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void SelectingASpriteFileKeepsItsTextureWithoutAddingDependencyTextureRows(bool compact)
	{
		(byte[] selected, byte[] atlas) = UnityFixtureBuilder.SerializedAtlasSprite("hero",
			Pixels, 2, 2, [1, 2, 3, 255], 1, 1, 0, 0, 1, 1, 0, 0, 2u, 7, 7);
		string path = Write("selected.assets", selected);
		Write(UnityFixtureBuilder.AtlasDependencyName, atlas);
		using UnitySession session = Open(path, compact);

		UnityCatalogEntry texture = Assert.Single(session.Entries);
		AssertTexture(session, texture, 0, "local_texture", 2, 2, TopDownPixels);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ASelectedBundleRetainsBothLocalAndAtlasTexturesWithoutSpriteRows(bool compact)
	{
		byte[] atlasPixels = [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];
		(byte[] selected, byte[] atlas) = UnityFixtureBuilder.SerializedAtlasSprite("hero",
			Pixels, 2, 2, atlasPixels, 4, 1, 0, 0, 4, 1, 0, 0, 2u, 7, 7);
		string path = Write("selected.unity3d", UnityFixtureBuilder.CompressedBundle(
			("selected.assets", selected), (UnityFixtureBuilder.AtlasDependencyName, atlas)));
		using UnitySession session = Open(path, compact);

		Assert.Equal(2, session.Entries.Count);
		Assert.Equal(new[] { 0, 1 }, session.Entries.Select(entry => entry.Index));
		AssertTexture(session, session.Entries[0], 0, "local_texture", 2, 2, TopDownPixels);
		AssertTexture(session, session.Entries[1], 1, "atlas_texture", 4, 1, atlasPixels);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void EagerWrappingAlsoLeavesOnlyTheCompleteTexture(bool fullImage)
	{
		using SmartStream input = SmartStream.CreateMemory(TextureWithSprite(fullImage));
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "selected.assets", "selected.assets");
		using GameBundle root = new();
		root.AddCollectionFromSerializedFile(file, new GameAssetFactory(new BaseManager(static _ => { })));
		root.InitializeAllDependencyLists();
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(
			new FakeCollection("selected", UnitySource.Wrap(root.FetchAssets()).ToArray())));

		UnityCatalogEntry texture = Assert.Single(session.Entries);
		AssertTexture(session, texture, 0, "hero_texture", 2, 2, TopDownPixels);
	}
}
