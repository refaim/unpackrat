using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
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

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityDescriptorRetentionTests
{
	private static ObjectInfo[] Table(SerializedFile file) => (ObjectInfo[])typeof(SerializedFile)
		.GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
	private static SerializedFile Read(SmartStream input, FileReadMode mode)
		=> (SerializedFile)SchemeReader.ReadFile(input, "selected.assets", "selected.assets", mode);

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static (UnitySession Session, WeakReference Table) OpenAndWatch(SerializedFile file, FullReader reader)
	{
		WeakReference table = new(Table(file));
		return (UnitySession.FromGraph(UnitySource.OpenGraph("selected.assets", _ => file, reader)), table);
	}

	[Fact]
	public void SourceDescriptorArrayIsCollectibleWhileItsLazySessionAndPayloadOwnerRemainAlive()
	{
		const int count = 4096;
		TrackedStream physical = new(UnityFixtureBuilder.SerializedRepeatedTextAssets(count));
		using SmartStream input = SmartStream.CreateOwned(physical);
		using SerializedFile file = Read(input, FileReadMode.MetadataOnly);
		Assert.Equal(count, file.Objects.Length);
		FullReader reader = new();
		var opened = OpenAndWatch(file, reader);
		using UnitySession session = opened.Session;
		Assert.True(file.Objects.IsEmpty, "The lazy graph must not retain a second source ObjectInfo table.");
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
		Assert.False(opened.Table.IsAlive);
		Assert.False(physical.Closed);
		Assert.Empty(reader.Reads);
		Assert.Equal(count, session.Entries.Count);
		Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), session.Entries.Select(e => e.PathId));
		Assert.All(session.Entries, e => { Assert.Equal("notes", e.Name); Assert.Equal(7, e.SourceSize); });
		foreach (int index in new[] { 0, count / 2, count - 1, 0 })
		{
			using MemoryStream output = new();
			Assert.True(session.Export(index, output));
			Assert.Equal(Encoding.UTF8.GetBytes("payload"), output.ToArray());
		}
		Assert.Equal(new long[] { 1, count / 2 + 1, count, 1 }, reader.Reads.Select(r => r.PathId));
		Assert.False(physical.Closed);
		session.Dispose();
		input.Dispose();
		Assert.True(physical.Closed);
		Assert.Equal(1, physical.DisposeCalls);
		GC.KeepAlive(file);
		GC.KeepAlive(session);
	}

	[Theory]
	[InlineData(FileReadMode.Eager)]
	[InlineData(FileReadMode.MetadataOnly)]
	public void DefaultFactoryKeepsItsOriginalDescriptorsAndCanReadTheFileAgain(FileReadMode mode)
	{
		using SmartStream input = SmartStream.CreateMemory(UnityFixtureBuilder.SerializedTextAsset("notes", "payload"));
		using SerializedFile file = Read(input, mode);
		ObjectInfo[] original = Table(file);
		LegacyReader reader = new();
		using SerializedBundle first = new();
		first.AddCollectionFromSerializedFile(file, reader);
		using SerializedBundle second = new();
		second.AddCollectionFromSerializedFile(file, reader);
		Assert.Same(original, Table(file));
		Assert.Equal(1, file.Objects.Length);
		Assert.Equal(2, reader.Calls);
		Assert.Equal(original[0].ObjectData, reader.LastBytes);
	}

	[Fact]
	public void LazyFactoryDoesNotDiscardAnEagerFilesDescriptorTable()
	{
		using SmartStream input = SmartStream.CreateMemory(UnityFixtureBuilder.SerializedTextAsset("notes", "payload"));
		using SerializedFile file = Read(input, FileReadMode.Eager);
		ObjectInfo[] original = Table(file);
		using SerializedBundle bundle = new();
		bundle.AddCollectionFromSerializedFile(file, new UnitySource.LazyAssetFactory(new FullReader()));
		Assert.Same(original, Table(file));
		Assert.True(file.Objects[0].HasObjectData);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FailedCollectionReadPreservesItsDescriptorTableAndOriginalException(bool allocationFailure)
	{
		using SmartStream input = SmartStream.CreateMemory(UnityFixtureBuilder.SerializedResourceManagerTextAsset("notes", "notes", "payload"));
		using SerializedFile file = Read(input, FileReadMode.MetadataOnly);
		ObjectInfo[] original = Table(file);
		Exception failure = allocationFailure ? new OutOfMemoryException("descriptor sentinel") : new InvalidDataException("descriptor sentinel");
		FullReader reader = new() { FailureClass = 147, Failure = failure };
		using SerializedBundle bundle = new();
		Assert.Same(failure, Assert.ThrowsAny<Exception>(() => bundle.AddCollectionFromSerializedFile(file, new UnitySource.LazyAssetFactory(reader))));
		Assert.Same(original, Table(file));
		Assert.Equal(2, file.Objects.Length);
	}

	[Fact]
	public void FailedAssetInsertionDoesNotDiscardTheUnfinishedDescriptorTable()
	{
		using SmartStream input = SmartStream.CreateMemory(UnityFixtureBuilder.SerializedRepeatedTextAssets(2));
		using SerializedFile file = Read(input, FileReadMode.MetadataOnly);
		ObjectInfo[] original = Table(file);
		original[1].TypeID = 147;
		CollidingPathReader reader = new();
		using SerializedBundle bundle = new();
		Assert.Throws<ArgumentException>(() => bundle.AddCollectionFromSerializedFile(file, new UnitySource.LazyAssetFactory(reader)));
		Assert.Equal(1, reader.Calls);
		IUnityObjectBase retained = Assert.Single(Assert.Single(bundle.Collections));
		Assert.Equal(1, retained.PathID);
		Assert.Equal(49, retained.ClassID);
		Assert.Same(original, Table(file));
		Assert.Equal(2, file.Objects.Length);
	}

	[Theory]
	[InlineData("ok")]
	[InlineData("null")]
	[InlineData("oom")]
	public void DetachedSelectedAndDependencyTablesKeepSpriteOrderAndRepeatedExtraction(string outcome)
	{
		byte[] pixels = Enumerable.Repeat(new byte[] { 10, 20, 30, 255 }, 4).SelectMany(p => p).ToArray();
		(byte[] sprite, byte[] atlas) = UnityFixtureBuilder.SerializedAtlasSprite("hero", pixels, 2, 2, pixels, 2, 2, 0, 0, 2, 2, 0, 0, 2, 7, 7);
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "selected.assets");
		string dependency = Path.Combine(directory, UnityFixtureBuilder.AtlasDependencyName);
		File.WriteAllBytes(selected, sprite);
		File.WriteAllBytes(dependency, atlas);
		List<SmartStream> roots = [];
		List<SerializedFile> files = [];
		FileBase Load(string path)
		{
			SmartStream root = SmartStream.OpenReadMulti(path, LocalFileSystem.Instance);
			roots.Add(root);
			SerializedFile file = (SerializedFile)SchemeReader.ReadFile(root, path, Path.GetFileName(path), FileReadMode.MetadataOnly);
			files.Add(file);
			return file;
		}
		FullReader reader = new();
		try
		{
			IUnityGraph graph = UnitySource.OpenGraph(selected, Load, reader);
			using UnitySession session = UnitySession.FromGraph(graph);
			Assert.Equal(2, files.Count);
			Assert.All(files, file => Assert.True(file.Objects.IsEmpty, "Both selected and dependency source tables must be released."));
			Assert.Equal(1, Assert.IsType<UnityGraph>(graph).DependencyCollectionCount);
			Assert.Equal(new[] { "local_texture", "hero" }, session.Entries.Select(e => e.Name));
			Assert.Equal(new[] { 0, 0 }, session.Entries.Select(e => e.CollectionIndex));
			Assert.Empty(reader.Reads);
			if (outcome != "ok")
			{
				reader.FailureClass = 28;
				reader.Failure = outcome == "oom" ? new OutOfMemoryException("dependency sentinel") : null;
				reader.NullClass = outcome == "null" ? 28 : -1;
				using MemoryStream failed = new();
				if (outcome == "oom") Assert.Same(reader.Failure, Assert.Throws<OutOfMemoryException>(() => session.Export(1, failed)));
				else Assert.False(session.Export(1, failed));
				Assert.Empty(failed.ToArray());
				reader.FailureClass = -1; reader.NullClass = -1;
			}
			for (int pass = 0; pass < 2; pass++)
			{
				using MemoryStream output = new();
				Assert.True(session.Export(1, output));
				Assert.Equal(pixels, PngDecoder.Decode(output.ToArray()).Rgba);
			}
			int attempts = outcome == "ok" ? 2 : 3;
			Assert.Equal(Enumerable.Range(0, attempts).SelectMany(_ => new[] { 213, 687078895, 28 }), reader.Reads.Select(r => r.ClassId));
		}
		finally { UnitySource.DisposeRoots(roots); }
		using FileStream selectedExclusive = new(selected, FileMode.Open, FileAccess.Read, FileShare.None);
		using FileStream dependencyExclusive = new(dependency, FileMode.Open, FileAccess.Read, FileShare.None);
	}

	private sealed class CollidingPathReader : AssetFactoryBase
	{
		public int Calls { get; private set; }
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> bytes, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{
			Calls++;
			Assert.Equal(147, info.ClassID);
			Assert.Equal(2, info.PathID);
			Assert.NotEmpty(bytes.ToArray());
			Assert.Equal(1, Assert.Single(info.Collection).PathID);
			return new UnknownObject(info with { PathID = 1 }, bytes.ToArray());
		}
	}
	private sealed class LegacyReader : AssetFactoryBase
	{
		public int Calls { get; private set; }
		public byte[] LastBytes { get; private set; } = [];
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> bytes, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{ Calls++; LastBytes = bytes.ToArray(); return null; }
	}
	private sealed class FullReader : AssetFactoryBase
	{
		private readonly GameAssetFactory inner = new(new BaseManager(static _ => { }));
		public List<(int ClassId, long PathId)> Reads { get; } = [];
		public int FailureClass { get; set; } = -1;
		public int NullClass { get; set; } = -1;
		public Exception? Failure { get; set; }
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> bytes, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{
			Reads.Add((info.ClassID, info.PathID));
			if (info.ClassID == FailureClass && Failure is { } error) throw error;
			return info.ClassID == NullClass ? null : inner.ReadAsset(info, bytes, type, references);
		}
	}
	private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes, false)
	{
		public bool Closed { get; private set; }
		public int DisposeCalls { get; private set; }
		protected override void Dispose(bool disposing) { if (disposing) { Closed = true; DisposeCalls++; } base.Dispose(disposing); }
	}
}
