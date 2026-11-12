using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

	private static byte[] TextRows(params bool[] visible)
	{
		using SerializedFile file = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(visible.Length), "rows.assets");
		_ = file.Objects;
		ObjectInfo[] rows = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
		for (int index = 0; index < rows.Length; index++)
			rows[index].ObjectData = visible[index] ? rows[index].ObjectData : [0xFF];
		using MemoryStream output = new();
		file.Write(output);
		return output.ToArray();
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
		using LoadedGraph loaded = new(TextRows(true, false, false, true));
		ICompactUnityGraph catalogue = loaded.Catalogue;
		Assert.Equal(new long[] { 1, 4 }, catalogue.Entries.Select(entry => entry.PathId));
		Assert.All(catalogue.Entries, entry => { Assert.Equal(UnityEntryKind.Text, entry.Kind); Assert.Equal("notes", entry.Name); });
		for (int index = 0; index < catalogue.Entries.Count; index++) AssertParity(catalogue, index);
		Assert.Equal(Encoding.UTF8.GetBytes("payload"), catalogue.Export(1));
	}

	[Fact]
	public void AllHiddenResourceRowsDoNotLeakIntoTheVisibleCatalogue()
	{
		using LoadedGraph loaded = new(TextRows(false, false, false));
		ICompactUnityGraph catalogue = loaded.Catalogue;
		Assert.Empty(catalogue.Entries);
		Assert.Throws<IndexOutOfRangeException>(() => catalogue.Export(0));
	}

	[Fact]
	public void ManyVisibleRowsRemainOrderedAcrossOneHiddenTailRow()
	{
		const int count = 512;
		using LoadedGraph loaded = new(TextRows(Enumerable.Repeat(true, count).Append(false).ToArray()));
		ICompactUnityGraph catalogue = loaded.Catalogue;
		Assert.Equal(count, catalogue.Entries.Count);
		Assert.Equal(Enumerable.Range(1, count).Select(path => (long)path), catalogue.Entries.Select(entry => entry.PathId));
		Assert.All(catalogue.Entries, entry => { Assert.Equal("notes", entry.Name); Assert.Equal(UnityEntryKind.Text, entry.Kind); });
		foreach (int index in new[] { 0, count / 2, count - 1 }) AssertParity(catalogue, index);
	}

	[Fact]
	public void TwoHiddenCollectionsKeepIndependentVisibleMapsAndGlobalOrder()
	{
		using LoadedGraph loaded = new(TextRows(true, false, true, false, true), TextRows(true, true, true, true, false));
		ICompactUnityGraph catalogue = loaded.Catalogue;
		Assert.Equal(2, loaded.Graph.Collections.Count);
		Assert.Equal(7, catalogue.Entries.Count);
		long[] paths = [1, 3, 5, 1, 2, 3, 4];
		int[] collections = [0, 0, 0, 1, 1, 1, 1];
		for (int index = 0; index < catalogue.Entries.Count; index++)
		{
			UnityCatalogEntry entry = catalogue.Entries[index];
			Assert.Equal(index, entry.Index); Assert.Equal(collections[index], entry.CollectionIndex);
			Assert.Equal(paths[index], entry.PathId); Assert.Equal("notes", entry.Name);
			Assert.Equal(UnityEntryKind.Text, entry.Kind); AssertParity(catalogue, index);
			Assert.Equal(entry, catalogue.Entries[index]);
		}
		Assert.Equal(Encoding.UTF8.GetBytes("payload"), catalogue.Export(0));
	}
}
