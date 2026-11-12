using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityGraphCompactionTests(ITestOutputHelper output)
{
	private static readonly byte[] Pixels = [10, 20, 30, 255];
	private static readonly BindingFlags PrivateFields = BindingFlags.Instance | BindingFlags.NonPublic;
	private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
	private static bool Deferred(IUnityObjectBase value) => value.GetType().Name == "DeferredObject";
	private static GameBundle Root(IUnityGraph graph) => (GameBundle)typeof(UnityGraph).GetFields(PrivateFields)
		.Single(field => field.FieldType == typeof(GameBundle)).GetValue(graph)!;
	private static SerializedFile Read(byte[] bytes, string name)
	{
		using SmartStream input = SmartStream.CreateMemory(bytes);
		return (SerializedFile)SchemeReader.ReadFile(input, name, name, FileReadMode.MetadataOnly);
	}
	private sealed class FullReader : AssetFactoryBase
	{
		private readonly GameAssetFactory inner = new(new BaseManager(static _ => { }));
		public List<int> Reads { get; } = [];
		public Exception? TextureFailure { get; set; }
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{
			Reads.Add(info.ClassID);
			if (info.ClassID == 28 && TextureFailure is { } failure) throw failure;
			return inner.ReadAsset(info, data, type, references);
		}
	}

	[Theory]
	[InlineData("texture")]
	[InlineData("text")]
	[InlineData("font")]
	[InlineData("movie")]
	[InlineData("video")]
	[InlineData("audio")]
	[InlineData("mesh")]
	public void EveryResourceViewReusesItsOrdinaryIdentityAndExportsAfterProjectionRelease(string family)
	{
		byte[] payload = [1, 2, 3, 4];
		byte[] bytes = family switch
		{
			"texture" => UnityFixtureBuilder.SerializedInlineTexture("resource", Pixels),
			"text" => UnityFixtureBuilder.SerializedTextAsset("resource", "payload"),
			"font" => UnityFixtureBuilder.SerializedFont("resource", payload),
			"movie" => UnityFixtureBuilder.SerializedMovieTexture("resource", payload),
			"video" => UnityFixtureBuilder.SerializedVideoClip("resource", "movie.mp4", "media.resS", 4),
			"audio" => UnityFixtureBuilder.SerializedLegacyAudioClip("resource", payload),
			"mesh" => UnityFixtureBuilder.SerializedTriangleMesh("resource"),
			_ => throw new InvalidOperationException(),
		};
		string directory = Temp.NewDirectory();
		string path = Path.Combine(directory, "selected.assets");
		File.WriteAllBytes(path, bytes); File.WriteAllBytes(Path.Combine(directory, "media.resS"), payload);
		FullReader reader = new();
		IUnityGraph graph = UnitySource.OpenGraph(path, readFile: null, fullReader: reader);
		using UnitySession session = UnitySession.FromGraph(graph);
		IProbeAsset view = graph.Collections[0].Assets.Single(asset => ((IProbeResource)asset).Name == "resource");
		IUnityObjectBase identity = Root(graph).FetchAssets().Single(asset => Deferred(asset) && ((INamed)asset).Name.String == "resource");
		Assert.Same(identity, view);
		Assert.Null(Property(identity, "Projection"));
		UnityCatalogEntry entry = session.Entries.Single(e => e.Name == "resource");
		Assert.Empty(reader.Reads);
		for (int pass = 0; pass < 2; pass++)
		{
			using MemoryStream destination = new();
			Assert.True(session.Export(entry.Index, destination));
			Assert.NotEmpty(destination.ToArray());
			Assert.Same(identity, identity.Collection.Single(a => a.PathID == identity.PathID));
			Assert.Equal(entry.SourceSize, ((IProbeResource)view).SourceSize);
			Assert.Equal(entry.OutputExtension, ((IProbeResource)view).OutputExtension);
			Assert.Equal("resource", ((IProbeResource)view).Name);
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference[] WatchNameAdapters(IEnumerable<IUnityObjectBase> identities)
	{
		List<WeakReference> watched = [];
		FieldInfo bytesField = typeof(Utf8String).GetFields(PrivateFields).Single(field => field.FieldType == typeof(byte[]));
		foreach (IUnityObjectBase identity in identities)
		{
			Utf8String name = ((INamed)identity).Name;
			byte[] bytes = (byte[])bytesField.GetValue(name)!;
			Assert.NotEmpty(bytes); // Empty UTF-8 buffers may be shared singletons.
			watched.Add(new(name));
			watched.Add(new(bytes));
		}
		return watched.ToArray();
	}

	[Fact]
	public void ACompleteLazyCatalogueDoesNotRetainItsTransientUtf8NameAdaptersOrBuffers()
	{
		const int count = 4096;
		using SerializedFile file = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(count), "large.assets");
		GameBundle root = new(); FullReader reader = new(); UnitySource.LazyAssetFactory factory = new(reader);
		SerializedAssetCollection collection = root.AddCollectionFromSerializedFile(file, factory);
		using UnityGraph graph = new(root, file, [collection], 0, [], [], factory);
		using UnitySession session = UnitySession.FromGraph(graph);
		IUnityObjectBase[] identities = collection.ToArray();
		Assert.Equal(count, session.Entries.Count);
		Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), session.Entries.Select(e => e.PathId));
		Assert.Equal(Enumerable.Range(0, count), session.Entries.Select(e => e.Index));
		Assert.All(session.Entries, entry => { Assert.Equal("notes", entry.Name); Assert.Equal(7, entry.SourceSize); Assert.Equal(string.Empty, entry.OriginalPath); });
		Assert.Empty(reader.Reads);
		WeakReference[] watched = WatchNameAdapters(identities);
		Assert.Equal(2 * count, watched.Length);
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
		int alive = watched.Count(weak => weak.IsAlive);
		output.WriteLine($"Retained UTF-8 name adapters and buffers: {alive}/{watched.Length}.");
		Assert.Equal(0, alive);
		foreach (int index in new[] { 0, count / 2, count - 1, 0 })
		{
			using MemoryStream destination = new(); Assert.True(session.Export(index, destination));
			Assert.Equal(Encoding.UTF8.GetBytes("payload"), destination.ToArray());
			Assert.Same(identities[index], collection.Single(asset => asset.PathID == index + 1));
			Assert.Equal("notes", ((IProbeResource)identities[index]).Name);
		}
		Assert.Equal(new[] { 49, 49, 49, 49 }, reader.Reads);
		GC.KeepAlive(root); GC.KeepAlive(graph); GC.KeepAlive(session); GC.KeepAlive(identities);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference AssignName(INamed identity, string text)
	{
		Utf8String adapter = new(text);
		identity.Name = adapter;
		return new(adapter);
	}

	[Theory]
	[InlineData("")]
	[InlineData("renamed")]
	[InlineData("имя-猫-🌙")]
	public void TheNamedBridgePreservesAssignedValuesWithoutOwningTheIncomingAdapter(string name)
	{
		using SerializedFile file = Read(UnityFixtureBuilder.SerializedTextAsset("original", "payload"), "named.assets");
		GameBundle root = new(); FullReader reader = new(); UnitySource.LazyAssetFactory factory = new(reader);
		SerializedAssetCollection collection = root.AddCollectionFromSerializedFile(file, factory);
		IUnityObjectBase identity = Assert.Single(collection);
		WeakReference assigned = AssignName((INamed)identity, name);
		using UnityGraph graph = new(root, file, [collection], 0, [], [], factory);
		using UnitySession session = UnitySession.FromGraph(graph);
		Assert.Equal(name, Assert.Single(session.Entries).Name);
		Assert.Equal(name, ((INamed)identity).Name.String);
		Assert.Equal(Encoding.UTF8.GetBytes(name), ((INamed)identity).Name.Data.ToArray());
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
		Assert.False(assigned.IsAlive);
		for (int pass = 0; pass < 2; pass++)
		{
			using MemoryStream destination = new(); Assert.True(session.Export(0, destination));
			Assert.Equal(Encoding.UTF8.GetBytes("payload"), destination.ToArray());
			Assert.Same(identity, Assert.Single(collection));
			Assert.Equal(name, ((IProbeResource)identity).Name);
		}
		Assert.Equal(new[] { 49, 49 }, reader.Reads);
		GC.KeepAlive(root); GC.KeepAlive(graph); GC.KeepAlive(session); GC.KeepAlive(identity);
	}

	[Fact]
	public void DescribeAllocatesOnlyItsViewArrayForAnExistingLargeResourceCollection()
	{
		const int count = 4096;
		using SerializedFile file = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(count), "large.assets");
		using GameBundle root = new();
		FullReader reader = new(); UnitySource.LazyAssetFactory factory = new(reader);
		SerializedAssetCollection collection = root.AddCollectionFromSerializedFile(file, factory);
		IUnityObjectBase[] identities = collection.ToArray();
		Assert.Equal(count, identities.Length);
		Assert.Equal(count, factory.Describe(collection).Count); // Warm the actual Describe path before measuring.
		long allocated = GC.GetAllocatedBytesForCurrentThread();
		IReadOnlyList<IProbeAsset> views = factory.Describe(collection);
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		output.WriteLine($"4096 precreated resource views allocated {allocated} bytes.");
		Assert.True(allocated < 64 * 1024, $"Describe allocated {allocated} bytes; it must reuse resource identities rather than allocate per-entry wrappers.");
		for (int i = 0; i < count; i++) Assert.Same(identities[i], views[i]);
		Assert.Empty(reader.Reads);
		GC.KeepAlive(root); GC.KeepAlive(views);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference[] WatchProjectionStorage(GameBundle root)
	{
		List<WeakReference> watched = [];
		foreach (IUnityObjectBase identity in root.FetchAssets().Where(Deferred))
		{
			object? projection = Property(identity, "Projection");
			if (projection is not null)
			{
				watched.Add(new(projection));
				foreach (FieldInfo field in projection.GetType().GetFields(PrivateFields))
					if (field.GetValue(projection) is Array { Length: > 0 } values) watched.Add(new(values));
			}
		}
		return watched.ToArray();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AllSelectedSnapshotsFinishBeforeSelectedAndDependencyProjectionStorageIsReleased(bool failedFirstExport)
	{
		byte[] firstBytes = UnityFixtureBuilder.SerializedInlineTexture("first", Pixels, dependencies: ["dependency.assets"]);
		byte[] dependencyBytes = UnityFixtureBuilder.SerializedTextAsset("dependency", "payload");
		byte[] secondBytes = UnityFixtureBuilder.SerializedInlineTexture("second", Pixels, dependencies: ["dependency.assets"]);
		SerializedFile firstFile = Read(firstBytes, "first.assets"), secondFile = Read(secondBytes, "second.assets"), dependencyFile = Read(dependencyBytes, "dependency.assets");
		GameBundle root = new(); FullReader reader = new(); UnitySource.LazyAssetFactory factory = new(reader);
		SerializedAssetCollection first = root.AddCollectionFromSerializedFile(firstFile, factory);
		SerializedAssetCollection second = root.AddCollectionFromSerializedFile(secondFile, factory);
		root.AddCollectionFromSerializedFile(dependencyFile, factory); root.InitializeAllDependencyLists();
		Assert.Single(factory.Describe(first));
		WeakReference[] watched = WatchProjectionStorage(root);
		Assert.True(watched.Length >= 3);
		using UnityGraph graph = new(root, firstFile, [first, second], 1, [secondFile, dependencyFile], [], factory);
		using UnitySession session = UnitySession.FromGraph(graph);
		Assert.Equal(new[] { "first", "second" }, session.Entries.Select(e => e.Name));
		Assert.Equal(new long[] { 4, 4 }, session.Entries.Select(e => e.SourceSize));
		Assert.Equal(new[] { 0, 1 }, session.Entries.Select(e => e.CollectionIndex));
		IUnityObjectBase[] identities = root.FetchAssets().Where(Deferred).ToArray();
		Assert.Equal(3, identities.Length);
		Assert.All(identities, identity => { Assert.Null(Property(identity, "Projection")); });
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
		Assert.All(watched, weak => Assert.False(weak.IsAlive));
		Assert.Empty(reader.Reads);
		if (failedFirstExport)
		{
			OutOfMemoryException failure = new("texture sentinel"); reader.TextureFailure = failure;
			using MemoryStream failed = new();
			Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => session.Export(1, failed)));
			Assert.Empty(failed.ToArray()); reader.TextureFailure = null;
			Assert.All(identities, identity => Assert.Same(identity, identity.Collection.Single(a => a.PathID == identity.PathID)));
		}
		foreach (int index in new[] { 0, 1, 0, 1 })
		{
			using MemoryStream destination = new(); Assert.True(session.Export(index, destination));
			Assert.Equal(Pixels, PngDecoder.Decode(destination.ToArray()).Rgba);
		}
		Assert.All(identities, identity => Assert.Same(identity, identity.Collection.Single(a => a.PathID == identity.PathID)));
		GC.KeepAlive(root); GC.KeepAlive(session);
	}

	private sealed class FailingSelections(SerializedAssetCollection first, Exception failure) : IReadOnlyList<SerializedAssetCollection>
	{
		public int Count => 2;
		public SerializedAssetCollection this[int index] => index == 0 ? first : throw failure;
		public IEnumerator<SerializedAssetCollection> GetEnumerator() => throw new NotSupportedException();
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}


	[Fact]
	public void AnUnfinishedGraphBuildKeepsEveryProjectionAvailableToItsOwner()
	{
		using SerializedFile file = Read(UnityFixtureBuilder.SerializedTextAsset("notes", "payload"), "first.assets");
		using GameBundle root = new(); FullReader reader = new(); UnitySource.LazyAssetFactory factory = new(reader);
		SerializedAssetCollection collection = root.AddCollectionFromSerializedFile(file, factory);
		IUnityObjectBase identity = Assert.Single(collection);
		object projection = Property(identity, "Projection")!;
		InvalidDataException failure = new("later selected collection failed");
		Assert.Same(failure, Assert.Throws<InvalidDataException>(() => new UnityGraph(root, file, new FailingSelections(collection, failure), 0, [], [], factory)));
		Assert.Same(projection, Property(identity, "Projection"));
		IProbeResource retained = Assert.IsAssignableFrom<IProbeResource>(Assert.Single(factory.Describe(collection)));
		Assert.Equal("notes", retained.Name); Assert.Equal(7, retained.SourceSize);
		Assert.Equal(Encoding.UTF8.GetBytes("payload"), retained.ExportData());
		Assert.Single(reader.Reads);
	}
}
