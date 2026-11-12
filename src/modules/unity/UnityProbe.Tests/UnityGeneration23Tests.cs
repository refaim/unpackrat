using System;
using System.Buffers.Binary;
using System.IO;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Unity 6000.5 serialized files must keep the same observable texture export contract as generation
/// 22, both on their own and inside a compressed bundle. Future unknown generations remain rejected.
/// </summary>
public sealed class UnityGeneration23Tests : IDisposable
{
	private static readonly byte[] Pixels =
	[
		10, 20, 30, 40, 50, 60, 70, 80,
		90, 100, 110, 120, 130, 140, 150, 160,
	];
	private static readonly byte[] TopDownPixels =
	[
		90, 100, 110, 120, 130, 140, 150, 160,
		10, 20, 30, 40, 50, 60, 70, 80,
	];
	private readonly string directory = Temp.NewDirectory();

	public void Dispose() => Directory.Delete(directory, recursive: true);

	[Theory]
	[InlineData(22, false, false)]
	[InlineData(22, false, true)]
	[InlineData(22, true, false)]
	[InlineData(22, true, true)]
	[InlineData(23, false, false)]
	[InlineData(23, false, true)]
	[InlineData(23, true, false)]
	[InlineData(23, true, true)]
	public void KnownGenerationsExportTheCompleteTextureWithExactPixels(int generation, bool bundled, bool compact)
	{
		byte[] serialized = UnityFixtureBuilder.SerializedGenerationTexture(generation, "generation_texture", Pixels);
		Assert.Equal(generation, BinaryPrimitives.ReadInt32BigEndian(serialized.AsSpan(8, 4)));
		byte[] input = bundled
			? UnityFixtureBuilder.CompressedBundle(("CAB-generation", serialized))
			: serialized;
		string path = Write(bundled ? "selected.bundle" : "selected.assets", input);
		using UnitySession session = Open(path, compact);
		AssertTexture(session);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void Generation23WithAnInlineTypeTreeExportsExactTexturePixels(bool bundled, bool compact)
	{
		byte[] serialized = UnityFixtureBuilder.SerializedGeneration23TypedTexture("generation_texture", Pixels);
		// Validate the hermetic writer's framed type tree with an independent format reader before
		// asking our production reader to consume it.
		AssertTypedFixture(serialized);

		byte[] bytes = bundled
			? UnityFixtureBuilder.CompressedBundle(("CAB-generation-typed", serialized))
			: serialized;
		string path = Write(bundled ? "typed.bundle" : "typed.assets", bytes);
		using UnitySession session = Open(path, compact);
		AssertTexture(session);
	}

	[Fact]
	public void TypedFixtureUsesGeneration23InlineDefinition()
	{
		AssertTypedFixture(UnityFixtureBuilder.SerializedGeneration23TypedTexture("generation_texture", Pixels));
	}

	private static void AssertTypedFixture(byte[] serialized)
	{
		using MemoryStream input = new(serialized, writable: false);
		using AssetsFileReader reader = new(input);
		AssetsFile file = new();
		file.Read(reader);
		Assert.Equal(23u, file.Header.Version);
		Assert.True(file.Metadata.TypeTreeEnabled);
		TypeTreeType type = Assert.Single(file.Metadata.TypeTreeTypes);
		Assert.Equal(28, type.TypeId);
		Assert.Equal(new byte[16], type.ExtTypeHash.data);
		Assert.True(type.TypeBlobIsDefinition);
		Assert.NotNull(type.TypeBlob);
		Assert.NotEmpty(type.TypeBlob.Nodes);
		Assert.NotEmpty(type.TypeBlob.StringBufferBytes);
		Assert.Single(file.Metadata.AssetInfos);
	}

	private static void AssertTexture(UnitySession session)
	{
		UnityCatalogEntry texture = Assert.Single(session.Entries);
		Assert.Equal(0, texture.Index);
		Assert.Equal(UnityEntryKind.Texture, texture.Kind);
		Assert.Equal(0, texture.CollectionIndex);
		Assert.Equal(1L, texture.PathId);
		Assert.Equal("generation_texture", texture.Name);
		Assert.Equal("png", texture.OutputExtension);
		Assert.Equal(16L, texture.SourceSize);
		using MemoryStream destination = new();
		Assert.True(session.Export(texture.Index, destination));
		(int width, int height, byte[] rgba) = PngDecoder.Decode(destination.ToArray());
		Assert.Equal(2, width);
		Assert.Equal(2, height);
		Assert.Equal(TopDownPixels, rgba);
	}

	[Theory]
	[InlineData(24, false)]
	[InlineData(24, true)]
	[InlineData(int.MaxValue, false)]
	[InlineData(int.MaxValue, true)]
	public void UnknownFutureGenerationsAreRejected(int generation, bool compact)
	{
		byte[] serialized = UnityFixtureBuilder.SerializedGenerationTexture(23, "generation_texture", Pixels);
		BinaryPrimitives.WriteInt32BigEndian(serialized.AsSpan(8, 4), generation);
		string path = Write("unknown.assets", serialized);

		Assert.Throws<UnityLoadException>(() =>
		{
			using UnitySession unexpected = Open(path, compact);
		});
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Generation23WithTruncatedMetadataIsRejected(bool compact)
	{
		byte[] serialized = UnityFixtureBuilder.SerializedGenerationTexture(23, "generation_texture", Pixels);
		// Keep a coherent 48-byte large-file header and the version string, then cut off the
		// platform field. The header probe succeeds; reading actual metadata must reject the input.
		const int headerSize = 48;
		const int truncatedMetadataSize = 13;
		Array.Resize(ref serialized, headerSize + truncatedMetadataSize);
		BinaryPrimitives.WriteInt32BigEndian(serialized.AsSpan(20, 4), truncatedMetadataSize);
		BinaryPrimitives.WriteInt64BigEndian(serialized.AsSpan(24, 8), serialized.LongLength);
		BinaryPrimitives.WriteInt64BigEndian(serialized.AsSpan(32, 8), serialized.LongLength);
		string path = Write("truncated.assets", serialized);

		Assert.Throws<UnityLoadException>(() =>
		{
			using UnitySession unexpected = Open(path, compact);
		});
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
