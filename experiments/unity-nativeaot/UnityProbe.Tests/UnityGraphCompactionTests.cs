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
using AssetRipper.Assets.IO.Writing;
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
	private static readonly byte[] Pixels = Enumerable.Repeat(new byte[] { 10, 20, 30, 255 }, 4).SelectMany(p => p).ToArray();
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
	[InlineData("sprite")]
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
			"sprite" => UnityFixtureBuilder.SerializedSprite("resource", Pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0),
			_ => throw new InvalidOperationException(),
		};
		string directory = Temp.NewDirectory();
		string path = Path.Combine(directory, "selected.assets");
		File.WriteAllBytes(path, bytes); File.WriteAllBytes(Path.Combine(directory, "media.resS"), payload);
		FullReader reader = new();
		IUnityGraph graph = UnitySource.OpenGraph(path, readFile: null, fullReader: reader);
		using UnitySession session = UnitySession.FromGraph(graph);
		IProbeAsset view = graph.Collections[0].Assets.Single(asset => ((IProbeResource)asset).Name == "resource");
		IUnityObjectBase identity = Root(graph).FetchAssets().Single(asset => Deferred(asset) && Property(asset, "Name")!.ToString() == "resource");
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
			if (Property(identity, "AtlasTextures") is { } index) watched.Add(new(index));
		}
		return watched.ToArray();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AllSelectedSnapshotsFinishBeforeSelectedAndDependencyProjectionStorageIsReleased(bool failedFirstExport)
	{
		(byte[] firstBytes, byte[] atlasBytes) = UnityFixtureBuilder.SerializedAtlasSprite("hero", Pixels, 2, 2, Pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 7, 7);
		(byte[] secondBytes, _) = UnityFixtureBuilder.SerializedAtlasSprite("background", Pixels, 2, 2, Pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 7, 7);
		SerializedFile firstFile = Read(firstBytes, "first.assets"), secondFile = Read(secondBytes, "second.assets"), atlasFile = Read(atlasBytes, UnityFixtureBuilder.AtlasDependencyName);
		GameBundle root = new(); FullReader reader = new(); UnitySource.LazyAssetFactory factory = new(reader);
		SerializedAssetCollection first = root.AddCollectionFromSerializedFile(firstFile, factory);
		SerializedAssetCollection second = root.AddCollectionFromSerializedFile(secondFile, factory);
		root.AddCollectionFromSerializedFile(atlasFile, factory); root.InitializeAllDependencyLists();
		Assert.Equal(2, factory.Describe(first).Count); // Populate the shared atlas index before capturing weak references.
		Assert.Contains(root.FetchAssets().Where(Deferred), asset => Property(asset, "AtlasTextures") is not null);
		WeakReference[] watched = WatchProjectionStorage(root);
		Assert.True(watched.Length >= 12);
		using UnityGraph graph = new(root, firstFile, [first, second], 1, [secondFile, atlasFile], [], factory);
		using UnitySession session = UnitySession.FromGraph(graph);
		Assert.Equal(new[] { "local_texture", "hero", "local_texture", "background" }, session.Entries.Select(e => e.Name));
		Assert.Equal(new long[] { 16, 0, 16, 0 }, session.Entries.Select(e => e.SourceSize));
		Assert.Equal(new[] { 0, 0, 1, 1 }, session.Entries.Select(e => e.CollectionIndex));
		IUnityObjectBase[] identities = root.FetchAssets().Where(Deferred).ToArray();
		Assert.Equal(6, identities.Length);
		Assert.All(identities, identity => { Assert.Null(Property(identity, "Projection")); Assert.Null(Property(identity, "AtlasTextures")); });
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
		Assert.All(watched, weak => Assert.False(weak.IsAlive));
		Assert.Empty(reader.Reads);
		if (failedFirstExport)
		{
			OutOfMemoryException failure = new("texture sentinel"); reader.TextureFailure = failure;
			using MemoryStream failed = new();
			Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => session.Export(3, failed)));
			Assert.Empty(failed.ToArray()); reader.TextureFailure = null;
			Assert.All(identities, identity => Assert.Same(identity, identity.Collection.Single(a => a.PathID == identity.PathID)));
		}
		foreach (int index in new[] { 1, 3, 1, 3 })
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
	public void ALaterSelectedSpriteCanDescribeAndExportItsEarlierSelectedTextureBeforeProjectionRelease()
	{
		byte[] pixel = [10, 20, 30, 255];
		UnityVersion version = UnityVersion.Parse("2021.3.0f1");
		ProcessedAssetCollection earlierDefinitions = AssetCreator.CreateCollection(version);
		var textureDefinition = AssetCreator.CreateTexture2D(earlierDefinitions);
		ProcessedAssetCollection laterDefinitions = AssetCreator.CreateCollection(version);
		var spriteDefinition = AssetCreator.CreateSprite(laterDefinitions);
		spriteDefinition.Name = new Utf8String("later");
		spriteDefinition.Rect.Width = 1; spriteDefinition.Rect.Height = 1;
		spriteDefinition.Pivot.X = .5f; spriteDefinition.Pivot.Y = .5f;
		spriteDefinition.PixelsToUnits = 100;
		spriteDefinition.RD.Texture.SetAsset(laterDefinitions, textureDefinition);
		spriteDefinition.RD.TextureRect.Width = 1; spriteDefinition.RD.TextureRect.Height = 1;
		spriteDefinition.RD.SettingsRaw = 2;
		Assert.Equal(1, spriteDefinition.RD.Texture.FileID);
		Assert.Equal(textureDefinition.PathID, spriteDefinition.RD.Texture.PathID);
		using MemoryStream body = new();
		using (AssetWriter writer = new(body, laterDefinitions)) spriteDefinition.WriteRelease(writer);
		SerializedFile earlierFile = Read(UnityFixtureBuilder.SerializedInlineTexture("earlier", pixel, textureDefinition.PathID), "earlier.assets");
		SerializedFile laterFile = Read(UnityFixtureBuilder.SerializedSprite("later", pixel, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0,
			dependencies: ["earlier.assets"]), "later.assets");
		ObjectInfo spriteDescriptor = laterFile.Objects.ToArray().Single(info => info.TypeID == 213);
		spriteDescriptor.ObjectData = body.ToArray();
		typeof(SerializedFile).GetField("m_objects", PrivateFields)!.SetValue(laterFile, new[] { spriteDescriptor });
		GameBundle root = new(); FullReader reader = new(); UnitySource.LazyAssetFactory factory = new(reader);
		SerializedAssetCollection earlier = root.AddCollectionFromSerializedFile(earlierFile, factory);
		SerializedAssetCollection later = root.AddCollectionFromSerializedFile(laterFile, factory);
		root.InitializeAllDependencyLists();
		using UnityGraph graph = new(root, earlierFile, [earlier, later], 0, [laterFile], [], factory);
		using UnitySession session = UnitySession.FromGraph(graph);
		Assert.Equal(new[] { "earlier", "later" }, session.Entries.Select(entry => entry.Name));
		Assert.Equal(new long[] { 4, 4 }, session.Entries.Select(entry => entry.SourceSize));
		Assert.Equal(new[] { 0, 1 }, session.Entries.Select(entry => entry.CollectionIndex));
		Assert.All(root.FetchAssets(), identity => Assert.Null(Property(identity, "Projection")));
		Assert.Empty(reader.Reads);
		for (int pass = 0; pass < 2; pass++)
		{
			using MemoryStream destination = new(); Assert.True(session.Export(1, destination));
			Assert.Equal(pixel, PngDecoder.Decode(destination.ToArray()).Rgba);
		}
		Assert.Equal(new[] { 213, 28, 213, 28 }, reader.Reads);
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
