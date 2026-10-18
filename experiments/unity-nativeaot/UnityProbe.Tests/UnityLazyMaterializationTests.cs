using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_128;
using AssetRipper.SourceGenerated.Classes.ClassID_152;
using AssetRipper.SourceGenerated.Classes.ClassID_329;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Streams.Smart;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Classes.ClassID_687078895;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityLazyMaterializationTests
{
	private sealed class FullReader : AssetFactoryBase
	{
		private readonly GameAssetFactory inner = new(new BaseManager(static _ => { }));
		public List<(int ClassId, long PathId)> Reads { get; } = [];
		public Exception? TextureFailure { get; set; }
		public int NullClassId { get; set; } = -1;
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{
			Reads.Add((info.ClassID, info.PathID));
			if (info.ClassID == NullClassId) return null;
			if (info.ClassID == 28 && TextureFailure is { } error) throw error;
			return inner.ReadAsset(info, data, type, references);
		}
	}

	private static readonly byte[] Pixels = Enumerable.Repeat(new byte[] { 10, 20, 30, 255 }, 4).SelectMany(p => p).ToArray();

	private static (string Selected, string Dependency) Fixture()
	{
		(byte[] first, byte[] atlas) = UnityFixtureBuilder.SerializedAtlasSprite(
			"hero", Pixels, 2, 2, Pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 7, 7);
		(byte[] second, _) = UnityFixtureBuilder.SerializedAtlasSprite(
			"background", Pixels, 2, 2, Pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 7, 7);
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "selected.unity3d");
		string dependency = Path.Combine(directory, UnityFixtureBuilder.AtlasDependencyName);
		File.WriteAllBytes(selected, UnityFixtureBuilder.CompressedBundle(("first.assets", first), ("second.assets", second)));
		File.WriteAllBytes(dependency, atlas);
		return (selected, dependency);
	}

	private static IUnityGraph Open(string path, FullReader reader) => UnitySource.OpenGraph(path, readFile: null, fullReader: reader);

	private static void AssertNoFullResourceObjects(IUnityGraph graph)
	{
		FieldInfo rootField = typeof(UnityGraph).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
			.Single(field => field.FieldType == typeof(GameBundle));
		GameBundle root = (GameBundle)rootField.GetValue(graph)!;
		Assert.DoesNotContain(root.FetchAssets(), asset => asset is ISprite or ITexture2D or ISpriteAtlas or ITextAsset or IFont or IMovieTexture or IVideoClip or IAudioClip or IMesh);
	}

	[Fact]
	public void AtlasSpriteOpenAndListingUseOnlyMetadataAndExportReadsOnlyItsDependencyClosure()
	{
		(string selected, string dependency) = Fixture();
		FullReader reader = new();
		IUnityGraph graph = Open(selected, reader);
		using (UnitySession session = UnitySession.FromGraph(graph))
		{
			AssertNoFullResourceObjects(graph);
			Assert.Empty(reader.Reads);
			Assert.Equal(new[] { "local_texture", "hero", "local_texture", "background" }, session.Entries.Select(e => e.Name));
			Assert.Equal(new long[] { 16, 0, 16, 0 }, session.Entries.Select(e => e.SourceSize));
			Assert.Equal(new[] { 0, 0, 1, 1 }, session.Entries.Select(e => e.CollectionIndex));
			Assert.All(session.Entries, e => { Assert.Equal("png", e.OutputExtension); Assert.Equal(string.Empty, e.OriginalPath); });
			using MemoryStream output = new();
			Assert.True(session.Export(1, output));
			(int width, int height, byte[] rgba) = PngDecoder.Decode(output.ToArray());
			Assert.Equal(2, width);
			Assert.Equal(2, height);
			Assert.Equal(Pixels, rgba);
			Assert.Equal(new[] { 213, 687078895, 28 }, reader.Reads.Select(r => r.ClassId));
			AssertNoFullResourceObjects(graph);
		}
		using FileStream firstHandle = new(selected, FileMode.Open, FileAccess.Read, FileShare.None);
		using FileStream secondHandle = new(dependency, FileMode.Open, FileAccess.Read, FileShare.None);
	}

	[Fact]
	public void ADependencyMemoryFailureRestoresEveryPlaceholderAndCanRetry()
	{
		(string selected, _) = Fixture();
		OutOfMemoryException shortage = new("texture sentinel");
		FullReader reader = new() { TextureFailure = shortage };
		IUnityGraph graph = Open(selected, reader);
		using UnitySession session = UnitySession.FromGraph(graph);
		AssertNoFullResourceObjects(graph);
		using MemoryStream failed = new();
		Assert.Same(shortage, Assert.Throws<OutOfMemoryException>(() => session.Export(1, failed)));
		Assert.Empty(failed.ToArray());
		AssertNoFullResourceObjects(graph);
		reader.TextureFailure = null;
		using MemoryStream retry = new();
		Assert.True(session.Export(1, retry));
		Assert.Equal(Pixels, PngDecoder.Decode(retry.ToArray()).Rgba);
		Assert.Equal(new[] { 213, 687078895, 28, 213, 687078895, 28 }, reader.Reads.Select(r => r.ClassId));
		AssertNoFullResourceObjects(graph);
	}
	[Theory]
	[InlineData("texture")]
	[InlineData("text")]
	[InlineData("font")]
	[InlineData("movie")]
	[InlineData("video")]
	[InlineData("videoWithoutExtension")]
	[InlineData("audio")]
	[InlineData("mesh")]
	public void EverySupportedFamilyDefersFullParsingUntilItsOwnExport(string family)
	{
		byte[] payload = [1, 2, 3, 4];
		byte[] bytes = family switch
		{
			"texture" => UnityFixtureBuilder.SerializedInlineTexture("resource", Pixels),
			"text" => UnityFixtureBuilder.SerializedTextAsset("resource", "payload"),
			"font" => UnityFixtureBuilder.SerializedFont("resource", payload),
			"movie" => UnityFixtureBuilder.SerializedMovieTexture("resource", payload),
			"video" => UnityFixtureBuilder.SerializedVideoClip("resource", "movie.mp4", "media.resS", 4),
			"videoWithoutExtension" => UnityFixtureBuilder.SerializedVideoClip("resource", "movie", "media.resS", 4),
			"audio" => UnityFixtureBuilder.SerializedLegacyAudioClip("resource", payload),
			"mesh" => UnityFixtureBuilder.SerializedTriangleMesh("resource"),
			_ => throw new InvalidOperationException(),
		};
		string directory = Temp.NewDirectory();
		string path = Path.Combine(directory, "selected.assets");
		File.WriteAllBytes(path, bytes);
		File.WriteAllBytes(Path.Combine(directory, "media.resS"), payload);
		FullReader reader = new();
		IUnityGraph graph = Open(path, reader);
		using UnitySession session = UnitySession.FromGraph(graph);
		Assert.Equal("resource", Assert.Single(session.Entries).Name);
		Assert.Empty(reader.Reads);
		AssertNoFullResourceObjects(graph);
		using MemoryStream output = new();
		Assert.True(session.Export(0, output));
		Assert.Single(reader.Reads);
		AssertNoFullResourceObjects(graph);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void InlineTextureAndTextBodiesAreNotReadWhileOpeningOrListing(bool text)
	{
		byte[] payload = Enumerable.Repeat((byte)'x', 2 * 1024 * 1024).ToArray();
		byte[] serialized = text
			? UnityFixtureBuilder.SerializedTextAsset("large", System.Text.Encoding.ASCII.GetString(payload))
			: UnityFixtureBuilder.SerializedInlineTexture("large", payload);
		int start = serialized.AsSpan().IndexOf(payload);
		Assert.True(start > 0);
		using PayloadGuard guard = new(serialized, start, payload.Length);
		using SmartStream source = SmartStream.CreateOwned(guard);
		FileBase selected = SchemeReader.ReadFile(source, "guard.assets", "guard.assets", FileReadMode.MetadataOnly);
		FullReader reader = new();
		using UnitySession session = UnitySession.FromGraph(UnitySource.OpenGraph("guard.assets", _ => selected, reader));
		Assert.Equal(payload.LongLength, Assert.Single(session.Entries).SourceSize);
		Assert.Empty(reader.Reads);
		Assert.Equal(0, guard.Attempts);
		guard.Allow = true;
		using MemoryStream output = new();
		Assert.True(session.Export(0, output));
		Assert.True(guard.Attempts > 0);
		if (text) Assert.Equal(payload, output.ToArray());
	}

	private sealed class PayloadGuard(byte[] bytes, int start, int count) : MemoryStream(bytes, false)
	{
		public bool Allow { get; set; }
		public int Attempts { get; private set; }
		private void Check(int requested)
		{
			if (requested > 0 && Position < start + count && Position + requested > start)
			{
				Attempts++;
				if (!Allow) throw new InvalidOperationException("Inline resource body was read before export.");
			}
		}
		public override int Read(byte[] buffer, int offset, int requested) { Check(requested); return base.Read(buffer, offset, requested); }
		public override int Read(Span<byte> buffer) { Check(buffer.Length); return base.Read(buffer); }
		public override int ReadByte() { Check(1); return base.ReadByte(); }
	}	[Fact]
	public void SharedAtlasCatalogueWorkIsBoundedByTheMapAndSpriteCounts()
	{
		(byte[] sprite, byte[] atlas) = UnityFixtureBuilder.SerializedAtlasSprite(
			"sprite", Pixels, 2, 2, Pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 7, 7, leadingAtlasEntries: 2048);
		string directory = Temp.NewDirectory();
		string path = Path.Combine(directory, "selected.unity3d");
		File.WriteAllBytes(Path.Combine(directory, UnityFixtureBuilder.AtlasDependencyName), atlas);
		File.WriteAllBytes(path, UnityFixtureBuilder.CompressedBundle(Enumerable.Range(0, 512).Select(i => ($"sprite{i}.assets", sprite)).ToArray()));
		// Exclude one-time schema/JIT startup from the deterministic managed allocation budget.
		using (UnitySession warm = UnitySession.Open(Path.Combine(directory, UnityFixtureBuilder.AtlasDependencyName))) { Assert.Single(warm.Entries); }
		long before = GC.GetAllocatedBytesForCurrentThread();
		using UnitySession session = UnitySession.Open(path);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.Equal(1024, session.Entries.Count);
		Assert.True(allocated < 80L * 1024 * 1024, $"Shared-atlas metadata allocated {allocated} bytes; repeated full map scans exceed the bounded-work contract.");
		using MemoryStream output = new();
		Assert.True(session.Export(1023, output));
		Assert.Equal(Pixels, PngDecoder.Decode(output.ToArray()).Rgba);
	}	[Fact]
	public void RepeatedTinyTextResourcesReuseSessionTraversalWorkspace()
	{
		string path = Path.Combine(Temp.NewDirectory(), "many.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedRepeatedTextAssets(4096));
		string warmPath = Path.Combine(Temp.NewDirectory(), "warm.assets");
		File.WriteAllBytes(warmPath, UnityFixtureBuilder.SerializedTextAsset("notes", "payload"));
		using (UnitySession warm = UnitySession.Open(warmPath)) { Assert.Single(warm.Entries); }
		long before = GC.GetAllocatedBytesForCurrentThread();
		using UnitySession session = UnitySession.Open(path);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.Equal(4096, session.Entries.Count);
		Assert.All(session.Entries, e => { Assert.Equal("notes", e.Name); Assert.Equal(7, e.SourceSize); });
		Assert.True(allocated < 9L * 1024 * 1024, $"Tiny resources allocated {allocated} bytes; traversal buffers must be reused inside the session.");
	}	[Theory]
	[InlineData(89, 0, 4)]
	[InlineData(115, 1, 0)]
	[InlineData(954905827, 1, 0)]
	[InlineData(662584278, 1, 0)]
	[InlineData(1896753125, 1, 0)]
	[InlineData(1152215463, 1, 0)]
	public void DerivedResourceFamiliesKeepTheirDeclaredNamesAndAbsentFieldDefaults(int classId, int expectedKind, long size)
	{
		string path = Path.Combine(Temp.NewDirectory(), "derived.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedDefaultNamedClass(classId, "derived"));
		FullReader reader = new();
		using UnitySession session = UnitySession.FromGraph(Open(path, reader));
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal((UnityEntryKind)expectedKind, entry.Kind); Assert.Equal("derived", entry.Name); Assert.Equal(size, entry.SourceSize);
		Assert.Empty(reader.Reads);
		using MemoryStream output = new();
		Assert.True(session.Export(0, output));
		if ((UnityEntryKind)expectedKind == UnityEntryKind.Text) Assert.Empty(output.ToArray());
	}

	[Theory]
	[InlineData(28)]
	[InlineData(213)]
	[InlineData(687078895)]
	public void AnUnmaterialisableSelectedObjectOrDependencyLeavesTheSessionRetryable(int nullClassId)
	{
		(string path, _) = Fixture();
		FullReader reader = new() { NullClassId = nullClassId };
		IUnityGraph graph = Open(path, reader);
		using UnitySession session = UnitySession.FromGraph(graph);
		using MemoryStream output = new();
		Assert.False(session.Export(1, output));
		Assert.Empty(output.ToArray());
		AssertNoFullResourceObjects(graph);
		reader.NullClassId = -1;
		Assert.True(session.Export(1, output));
		Assert.Equal(Pixels, PngDecoder.Decode(output.ToArray()).Rgba);
		AssertNoFullResourceObjects(graph);
	}	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void SpriteReferencingAnUnreadableOrWrongKindTextureIsNotCatalogued(bool wrongKind)
	{
		byte[] bytes = UnityFixtureBuilder.SerializedSprite("hero", Pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0);
		using SmartStream input = SmartStream.CreateMemory(bytes);
		var file = (AssetRipper.IO.Files.SerializedFiles.SerializedFile)SchemeReader.ReadFile(input, "selected.assets", "selected.assets");
		ObjectInfo[] objects = (ObjectInfo[])file.GetType().GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
		if (wrongKind) objects[0].TypeID = 1;
		else objects[0].ObjectData = [1];
		FullReader reader = new();
		using UnitySession session = UnitySession.FromGraph(UnitySource.OpenGraph("selected.assets", _ => file, reader));
		Assert.Empty(session.Entries); Assert.Empty(reader.Reads);
	}	[Fact]
	public void PatchVersionSpriteUsesTheMatureNextBuildLayoutWithoutReadingTexturePixelsAtOpen()
	{
		byte[] bytes = UnityFixtureBuilder.SerializedSprite("patched", Pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0,
			downscale: 1f, layoutVersion: "5.4.2f1", headerVersion: "5.4.1p1");
		byte[] expected;
		using (SmartStream eagerInput = SmartStream.CreateMemory(bytes))
		using (var file = (AssetRipper.IO.Files.SerializedFiles.SerializedFile)SchemeReader.ReadFile(eagerInput, "selected.assets", "selected.assets"))
		using (GameBundle root = new())
		{
			root.AddCollectionFromSerializedFile(file, new GameAssetFactory(new BaseManager(static _ => { })));
			root.InitializeAllDependencyLists();
			Assert.True(root.FetchAssets().Any(a => a is ISprite), string.Join(";", root.FetchAssets().Select(a => a.GetType().FullName + "=" + a.Collection.Version)));
			Assert.True(root.FetchAssets().Any(a => a is ITexture2D), string.Join(";", root.FetchAssets().Select(a => a.GetType().FullName)));
			ISprite fullSprite = root.FetchAssets().OfType<ISprite>().Single();
			Assert.True(fullSprite.RD.Texture.PathID == 1 && fullSprite.RD.Texture.FileID == 0, $"{fullSprite.GetType().Name} pointer={fullSprite.RD.Texture.FileID}/{fullSprite.RD.Texture.PathID}");
			IProbeResource sprite = Assert.Single(UnitySource.Wrap(root.FetchAssets()).OfType<IProbeResource>(), p => p.Kind == UnityEntryKind.Sprite);
			expected = sprite.ExportData()!;
			Assert.Equal(Pixels, PngDecoder.Decode(expected).Rgba);
		}
		int start = bytes.AsSpan().IndexOf(Pixels);
		Assert.True(start > 0);
		using PayloadGuard guard = new(bytes, start, Pixels.Length);
		using SmartStream input = SmartStream.CreateOwned(guard);
		var selected = SchemeReader.ReadFile(input, "selected.assets", "selected.assets", FileReadMode.MetadataOnly);
		FullReader reader = new();
		IUnityGraph graph = UnitySource.OpenGraph("selected.assets", _ => selected, reader);
		using UnitySession session = UnitySession.FromGraph(graph);
		UnityCatalogEntry entry = Assert.Single(session.Entries, e => e.Kind == UnityEntryKind.Sprite);
		Assert.Equal("patched", entry.Name); Assert.Equal(16, entry.SourceSize); Assert.Equal("png", entry.OutputExtension);
		Assert.Empty(reader.Reads); Assert.Equal(0, guard.Attempts); AssertNoFullResourceObjects(graph);
		guard.Allow = true;
		using MemoryStream output = new(); Assert.True(session.Export(entry.Index, output));
		Assert.Equal(expected, output.ToArray()); AssertNoFullResourceObjects(graph);
	}
	[Fact]
	public void BuiltinPatchFilesKeepTheMatureNominalLayoutPreference()
	{
		byte[] bytes = UnityFixtureBuilder.SerializedSprite("builtin", Pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 0, 0,
			layoutVersion: "5.4.2f1", headerVersion: "5.4.1p1");
		string path = Path.Combine(Temp.NewDirectory(), "unity_builtin_extra");
		File.WriteAllBytes(path, bytes);
		using (SmartStream eagerInput = SmartStream.CreateMemory(bytes))
		using (var file = (AssetRipper.IO.Files.SerializedFiles.SerializedFile)SchemeReader.ReadFile(eagerInput, path, "unity_builtin_extra"))
		using (GameBundle root = new())
		{
			root.AddCollectionFromSerializedFile(file, new GameAssetFactory(new BaseManager(static _ => { })));
			root.InitializeAllDependencyLists();
			Assert.DoesNotContain(UnitySource.Wrap(root.FetchAssets()).OfType<IProbeResource>(), p => p.Kind == UnityEntryKind.Sprite);
		}
		FullReader reader = new();
		using UnitySession session = UnitySession.FromGraph(Open(path, reader));
		Assert.Equal(UnityEntryKind.Texture, Assert.Single(session.Entries).Kind);
		Assert.Empty(reader.Reads);
	}}
