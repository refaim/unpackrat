using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityObjectSelectionTests
{
	[Theory]
	[InlineData(1, "2022.1.0f1")]
	[InlineData(114, "2022.1.0f1")]
	[InlineData(21, "2022.1.0f1")]
	[InlineData(123456789, "2022.1.0f1")]
	[InlineData(114, "5.4.1p1")]
	[InlineData(49, "3.4.0f1")]
	public void TrulyUnsupportedShapesAreSkippedWithoutCreatingAnIdentityOrReadingTheirBody(int classId, string version)
	{
		using ReadGuard physical = new(UnityFixtureBuilder.SerializedTextAsset("unused", "payload"));
		using SmartStream input = SmartStream.CreateOwned(physical);
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "guard.assets", "guard.assets", FileReadMode.MetadataOnly);
		ObjectInfo descriptor = file.Objects[0];
		AssetInfo info = new(AssetCreator.CreateCollection(UnityVersion.Parse(version)), 7, classId);
		Reader reader = new();
		UnitySource.LazyAssetFactory factory = new(reader);
		physical.ForbidReads = true;
		Assert.Null(factory.ReadAsset(info, in descriptor, []));
		Assert.Equal(0, physical.Attempts);
		Assert.Equal(0, reader.Calls);
	}

	[Theory]
	[InlineData(28, "2022.1.0f1")]
	[InlineData(49, "2022.1.0f1")]
	[InlineData(128, "2022.1.0f1")]
	[InlineData(329, "2022.1.0f1")]
	[InlineData(152, "2018.3.0f1")]
	[InlineData(83, "2022.1.0f1")]
	[InlineData(43, "2022.1.0f1")]
	[InlineData(213, "2022.1.0f1")]
	[InlineData(89, "2022.1.0f1")]
	[InlineData(115, "2022.1.0f1")]
	[InlineData(687078895, "2022.1.0f1")]
	public void KnownResourcesAndAtlasKeepTheirIdentityEvenWhenTheirMetadataCannotBeRead(int classId, string version)
	{
		AssetInfo info = new(AssetCreator.CreateCollection(UnityVersion.Parse(version)), 7, classId);
		ObjectInfo descriptor = new() { ObjectData = [0xFF] };
		Reader reader = new();
		UnitySource.LazyAssetFactory factory = new(reader);
		Assert.Equal(info, Assert.IsAssignableFrom<IUnityObjectBase>(factory.ReadAsset(info, in descriptor, [])).AssetInfo);
		Assert.Equal(0, reader.Calls);
	}

	[Theory]
	[InlineData(142)]
	[InlineData(147)]
	public void OriginalPathMetadataStillUsesTheFullReader(int classId)
	{
		AssetInfo info = new(AssetCreator.CreateCollection(UnityVersion.V_2022), 7, classId);
		ObjectInfo descriptor = new() { ObjectData = [1, 2, 3] };
		Reader reader = new();
		UnitySource.LazyAssetFactory factory = new(reader);
		Assert.Null(factory.ReadAsset(info, in descriptor, []));
		Assert.Equal(1, reader.Calls);
	}

	[Theory]
	[InlineData(false, 114)]
	[InlineData(true, 114)]
	[InlineData(false, -1)]
	[InlineData(true, -1)]
	public void SkippingUnusedScriptsPreservesSelectedPathsOrderAndRepeatedExports(bool bundled, int scriptClassId)
	{
		byte[] bytes = UnityFixtureBuilder.SerializedResourceManagerTextAsset("hero.txt", "hero", "payload", scriptClassId);
		string path = Path.Combine(Temp.NewDirectory(), bundled ? "selected.unity3d" : "selected.assets");
		File.WriteAllBytes(path, bundled ? UnityFixtureBuilder.CompressedBundle(("CAB-selected", bytes)) : bytes);
		IUnityGraph graph = UnitySource.OpenGraph(path);
		using UnitySession session = UnitySession.FromGraph(graph);
		Assert.Equal(2, Assert.Single(graph.Collections).Assets.Count);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(0, entry.Index);
		Assert.Equal(0, entry.CollectionIndex);
		Assert.Equal("hero", entry.Name);
		Assert.Equal(Path.Join("Assets", "Resources", "hero.txt"), entry.OriginalPath);
		for (int pass = 0; pass < 2; pass++)
		{
			using MemoryStream output = new();
			Assert.True(session.Export(0, output));
			Assert.Equal(Encoding.UTF8.GetBytes("payload"), output.ToArray());
		}
	}

	[Theory]
	[InlineData(0L)]
	[InlineData(1L)]
	public void InvalidIgnoredIdsAreRejectedBeforeAnyObjectBodyIsRead(long pathId)
	{
		using ReadGuard physical = new(UnityFixtureBuilder.SerializedResourceManagerTextAsset("hero.txt", "hero", "payload", 114, pathId));
		using SmartStream input = SmartStream.CreateOwned(physical);
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "invalid.assets", "invalid.assets", FileReadMode.MetadataOnly);
		physical.ForbidReads = true;
		UnityLoadException error = Assert.Throws<UnityLoadException>(() => UnitySource.OpenGraph("invalid.assets", _ => file));
		Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
		Assert.Equal(0, physical.Attempts);
	}

	[Theory]
	[InlineData(-7L)]
	[InlineData(long.MinValue)]
	public void ValidNegativePathIdsRemainCataloguedAndExportable(long pathId)
	{
		using SmartStream input = SmartStream.CreateMemory(UnityFixtureBuilder.SerializedTextAsset("negative", "payload", pathId));
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "negative.assets", "negative.assets", FileReadMode.MetadataOnly);
		using UnitySession session = UnitySession.FromGraph(UnitySource.OpenGraph("negative.assets", _ => file));
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(pathId, entry.PathId);
		Assert.Equal("negative", entry.Name);
		using MemoryStream output = new();
		Assert.True(session.Export(0, output));
		Assert.Equal(Encoding.UTF8.GetBytes("payload"), output.ToArray());
	}

	[Theory]
	[InlineData(114, 49)]
	[InlineData(49, 114)]
	[InlineData(114, 1)]
	[InlineData(123456789, 114)]
	public void DuplicateIdsAcrossSkippedAndRetainedKindsAreRejectedBeforeReadingEitherBody(int firstClass, int secondClass)
	{
		using ReadGuard physical = new(UnityFixtureBuilder.SerializedRepeatedTextAssets(2));
		using SmartStream input = SmartStream.CreateOwned(physical);
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "duplicate.assets", "duplicate.assets", FileReadMode.MetadataOnly);
		ObjectInfo[] table = SourceTable(file);
		table[0].FileID = 7; table[1].FileID = 7;
		table[0].TypeID = firstClass; table[1].TypeID = secondClass;
		physical.ForbidReads = true;
		UnityLoadException error = Assert.Throws<UnityLoadException>(() => UnitySource.OpenGraph("duplicate.assets", _ => file));
		Assert.IsType<ArgumentException>(error.InnerException);
		Assert.Equal(0, physical.Attempts);
		Assert.Same(table, SourceTable(file));
	}

	[Theory]
	[InlineData(114)]
	[InlineData(49)]
	[InlineData(123456789)]
	public void ZeroIdIsRejectedForEverySelectionOutcomeBeforeReadingItsBody(int classId)
	{
		using ReadGuard physical = new(UnityFixtureBuilder.SerializedTextAsset("invalid", "payload"));
		using SmartStream input = SmartStream.CreateOwned(physical);
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "zero.assets", "zero.assets", FileReadMode.MetadataOnly);
		ObjectInfo[] table = SourceTable(file);
		table[0].FileID = 0; table[0].TypeID = classId;
		physical.ForbidReads = true;
		UnityLoadException error = Assert.Throws<UnityLoadException>(() => UnitySource.OpenGraph("zero.assets", _ => file));
		Assert.IsType<ArgumentException>(error.InnerException);
		Assert.Equal(0, physical.Attempts);
		Assert.Same(table, SourceTable(file));
	}

	private static ObjectInfo[] SourceTable(SerializedFile file) => (ObjectInfo[])typeof(SerializedFile)
		.GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;

	private sealed class Reader : AssetFactoryBase
	{
		public int Calls { get; private set; }
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> bytes, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{ Calls++; return null; }
	}
	private sealed class ReadGuard(byte[] bytes) : MemoryStream(bytes, false)
	{
		public bool ForbidReads { get; set; }
		public int Attempts { get; private set; }
		private void Check() { if (ForbidReads) { Attempts++; throw new InvalidOperationException("Object body was read before table validation or for an ignored shape."); } }
		public override int Read(byte[] buffer, int offset, int count) { Check(); return base.Read(buffer, offset, count); }
		public override int Read(Span<byte> buffer) { Check(); return base.Read(buffer); }
		public override int ReadByte() { Check(); return base.ReadByte(); }
	}
}
