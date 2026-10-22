using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityCataloguePrefixExtraTests
{
	private static SerializedFile Read(byte[] bytes, string name)
	{
		using SmartStream input = SmartStream.CreateMemory(bytes);
		return (SerializedFile)SchemeReader.ReadFile(input, name, name, FileReadMode.MetadataOnly);
	}

	private sealed class LoadedGraph : IDisposable
	{
		private readonly List<SerializedFile> files = [];
		public IUnityGraph Graph { get; }
		public ICompactUnityGraph Catalogue => Assert.IsAssignableFrom<ICompactUnityGraph>(Graph);

		public LoadedGraph(params byte[][] fixtures)
		{
			GameBundle root = new();
			UnitySource.LazyAssetFactory factory = new(
				new GameAssetFactory(new BaseManager(static _ => { })), compact: true);
			SerializedAssetCollection[] collections = new SerializedAssetCollection[fixtures.Length];
			try
			{
				for (int i = 0; i < fixtures.Length; i++)
					files.Add(Read(fixtures[i], $"collection-{i}.assets"));
				for (int i = 0; i < files.Count; i++)
					collections[i] = root.AddCollectionFromSerializedFile(files[i], factory);
				Graph = factory.BuildCompactGraph(root, files[0], collections, files.Skip(1).ToArray(), []);
			}
			catch
			{
				foreach (SerializedFile file in files) file.Dispose();
				throw;
			}
		}

		public void Dispose()
		{
			Graph.Dispose();
			foreach (SerializedFile file in files) file.Dispose();
		}
	}

	private static void AssertParity(ICompactUnityGraph catalogue, int index)
	{
		UnityCatalogEntry entry = catalogue.Entries[index];
		UnityEntryMetadata metadata = catalogue.Metadata(index);
		Assert.Equal(entry.Kind, metadata.Kind);
		Assert.Equal(entry.CollectionIndex, metadata.CollectionIndex);
		Assert.Equal(entry.PathId, metadata.PathId);
		Assert.Equal(entry.SourceSize, metadata.SourceSize);
		Assert.Equal(entry.OriginalPath, metadata.OriginalPath);
		Assert.Equal(entry.OutputExtension, metadata.OutputExtension);
		Assert.Equal(entry.Name.Length, catalogue.NameLength(index));
		char[] name = new char[catalogue.NameLength(index)];
		catalogue.CopyName(index, name);
		Assert.Equal(entry.Name, new string(name));
	}

	[Fact]
	public void EmptyCollectionSegmentsAreSkippedWithoutChangingCrossCollectionOrder()
	{
		using LoadedGraph loaded = new(
			UnityFixtureBuilder.SerializedRepeatedTextAssets(2),
			UnityFixtureBuilder.SerializedRepeatedTextAssets(0),
			UnityFixtureBuilder.SerializedRepeatedTextAssets(2));

		ICompactUnityGraph catalogue = loaded.Catalogue;
		Assert.Equal(3, loaded.Graph.Collections.Count);
		Assert.Equal(4, catalogue.Entries.Count);
		Assert.Equal(new[] { 0, 0, 2, 2 }, catalogue.Entries.Select(entry => entry.CollectionIndex));
		Assert.Equal(new[] { 0, 1, 2, 3 }, catalogue.Entries.Select(entry => entry.Index));
		Assert.Equal(new long[] { 1, 2, 1, 2 }, catalogue.Entries.Select(entry => entry.PathId));
		Assert.All(catalogue.Entries, entry =>
		{
			Assert.Equal(UnityEntryKind.Text, entry.Kind);
			Assert.Equal("notes", entry.Name);
			Assert.Equal(7, entry.SourceSize);
			Assert.Equal(string.Empty, entry.OriginalPath);
		});

		for (int index = 0; index < catalogue.Entries.Count; index++) AssertParity(catalogue, index);
		IReadOnlyList<UnityCatalogEntry> enumerable = Assert.IsAssignableFrom<IReadOnlyList<UnityCatalogEntry>>(loaded.Graph);
		Assert.Equal(catalogue.Entries.ToArray(), enumerable.ToArray());
		Assert.Equal(Encoding.UTF8.GetBytes("payload"), catalogue.Export(3));
	}

	[Fact]
	public void AnEmptyGraphRejectsIndexZeroAndNegativeIndices()
	{
		using LoadedGraph loaded = new(UnityFixtureBuilder.SerializedRepeatedTextAssets(0));
		ICompactUnityGraph catalogue = loaded.Catalogue;
		Assert.Equal(1, loaded.Graph.Collections.Count);
		Assert.Empty(catalogue.Entries);

		foreach (int index in new[] { -1, 0 })
		{
			Assert.Throws<IndexOutOfRangeException>(() => _ = catalogue.Entries[index]);
			Assert.Throws<IndexOutOfRangeException>(() => catalogue.Metadata(index));
			Assert.Throws<IndexOutOfRangeException>(() => catalogue.NameLength(index));
			Assert.Throws<IndexOutOfRangeException>(() => catalogue.CopyName(index, new char[0]));
		}
	}

	[Fact]
	public void ANonEmptyGraphRejectsNegativeAndCountIndices()
	{
		using LoadedGraph loaded = new(UnityFixtureBuilder.SerializedRepeatedTextAssets(2));
		ICompactUnityGraph catalogue = loaded.Catalogue;
		Assert.Equal(2, catalogue.Entries.Count);

		foreach (int index in new[] { -1, catalogue.Entries.Count })
		{
			Assert.Throws<IndexOutOfRangeException>(() => _ = catalogue.Entries[index]);
			Assert.Throws<IndexOutOfRangeException>(() => catalogue.Metadata(index));
			Assert.Throws<IndexOutOfRangeException>(() => catalogue.NameLength(index));
			Assert.Throws<IndexOutOfRangeException>(() => catalogue.CopyName(index, new char[0]));
		}
	}

	[Fact]
	public void AdjacentHiddenRowsKeepTheRemainingVisibleRowIdentities()
	{
		using LoadedGraph loaded = new(UnityFixtureBuilder.SerializedSpriteLinkCases(0, 3, 3, 0).Selected);
		ICompactUnityGraph catalogue = loaded.Catalogue;

		Assert.Single(catalogue.Entries);
		Assert.Equal(new long[] { 2 }, catalogue.Entries.Select(entry => entry.PathId));
		Assert.Equal(new[] { UnityEntryKind.Sprite },
			catalogue.Entries.Select(entry => entry.Kind));
		Assert.Equal(new[] { "hero" }, catalogue.Entries.Select(entry => entry.Name));
		for (int index = 0; index < catalogue.Entries.Count; index++) AssertParity(catalogue, index);
	}

	[Fact]
	public void AllHiddenSpriteRowsDoNotLeakIntoTheVisibleCatalogue()
	{
		using LoadedGraph loaded = new(UnityFixtureBuilder.SerializedSpriteLinkCases(3, 3, 3).Selected);
		ICompactUnityGraph catalogue = loaded.Catalogue;

		UnityCatalogEntry only = Assert.Single(catalogue.Entries);
		Assert.Equal(0, only.Index);
		Assert.Equal(0, only.CollectionIndex);
		Assert.Equal(1, only.PathId);
		Assert.Equal(UnityEntryKind.Texture, only.Kind);
		Assert.Equal("hero_texture", only.Name);
		AssertParity(catalogue, 0);
	}

	[Fact]
	public void ManyVisibleRowsRemainOrderedAcrossOneHiddenTailRow()
	{
		const int count = 512;
		using LoadedGraph loaded = new(UnityFixtureBuilder.SerializedDistinctSprites(count, malformedLast: true));
		ICompactUnityGraph catalogue = loaded.Catalogue;

		Assert.Equal(count, catalogue.Entries.Count);
		Assert.Equal(1, catalogue.Entries[0].PathId);
		Assert.Equal("hero_texture", catalogue.Entries[0].Name);
		Assert.Equal(2, catalogue.Entries[1].PathId);
		Assert.Equal(count, catalogue.Entries[count - 1].PathId);
		Assert.Equal("hero", catalogue.Entries[count - 1].Name);
		for (int index = 0; index < catalogue.Entries.Count; index++) AssertParity(catalogue, index);
	}

	[Fact]
	public void TwoHiddenCollectionsKeepIndependentVisibleMapsAndGlobalOrder()
	{
		using LoadedGraph loaded = new(
			UnityFixtureBuilder.SerializedSpriteLinkCases(0, 3, 0, 3, 0).Selected,
			UnityFixtureBuilder.SerializedDistinctSprites(5, malformedLast: true));
		ICompactUnityGraph catalogue = loaded.Catalogue;

		Assert.Equal(2, loaded.Graph.Collections.Count);
		Assert.Equal(6, catalogue.Entries.Count);
		long[] paths = [2, 1, 2, 3, 4, 5];
		int[] collections = [0, 1, 1, 1, 1, 1];
		string[] names = ["hero", "hero_texture", "hero", "hero", "hero", "hero"];
		for (int index = 0; index < catalogue.Entries.Count; index++)
		{
			UnityCatalogEntry entry = catalogue.Entries[index];
			Assert.Equal(index, entry.Index);
			Assert.Equal(collections[index], entry.CollectionIndex);
			Assert.Equal(paths[index], entry.PathId);
			Assert.Equal(names[index], entry.Name);
			Assert.Equal(index == 1 ? UnityEntryKind.Texture : UnityEntryKind.Sprite, entry.Kind);
			AssertParity(catalogue, index);
			Assert.Equal(entry, catalogue.Entries[index]);
		}

		Assert.NotEmpty(Assert.IsType<byte[]>(catalogue.Export(0)));
	}
}
