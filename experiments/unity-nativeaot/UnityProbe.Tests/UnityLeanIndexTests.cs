using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AssetRipper.Assets;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Generics;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.Streams.Smart;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetRipper.Assets.Bundles;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityLeanIndexTests(ITestOutputHelper output)
{
	private sealed class NonResourceIdentity(AssetInfo info) : UnityObjectBase(info) { }
	private static GameBundle Root(UnitySession session)
	{
		const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
		IUnityGraph graph = (IUnityGraph)typeof(UnitySession).GetField("graph", fields)!.GetValue(session)!;
		return (GameBundle)graph.GetType().GetFields(fields).Single(field => field.FieldType == typeof(GameBundle)).GetValue(graph)!;
	}

	private static string Hash(System.Collections.Generic.IEnumerable<UnityCatalogEntry> entries)
	{
		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (UnityCatalogEntry entry in entries) hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n"));
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	[Fact]
	public void ThousandsOfRealResourcesListAndExportWithoutEagerUnityIdentitiesOrAssetDictionaryRows()
	{
		const int count = 4096;
		string path = Path.Combine(Temp.NewDirectory(), "large.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedRepeatedTextAssets(count));
		using UnitySession session = UnitySession.Open(path);
		UnityCatalogEntry[] expected = Enumerable.Range(0, count)
			.Select(i => new UnityCatalogEntry(i, UnityEntryKind.Text, 0, i + 1, "notes", 7, "", "bin")).ToArray();
		Assert.Equal(expected, session.Entries);
		Assert.Equal(Hash(expected), Hash(session.Entries));
		GameBundle root = Root(session);
		int identities = root.FetchAssets().Count(asset => asset.GetType().Name == "DeferredObject");
		int dictionaryRows = root.FetchAssetCollections().Sum(collection => collection.Assets.Count);
		output.WriteLine($"4096 listed resources: eager identities={identities}, upstream dictionary rows={dictionaryRows}.");
		Assert.Equal(0, identities);
		Assert.Equal(0, dictionaryRows);
		foreach (int index in new[] { 0, count / 2, count - 1, 0 })
		{
			using MemoryStream destination = new();
			Assert.True(session.Export(index, destination));
			Assert.Equal(Encoding.UTF8.GetBytes("payload"), destination.ToArray());
		}
		Assert.Equal(Hash(expected), Hash(session.Entries));
		GC.KeepAlive(root); GC.KeepAlive(session);
	}
	private sealed class TrackingReader : AssetFactoryBase
	{
		private readonly GameAssetFactory inner = new(new BaseManager(static _ => { }));
		public List<WeakReference> Watched { get; } = [];
		public long FailurePath { get; set; }
		public OutOfMemoryException Failure { get; } = new("export failure sentinel");
		[MethodImpl(MethodImplOptions.NoInlining)]
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> bytes, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{
			IUnityObjectBase? identity = info.Collection.TryGetAsset(info.PathID);
			Assert.NotNull(identity); Assert.Same(identity, info.Collection.TryGetAsset(info.PathID));
			Watched.Add(new(identity));
			if (FailurePath == info.PathID) throw Failure;
			return inner.ReadAsset(info, bytes, type, references);
		}
	}
	private static int ResolvedIdentityCount(GameBundle root)
		=> root.FetchAssetCollections().Sum(collection => collection.AssetResolver is null ? 0
			: ((IDictionary)collection.AssetResolver.GetType().GetField("identities", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(collection.AssetResolver)!).Count);

	[Fact]
	public void ExportingEveryDistinctRowRetiresOperationIdentitiesAndFailureCanRetry()
	{
		const int count = 4096;
		string path = Path.Combine(Temp.NewDirectory(), "many-exports.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedRepeatedTextAssets(count));
		TrackingReader reader = new();
		using UnitySession session = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null, reader, compact: true));
		GameBundle root = Root(session);
		for (int i = 0; i < count; i++)
		{
			using MemoryStream destination = new(); Assert.True(session.Export(i, destination));
			Assert.Equal("payload", Encoding.UTF8.GetString(destination.ToArray()));
		}
		Assert.Equal(count, reader.Watched.Count);
		output.WriteLine($"After4096 distinct exports: resolver identities={ResolvedIdentityCount(root)}.");
		Assert.Equal(0, ResolvedIdentityCount(root));
		reader.FailurePath = count / 2;
		using MemoryStream failed = new();
		Assert.Same(reader.Failure, Assert.Throws<OutOfMemoryException>(() => session.Export(count / 2 - 1, failed)));
		Assert.Empty(failed.ToArray()); Assert.Equal(0, ResolvedIdentityCount(root));
		reader.FailurePath = 0;
		foreach (int index in new[] { 0, count / 2, count - 1 })
		{
			using MemoryStream destination = new(); Assert.True(session.Export(index, destination));
			Assert.Equal("payload", Encoding.UTF8.GetString(destination.ToArray()));
		}
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
		Assert.All(reader.Watched, weak => Assert.False(weak.IsAlive));
		Assert.Equal(0, ResolvedIdentityCount(root)); GC.KeepAlive(session); GC.KeepAlive(root);
	}

	[Fact]
	public void OriginalPathPassPersistsMetadataAndRetiresItsResolvedIdentities()
	{
		string path = Path.Combine(Temp.NewDirectory(), "paths.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedResourceManagerTextAsset("hero.txt", "hero", "payload"));
		using UnitySession session = UnitySession.Open(path);
		GameBundle root = Root(session);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
		Assert.Equal(Assert.Single(legacy.Entries), entry);
		Assert.Equal(Hash(legacy.Entries), Hash(session.Entries));
		Assert.Equal(0, ResolvedIdentityCount(root));
		for (int pass = 0; pass < 2; pass++)
		{
			using MemoryStream destination = new(); Assert.True(session.Export(0, destination));
			Assert.Equal("payload", Encoding.UTF8.GetString(destination.ToArray()));
			Assert.Equal(entry, Assert.Single(session.Entries)); Assert.Equal(0, ResolvedIdentityCount(root));
		}
		GC.KeepAlive(root); GC.KeepAlive(session);
	}

	private static SerializedFile ReadTable(byte[] bytes, string name = "selected.assets")
	{
		using SmartStream input = SmartStream.CreateMemory(bytes);
		return (SerializedFile)SchemeReader.ReadFile(input, name, name, FileReadMode.MetadataOnly);
	}
	[Fact]
	public void MixedBorrowedAndEagerDescriptorsPreserveOriginalResourceOrderAndFullLegacyHash()
	{
		byte[] bytes = UnityFixtureBuilder.SerializedRepeatedTextAssets(3);
		FileBase ReadMixed(string ignored)
		{
			SerializedFile file = ReadTable(bytes);
			ObjectInfo[] table = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
			ObjectInfo eager = table[1]; eager.ObjectData = eager.ObjectData; table[1] = eager;
			return file;
		}
		string path = Path.Combine(Temp.NewDirectory(), "mixed.assets");
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, ReadMixed));
		using UnitySession compact = UnitySession.FromGraph(UnitySource.OpenGraph(path, ReadMixed, compact: true));
		Assert.Equal(new long[] { 1, 2, 3 }, compact.Entries.Select(entry => entry.PathId));
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		for (int i = 0; i < 3; i++)
		{
			using MemoryStream destination = new(); Assert.True(compact.Export(i, destination));
			Assert.Equal("payload", Encoding.UTF8.GetString(destination.ToArray()));
		}
	}

	private sealed class WideBodyStream(byte[] prefix, long extent = 4L * 1024 * 1024 * 1024) : Stream
	{
		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => extent;
		public override long Position { get; set; }
		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
		public override int Read(Span<byte> buffer)
		{
			if (Position >= prefix.Length) throw new IOException("Resource payload must remain unread.");
			int count = Math.Min(buffer.Length, prefix.Length - (int)Position);
			prefix.AsSpan((int)Position, count).CopyTo(buffer); Position += count; return count;
		}
		public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
		{
			SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => Length + offset,
		};
		public override void Flush() { }
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
	[Fact]
	public void WideBorrowedObjectRangesKeepMetadataWithoutNarrowingOrReadingThePayload()
	{
		byte[] bytes = UnityFixtureBuilder.SerializedTextAsset("wide", "payload");
		using SerializedFile original = ReadTable(bytes);
		using SmartStream source = SmartStream.CreateOwned(new WideBodyStream(original.Objects[0].ObjectData));
		FileBase ReadWide(string ignored)
		{
			SerializedFile file = ReadTable(bytes);
			ObjectInfo[] table = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
			object descriptor = table[0]; const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
			typeof(ObjectInfo).GetField("dataSource", fields)!.SetValue(descriptor, source);
			typeof(ObjectInfo).GetField("dataSize", fields)!.SetValue(descriptor, 3L * 1024 * 1024 * 1024);
			typeof(ObjectInfo).GetProperty("DataOffset")!.SetValue(descriptor, 0L); table[0] = (ObjectInfo)descriptor;
			return file;
		}
		string path = Path.Combine(Temp.NewDirectory(), "wide.assets");
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, ReadWide));
		using UnitySession compact = UnitySession.FromGraph(UnitySource.OpenGraph(path, ReadWide, compact: true));
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		Assert.Equal(7, Assert.Single(compact.Entries).SourceSize);
	}

	[Theory]
	[InlineData(false)] [InlineData(true)]
	public void PackedPayloadRowsPreserveMixedAndAllWideCanonicalRangesAndOwnerAcrossUnavailableExportRetry(bool allWide)
	{
		byte[] bytes = UnityFixtureBuilder.SerializedTextAsset("wide", "payload");
		using SerializedFile original = ReadTable(bytes); byte[] body = original.Objects[0].ObjectData;
		using SmartStream source = SmartStream.CreateOwned(new WideBodyStream(body, long.MaxValue));
		long[] lengths = allWide ? [(long)uint.MaxValue + 1, long.MaxValue, (long)uint.MaxValue + 7]
			: [body.Length, 0, uint.MaxValue, (long)uint.MaxValue + 1, long.MaxValue, body.Length];
		FileBase ReadWide(string ignored)
		{
			SerializedFile file = ReadTable(UnityFixtureBuilder.SerializedRepeatedTextAssets(lengths.Length));
			ObjectInfo[] table = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
			for (int i = 0; i < table.Length; i++)
			{
				object seed = table[i]; typeof(ObjectInfo).GetField("dataSource", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(seed, source);
				table[i] = ((ObjectInfo)seed).WithDataRange(i + 1, 0, lengths[i]);
			}
			Assert.Throws<ArgumentOutOfRangeException>(() => table[0].WithDataRange(1, 0, -1));
			return file;
		}
		string path = Path.Combine(Temp.NewDirectory(), "wide24.assets");
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, ReadWide));
		using UnitySession compact = UnitySession.FromGraph(UnitySource.OpenGraph(path, ReadWide, compact: true));
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		Assert.Equal(lengths.Count(value => value != 0), compact.Entries.Count); Assert.All(compact.Entries, row => { Assert.Equal("wide", row.Name); Assert.Equal(7, row.SourceSize); });
		var collection = Assert.Single(Root(compact).FetchAssetCollections()); var resolver = collection.AssetResolver!;
		long[] wide = (long[])resolver.GetType().GetField("wideLengths", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resolver)!;
		Assert.Equal(lengths.Length, wide.Length); Assert.Equal(lengths.Count(value => value > uint.MaxValue), wide.Count(value => value != 0));
		for (int pass = 0; pass < 2; pass++)
		{
			for (int i = 0; i < lengths.Length; i++)
			{
				IUnityObjectBase identity = collection.TryGetAsset(i + 1)!; Assert.Same(identity, collection.TryGetAsset(i + 1));
				ObjectInfo range = (ObjectInfo)identity.GetType().GetProperty("Data")!.GetValue(identity)!;
				Assert.Equal(lengths[i], range.DataSize); Assert.Equal(0, range.DataOffset); Assert.Same(source, range.BorrowedSource);
				using SmartStream cursor = range.OpenDataStream(); Assert.Equal(lengths[i], cursor.Length); if (lengths[i] != 0) Assert.Equal(body[0], cursor.ReadByte());
			}
			collection.AssetResolver = null;
			try { using MemoryStream unavailable = new(); Assert.False(compact.Export(0, unavailable)); Assert.Equal(0, unavailable.Length); }
			finally { collection.AssetResolver = resolver; }
			Assert.Equal(0, ResolvedIdentityCount(Root(compact)));
			if (!allWide) foreach (int index in new[] { 0, compact.Entries.Count - 1, 0 }) { using MemoryStream payload = new(); Assert.True(compact.Export(index, payload)); Assert.Equal("payload", Encoding.UTF8.GetString(payload.ToArray())); }
		}
		Assert.True(source.CanRead); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
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
	public void EveryResourceFamilyKeepsTheFullLegacyCatalogueAndRepeatedExportBytes(string family)
	{
		byte[] pixels = Enumerable.Repeat(new byte[] { 10, 20, 30, 255 }, 4).SelectMany(pixel => pixel).ToArray();
		byte[] payload = [1, 2, 3, 4];
		byte[] bytes = family switch
		{
			"texture" => UnityFixtureBuilder.SerializedInlineTexture("имя-猫", pixels),
			"text" => UnityFixtureBuilder.SerializedTextAsset("имя-猫", "payload"),
			"font" => UnityFixtureBuilder.SerializedFont("имя-猫", payload),
			"movie" => UnityFixtureBuilder.SerializedMovieTexture("имя-猫", payload),
			"video" => UnityFixtureBuilder.SerializedVideoClip("имя-猫", "movie.mp4", "media.resS", 4),
			"audio" => UnityFixtureBuilder.SerializedLegacyAudioClip("имя-猫", payload),
			"mesh" => UnityFixtureBuilder.SerializedTriangleMesh("имя-猫"),
			_ => UnityFixtureBuilder.SerializedSprite("имя-猫", pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0),
		};
		string directory = Temp.NewDirectory(); string path = Path.Combine(directory, "family.assets");
		File.WriteAllBytes(path, bytes); File.WriteAllBytes(Path.Combine(directory, "media.resS"), payload);
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
		using UnitySession compact = UnitySession.Open(path);
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		for (int pass = 0; pass < 2; pass++)
			for (int i = 0; i < compact.Entries.Count; i++)
			{
				using MemoryStream expected = new(), actual = new();
				Assert.True(legacy.Export(i, expected)); Assert.True(compact.Export(i, actual));
				Assert.Equal(expected.ToArray(), actual.ToArray());
			}
		Assert.Equal(0, ResolvedIdentityCount(Root(compact)));
		Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
	}

	[Fact]
	public void CrossFileAtlasDependenciesKeepLegacySizesHashesAndRetryWithoutRetainedIdentities()
	{
		byte[] pixels = Enumerable.Repeat(new byte[] { 10, 20, 30, 255 }, 4).SelectMany(pixel => pixel).ToArray();
		(byte[] first, byte[] atlas) = UnityFixtureBuilder.SerializedAtlasSprite("hero", pixels, 2, 2, pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 7, 7);
		(byte[] second, _) = UnityFixtureBuilder.SerializedAtlasSprite("background", pixels, 2, 2, pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 7, 7);
		string directory = Temp.NewDirectory(); string path = Path.Combine(directory, "selected.unity3d");
		File.WriteAllBytes(path, UnityFixtureBuilder.CompressedBundle(("first.assets", first), ("second.assets", second)));
		File.WriteAllBytes(Path.Combine(directory, UnityFixtureBuilder.AtlasDependencyName), atlas);
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
		using UnitySession compact = UnitySession.Open(path);
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		Assert.Equal(new long[] { 16, 0, 16, 0 }, compact.Entries.Select(entry => entry.SourceSize));
		for (int pass = 0; pass < 2; pass++)
			for (int i = 0; i < compact.Entries.Count; i++)
			{
				using MemoryStream expected = new(), actual = new();
				Assert.True(legacy.Export(i, expected)); Assert.True(compact.Export(i, actual));
				Assert.Equal(expected.ToArray(), actual.ToArray()); Assert.Equal(0, ResolvedIdentityCount(Root(compact)));
			}
	}

	[Fact]
	public void CompactHeaderProcessingReusesPerShapeStorageInsteadOfAllocatingObjectGraphs()
	{
		const int count = 4096;
		using SerializedFile warm = ReadTable(UnityFixtureBuilder.SerializedTextAsset("warm", "payload"));
		using SerializedFile file = ReadTable(UnityFixtureBuilder.SerializedRepeatedTextAssets(count));
		GameBundle root = new();
		UnitySource.LazyAssetFactory factory = new(new GameAssetFactory(new BaseManager(static _ => { })), compact: true);
		root.AddCollectionFromSerializedFile(warm, factory);
		long start = GC.GetAllocatedBytesForCurrentThread();
		var collection = root.AddCollectionFromSerializedFile(file, factory);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
		output.WriteLine($"4096 compact headers allocated {allocated} B (parsed source table excluded).");
		using UnitySession session = UnitySession.FromGraph(factory.BuildCompactGraph(root, file, [collection], [], []));
		Assert.Equal(count, session.Entries.Count);
		Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), session.Entries.Select(entry => entry.PathId));
		Assert.All(session.Entries, entry => { Assert.Equal("notes", entry.Name); Assert.Equal(7, entry.SourceSize); });
		foreach (int index in new[] { 0, count / 2, count - 1 })
		{
			using MemoryStream destination = new(); Assert.True(session.Export(index, destination));
			Assert.Equal("payload", Encoding.UTF8.GetString(destination.ToArray()));
		}
		Assert.True(allocated < 2 * 1024 * 1024,
			$"4096 already-parsed resource headers allocated {allocated} B; compact capture must not allocate a Unity identity and independent projection arrays for every object.");
		GC.KeepAlive(session); GC.KeepAlive(root);
	}

	[Fact]
	public void AFailedBorrowedHeaderDoesNotRetainItsPartiallyDecodedNameInShapeScratch()
	{
		byte[] bytes = UnityFixtureBuilder.SerializedTextAsset("failed-name", "payload");
		using (SerializedFile control = ReadTable(bytes))
		{
			byte[] body = control.Objects[0].ObjectData;
			int payload = body.AsSpan().IndexOf(Encoding.UTF8.GetBytes("payload"));
			Assert.True(payload >= 4);
			System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(checked((int)control.Objects[0].DataOffset) + payload - 4), -1);
		}
		using SerializedFile file = ReadTable(bytes); using GameBundle root = new();
		UnitySource.LazyAssetFactory factory = new(new GameAssetFactory(new BaseManager(static _ => { })), compact: true);
		root.AddCollectionFromSerializedFile(file, factory);
		IDictionary shapes = (IDictionary)typeof(UnitySource.LazyAssetFactory).GetField("shapes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(factory)!;
		object shape = Assert.Single(shapes.Values.Cast<object>());
		UnityProjection.State state = (UnityProjection.State)shape.GetType().GetProperty("Traversal")!.GetValue(shape)!;
		Assert.NotNull(state.Scratch); Assert.Null(state.Scratch.Text("m_Name"));
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference[] WatchCatalogueNames(UnitySession session)
		=> session.Entries.Select(entry => new WeakReference(entry.Name)).ToArray();

	[Fact]
	public void CatalogueNamesAreDecodedValuesAndDoNotBecomePersistentStrings()
	{
		string path = Path.Combine(Temp.NewDirectory(), "names.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedRepeatedTextAssets(4096));
		using UnitySession session = UnitySession.Open(path);
		WeakReference[] names = WatchCatalogueNames(session);
		Assert.Equal(4096, names.Length);
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
		int retained = names.Count(name => name.IsAlive);
		output.WriteLine($"After catalogue enumeration, {retained}/4096 decoded nonempty name strings remain reachable.");
		Assert.Equal(0, retained);
		Assert.All(session.Entries, entry => Assert.Equal("notes", entry.Name));
		using MemoryStream destination = new(); Assert.True(session.Export(4095, destination));
		Assert.Equal("payload", Encoding.UTF8.GetString(destination.ToArray()));
		GC.KeepAlive(session);
	}

	[Theory]
	[InlineData("")]
	[InlineData("ascii")]
	[InlineData("雪😀")]
	[InlineData("A\0B")]
	[InlineData("malformed")]
	[InlineData("truncated")]
	public unsafe void Utf8ArenaPreservesLegacyNameValuesAndExactAbiUtf16Copies(string name)
	{
		byte[] bytes = UnityFixtureBuilder.SerializedTextAsset(name is "malformed" or "truncated" ? "zzzz" : name, "payload");
		if (name is "malformed" or "truncated")
		{
			using SerializedFile file = ReadTable(bytes);
			int index = checked((int)file.Objects[0].DataOffset) + file.Objects[0].ObjectData.AsSpan().IndexOf(Encoding.UTF8.GetBytes("zzzz"));
			(name == "malformed" ? new byte[] { 0xF0, 0x28, 0x8C, 0xBC } : new byte[] { 0x61, 0x62, 0xE2, 0x82 }).CopyTo(bytes, index);
		}
		string path = Path.Combine(Temp.NewDirectory(), "utf8.assets"); File.WriteAllBytes(path, bytes);
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
		UnitySessionAbi abi = new(UnitySession.Open, 1); ulong token; int count;
		fixed (char* input = path) Assert.Equal(UnityAbi.Ok, abi.Open((ushort*)input, path.Length, &token, &count));
		try
		{
			Assert.Equal(1, count); string expected = Assert.Single(legacy.Entries).Name;
			UnityEntry entry = new() { StructSize = UnityEntry.Size };
			Assert.Equal(UnityAbi.Ok, abi.GetEntry(token, 0, &entry, null, 0)); Assert.Equal((uint)expected.Length, entry.NameLength);
			char[] result = Enumerable.Repeat('Z', expected.Length + 1).ToArray();
			fixed (char* target = result) Assert.Equal(UnityAbi.Ok, abi.GetEntry(token, 0, &entry, (ushort*)target, expected.Length));
			Assert.Equal(expected, new string(result, 0, expected.Length)); Assert.Equal('Z', result[^1]);
			using UnitySession compact = UnitySession.Open(path); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		}
		finally { Assert.Equal(UnityAbi.Ok, abi.Close(token)); }
	}

	[Fact]
	public unsafe void ProductionAbiReadsAllCatalogueMetadataWithoutAllocatingDecodedNameStrings()
	{
		string path = Path.Combine(Temp.NewDirectory(), "abi-names.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedRepeatedTextAssets(4096));
		UnitySessionAbi abi = new(UnitySession.Open, 1); ulong token; int count;
		fixed (char* input = path) Assert.Equal(UnityAbi.Ok, abi.Open((ushort*)input, path.Length, &token, &count));
		try
		{
			UnityEntry entry = new() { StructSize = UnityEntry.Size }; char* text = stackalloc char[16]; long size; int length;
			Assert.Equal(UnityAbi.Ok, abi.GetEntry(token, 0, &entry, (ushort*)text, 16));
			Assert.Equal(UnityAbi.Ok, abi.GetSourceSize(token, 0, &size));
			Assert.Equal(UnityAbi.Ok, abi.GetEntryPath(token, 0, (ushort*)text, 16, &length));
			Assert.Equal(UnityAbi.Ok, abi.GetEntryExtension(token, 0, (ushort*)text, 16, &length));
			for (int i = 0; i < count; i++)
			{
				abi.GetEntry(token, i, &entry, (ushort*)text, 16); abi.GetSourceSize(token, i, &size);
				abi.GetEntryPath(token, i, (ushort*)text, 16, &length); abi.GetEntryExtension(token, i, (ushort*)text, 16, &length);
			}
			long start = GC.GetAllocatedBytesForCurrentThread(); int status = 0; long totalSize = 0, totalPaths = 0; int nameLengths = 0;
			for (int i = 0; i < count; i++)
			{
				status |= abi.GetEntry(token, i, &entry, (ushort*)text, 16); totalPaths += entry.PathId; nameLengths += (int)entry.NameLength;
				status |= abi.GetSourceSize(token, i, &size); totalSize += size;
				status |= abi.GetEntryPath(token, i, (ushort*)text, 16, &length);
				status |= abi.GetEntryExtension(token, i, (ushort*)text, 16, &length);
			}
			long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
			output.WriteLine($"All four ABI metadata queries for4096 resources allocated {allocated} B.");
			Assert.Equal(0, status); Assert.Equal(4096 * 7L, totalSize); Assert.Equal(4096 * 4097L / 2, totalPaths); Assert.Equal(4096 * 5, nameLengths);
			Assert.True(allocated < 4096, $"ABI enumeration allocated {allocated} B; metadata decoding must use spans without per-entry names.");
		}
		finally { Assert.Equal(UnityAbi.Ok, abi.Close(token)); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void BundledModernAudioHeaderUsesResourcesLoadedAfterItsSerializedCollection(bool resourceFirst)
	{
		byte[] bank = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "audio", "short_vorbis.fsb"));
		byte[] clip = UnityFixtureBuilder.SerializedStreamedFsbAudioClip("bundled-sound", "audio.resource", (uint)bank.Length);
		string path = Path.Combine(Temp.NewDirectory(), "audio.unity3d");
		File.WriteAllBytes(path, resourceFirst
			? UnityFixtureBuilder.CompressedBundle(("audio.resource", bank), ("nested.bundle", UnityFixtureBuilder.CompressedBundle(("selected.assets", clip))))
			: UnityFixtureBuilder.CompressedBundle(("nested.bundle", UnityFixtureBuilder.CompressedBundle(("selected.assets", clip))), ("audio.resource", bank)));
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
		using UnitySession compact = UnitySession.Open(path);
		Assert.Equal("ogg", Assert.Single(legacy.Entries).OutputExtension);
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		for (int pass = 0; pass < 2; pass++)
		{
			using MemoryStream expected = new(), actual = new();
			Assert.True(legacy.Export(0, expected)); Assert.True(compact.Export(0, actual));
			Assert.Equal(expected.ToArray(), actual.ToArray());
		}
	}

	[Fact]
	public void SparseResolverKeepsTypedLookupAndReplacementRulesAndRecoversAfterUnavailableTarget()
	{
		string path = Path.Combine(Temp.NewDirectory(), "resolver.assets"); File.WriteAllBytes(path, UnityFixtureBuilder.SerializedTextAsset("first", "payload"));
		IUnityGraph graph = UnitySource.OpenGraph(path, readFile: null, compact: true);
		IUnityGraphCollection view = Assert.Single(graph.Collections); Assert.Equal("resolver.assets", view.Identity); Assert.Empty(view.Assets);
		using UnitySession session = UnitySession.FromGraph(graph);
		var collection = Assert.Single(Root(session).FetchAssetCollections());
		IUnityObjectBase original = collection.TryGetAsset(1)!; Assert.NotNull(original); Assert.Same(original, collection.TryGetAsset(1));
		Assert.Null(collection.TryGetAsset(0)); Assert.Null(collection.TryGetAsset(999)); Assert.Null(collection.TryGetAsset<AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D>(1));
		using GameBundle other = new(); var unrelated = other.AddNewProcessedCollection("other", collection.Version);
		Assert.Throws<ArgumentException>(() => collection.ReplaceAsset(new UnknownObject(new(unrelated, 1, 49), [])));
		Assert.Throws<ArgumentException>(() => collection.ReplaceAsset(new UnknownObject(new(collection, 999, 49), [])));
		Assert.Throws<ArgumentException>(() => collection.ReplaceAsset(new UnknownObject(new(collection, 1, 28), [])));
		using SerializedFile replacementFile = ReadTable(UnityFixtureBuilder.SerializedTextAsset("replacement", "changed"));
		var replacement = new GameAssetFactory(new BaseManager(static _ => { })).ReadAsset(new(collection, 1, 49), replacementFile.Objects[0].ObjectData, null, [])!;
		collection.ReplaceAsset(replacement); Assert.Same(replacement, collection.TryGetAsset(1)); Assert.Empty(collection.Assets);
		using MemoryStream output = new(); Assert.True(session.Export(0, output)); Assert.Equal("changed", Encoding.UTF8.GetString(output.ToArray()));
		Assert.Equal("first", session.Entries[0].Name); Assert.Equal(0, ResolvedIdentityCount(Root(session)));
		var resolver = collection.AssetResolver; collection.AssetResolver = null;
		using MemoryStream unavailable = new(); Assert.False(session.Export(0, unavailable)); Assert.Equal(0, unavailable.Length);
		collection.AssetResolver = resolver;
		using MemoryStream retry = new(); Assert.True(session.Export(0, retry)); Assert.Equal("payload", Encoding.UTF8.GetString(retry.ToArray()));
		Assert.Equal(session.Entries.ToArray(), ((IEnumerable)session.Entries).Cast<UnityCatalogEntry>().ToArray());
	}

	[Fact]
	public void BorrowedPrototypeCopiesWideRangesWithoutChangingTypeOrOwnerAndRejectsInvalidRanges()
	{
		using SerializedFile file = ReadTable(UnityFixtureBuilder.SerializedTextAsset("row", "payload")); ObjectInfo original = file.Objects[0];
		ObjectInfo copy = original.WithDataRange(9, original.DataOffset, original.DataSize);
		Assert.Equal(9, copy.FileID); Assert.Equal(original.TypeID, copy.TypeID); Assert.Same(original.Type, copy.Type); Assert.Same(original.BorrowedSource, copy.BorrowedSource);
		Assert.Equal(original.DataSize, copy.DataSize); Assert.Equal(1, original.FileID);
		Assert.Throws<ArgumentException>(() => original.WithDataRange(0, 0, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => original.WithDataRange(1, -1, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => original.WithDataRange(1, 0, -1));
		Assert.Throws<ArgumentOutOfRangeException>(() => original.WithDataRange(1, original.BorrowedSource!.Length + 1, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => original.WithDataRange(1, original.BorrowedSource!.Length, 1));
		ObjectInfo eager = new() { ObjectData = [1, 2] }; Assert.Throws<InvalidOperationException>(() => eager.WithDataRange(1, 0, 1));
	}

	[Theory]
	[InlineData("texture")]
	[InlineData("text")]
	[InlineData("movie")]
	public unsafe void CompactGraphAcceptsMatureEagerFallbackRowsAndAbiMetadataWithoutRegrouping(string family)
	{
		byte[] bytes = family switch
		{
			"texture" => UnityFixtureBuilder.SerializedInlineTexture("eager", Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 4).SelectMany(pixel => pixel).ToArray()),
			"text" => UnityFixtureBuilder.SerializedTextAsset("eager", "payload"),
			_ => UnityFixtureBuilder.SerializedMovieTexture("eager", new byte[] { 1, 2, 3 }),
		};
		string path = Path.Combine(Temp.NewDirectory(), "eager.assets"); File.WriteAllBytes(path, bytes);
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
		using SerializedFile file = ReadTable(bytes); GameBundle root = new();
		GameAssetFactory mature = new(new BaseManager(static _ => { }));
		var collection = root.AddCollectionFromSerializedFile(file, mature);
		UnitySource.LazyAssetFactory factory = new(mature, compact: true);
		UnitySession session = UnitySession.FromGraph(factory.BuildCompactGraph(root, file, [collection], [], []));
		Assert.Equal(legacy.Entries, session.Entries); Assert.Equal(Assert.Single(legacy.Entries), session.Entries[0]);
		UnitySessionAbi abi = new(_ => session, 1); ulong token; int count;
		fixed (char* input = path) Assert.Equal(UnityAbi.Ok, abi.Open((ushort*)input, path.Length, &token, &count));
		try
		{
			UnityEntry descriptor = new() { StructSize = UnityEntry.Size }; char* name = stackalloc char[5];
			Assert.Equal(UnityAbi.Ok, abi.GetEntry(token, 0, &descriptor, (ushort*)name, 5)); Assert.Equal("eager", new string(name, 0, 5));
			using MemoryStream expected = new(), actual = new(); Assert.True(legacy.Export(0, expected)); Assert.True(session.Export(0, actual)); Assert.Equal(expected.ToArray(), actual.ToArray());
			collection.ReplaceAsset(new NonResourceIdentity(new(collection, 1, collection.TryGetAsset(1)!.ClassID)));
			using MemoryStream rejected = new(); Assert.False(session.Export(0, rejected)); Assert.Equal(0, rejected.Length);
		}
		finally { Assert.Equal(UnityAbi.Ok, abi.Close(token)); }
	}

	[Fact]
	public void EmptyResourcesAndMissingAudioKeepFullLegacyVisibilityAndExtensionRules()
	{
		foreach (byte[] bytes in new[] { UnityFixtureBuilder.SerializedFont("empty", []), UnityFixtureBuilder.SerializedStreamedFsbAudioClip("absent", "absent.resource", 128) })
		{
			string path = Path.Combine(Temp.NewDirectory(), "visibility.assets"); File.WriteAllBytes(path, bytes);
			using UnitySession compact = UnitySession.Open(path);
			using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
			Assert.Equal(legacy.Entries, compact.Entries);
			foreach (UnityCatalogEntry entry in legacy.Entries) Assert.Equal("bin", entry.OutputExtension);
		}
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public void RetiringPathPassPreservesEachOwnedPathMetadataFieldInFrozenRows(int field)
	{
		string path = Path.Combine(Temp.NewDirectory(), "paths.assets"); File.WriteAllBytes(path, UnityFixtureBuilder.SerializedRepeatedTextAssets(2));
		using UnitySession session = UnitySession.Open(path); GameBundle root = Root(session); var collection = Assert.Single(root.FetchAssetCollections());
		IUnityObjectBase before = collection.TryGetAsset(1)!;
		IUnityObjectBase? main = field == 3 ? collection.TryGetAsset(2) : null;
		if (field == 0) before.OriginalPath = "original";
		if (field == 1) before.OverridePath = "override";
		if (field == 2) before.AssetBundleName = "bundle";
		if (field == 3) before.MainAsset = main;
		IUnityGraph graph = (IUnityGraph)typeof(UnitySession).GetField("graph", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
		object factory = graph.GetType().GetField("factory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph)!;
		factory.GetType().GetMethod("EndCompactOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(factory, [true]);
		Assert.Equal(0, ResolvedIdentityCount(root));
		IUnityObjectBase after = collection.TryGetAsset(1)!; Assert.NotSame(before, after); Assert.Same(after, collection.TryGetAsset(1));
		Assert.Equal(field == 0 ? "original" : null, after.OriginalPath);
		Assert.Equal(field == 1 ? "override" : null, after.OverridePath);
		Assert.Equal(field == 2 ? "bundle" : null, after.AssetBundleName); Assert.Same(main, after.MainAsset);
		using MemoryStream destination = new(); Assert.True(session.Export(0, destination)); Assert.Equal("payload", Encoding.UTF8.GetString(destination.ToArray()));
		IEnumerator enumerator = ((IEnumerable)session.Entries).GetEnumerator(); int count = 0;
		while (enumerator.MoveNext()) { Assert.IsType<UnityCatalogEntry>(enumerator.Current); count++; } Assert.Equal(2, count);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AnUnreadableOrWrongKindAtlasNeverMakesASpriteVisibleInEitherGraph(bool wrongKind)
	{
		byte[] pixels = Enumerable.Repeat(new byte[] { 1, 2, 3, 255 }, 4).SelectMany(p => p).ToArray();
		(byte[] sprite, byte[] atlas) = UnityFixtureBuilder.SerializedAtlasSprite("hero", pixels, 2, 2, pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 7, 7);
		string directory = Temp.NewDirectory(), path = Path.Combine(directory, "sprite.assets"); File.WriteAllBytes(path, sprite);
		File.WriteAllBytes(Path.Combine(directory, UnityFixtureBuilder.AtlasDependencyName), atlas);
		FileBase Read(string input)
		{
			SerializedFile file = (SerializedFile)UnitySource.ReadFile(input);
			if (Path.GetFileName(input) == UnityFixtureBuilder.AtlasDependencyName)
			{
				ObjectInfo[] objects = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
				if (wrongKind) { objects[1].TypeID = 28; objects[1].ObjectData = objects[0].ObjectData; }
				else objects[1].ObjectData = [1];
			}
			return file;
		}
		using UnitySession compact = UnitySession.FromGraph(UnitySource.OpenGraph(path, Read, compact: true));
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, Read));
		Assert.Equal(legacy.Entries, compact.Entries); Assert.DoesNotContain(compact.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void OwnerShrinkingAfterTableValidationDoesNotRetainPartialScratchOrDeserializeBodies(bool outsideRange)
	{
		using MemoryStream physical = new(); physical.Write(UnityFixtureBuilder.SerializedTextAsset("changed", "payload")); physical.Position = 0;
		using SmartStream input = SmartStream.CreateOwned(physical);
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "changed.assets", "changed.assets", FileReadMode.MetadataOnly);
		if (outsideRange)
		{
			ObjectInfo[] objects = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
			object malformed = objects[0];
			typeof(ObjectInfo).GetProperty("DataOffset")!.SetValue(malformed, physical.Length + 1); objects[0] = (ObjectInfo)malformed;
		}
		else physical.SetLength(0);
		TrackingReader full = new(); GameBundle root = new(); UnitySource.LazyAssetFactory factory = new(full, compact: true);
		var collection = root.AddCollectionFromSerializedFile(file, factory);
		using UnitySession session = UnitySession.FromGraph(factory.BuildCompactGraph(root, file, [collection], [], []));
		Assert.Empty(session.Entries); Assert.Empty(full.Watched); Assert.True(input.CanRead);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void CrossFileSpriteMetadataAndExportsCrossCompactAndWholeTableLegacyRepresentations(bool atlas, bool selectedEager)
	{
		byte[] pixels = Enumerable.Repeat(new byte[] { 10, 20, 30, 255 }, 4).SelectMany(pixel => pixel).ToArray();
		string dependencyName = atlas ? UnityFixtureBuilder.AtlasDependencyName : "texture.assets";
		byte[] selectedBytes, dependencyBytes;
		if (atlas) (selectedBytes, dependencyBytes) = UnityFixtureBuilder.SerializedAtlasSprite("hero", pixels, 2, 2, pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 7, 7);
		else
		{
			selectedBytes = UnityFixtureBuilder.SerializedSprite("hero", pixels[..4], 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, externalTexture: true, dependencies: [dependencyName]);
			dependencyBytes = UnityFixtureBuilder.SerializedInlineTexture("external", pixels[..4]);
		}
		string directory = Temp.NewDirectory(), path = Path.Combine(directory, "sprite.assets");
		File.WriteAllBytes(path, selectedBytes); File.WriteAllBytes(Path.Combine(directory, dependencyName), dependencyBytes);
		FileBase Read(string input)
		{
			SerializedFile file = (SerializedFile)UnitySource.ReadFile(input);
			if ((Path.GetFileName(input) == "sprite.assets") == selectedEager)
			{
				ObjectInfo[] objects = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
				objects[0].ObjectData = objects[0].ObjectData;
			}
			return file;
		}
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, Read));
		using UnitySession compact = UnitySession.FromGraph(UnitySource.OpenGraph(path, Read, compact: true));
		Assert.Equal(2, legacy.Entries.Count); Assert.Equal(UnityEntryKind.Sprite, legacy.Entries[1].Kind);
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		for (int pass = 0; pass < 2; pass++)
			for (int index = 0; index < 2; index++)
			{
				using MemoryStream expected = new(), actual = new(); Assert.True(legacy.Export(index, expected), $"Legacy export index={index} pass={pass}"); Assert.True(compact.Export(index, actual), $"Compact export index={index} pass={pass}");
				Assert.Equal(expected.ToArray(), actual.ToArray()); Assert.Equal(0, ResolvedIdentityCount(Root(compact)));
			}
	}

	private sealed class RegistrationFault
	{
		public bool Fail = true;
		public readonly OutOfMemoryException Error = new("resolver registration allocation sentinel");
	}
	private sealed class RegistrationComparer<T>(RegistrationFault fault) : IEqualityComparer<T>
	{
		public bool Equals(T? left, T? right) => ReferenceEquals(left, right);
		public int GetHashCode(T value) => fault.Fail ? throw fault.Error : RuntimeHelpers.GetHashCode(value!);
	}
	[Fact]
	public void ResolverRegistrationFailureCannotPublishAnUntrackedIdentityAndRetryRetiresItsIdentity()
	{
		string path = Path.Combine(Temp.NewDirectory(), "fault.assets"); File.WriteAllBytes(path, UnityFixtureBuilder.SerializedTextAsset("row", "payload"));
		TrackingReader reader = new();
		using UnitySession session = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null, reader, compact: true));
		GameBundle root = Root(session);
		IUnityGraph graph = (IUnityGraph)typeof(UnitySession).GetField("graph", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
		object factory = graph.GetType().GetField("factory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph)!;
		FieldInfo touched = factory.GetType().GetField("touchedResolvers", BindingFlags.Instance | BindingFlags.NonPublic)!;
		RegistrationFault fault = new(); Type comparerType = typeof(RegistrationComparer<>).MakeGenericType(touched.FieldType.GetGenericArguments());
		object comparer = Activator.CreateInstance(comparerType, [fault])!;
		touched.SetValue(factory, Activator.CreateInstance(touched.FieldType, [comparer]));
		using MemoryStream failed = new(); Assert.Same(fault.Error, Assert.Throws<OutOfMemoryException>(() => session.Export(0, failed)));
		Assert.Equal(0, failed.Length); int afterFailure = ResolvedIdentityCount(root);
		fault.Fail = false;
		using MemoryStream retry = new(); Assert.True(session.Export(0, retry)); Assert.Equal("payload", Encoding.UTF8.GetString(retry.ToArray()));
		int afterRetry = ResolvedIdentityCount(root); output.WriteLine($"Registration-failure cache={afterFailure}; after successful retry cache={afterRetry}.");
		Assert.Equal(0, afterFailure); Assert.Equal(0, afterRetry);
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
		Assert.NotEmpty(reader.Watched); Assert.All(reader.Watched, weak => Assert.False(weak.IsAlive)); GC.KeepAlive(session);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(1)]
	[InlineData(2)]
	public void InvalidOrMissingSpriteTextureFileReferencesAreRejectedByBothRepresentations(int file)
	{
		string path = Path.Combine(Temp.NewDirectory(), "bad-reference.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedSprite("hero", new byte[] { 1, 2, 3, 255 }, 1, 1,
			0, 0, 1, 1, 0, 0, 1, 1, 0, 0, textureFileOverride: file, dependencies: ["absent.assets"]));
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
		using UnitySession compact = UnitySession.Open(path);
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		Assert.Equal(UnityEntryKind.Texture, Assert.Single(compact.Entries).Kind);
		using MemoryStream expected = new(), actual = new(); Assert.True(legacy.Export(0, expected)); Assert.True(compact.Export(0, actual)); Assert.Equal(expected.ToArray(), actual.ToArray());
	}

	[Theory]
	[InlineData(false, 280000)]
	[InlineData(true, 380000)]
	public void KnownTableCardinalityBuildsFinalResourceAndSpriteStorageWithinOneAllocationBudget(bool sprites, int budget)
	{
		const int count = 4096;
		byte[] warmBytes = sprites ? UnityFixtureBuilder.SerializedRepeatedSprites(1) : UnityFixtureBuilder.SerializedRepeatedTextAssets(1);
		byte[] bytes = sprites ? UnityFixtureBuilder.SerializedRepeatedSprites(count) : UnityFixtureBuilder.SerializedRepeatedTextAssets(count);
		using SerializedFile warm = ReadTable(warmBytes); using SerializedFile file = ReadTable(bytes);
		GameBundle root = new(); UnitySource.LazyAssetFactory factory = new(new GameAssetFactory(new BaseManager(static _ => { })), compact: true);
		root.AddCollectionFromSerializedFile(warm, factory);
		long start = GC.GetAllocatedBytesForCurrentThread();
		var collection = root.AddCollectionFromSerializedFile(file, factory);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
		using UnitySession session = UnitySession.FromGraph(factory.BuildCompactGraph(root, file, [collection], [], []));
		Assert.Equal(count + (sprites ? 1 : 0), session.Entries.Count);
		Assert.Equal(Enumerable.Range(1, session.Entries.Count).Select(path => (long)path), session.Entries.Select(entry => entry.PathId));
		Assert.All(session.Entries, entry => Assert.Equal(sprites ? 4 : 7, entry.SourceSize));
		Assert.All(session.Entries.Skip(sprites ? 1 : 0), entry => { Assert.Equal(sprites ? "hero" : "notes", entry.Name); Assert.Equal(sprites ? UnityEntryKind.Sprite : UnityEntryKind.Text, entry.Kind); });
		foreach (int index in new[] { 0, session.Entries.Count / 2, session.Entries.Count - 1, 0 })
		{
			using MemoryStream destination = new(); Assert.True(session.Export(index, destination));
			if (sprites) Assert.Equal(new byte[] { 10, 20, 30, 255 }, PngDecoder.Decode(destination.ToArray()).Rgba);
			else Assert.Equal("payload", Encoding.UTF8.GetString(destination.ToArray()));
		}
		output.WriteLine($"{count} parsed {(sprites ? "Sprite" : "Text")} headers: {allocated} B; fixed allocation bound {budget} B.");
		Assert.True(allocated <= budget, $"Known table cardinality allocated {allocated} B against {budget} B: final metadata storage must not grow and then copy to another array.");
		Type rowType = typeof(UnitySource.LazyAssetFactory).GetNestedType("PayloadRow", BindingFlags.NonPublic)!;
		Assert.Equal(24, (int)typeof(Unsafe).GetMethod("SizeOf")!.MakeGenericMethod(rowType).Invoke(null, null)!);
	}

	private sealed class CompletionAllocationReader(UnitySource.LazyAssetFactory factory) : AssetFactoryBase
	{
		public long Allocated { get; private set; }
		public override bool ReleaseSourceObjectDescriptors => factory.ReleaseSourceObjectDescriptors;
		public override void ValidateObjectTable(AssetRipper.Assets.Collections.AssetCollection collection, SerializedFile file) => factory.ValidateObjectTable(collection, file);
		public override IUnityObjectBase? ReadAsset(AssetInfo info, in ObjectInfo descriptor, IReadOnlyList<SerializedTypeReference> references) => factory.ReadAsset(info, in descriptor, references);
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> references) => factory.ReadAsset(info, data, type, references);
		public override void CompleteCollection(AssetRipper.Assets.Collections.AssetCollection collection)
		{
			long start = GC.GetAllocatedBytesForCurrentThread(); factory.CompleteCollection(collection); Allocated = GC.GetAllocatedBytesForCurrentThread() - start;
		}
	}
	[Fact]
	public void Completing4096OrderedResourceRowsDoesNotAllocateASecondPathAndRowIndex()
	{
		const int count = 4096;
		using SerializedFile warm = ReadTable(UnityFixtureBuilder.SerializedRepeatedTextAssets(1));
		using SerializedFile file = ReadTable(UnityFixtureBuilder.SerializedRepeatedTextAssets(count));
		GameBundle root = new(); UnitySource.LazyAssetFactory factory = new(new GameAssetFactory(new BaseManager(static _ => { })), compact: true);
		CompletionAllocationReader reader = new(factory); root.AddCollectionFromSerializedFile(warm, reader);
		var collection = root.AddCollectionFromSerializedFile(file, reader); long allocated = reader.Allocated;
		using UnitySession session = UnitySession.FromGraph(factory.BuildCompactGraph(root, file, [collection], [], []));
		Assert.Equal(count, session.Entries.Count);
		Assert.Equal(Enumerable.Range(1, count).Select(path => (long)path), session.Entries.Select(entry => entry.PathId));
		Assert.All(session.Entries, entry => { Assert.Equal("notes", entry.Name); Assert.Equal(7, entry.SourceSize); Assert.Equal(UnityEntryKind.Text, entry.Kind); });
		foreach (int index in new[] { 0, count / 2, count - 1, 0 })
		{
			IUnityObjectBase identity = collection.TryGetAsset(index + 1)!; Assert.NotNull(identity); Assert.Same(identity, collection.TryGetAsset(index + 1));
			using MemoryStream destination = new(); Assert.True(session.Export(index, destination)); Assert.Equal("payload", Encoding.UTF8.GetString(destination.ToArray()));
		}
		output.WriteLine($"Completing4096 ordered parsed resources allocated {allocated} B.");
		Assert.True(allocated <= 1024, $"Ordered resource completion allocated {allocated} B; it must not retain an input-scaled duplicate path/row index.");
	}

	[Fact]
	public void UncompletedOrPartiallyFilledLookupCannotExposePhantomRowsOrResourceIdentities()
	{
		using SerializedFile file = ReadTable(UnityFixtureBuilder.SerializedRepeatedTextAssets(2));
		var collection = AssetCreator.CreateCollection(file.Version);
		UnitySource.LazyAssetFactory factory = new(new GameAssetFactory(new BaseManager(static _ => { })), compact: true);
		factory.ValidateObjectTable(collection, file);
		IDictionary tables = (IDictionary)typeof(UnitySource.LazyAssetFactory).GetField("compactCollections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(factory)!;
		var resolver = (AssetRipper.Assets.Collections.IAssetResolver)tables[collection]!;
		Assert.Null(resolver.TryGetAsset(0)); Assert.Null(resolver.TryGetAsset(1)); Assert.Null(collection.AssetResolver);
		ObjectInfo first = file.GetObjectInfo(0); factory.ReadAsset(new(collection, first.FileID, 49), in first, []);
		Assert.Null(resolver.TryGetAsset(0)); Assert.Null(resolver.TryGetAsset(1)); Assert.Null(resolver.TryGetAsset(2));
		Assert.Throws<InvalidDataException>(() => factory.CompleteCollection(collection)); Assert.Null(collection.AssetResolver);
		Assert.Null(resolver.TryGetAsset(1));
		ObjectInfo second = file.GetObjectInfo(1); factory.ReadAsset(new(collection, second.FileID, 49), in second, []);
		Assert.Null(resolver.TryGetAsset(1)); factory.CompleteCollection(collection);
		Assert.Same(resolver, collection.AssetResolver); Assert.Equal(1, resolver.TryGetAsset(1)!.PathID); Assert.Equal(2, resolver.TryGetAsset(2)!.PathID);
		Assert.Null(resolver.TryGetAsset(0)); Assert.Null(resolver.TryGetAsset(3));
		factory.CompleteCollection(collection); Assert.Equal(2, resolver.TryGetAsset(2)!.PathID);
	}
	public static IEnumerable<object[]> OrderedLookupCases()
	{
		yield return [new long[] { long.MinValue, -10, -1, 1, 10, long.MaxValue }];
		yield return [new long[] { long.MaxValue, 10, 1, -1, -10, long.MinValue }];
		yield return [new long[] { -1, long.MaxValue, 1, long.MinValue, -10, 10 }];
		yield return [new long[] { long.MinValue }]; yield return [new long[] { long.MaxValue }];
	}
	[Theory][MemberData(nameof(OrderedLookupCases))]
	public void OrderedAndUnorderedLookupsPreserveSignedLongKeysRowIdentityAndDistinctExportPayloads(long[] ids)
	{
		byte[] bytes = UnityFixtureBuilder.SerializedRepeatedTextAssets(ids.Length);
		FileBase ReadRows(string ignored)
		{
			SerializedFile file = ReadTable(bytes);
			_ = file.Objects; ObjectInfo[] rows = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
			for (int i = 0; i < ids.Length; i++)
			{
				using SerializedFile named = ReadTable(UnityFixtureBuilder.SerializedTextAsset($"row{i}", $"payload{i}"));
				rows[i].FileID = ids[i]; rows[i].ObjectData = named.Objects[0].ObjectData;
			}
			// Rewrite with the mature SDK so the production graph receives only borrowed rows.
			using MemoryStream serialized = new(); file.Write(serialized); file.Dispose(); return ReadTable(serialized.ToArray());
		}
		string path = Path.Combine(Temp.NewDirectory(), "lookup.assets");
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, ReadRows));
		using UnitySession compact = UnitySession.FromGraph(UnitySource.OpenGraph(path, ReadRows, compact: true));
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		Assert.Equal(ids, compact.Entries.Select(entry => entry.PathId));
		var collection = Assert.Single(Root(compact).FetchAssetCollections());
		foreach (long missing in new[] { 0L, -2L, 2L, long.MinValue + 1, long.MaxValue - 1 }) Assert.Null(collection.TryGetAsset(missing));
		for (int i = 0; i < ids.Length; i++)
		{
			Assert.Equal($"row{i}", compact.Entries[i].Name); Assert.Equal(8, compact.Entries[i].SourceSize);
			IUnityObjectBase identity = collection.TryGetAsset(ids[i])!; Assert.Equal(ids[i], identity.PathID); Assert.Same(identity, collection.TryGetAsset(ids[i]));
			using MemoryStream expected = new(), actual = new(); Assert.True(legacy.Export(i, expected)); Assert.True(compact.Export(i, actual));
			Assert.Equal($"payload{i}", Encoding.UTF8.GetString(actual.ToArray())); Assert.Equal(expected.ToArray(), actual.ToArray());
		}
		using MemoryStream retry = new(); Assert.True(compact.Export(0, retry)); Assert.Equal("payload0", Encoding.UTF8.GetString(retry.ToArray()));
	}

	[Theory][InlineData(false)][InlineData(true)]
	public void LookupUsesFilteredResourceOrderAndPreservesInterleavedKindsWithoutReorderingIgnoredRows(bool descending)
	{
		SerializedFileBuilder builder = new() { Generation = (FormatVersion)22, Version = UnityVersion.V_2022, Platform = BuildTarget.StandaloneWin64Player, EndianType = AssetRipper.IO.Endian.EndianType.LittleEndian };
		int[] kinds = [49, 1, 28]; long[] ids = descending ? [4, 100, -4] : [-4, 100, 4];
		for (int i = 0; i < kinds.Length; i++)
		{
			using SerializedFile source = ReadTable(UnityFixtureBuilder.SerializedDefaultNamedClass(kinds[i], $"kind{kinds[i]}"));
			int typeStart = builder.Types.Count; builder.Types.AddRange(source.Types.ToArray());
			ObjectInfo descriptor = source.GetObjectInfo(0); descriptor.FileID = ids[i]; descriptor.SerializedTypeIndex += typeStart;
			descriptor.ObjectData = descriptor.ObjectData; builder.Objects.Add(descriptor);
		}
		using SerializedFile built = builder.Build(); using MemoryStream bytes = new(); built.Write(bytes);
		string path = Path.Combine(Temp.NewDirectory(), "interleaved.assets"); File.WriteAllBytes(path, bytes.ToArray());
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
		using UnitySession compact = UnitySession.Open(path);
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		Assert.Equal(new[] { ids[0], ids[2] }, compact.Entries.Select(row => row.PathId));
		Assert.Equal(new[] { UnityEntryKind.Text, UnityEntryKind.Texture }, compact.Entries.Select(row => row.Kind));
		var collection = Assert.Single(Root(compact).FetchAssetCollections()); Assert.Null(collection.TryGetAsset(100)); Assert.Null(collection.TryGetAsset(0));
		for (int i = 0; i < compact.Entries.Count; i++)
		{
			Assert.Equal(compact.Entries[i].PathId, collection.TryGetAsset(compact.Entries[i].PathId)!.PathID);
			using MemoryStream expected = new(), actual = new(); Assert.True(legacy.Export(i, expected)); Assert.True(compact.Export(i, actual)); Assert.Equal(expected.ToArray(), actual.ToArray());
			if (i == 1) Assert.Equal(new byte[] { 10, 20, 30, 255 }, PngDecoder.Decode(actual.ToArray()).Rgba);
		}
	}

	[Fact]
	public void IncompleteDeclaredCompactTableCannotPublishPhantomDefaultRows()
	{
		using SerializedFile file = ReadTable(UnityFixtureBuilder.SerializedRepeatedTextAssets(2));
		var collection = AssetCreator.CreateCollection(file.Version);
		UnitySource.LazyAssetFactory factory = new(new GameAssetFactory(new BaseManager(static _ => { })), compact: true);
		factory.ValidateObjectTable(collection, file.Objects);
		ObjectInfo first = file.Objects[0]; factory.ReadAsset(new(collection, first.FileID, 49), in first, []);
		Assert.Throws<InvalidDataException>(() => factory.CompleteCollection(collection));
		Assert.Null(collection.AssetResolver); Assert.Empty(collection.Assets);
	}

	[Fact]
	public void StrictlyOrderedNonzeroPathValidationDoesNotAllocateAnEntryPerObject()
	{
		using SerializedFile file = ReadTable(UnityFixtureBuilder.SerializedRepeatedTextAssets(4096));
		UnitySource.LazyAssetFactory factory = new(new GameAssetFactory(new BaseManager(static _ => { })), compact: true);
		factory.ValidateObjectTable([]);
		long start = GC.GetAllocatedBytesForCurrentThread(); factory.ValidateObjectTable(file.Objects);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
		Assert.Equal(Enumerable.Range(1, 4096).Select(value => (long)value), file.Objects.ToArray().Select(row => row.FileID));
		GameBundle root = new(); var collection = root.AddCollectionFromSerializedFile(file, factory);
		using UnitySession session = UnitySession.FromGraph(factory.BuildCompactGraph(root, file, [collection], [], []));
		Assert.Equal(4096, session.Entries.Count); Assert.Equal(Enumerable.Range(1, 4096).Select(value => (long)value), session.Entries.Select(row => row.PathId));
		using MemoryStream destination = new(); Assert.True(session.Export(4095, destination)); Assert.Equal("payload", Encoding.UTF8.GetString(destination.ToArray()));
		output.WriteLine($"4096 strictly ordered nonzero source IDs: validation allocated {allocated} B.");
		Assert.True(allocated <= 1024, $"Ordered source-table validation allocated {allocated} B; an input-scaled identity set must remain a fallback.");
	}

	public static IEnumerable<object[]> PathIdValidationCases()
	{
		(long[] Ids, bool Valid)[] cases =
		[
			([], true), ([1], true), ([-1], true), ([long.MinValue], true), ([long.MaxValue], true),
			([long.MinValue, -1, 1, long.MaxValue], true), ([4, -2, 3], true), ([2, 1], true),
			([0], false), ([-1, 0, 1], false), ([2, 2], false), ([2, 1, 2], false),
			([long.MinValue, 3, long.MinValue], false), ([long.MaxValue, 1, long.MaxValue], false),
		];
		foreach (var item in cases)
			foreach (bool mixed in new[] { false, true }) yield return [item.Ids, item.Valid, mixed];
	}
	[Theory]
	[MemberData(nameof(PathIdValidationCases))]
	public void SourcePathIdValidationKeepsErrorsBorrowedFlagsAndOriginalTableOrder(long[] ids, bool valid, bool mixed)
	{
		using SerializedFile file = ReadTable(UnityFixtureBuilder.SerializedRepeatedTextAssets(ids.Length));
		ObjectInfo[] rows = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
		for (int i = 0; i < rows.Length; i++) rows[i].FileID = ids[i];
		if (mixed && rows.Length > 0) rows[^1].ObjectData = rows[^1].ObjectData;
		TrackingReader reader = new(); UnitySource.LazyAssetFactory factory = new(reader, compact: true);
		if (!valid)
		{
			Assert.Equal("objects", Assert.Throws<ArgumentException>(() => factory.ValidateObjectTable(file.Objects)).ParamName);
			Assert.Empty(reader.Watched); Assert.Equal(ids, rows.Select(row => row.FileID)); return;
		}
		factory.ValidateObjectTable(file.Objects);
		Assert.Equal(!mixed || ids.Length == 0, typeof(UnitySource.LazyAssetFactory).GetField("compactTable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(factory));
		GameBundle root = new(); var collection = root.AddCollectionFromSerializedFile(file, factory);
		using UnitySession session = UnitySession.FromGraph(factory.BuildCompactGraph(root, file, [collection], [], []));
		Assert.Equal(ids, session.Entries.Select(row => row.PathId)); Assert.All(session.Entries, row => { Assert.Equal("notes", row.Name); Assert.Equal(7, row.SourceSize); });
		if (ids.Length > 0) { using MemoryStream output = new(); Assert.True(session.Export(ids.Length - 1, output)); Assert.Equal("payload", Encoding.UTF8.GetString(output.ToArray())); }
	}

	[Fact]
	public void ExactShapeCountingKeepsMetadataNegativeScriptsUnsupportedAndMalformedRowsDistinct()
	{
		(byte[] Bytes, int Count)[] cases =
		[
			(UnityFixtureBuilder.SerializedResourceManagerTextAsset("hero.txt", "hero", "payload", monoBehaviourClassId: -1), 1),
			(UnityFixtureBuilder.SerializedDefaultNamedClass(142, "bundle"), 0),
			(UnityFixtureBuilder.SerializedDefaultNamedClass(1, "ignored"), 0),
			(UnityFixtureBuilder.CorruptSerializedTextAsset(), 0),
		];
		foreach (var item in cases)
		{
			string path = Path.Combine(Temp.NewDirectory(), "shapes.assets"); File.WriteAllBytes(path, item.Bytes);
			using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
			using UnitySession compact = UnitySession.Open(path);
			Assert.Equal(item.Count, compact.Entries.Count); Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
			foreach (UnityCatalogEntry entry in compact.Entries)
			{
				using MemoryStream expected = new(), actual = new(); Assert.True(legacy.Export(entry.Index, expected)); Assert.True(compact.Export(entry.Index, actual)); Assert.Equal(expected.ToArray(), actual.ToArray());
			}
		}
	}

	[Theory]
	[InlineData(1)]
	[InlineData(4)]
	public void MalformedSpriteHeadersFillRowsWithoutExposingUnusedReferenceSlots(int count)
	{
		string path = Path.Combine(Temp.NewDirectory(), "malformed-sprites.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedRepeatedSprites(count, malformedLast: true));
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
		using UnitySession compact = UnitySession.Open(path);
		Assert.Equal(count, compact.Entries.Count); Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		Assert.Equal(Enumerable.Range(1, count).Select(value => (long)value), compact.Entries.Select(row => row.PathId));
		for (int i = 0; i < count; i++)
		{
			using MemoryStream expected = new(), actual = new(); Assert.True(legacy.Export(i, expected)); Assert.True(compact.Export(i, actual)); Assert.Equal(expected.ToArray(), actual.ToArray());
		}
		Assert.Equal(0, ResolvedIdentityCount(Root(compact)));
	}

	[Theory]
	[InlineData(-1, false, 0)]
	[InlineData(1, false, 0)]
	[InlineData(1, true, 0)]
	[InlineData(0, false, 1)]
	[InlineData(0, false, 999)]
	public void InvalidAtlasTextureFileIndicesAndWrongKindAtlasTargetsNeverExposeSprites(int fileId, bool missingDependency, int atlasTarget)
	{
		byte[] pixels = new byte[] { 10, 20, 30, 255 };
		(byte[] sprite, byte[] atlas) = UnityFixtureBuilder.SerializedAtlasSprite("hero", pixels, 1, 1, pixels, 1, 1, 0, 0, 1, 1, 0, 0, 2, 7, 7,
			atlasTextureFileId: fileId, missingTextureDependency: missingDependency, referencedAtlasPath: atlasTarget == 0 ? null : atlasTarget);
		string directory = Temp.NewDirectory(), path = Path.Combine(directory, "sprite.assets");
		File.WriteAllBytes(path, sprite); File.WriteAllBytes(Path.Combine(directory, UnityFixtureBuilder.AtlasDependencyName), atlas);
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
		using UnitySession compact = UnitySession.Open(path);
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		Assert.Single(compact.Entries); Assert.Equal(UnityEntryKind.Texture, compact.Entries[0].Kind);
		using MemoryStream expected = new(), actual = new(); Assert.True(legacy.Export(0, expected)); Assert.True(compact.Export(0, actual)); Assert.Equal(expected.ToArray(), actual.ToArray());
		Assert.Equal(0, ResolvedIdentityCount(Root(compact)));
	}

	[Fact]
	public void AllAtlasSpriteLinksRemainWithinThePriorFixedStorageAllocationBudget()
	{
		const int count = 4096;
		(byte[] bytes, byte[] atlasBytes) = UnityFixtureBuilder.SerializedSpriteLinkCases(Enumerable.Repeat(1, count).ToArray());
		(byte[] warmBytes, _) = UnityFixtureBuilder.SerializedSpriteLinkCases(1);
		using SerializedFile warm = ReadTable(warmBytes, "warm.assets"), file = ReadTable(bytes), atlas = ReadTable(atlasBytes, UnityFixtureBuilder.AtlasDependencyName);
		GameBundle root = new(); UnitySource.LazyAssetFactory factory = new(new GameAssetFactory(new BaseManager(static _ => { })), compact: true);
		root.AddCollectionFromSerializedFile(warm, factory); root.AddCollectionFromSerializedFile(atlas, factory);
		long start = GC.GetAllocatedBytesForCurrentThread(); var collection = root.AddCollectionFromSerializedFile(file, factory);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
		root.InitializeAllDependencyLists();
		using UnitySession session = UnitySession.FromGraph(factory.BuildCompactGraph(root, file, [collection], [], []));
		Assert.Equal(count + 1, session.Entries.Count); Assert.Equal(Enumerable.Range(1, count + 1).Select(i => (long)i), session.Entries.Select(entry => entry.PathId));
		Assert.All(session.Entries.Skip(1), entry => { Assert.Equal(UnityEntryKind.Sprite, entry.Kind); Assert.Equal("hero", entry.Name); Assert.Equal(0, entry.SourceSize); });
		foreach (int index in new[] { 0, count / 2, count, 0 })
		{
			using MemoryStream destination = new(); Assert.True(session.Export(index, destination));
			Assert.Equal(new byte[] { 10, 20, 30, 255 }, PngDecoder.Decode(destination.ToArray()).Rgba); Assert.Equal(0, ResolvedIdentityCount(root));
		}
		output.WriteLine($"4096 all-atlas Sprite headers allocated {allocated} B.");
		// Original fixed64B/link storage measured2,245,688 B; allow only a small fixed setup margin.
		Assert.True(allocated <= 2_250_000, $"All-atlas links allocated {allocated} B; no geometric key-storage growth is permitted.");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void MixedDirectAtlasMissingKeyAndMalformedSpritesKeepSourceOrderAndFullLegacyExports(bool malformedFirst)
	{
		int[] modes = malformedFirst ? [3, 1, 0, 2, 4, 1, 0] : [0, 1, 3, 2, 4, 1, 0];
		(byte[] bytes, byte[] atlas) = UnityFixtureBuilder.SerializedSpriteLinkCases(modes);
		string directory = Temp.NewDirectory(), path = Path.Combine(directory, "mixed.assets"); File.WriteAllBytes(path, bytes);
		File.WriteAllBytes(Path.Combine(directory, UnityFixtureBuilder.AtlasDependencyName), atlas);
		using UnitySession legacy = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null)); using UnitySession compact = UnitySession.Open(path);
		Assert.Equal(legacy.Entries, compact.Entries); Assert.Equal(Hash(legacy.Entries), Hash(compact.Entries));
		Assert.Equal(new long[] { 1 }.Concat(modes.Select((mode, i) => (mode, i)).Where(row => row.mode is 0 or 1).Select(row => (long)row.i + 2)), compact.Entries.Select(entry => entry.PathId));
		Assert.Equal(new long[] { 4 }.Concat(modes.Where(mode => mode is 0 or 1).Select(mode => mode == 0 ? 4L : 0L)), compact.Entries.Select(entry => entry.SourceSize));
		for (int pass = 0; pass < 2; pass++) foreach (UnityCatalogEntry entry in compact.Entries)
		{
			using MemoryStream expected = new(), actual = new(); Assert.True(legacy.Export(entry.Index, expected)); Assert.True(compact.Export(entry.Index, actual));
			Assert.Equal(expected.ToArray(), actual.ToArray()); Assert.Equal(new byte[] { 10, 20, 30, 255 }, PngDecoder.Decode(actual.ToArray()).Rgba); Assert.Equal(0, ResolvedIdentityCount(Root(compact)));
		}
	}

}
