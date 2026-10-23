using System;
using System.IO;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Exercises the project-owned session catalogue against tiny graph doubles: listing must not
/// convert or copy anything, and only the entry named in an export may be materialised.
/// </summary>
public sealed class ResourceCatalogTests
{
	private static readonly byte[] PngFirst = [0x89, 0x50, 0x4E, 0x47, 1, 2];
	private static readonly byte[] PngSecond = [0x89, 0x50, 0x4E, 0x47, 7];

	[Fact]
	public void ListingTwoEntriesDoesNotConvertOrCopy()
	{
		FakeTexture texture = new(10, PngFirst, "2021.3.1", "hero");
		FakeText text = new(20, [1, 2, 3], "2021.3.1", "notes");
		FakeGraph graph = new(
			new FakeCollection("a.assets", texture, new FakeAsset(1)),
			new FakeCollection("b.assets", text));

		using UnitySession session = UnitySession.FromGraph(graph);

		Assert.Equal(2, session.Entries.Count);
		Assert.Equal(0, texture.RenderCalls);
		Assert.Equal(0, text.ReadCalls);

		UnityCatalogEntry first = session.Entries[0];
		Assert.Equal(0, first.Index);
		Assert.Equal(UnityEntryKind.Texture, first.Kind);
		Assert.Equal(0, first.CollectionIndex);
		Assert.Equal(10, first.PathId);
		Assert.Equal("hero", first.Name);

		UnityCatalogEntry second = session.Entries[1];
		Assert.Equal(1, second.Index);
		Assert.Equal(UnityEntryKind.Text, second.Kind);
		Assert.Equal(1, second.CollectionIndex);
		Assert.Equal(20, second.PathId);
		Assert.Equal("notes", second.Name);
	}

	[Fact]
	public void DuplicatePathIdsInDifferentCollectionsStayDistinct()
	{
		FakeText first = new(1, [1], "1.0", "first");
		FakeText second = new(1, [2], "1.0", "second");
		FakeGraph graph = new(new FakeCollection("a", first), new FakeCollection("b", second));

		using UnitySession session = UnitySession.FromGraph(graph);

		Assert.Equal(2, session.Entries.Count);
		Assert.Equal(1, session.Entries[0].PathId);
		Assert.Equal(1, session.Entries[1].PathId);
		Assert.Equal(0, session.Entries[0].CollectionIndex);
		Assert.Equal(1, session.Entries[1].CollectionIndex);

		using MemoryStream stream = new();
		Assert.True(session.Export(1, stream));
		Assert.Equal([2], stream.ToArray());
	}

	[Fact]
	public void ExportConvertsOnlyTheSelectedEntry()
	{
		FakeTexture first = new(10, PngFirst, "1.0", "hero");
		FakeTexture second = new(11, PngSecond, "1.0", "foe");
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("a", first, second)));

		using MemoryStream stream = new();
		Assert.True(session.Export(1, stream));

		Assert.Equal(0, first.RenderCalls);
		Assert.Equal(1, second.RenderCalls);
		Assert.Equal(PngSecond, stream.ToArray());
	}

	[Fact]
	public void ExportingTextWritesRawBytesOnly()
	{
		FakeText text = new(20, [9, 8, 7], "1.0", "notes");
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("a", text)));

		using MemoryStream stream = new();
		Assert.True(session.Export(0, stream));
		Assert.Equal([9, 8, 7], stream.ToArray());
		Assert.Equal(1, text.ReadCalls);
	}

	[Fact]
	public void UnsupportedEntryExportsNothing()
	{
		FakeTexture unsupported = new(10, null, "1.0", "bad");
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("a", unsupported)));

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
		Assert.Equal(1, unsupported.RenderCalls);
	}

	[Fact]
	public void UnsupportedTextEntryExportsNothing()
	{
		FakeText unsupported = new(20, null, "1.0", "bad");
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("a", unsupported)));

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
		Assert.Equal(1, unsupported.ReadCalls);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(1)]
	public void InvalidIndexIsRejected(int index)
	{
		FakeTexture only = new(10, PngFirst, "1.0", "hero");
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("a", only)));

		using MemoryStream stream = new();
		Assert.False(session.Export(index, stream));
		Assert.Equal(0, only.RenderCalls);
	}

	[Fact]
	public void ExportAfterDisposeIsRejectedAndDisposeIsIdempotent()
	{
		FakeGraph graph = new(new FakeCollection("a", new FakeTexture(10, PngFirst, "1.0", "hero")));
		UnitySession session = UnitySession.FromGraph(graph);
		session.Dispose();

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(1, graph.DisposeCalls);
		session.Dispose();
		Assert.Equal(1, graph.DisposeCalls);
	}

	[Fact]
	public void NullDestinationIsRejected()
	{
		using UnitySession session = UnitySession.FromGraph(
			new FakeGraph(new FakeCollection("a", new FakeTexture(10, PngFirst, "1.0", "hero"))));
		Assert.Throws<ArgumentNullException>(() => session.Export(0, null!));
	}

	[Fact]
	public void NullGraphIsRejected()
	{
		Assert.Throws<ArgumentNullException>(() => UnitySession.FromGraph(null!));
	}

	[Fact]
	public void AnAdditionalResourceKindCarriesItsMetadataAndExportsLazily()
	{
		FakeResource resource = new(
			UnityEntryKind.Font, 30, [7, 8], name: "legacy", sourceSize: 2,
			originalPath: "Assets/fonts/legacy.ttf", outputExtension: "ttf");
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("a", resource)));

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Font, entry.Kind);
		Assert.Equal(30, entry.PathId);
		Assert.Equal("legacy", entry.Name);
		Assert.Equal(2, entry.SourceSize);
		Assert.Equal("Assets/fonts/legacy.ttf", entry.OriginalPath);
		Assert.Equal("ttf", entry.OutputExtension);
		Assert.Equal(0, resource.ExportCalls);

		using MemoryStream stream = new();
		Assert.True(session.Export(0, stream));
		Assert.Equal([7, 8], stream.ToArray());
		Assert.Equal(1, resource.ExportCalls);
	}

	[Fact]
	public void AnUnexportableAdditionalResourceKindExportsNothing()
	{
		FakeResource resource = new(UnityEntryKind.VideoClip, 40, null, name: "clip", outputExtension: "mp4");
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("a", resource)));

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
		Assert.Equal(1, resource.ExportCalls);
	}
}
