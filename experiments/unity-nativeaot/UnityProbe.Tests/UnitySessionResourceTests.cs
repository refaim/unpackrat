using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// End-to-end session tests over real generated fixtures: a streamed Texture2D that needs a sibling
/// resource file, unreadable declared dependencies, load failures injected at the reader boundary,
/// and handle ownership across the session lifetime.
/// </summary>
public sealed class UnitySessionResourceTests
{
	private static readonly byte[] Pixels = [0, 128, 255, 255];

	private static string Write(string directory, string name, byte[] bytes)
	{
		string path = Path.Combine(directory, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	/// <summary>Opens the file exclusively, which only succeeds once every earlier handle is closed.</summary>
	private static void AssertExclusivelyOpenable(string path)
	{
		using FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
	}

	[Fact]
	public void StreamedTextureWithASiblingResourceExportsPngTwice()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "texture.assets",
			UnityFixtureBuilder.SerializedStreamedTexture("hero", "hero.resource", (uint)Pixels.Length));
		string resource = Write(directory, "hero.resource", Pixels);

		// Hold the resource exclusively open across the whole Open call: nothing the session does
		// while opening may read or retain it, or the sharing violation would surface here.
		UnitySession session;
		using (FileStream locked = new(resource, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			session = UnitySession.Open(selected);
		}

		using (session)
		{
			UnityCatalogEntry entry = Assert.Single(session.Entries);
			Assert.Equal(UnityEntryKind.Texture, entry.Kind);
			Assert.Equal("hero", entry.Name);

			using MemoryStream first = new();
			Assert.True(session.Export(0, first));
			byte[] firstBytes = first.ToArray();

			using MemoryStream second = new();
			Assert.True(session.Export(0, second));
			Assert.Equal(firstBytes, second.ToArray());
		}

		// Once the session is gone the graph has released the resolved resource.
		AssertExclusivelyOpenable(resource);
	}

	[Theory]
	[InlineData("hero.resource")]
	[InlineData("hero.resS")]
	public void StreamedTextureSiblingResourceExportsExactRgbaPixels(string resourceName)
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "texture.assets",
			UnityFixtureBuilder.SerializedStreamedTexture("hero", resourceName, (uint)Pixels.Length));
		Write(directory, resourceName, Pixels);

		using UnitySession session = UnitySession.Open(selected);
		using MemoryStream first = new();
		Assert.True(session.Export(0, first));

		byte[] firstBytes = first.ToArray();
		(int width, int height, byte[] rgba) = PngDecoder.Decode(firstBytes);
		Assert.Equal(1, width);
		Assert.Equal(1, height);
		Assert.Equal(Pixels, rgba);

