using System;
using System.Buffers.Binary;
using System.IO;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.Streams.Smart;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityGeneration23FrameTests : IDisposable
{
	private static readonly byte[] Pixels =
	[
		10, 20, 30, 40, 50, 60, 70, 80,
		90, 100, 110, 120, 130, 140, 150, 160,
	];
	private static readonly byte[] Hash =
	[
		1, 2, 3, 4, 5, 6, 7, 8,
		9, 10, 11, 12, 13, 14, 15, 16,
	];
	private readonly string directory = Temp.NewDirectory();

	public void Dispose() => Directory.Delete(directory, recursive: true);

	[Theory]
	[InlineData(1, false, false)]
	[InlineData(1, false, true)]
	[InlineData(1, true, false)]
	[InlineData(1, true, true)]
	[InlineData(2, false, false)]
	[InlineData(2, false, true)]
	[InlineData(2, true, false)]
	[InlineData(2, true, true)]
	public void ExternalReferencesAndEmptyDefinitionsUseTheKnownTextureSchema(int mode, bool bundled, bool compact)
	{
		byte[] serialized = Fixture((Generation23TypeTreeMode)mode);
		AssertIndependentTypeTree(serialized, (Generation23TypeTreeMode)mode);
		string path = Write(bundled ? "selected.bundle" : "selected.assets", bundled
			? UnityFixtureBuilder.CompressedBundle(("CAB-typed", serialized)) : serialized);
		using UnitySession session = Open(path, compact);
		AssertTexture(session);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public void MatureReaderWriterRoundTripPreservesExtendedHashAndDefinitionKind(int mode)
	{
		byte[] serialized = Fixture((Generation23TypeTreeMode)mode);
		AssertIndependentTypeTree(serialized, (Generation23TypeTreeMode)mode);
		using SmartStream input = SmartStream.CreateMemory(serialized);
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "roundtrip.assets", "roundtrip.assets");
		using MemoryStream output = new();
		file.Write(output);
		byte[] rewritten = output.ToArray();
		AssertIndependentTypeTree(rewritten, (Generation23TypeTreeMode)mode);
		string path = Write("roundtrip.assets", rewritten);
		using UnitySession session = Open(path, compact: true);
		AssertTexture(session);
	}

	public enum FrameCorruption
	{
		NegativeSize, OneByteSize, FifteenByteSize, OversizedFrame, FrameBeyondMetadata,
		BadMagic, EarlierEmbeddedVersion, FutureEmbeddedVersion,
		NegativeNodeCount, ExcessNodeCount, NegativeStringCount, ExcessStringCount,
		FrameOneByteShort, FrameOneByteLong,
		TruncatedExtendedHash, TruncatedSizeField, TruncatedFrameHeader,
		TruncatedNodeTable, TruncatedStringTable, TruncatedTypeDependencies,
		MetadataBeyondDataOffset, MetadataBeyondEof,
	}

	public static TheoryData<FrameCorruption, bool, bool> MalformedFrames
	{
		get
		{
			TheoryData<FrameCorruption, bool, bool> cases = new();
			foreach (FrameCorruption corruption in Enum.GetValues<FrameCorruption>())
			{
				foreach (bool bundled in new[] { false, true })
				{
					cases.Add(corruption, bundled, false);
					cases.Add(corruption, bundled, true);
				}
			}
			return cases;
		}
	}

	[Theory]
	[MemberData(nameof(MalformedFrames))]
	public void MalformedFramesFailTheSelectedInputInsteadOfReturningAnEmptyCatalogue(
		FrameCorruption corruption, bool bundled, bool compact)
	{
		byte[] bytes = Corrupt(Fixture(Generation23TypeTreeMode.InlineDefinition), corruption);
		string path = Write(bundled ? "corrupt.bundle" : "corrupt.assets", bundled
			? UnityFixtureBuilder.CompressedBundle(("CAB-corrupt", bytes)) : bytes);
		Assert.Throws<UnityLoadException>(() =>
		{
			using UnitySession unexpected = Open(path, compact);
		});
	}

	private static byte[] Corrupt(byte[] bytes, FrameCorruption corruption)
	{
		int magic = bytes.AsSpan().IndexOf("mhtt"u8);
		Assert.True(magic >= 20);
		int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(magic - 4, 4));
		int nodes = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(magic + 8, 4));
		void Field(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
		void MetadataEnd(int end) => BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), end - 48);
		switch (corruption)
		{
			case FrameCorruption.NegativeSize: Field(magic - 4, -1); break;
			case FrameCorruption.OneByteSize: Field(magic - 4, 1); break;
			case FrameCorruption.FifteenByteSize: Field(magic - 4, 15); break;
			case FrameCorruption.OversizedFrame: Field(magic - 4, (4 * 1024 * 1024) + 1); break;
			case FrameCorruption.FrameBeyondMetadata:
				Field(magic - 4, 48 + BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) - magic + 1);
				break;
			case FrameCorruption.BadMagic: Field(magic, 0); break;
			case FrameCorruption.EarlierEmbeddedVersion: Field(magic + 4, 22); break;
			case FrameCorruption.FutureEmbeddedVersion: Field(magic + 4, 24); break;
			case FrameCorruption.NegativeNodeCount: Field(magic + 8, -1); break;
			case FrameCorruption.ExcessNodeCount: Field(magic + 8, 65537); break;
			case FrameCorruption.NegativeStringCount: Field(magic + 12, -1); break;
			case FrameCorruption.ExcessStringCount: Field(magic + 12, 1048577); break;
			case FrameCorruption.FrameOneByteShort: Field(magic - 4, size - 1); break;
			case FrameCorruption.FrameOneByteLong: Field(magic - 4, size + 1); break;
			case FrameCorruption.TruncatedExtendedHash: MetadataEnd(magic - 5); break;
			case FrameCorruption.TruncatedSizeField: MetadataEnd(magic - 1); break;
			case FrameCorruption.TruncatedFrameHeader: MetadataEnd(magic + 15); break;
			case FrameCorruption.TruncatedNodeTable: MetadataEnd(magic + 16 + (nodes * 32) - 1); break;
			case FrameCorruption.TruncatedStringTable: MetadataEnd(magic + size - 1); break;
			case FrameCorruption.TruncatedTypeDependencies: MetadataEnd(magic + size + 3); break;
			case FrameCorruption.MetadataBeyondDataOffset:
				MetadataEnd(checked((int)BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(32, 8))) + 1);
				break;
			case FrameCorruption.MetadataBeyondEof: MetadataEnd(bytes.Length + 1); break;
			default: throw new ArgumentOutOfRangeException(nameof(corruption));
		}
		return bytes;
	}

	private static byte[] Fixture(Generation23TypeTreeMode mode)
		=> UnityFixtureBuilder.SerializedGeneration23TypedTexture("framed_texture", Pixels, mode, Hash);

	private static void AssertIndependentTypeTree(byte[] bytes, Generation23TypeTreeMode mode)
	{
		using MemoryStream input = new(bytes, writable: false);
		using AssetsFileReader reader = new(input);
		AssetsFile file = new();
		file.Read(reader);
		Assert.Equal(23u, file.Header.Version);
		Assert.True(file.Metadata.TypeTreeEnabled);
		TypeTreeType type = Assert.Single(file.Metadata.TypeTreeTypes);
		Assert.Equal(Hash, type.ExtTypeHash.data);
		Assert.Equal(mode != Generation23TypeTreeMode.ExternalReference, type.TypeBlobIsDefinition);
		int hashOffset = bytes.AsSpan().IndexOf(Hash);
		Assert.True(hashOffset > 48);
		int framedSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(hashOffset + 16, 4));
		if (mode == Generation23TypeTreeMode.InlineDefinition)
		{
			Assert.True(framedSize > 16);
			Assert.NotEmpty(type.TypeBlob.Nodes);
			Assert.NotEmpty(type.TypeBlob.StringBufferBytes);
		}
		else if (mode == Generation23TypeTreeMode.EmptyDefinition)
		{
			Assert.Equal(16, framedSize);
			Assert.Empty(type.TypeBlob.Nodes);
			Assert.Empty(type.TypeBlob.StringBufferBytes);
		}
		else Assert.Equal(0, framedSize);
		Assert.Single(file.Metadata.AssetInfos);
	}

	private static void AssertTexture(UnitySession session)
	{
		UnityCatalogEntry texture = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Texture, texture.Kind);
		Assert.Equal("framed_texture", texture.Name);
		Assert.Equal("png", texture.OutputExtension);
		Assert.Equal(16L, texture.SourceSize);
		using MemoryStream destination = new();
		Assert.True(session.Export(texture.Index, destination));
		(int width, int height, byte[] rgba) = PngDecoder.Decode(destination.ToArray());
		Assert.Equal(2, width);
		Assert.Equal(2, height);
		Assert.Equal(new byte[] { 90, 100, 110, 120, 130, 140, 150, 160, 10, 20, 30, 40, 50, 60, 70, 80 }, rgba);
	}

	private string Write(string name, byte[] bytes)
	{
		string path = Path.Combine(directory, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	private static UnitySession Open(string path, bool compact)
		=> UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null, compact: compact));
}
