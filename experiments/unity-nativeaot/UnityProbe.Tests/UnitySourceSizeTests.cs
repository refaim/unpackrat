using System.IO;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// The declared source payload length across real generated fixtures and project-owned provider
/// doubles: inline-over-stream precedence, absent/empty metadata, the streamed resource size and
/// the text span length. Every value is read from metadata alone, so no query converts an asset.
/// </summary>
public sealed class UnitySourceSizeTests
{
	private static readonly byte[] Pixels = [0, 128, 255, 255];

	private static string Write(string directory, string name, byte[] bytes)
	{
		string path = Path.Combine(directory, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	[Fact]
	public void InlineImageDataLengthWinsOverADeclaredStreamedSize()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "texture.assets",
			UnityFixtureBuilder.SerializedTextureWithInlineAndStream("hero", [1, 2, 3], "hero.resource", 99));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(3L, entry.SourceSize);
	}

	[Fact]
	public void StreamedTextureReportsItsDeclaredResourceSizeWithoutReadingTheBody()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "texture.assets",
			UnityFixtureBuilder.SerializedStreamedTexture("hero", "hero.resource", (uint)Pixels.Length));
		string resource = Write(directory, "hero.resource", Pixels);

		// The resource stays exclusively locked across open and the size query, so answering the
		// length cannot have read or retained the resS body.
		using FileStream locked = new(resource, FileMode.Open, FileAccess.Read, FileShare.None);
		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal((long)Pixels.Length, entry.SourceSize);
	}

	[Fact]
	public void ATextureWithoutInlineOrStreamedDataReportsUnknownZero()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "texture.assets",
			UnityFixtureBuilder.SerializedInlineTexture("hero", []));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(0L, entry.SourceSize);
	}

	[Fact]
	public void QueryingSourceSizeNeverRendersOrCopiesAnAsset()
	{
		FakeTexture texture = new(10, [1, 2, 3], name: "hero", sourceSize: 7);
		FakeText text = new(20, [4, 5], name: "notes", sourceSize: 2);
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("a", texture, text)));

		Assert.Equal(7L, session.Entries[0].SourceSize);
		Assert.Equal(2L, session.Entries[1].SourceSize);
		Assert.Equal(0, texture.RenderCalls);
		Assert.Equal(0, text.ReadCalls);
	}

	[Theory]
	[InlineData(0UL, 0L)]
	[InlineData(24UL, 24L)]
	[InlineData((ulong)long.MaxValue, long.MaxValue)]
	public void DeclaredSourceSizeNarrowsRepresentableLengths(ulong declared, long expected)
		=> Assert.Equal(expected, DeclaredSourceSize.Narrow(declared));

	[Fact]
	public void DeclaredSourceSizeReportsAnUnrepresentableLengthAsUnknownZero()
		=> Assert.Equal(0L, DeclaredSourceSize.Narrow((ulong)long.MaxValue + 1));
}