		using MemoryStream second = new();
		Assert.True(session.Export(0, second));
		Assert.Equal(firstBytes, second.ToArray());
	}

	[Fact]
	public void StreamedTextureResourceHandleIsReleasedOnlyAfterDispose()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "texture.assets",
			UnityFixtureBuilder.SerializedStreamedTexture("hero", "hero.resource", (uint)Pixels.Length));
		string resource = Write(directory, "hero.resource", Pixels);

		UnitySession session = UnitySession.Open(selected);
		using (MemoryStream exported = new())
		{
			Assert.True(session.Export(0, exported));
		}

		// The graph owns the resolved resource, so the handle is still held while the session is open
		// and released once it is disposed.
		Assert.Throws<IOException>(() => AssertExclusivelyOpenable(resource));

		session.Dispose();
		AssertExclusivelyOpenable(resource);
	}

	[Fact]
	public void StreamedTextureWithAMissingSiblingResourceExportsNothing()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "texture.assets",
			UnityFixtureBuilder.SerializedStreamedTexture("hero", "absent.resource", (uint)Pixels.Length));

		using UnitySession session = UnitySession.Open(selected);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void StreamedTextureResourceOutsideTheSelectedDirectoryIsRefused()
	{
		string directory = Temp.NewDirectory();
		string parent = Path.GetDirectoryName(directory)!;
		string escapeName = "escape.resource";
		Write(parent, escapeName, Pixels);

		string selected = Write(directory, "texture.assets",
			UnityFixtureBuilder.SerializedStreamedTexture("hero", "../" + escapeName, (uint)Pixels.Length));

		using UnitySession session = UnitySession.Open(selected);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
	}

	[Fact]
	public void TruncatedDeclaredDependencyDoesNotAbortTheSelectedSession()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", "payload", dependencies: "broken.bundle"));
		Write(directory, "broken.bundle", UnityFixtureBuilder.TruncatedBundle());

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		Assert.Equal(0, graph.DependencyCollectionCount);
		using UnitySession session = UnitySession.FromGraph(graph);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("fixture", entry.Name);

		using MemoryStream stream = new();
		Assert.True(session.Export(0, stream));
		Assert.Equal("payload", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
	}

	[Fact]
	public void UnreadableDeclaredDependencyIsDisposedAndSkipped()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", "payload", dependencies: "side.assets"));
		string dependencyPath = Path.Combine(directory, "side.assets");
		Write(directory, "side.assets", [1, 2, 3, 4]);

		FakeFileBase dependency = new(onReadContents: () => throw new InvalidDataException("broken dependency"));
		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected,
			path => path == dependencyPath ? dependency : UnitySource.ReadFile(path));

		Assert.Equal(0, graph.DependencyCollectionCount);
		Assert.Equal(1, dependency.DisposeCalls);
	}

	[Fact]
	public void DeclaredResourceDependencyHandleIsReleasedAfterDispose()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", "payload", dependencies: "junk.resource"));
		string resource = Write(directory, "junk.resource", [1, 2, 3, 4]);

		UnitySession session = UnitySession.Open(selected);
		Assert.Single(session.Entries);

		session.Dispose();
		AssertExclusivelyOpenable(resource);
	}

	[Fact]
	public void SelectedReaderOutOfMemoryEscapesUnchangedAfterCleanup()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets", UnityFixtureBuilder.SerializedTextAsset("fixture", "payload"));

		OutOfMemoryException oom = new();
		FakeFileBase injected = new(onReadContents: () => throw oom);

		Assert.Same(oom, Assert.Throws<OutOfMemoryException>(() => UnitySource.OpenGraph(selected, _ => injected)));
		Assert.Equal(1, injected.DisposeCalls);
	}

	[Fact]
	public void DependencyReaderOutOfMemoryEscapesUnchangedAfterCleanup()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", "payload", dependencies: "side.assets"));
		string dependencyPath = Path.Combine(directory, "side.assets");
		Write(directory, "side.assets", [1, 2, 3, 4]);

		OutOfMemoryException oom = new();
		FakeFileBase injected = new(onReadContents: () => throw oom);

		Assert.Same(oom, Assert.Throws<OutOfMemoryException>(() => UnitySource.OpenGraph(selected,
			path => path == dependencyPath ? injected : UnitySource.ReadFile(path))));
		Assert.Equal(1, injected.DisposeCalls);
	}

	[Fact]
	public void CorruptSerializedDependencyIsIsolatedAndReleased()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", "payload", dependencies: "corrupt.assets"));
		string dependency = Write(directory, "corrupt.assets", UnityFixtureBuilder.CorruptSerializedTextAsset());

		using (UnitySession session = UnitySession.Open(selected))
		{
			// The corrupt dependency's objects are read but never enter the selected catalog, and the
			// selected entry still exports its exact payload.
			UnityCatalogEntry entry = Assert.Single(session.Entries);
			Assert.Equal("fixture", entry.Name);
			using MemoryStream exported = new();
			Assert.True(session.Export(0, exported));
			Assert.Equal("payload", System.Text.Encoding.UTF8.GetString(exported.ToArray()));
		}

		AssertExclusivelyOpenable(dependency);
	}

	[Fact]
	public void DependencyOutOfMemoryAfterAnOwnedDependencyReleasesTheOwnedDependency()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", "payload", dependencies: new[] { "owned.assets", "failing.assets" }));
		string ownedPath = Path.Combine(directory, "owned.assets");
		string failingPath = Path.Combine(directory, "failing.assets");
		Write(directory, "owned.assets", UnityFixtureBuilder.SerializedTextAsset("other", "kept"));
		Write(directory, "failing.assets", [1, 2, 3, 4]);

		FakeFileBase owned = new();
		OutOfMemoryException oom = new();
		FakeFileBase failing = new(onReadContents: () => throw oom);

		Assert.Same(oom, Assert.Throws<OutOfMemoryException>(() => UnitySource.OpenGraph(selected,
			path => path == ownedPath ? owned : path == failingPath ? failing : UnitySource.ReadFile(path))));

		// The dependency loaded before the failure had already been taken into ownership, so the
		// failure cleanup must release it as well as the one that failed.
		Assert.Equal(1, owned.DisposeCalls);
		Assert.Equal(1, failing.DisposeCalls);
	}
}
