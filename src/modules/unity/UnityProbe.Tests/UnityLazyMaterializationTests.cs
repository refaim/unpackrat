using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
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
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
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

	private static readonly byte[] Pixels = [10, 20, 30, 255];
	private static byte[] PatchTexture(string name)
	{
		byte[] bytes = UnityFixtureBuilder.SerializedVersionedTexture(name, UnityVersion.Parse("5.4.2f1"), mipmapped: false);
		int versionOffset = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes("5.4.2f1"));
		Assert.True(versionOffset >= 0);
		Encoding.ASCII.GetBytes("5.4.1p1").CopyTo(bytes, versionOffset);
		return bytes;
	}



	private static (string Selected, string Dependency) Fixture()
	{
		byte[] first = UnityFixtureBuilder.SerializedInlineTexture("first", Pixels, dependencies: ["dependency.assets"]);
		byte[] second = UnityFixtureBuilder.SerializedInlineTexture("second", Pixels, dependencies: ["dependency.assets"]);
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "selected.unity3d");
		string dependency = Path.Combine(directory, "dependency.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.CompressedBundle(("first.assets", first), ("second.assets", second)));
		File.WriteAllBytes(dependency, UnityFixtureBuilder.SerializedInlineTexture("dependency", Pixels));
		return (selected, dependency);
	}

	private static IUnityGraph Open(string path, FullReader reader) => UnitySource.OpenGraph(path, readFile: null, fullReader: reader);

	private static void AssertNoFullResourceObjects(IUnityGraph graph)
	{
		FieldInfo rootField = graph.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
			.Single(field => field.FieldType == typeof(GameBundle));
		GameBundle root = (GameBundle)rootField.GetValue(graph)!;
		Assert.DoesNotContain(root.FetchAssets(), asset => asset is ITexture2D or ITextAsset or IFont or IMovieTexture or IVideoClip or IAudioClip or IMesh);
	}

	private static GameBundle Root(UnitySession session)
	{
		FieldInfo graphField = typeof(UnitySession).GetField("graph", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("UnitySession graph field is missing.");
		IUnityGraph graph = (IUnityGraph)graphField.GetValue(session)!;
		FieldInfo rootField = graph.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
			.Single(field => field.FieldType == typeof(GameBundle));
		return (GameBundle)rootField.GetValue(graph)!;
	}

	private static int ResolvedIdentityCount(GameBundle root)
	{
		const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		object? collections = root.GetType().GetProperty("Collections", all)?.GetValue(root)
			?? root.GetType().GetField("collections", all)?.GetValue(root)
			?? root.GetType().GetField("m_collections", all)?.GetValue(root);
		if (collections is not System.Collections.IEnumerable enumerable) return 0;
		int count = 0;
		foreach (object collection in enumerable)
		{
			object? resolver = collection.GetType().GetProperty("AssetResolver", all)?.GetValue(collection)
				?? collection.GetType().GetField("AssetResolver", all)?.GetValue(collection)
				?? collection.GetType().GetField("assetResolver", all)?.GetValue(collection);
			if (resolver is null) continue;
			if (resolver.GetType().GetField("identities", all)?.GetValue(resolver) is System.Collections.ICollection identities)
				count += identities.Count;
		}
		return count;
	}

	[Fact]
	public void SelectedTextureOpenAndListingUseOnlyMetadataAndExportReadsOnlyItsOwnBody()
	{
		(string selected, string dependency) = Fixture();
		FullReader reader = new();
		IUnityGraph graph = Open(selected, reader);
		using (UnitySession session = UnitySession.FromGraph(graph))
		{
			AssertNoFullResourceObjects(graph);
			Assert.Empty(reader.Reads);
			Assert.Equal(new[] { "first", "second" }, session.Entries.Select(e => e.Name));
			Assert.Equal(new long[] { 4, 4 }, session.Entries.Select(e => e.SourceSize));
			Assert.Equal(new[] { 0, 1 }, session.Entries.Select(e => e.CollectionIndex));
			Assert.All(session.Entries, e => { Assert.Equal("png", e.OutputExtension); Assert.Equal(string.Empty, e.OriginalPath); });
			using MemoryStream output = new();
			Assert.True(session.Export(1, output));
			(int width, int height, byte[] rgba) = PngDecoder.Decode(output.ToArray());
			Assert.Equal(1, width);
			Assert.Equal(1, height);
			Assert.Equal(Pixels, rgba);
			Assert.Equal(new[] { 28 }, reader.Reads.Select(r => r.ClassId));
			AssertNoFullResourceObjects(graph);
		}
		using FileStream firstHandle = new(selected, FileMode.Open, FileAccess.Read, FileShare.None);
		using FileStream secondHandle = new(dependency, FileMode.Open, FileAccess.Read, FileShare.None);
	}

	[Fact]
	public void ATextureMemoryFailureRestoresEveryPlaceholderAndCanRetry()
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
		Assert.Equal(new[] { 28, 28 }, reader.Reads.Select(r => r.ClassId));
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
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public void InlineTextureAndTextBodiesAreNotReadWhileOpeningOrListing(bool text, bool compact)
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
		using UnitySession session = UnitySession.FromGraph(UnitySource.OpenGraph("guard.assets", _ => selected, reader, compact));
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
	}


	[Fact]
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
	}

	[Theory]
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
		using UnitySession compact = UnitySession.Open(path);
		Assert.Equal(session.Entries, compact.Entries);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal((UnityEntryKind)expectedKind, entry.Kind); Assert.Equal("derived", entry.Name); Assert.Equal(size, entry.SourceSize);
		Assert.Empty(reader.Reads);
		using MemoryStream output = new();
		Assert.True(session.Export(0, output));
		using MemoryStream compactOutput = new(); Assert.True(compact.Export(0, compactOutput)); Assert.Equal(output.ToArray(), compactOutput.ToArray());
		if ((UnityEntryKind)expectedKind == UnityEntryKind.Text) Assert.Empty(output.ToArray());
	}

	[Theory]
	[InlineData(28)]
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
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PatchVersionTextureUsesTheMatureNextBuildLayoutWithoutReadingPixelsAtOpen(bool compact)
	{
		byte[] bytes = PatchTexture("patched");
		byte[] expected;
		using (SmartStream eagerInput = SmartStream.CreateMemory(bytes))
		using (var file = (AssetRipper.IO.Files.SerializedFiles.SerializedFile)SchemeReader.ReadFile(eagerInput, "selected.assets", "selected.assets"))
		using (GameBundle root = new())
		{
			root.AddCollectionFromSerializedFile(file, new GameAssetFactory(new BaseManager(static _ => { })));
			root.InitializeAllDependencyLists();
			IProbeTexture texture = Assert.Single(UnitySource.Wrap(root.FetchAssets()).OfType<IProbeTexture>());
			expected = Assert.IsType<byte[]>(texture.RenderPng());
			Assert.Equal(Enumerable.Repeat((byte)0xA5, 128).ToArray(), PngDecoder.Decode(expected).Rgba);
		}
		byte[] pixels = Enumerable.Repeat((byte)0xA5, 128).ToArray();
		int start = bytes.AsSpan().IndexOf(pixels);
		Assert.True(start > 0);
		using PayloadGuard guard = new(bytes, start, pixels.Length);
		using SmartStream input = SmartStream.CreateOwned(guard);
		var selected = SchemeReader.ReadFile(input, "selected.assets", "selected.assets",
			compact ? FileReadMode.CompactMetadataOnly : FileReadMode.MetadataOnly);
		FullReader reader = new();
		IUnityGraph graph = UnitySource.OpenGraph("selected.assets", _ => selected, reader, compact: compact);
		using UnitySession session = UnitySession.FromGraph(graph);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Texture, entry.Kind);
		Assert.Equal("patched", entry.Name); Assert.Equal(128, entry.SourceSize); Assert.Equal("png", entry.OutputExtension);
		Assert.Empty(reader.Reads); Assert.Equal(0, guard.Attempts); AssertNoFullResourceObjects(graph);
		guard.Allow = true;
		using MemoryStream output = new(); Assert.True(session.Export(entry.Index, output));
		Assert.Equal(expected, output.ToArray()); AssertNoFullResourceObjects(graph);
	}

	[Fact]
	public void BuiltinPatchFilesKeepTheMatureNominalLayoutPreference()
	{
		string path = Path.Combine(Temp.NewDirectory(), "unity_builtin_extra");
		File.WriteAllBytes(path, PatchTexture("builtin"));
		FullReader reader = new();
		using UnitySession session = UnitySession.FromGraph(Open(path, reader));
		UnityCatalogEntry texture = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Texture, texture.Kind);
		Assert.Equal("builtin", texture.Name);
		Assert.Empty(reader.Reads);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PatchFontWithANewerNonzeroTailUsesTheNextLayoutWithoutReadingItsBodyAtOpen(bool compact)
	{
		UnityVersion nominal = UnityVersion.Parse("5.6.4p1");
		UnityVersion next = UnityVersion.Parse("5.6.5f1");
		byte[] fontBytes = [0x4F, 0x54, 0x54, 0x4F, 0x12, 0x34, 0x56, 0x78];
		byte[] bytes = UnityFixtureBuilder.SerializedVersionedFont("patched-font", fontBytes, next, legacyBounds: true);
		int versionOffset = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(next.ToString()));
		Assert.True(versionOffset >= 0);
		Assert.Equal(next.ToString().Length, nominal.ToString().Length);
		Encoding.ASCII.GetBytes(nominal.ToString()).CopyTo(bytes, versionOffset);
		int payloadOffset = bytes.AsSpan().IndexOf(fontBytes);
		Assert.True(payloadOffset > 0);

		// The extension needs only a four-byte font signature. The rest of the embedded font
		// remains unread until export, including during the nominal-to-next layout retry.
		using PayloadGuard guard = new(bytes, payloadOffset + 4, fontBytes.Length - 4);
		using SmartStream input = SmartStream.CreateOwned(guard);
		var selected = (AssetRipper.IO.Files.SerializedFiles.SerializedFile)SchemeReader.ReadFile(input,
			"patched-font.assets", "patched-font.assets", compact ? FileReadMode.CompactMetadataOnly : FileReadMode.MetadataOnly);
		Assert.True(TypeTreeNodeStruct.TryMakeFromTpk(ClassIDType.Font, nominal, out var nominalTree, out _));
		Assert.True(TypeTreeNodeStruct.TryMakeFromTpk(ClassIDType.Font, next, out var nextTree, out _));
		var nominalTemplate = UnityProjection.Template(nominalTree);
		var nextTemplate = UnityProjection.Template(nextTree);
		ObjectInfo descriptor = selected.GetObjectInfo(0);
		using (Stream body = descriptor.OpenDataStream())
			Assert.Throws<InvalidDataException>(() => UnityProjection.Read(body, nominalTemplate, false, validateLayout: true));
		using (Stream body = descriptor.OpenDataStream())
		{
			UnityProjection fields = UnityProjection.Read(body, nextTemplate, false, validateLayout: true);
			Assert.Equal("patched-font", fields.Text("m_Name"));
			Assert.Equal(fontBytes.Length, UnitySource.LazyAssetFactory.Range(fields, "m_FontData").Length);
		}
		Assert.Equal(0, guard.Attempts);

		FullReader reader = new();
		IUnityGraph graph = UnitySource.OpenGraph("patched-font.assets", _ => selected, reader, compact: compact);
		using UnitySession session = UnitySession.FromGraph(graph);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Font, entry.Kind);
		Assert.Equal("patched-font", entry.Name);
		Assert.Equal(fontBytes.Length, entry.SourceSize);
		Assert.Equal("otf", entry.OutputExtension);
		Assert.Empty(reader.Reads);
		Assert.Equal(0, guard.Attempts);
		guard.Allow = true;
		for (int pass = 0; pass < 2; pass++)
		{
			using MemoryStream output = new();
			Assert.True(session.Export(entry.Index, output));
			Assert.Equal(fontBytes, output.ToArray());
			AssertNoFullResourceObjects(graph);
		}
		Assert.Equal(new[] { 128, 128 }, reader.Reads.Select(row => row.ClassId));
	}
}
