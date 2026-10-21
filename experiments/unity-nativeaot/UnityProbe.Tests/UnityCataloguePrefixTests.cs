using System;
using System.Collections.Generic;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityCataloguePrefixTests(ITestOutputHelper output)
{
	private static SerializedFile Read(byte[] bytes, string name)
	{
		using SmartStream input = SmartStream.CreateMemory(bytes);
		return (SerializedFile)SchemeReader.ReadFile(input, name, name, FileReadMode.MetadataOnly);
	}

	private sealed class UnusedFullReader : AssetFactoryBase
	{
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data,
			SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
			=> throw new InvalidOperationException("The compact catalogue must not read full assets.");
	}

	private static void WarmCompactPath(UnusedFullReader fullReader)
	{
		using SerializedFile file = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(1), "warm.assets");
		GameBundle root = new();
		UnitySource.LazyAssetFactory factory = new(fullReader, compact: true);
		SerializedAssetCollection collection = root.AddCollectionFromSerializedFile(file, factory);
		using IUnityGraph graph = factory.BuildCompactGraph(root, file, [collection], [], []);
		Assert.Equal(1, Assert.IsAssignableFrom<ICompactUnityGraph>(graph).Entries.Count);
	}

	[Fact]
	public void BuildingTheCompactCatalogueDoesNotAllocateAPerResourceMapping()
	{
		const int perCollection = 4096;
		const int collectionCount = 2;
		const int total = perCollection * collectionCount;

		using SerializedFile firstFile = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(perCollection), "first.assets");
		using SerializedFile secondFile = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(perCollection), "second.assets");
		UnusedFullReader fullReader = new();
		GameBundle root = new();
		UnitySource.LazyAssetFactory factory = new(fullReader, compact: true);
		SerializedAssetCollection first = root.AddCollectionFromSerializedFile(firstFile, factory);
		SerializedAssetCollection second = root.AddCollectionFromSerializedFile(secondFile, factory);

		WarmCompactPath(new UnusedFullReader());

		long before = GC.GetAllocatedBytesForCurrentThread();
		IUnityGraph compact = factory.BuildCompactGraph(root, firstFile, [first, second], [secondFile], []);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		using (compact)
		{
			ICompactUnityGraph catalogue = Assert.IsAssignableFrom<ICompactUnityGraph>(compact);
			Assert.Equal(total, catalogue.Entries.Count);
			for (int index = 0; index < total; index++)
			{
				int collectionIndex = index / perCollection;
				long pathID = index % perCollection + 1;
				UnityCatalogEntry entry = catalogue.Entries[index];
				Assert.Equal(index, entry.Index);
				Assert.Equal(collectionIndex, entry.CollectionIndex);
				Assert.Equal(pathID, entry.PathId);
				Assert.Equal(UnityEntryKind.Text, entry.Kind);
				Assert.Equal("notes", entry.Name);
				Assert.Equal(7, entry.SourceSize);
				Assert.Equal(string.Empty, entry.OriginalPath);

				UnityEntryMetadata metadata = catalogue.Metadata(index);
				Assert.Equal(UnityEntryKind.Text, metadata.Kind);
				Assert.Equal(collectionIndex, metadata.CollectionIndex);
				Assert.Equal(pathID, metadata.PathId);
				Assert.Equal(7, metadata.SourceSize);
				Assert.Equal(string.Empty, metadata.OriginalPath);
			}

			foreach (int index in new[] { 0, perCollection - 1, perCollection, total - 2, total - 1 })
			{
				Assert.Equal("notes", catalogue.Entries[index].Name);
				Assert.Equal(7, catalogue.Entries[index].SourceSize);
			}
		}

		output.WriteLine($"Building the {total}-resource compact catalogue allocated {allocated} bytes.");
		Assert.True(allocated < 16 * 1024,
			$"Building the {total}-resource compact catalogue allocated {allocated} bytes; " +
			"the compact path must not allocate a per-resource catalogue mapping.");
	}
}
