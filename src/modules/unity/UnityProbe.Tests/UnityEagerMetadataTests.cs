using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.Streams.Smart;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_0;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

// The command-line exporter deliberately retains its eager adapter. Its catalogue metadata must
// agree with the lazy Observer catalogue and still handle empty/unresolvable resource shapes.
public sealed class UnityEagerMetadataTests
{
	private static readonly byte[] Pixels = [10, 20, 30, 255];
	private static void Eager(byte[] bytes, Action<IUnityObjectBase[]> check, byte[]? dependency = null)
	{
		using SmartStream input = SmartStream.CreateMemory(bytes);
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "selected.assets", "selected.assets");
		using GameBundle root = new();
		GameAssetFactory factory = new(new BaseManager(static _ => { }));
		root.AddCollectionFromSerializedFile(file, factory);
		if (dependency is not null)
		{
			using SmartStream side = SmartStream.CreateMemory(dependency);
			using SerializedFile sideFile = (SerializedFile)SchemeReader.ReadFile(side, UnityFixtureBuilder.AtlasDependencyName, UnityFixtureBuilder.AtlasDependencyName);
			root.AddCollectionFromSerializedFile(sideFile, factory);
		}
		root.InitializeAllDependencyLists();
		check(root.FetchAssets().ToArray());
	}

	[Theory]
	[InlineData("text", "bin", 7)]
	[InlineData("font", "otf", 4)]
	[InlineData("fontcollection", "ttc", 4)]
	[InlineData("fontshort", "ttf", 1)]
	[InlineData("video", "mp4", 4)]
	[InlineData("movie", "ogv", 4)]
	[InlineData("audio", "wav", 4)]
	[InlineData("mesh", "glb", 3)]
	public void EagerMetadataRemainsUsableForTheCommandLineExporter(string kind, string extension, long size)
	{
		byte[] data = kind switch
		{
			"text" => UnityFixtureBuilder.SerializedTextAsset("resource", "payload"),
			"font" => UnityFixtureBuilder.SerializedFont("resource", "OTTO"u8.ToArray()),
			"fontcollection" => UnityFixtureBuilder.SerializedFont("resource", "ttcf"u8.ToArray()),
			"fontshort" => UnityFixtureBuilder.SerializedFont("resource", [1]),
			"video" => UnityFixtureBuilder.SerializedVideoClip("resource", "clip.mp4", "media", 4),
			"movie" => UnityFixtureBuilder.SerializedMovieTexture("resource", Pixels),
			"audio" => UnityFixtureBuilder.SerializedLegacyAudioClip("resource", Pixels),
			_ => UnityFixtureBuilder.SerializedTriangleMesh("resource"),
		};
		Eager(data, assets =>
		{
			foreach (IUnityObjectBase asset in assets) asset.OriginalPath = "Assets/Resource";
			using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("eager", UnitySource.Wrap(assets).ToArray())));
			UnityCatalogEntry entry = Assert.Single(session.Entries);
			Assert.Equal("resource", entry.Name); Assert.Equal("Assets/Resource", entry.OriginalPath); Assert.Equal(extension, entry.OutputExtension);
			Assert.True(entry.PathId > 0); Assert.True(entry.SourceSize >= 0);
			if (kind != "mesh") Assert.Equal(size, entry.SourceSize);
		});
	}

	[Theory]
	[InlineData("font")]
	[InlineData("video")]
	[InlineData("movie")]
	[InlineData("audio")]
	[InlineData("mesh")]
	public void EmptyEagerResourcesAreNotExportable(string kind)
	{
		byte[] bytes = kind switch
		{
			"font" => UnityFixtureBuilder.SerializedFont("empty", []),
			"video" => UnityFixtureBuilder.SerializedVideoClip("empty", "clip.mp4", "", 0),
			"movie" => UnityFixtureBuilder.SerializedMovieTexture("empty", []),
			"audio" => UnityFixtureBuilder.SerializedLegacyAudioClip("empty", []),
			_ => UnityFixtureBuilder.SerializedEmptyMesh("empty"),
		};
		Eager(bytes, assets => Assert.All(UnitySource.Wrap(assets), a => Assert.False(a is IProbeResource)));
	}

	[Fact]
	public void EagerTextureMetadataKeepsInlineAndLegacyAndNamedStreamRules()
	{
		foreach (var version in new[] { UnityVersion.Parse("4.0.0f1"), UnityVersion.V_2022 })
		{
			ITexture2D texture = AssetCreator.CreateTexture2D(AssetCreator.CreateCollection(version));
			texture.Width_C28 = 1;
			IProbeTexture view = Assert.IsAssignableFrom<IProbeTexture>(Assert.Single(UnitySource.Wrap([texture])));
			Assert.Equal(0, view.SourceSize);
			texture.ImageData_C28 = Pixels; Assert.Equal(4, view.SourceSize);
			texture.ImageData_C28 = [];
			if (texture.StreamData_C28 is { } stream)
			{
				stream.Size = 9; Assert.Equal(0, view.SourceSize);
				stream.Path = new Utf8String("media"); Assert.Equal(9, view.SourceSize);
			}
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void EagerSpriteMetadataUsesTheSameSelectedTextureRules(bool atlas)
	{
		(byte[] sprite, byte[] side) = UnityFixtureBuilder.SerializedAtlasSprite("hero", Pixels, 1, 1, Pixels, 1, 1, 0, 0, 1, 1, 0, 0, 2, 7, 7);
		byte[] bytes = atlas ? sprite : UnityFixtureBuilder.SerializedSprite("hero", Pixels, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0);
		Eager(bytes, assets =>
		{
			IProbeResource view = Assert.Single(UnitySource.Wrap(assets).OfType<IProbeResource>(), a => a.Kind == UnityEntryKind.Sprite);
			Assert.Equal("hero", view.Name); Assert.True(view.PathId > 0); Assert.Null(view.OriginalPath);
			Assert.Equal("png", view.OutputExtension); Assert.Equal(atlas ? 0 : 4, view.SourceSize);
		}, atlas ? side : null);
	}

	[Theory]
	[InlineData("missingatlas")]
	[InlineData("missingkey")]
	[InlineData("missingtexture")]
	public void EagerSpriteWithMissingDependenciesRemainsUnexportable(string missing)
	{
		(byte[] sprite, byte[] side) = UnityFixtureBuilder.SerializedAtlasSprite("hero", Pixels, 1, 1, Pixels, 1, 1, 0, 0, 1, 1, 0, 0, 2, 7,
			missing == "missingkey" ? 8 : 7, linkAtlasTexture: missing != "missingtexture");
		Eager(sprite, assets => Assert.DoesNotContain(UnitySource.Wrap(assets).OfType<IProbeResource>(), a => a.Kind == UnityEntryKind.Sprite), missing == "missingatlas" ? null : side);
	}
}
