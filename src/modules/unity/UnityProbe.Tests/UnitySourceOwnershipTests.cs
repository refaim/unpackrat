using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetRipper.IO.Files;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Resource-ownership regressions over real <c>FileContainer</c>-based bundles: every stream the
/// container produced must be released once the owning graph is disposed, both on a normal close
/// and when classifying an internal entry fails partway through. The hosting container type does
/// not dispose its children itself, so the adapter has to own that tree.
/// </summary>
public sealed class UnitySourceOwnershipTests
{
	private static string Write(string directory, string name, byte[] bytes)
	{
		string path = Path.Combine(directory, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	/// <summary>Opening exclusively only succeeds once every earlier handle is closed.</summary>
	private static void AssertExclusivelyOpenable(string path)
	{
		using FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
	}

	/// <summary>The set of temp files the AssetRipper temp directory currently holds.</summary>
	private static HashSet<string> SnapshotTemporaryFiles()
	{
		string directory = LocalFileSystem.Instance.TemporaryDirectory;
		return Directory.Exists(directory)
			? Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path)!).ToHashSet()
			: new HashSet<string>();
	}

	[Fact]
	public void LargeCompressedOuterBundleReleasesItsTemporaryBlockStreams()
	{
		string directory = Temp.NewDirectory();

		// 51 MiB of raw streaming-resource padding makes the nested bundle exceed the block reader's
		// 50 MiB in-memory limit, so reading the compressed outer bundle must spill both the entry
		// and the decompressed block to DeleteOnClose temp files. An uncompressed bundle would take
		// the direct slice path instead and create no temp file at all.
		byte[] padding = new byte[51 * 1024 * 1024];
		byte[] nested = UnityFixtureBuilder.Bundle(
			("padding.resS", padding),
			("deep.assets", UnityFixtureBuilder.SerializedTextAsset("deep", "deep payload")));
		string bundle = Write(directory, "large.bundle", UnityFixtureBuilder.CompressedBundle(
			("selected.assets", UnityFixtureBuilder.SerializedTextAsset("selected", "selected payload")),
			("inner.bundle", nested)));

		HashSet<string> before = SnapshotTemporaryFiles();

		using (UnitySession session = UnitySession.Open(bundle))
		{
			// The decompressed block spill is already gone, but the entry stream that the nested
			// bundle is sliced from stays live for as long as the session reads through it.
			HashSet<string> during = SnapshotTemporaryFiles();
			Assert.True(during.Except(before).Any(), "the compressed entry must spill a temp file while open");

			int index = -1;
			for (int i = 0; i < session.Entries.Count; i++)
			{
				if (session.Entries[i].Name == "selected")
				{
					index = i;
					break;
				}
			}

			Assert.True(index >= 0);
			using MemoryStream exported = new();
			Assert.True(session.Export(index, exported));
			Assert.Equal("selected payload", Encoding.UTF8.GetString(exported.ToArray()));
		}

		// Disposing the session removes every temp file it created, without waiting for a finalizer.
		Assert.Empty(SnapshotTemporaryFiles().Except(before));
		AssertExclusivelyOpenable(bundle);
	}

	[Fact]
	public void SelectedBundleChildStreamsAreReleasedOnDispose()
	{
		string directory = Temp.NewDirectory();
		string bundle = Write(directory, "game.bundle", UnityFixtureBuilder.Bundle(
			("data.assets", UnityFixtureBuilder.SerializedTextAsset("fixture", "payload")),
			("data.resS", new byte[] { 1, 2, 3, 4 })));

		UnitySession session = UnitySession.Open(bundle);
		Assert.Single(session.Entries);
		using (MemoryStream exported = new())
		{
			Assert.True(session.Export(0, exported));
			Assert.Equal("payload", System.Text.Encoding.UTF8.GetString(exported.ToArray()));
		}

		session.Dispose();
		AssertExclusivelyOpenable(bundle);
	}

	[Fact]
	public void FailedInternalEntryClassificationReleasesTheBundleHandle()
	{
		string directory = Temp.NewDirectory();
		string bundle = Write(directory, "game.bundle", UnityFixtureBuilder.Bundle(
			("data.resS", new byte[] { 1, 2, 3, 4 }),
			("broken.bundle", UnityFixtureBuilder.TruncatedBundle())));

		// Classifying "broken.bundle" fails after "data.resS" was already produced, so the failure
		// path must release the whole bundle tree it had built.
		Assert.Throws<UnityLoadException>(() => UnitySession.Open(bundle));

		AssertExclusivelyOpenable(bundle);
	}

	[Fact]
	public void LeadingBrokenEntryBeforeATrailingResourceReleasesTheBundleHandle()
	{
		string directory = Temp.NewDirectory();
		string bundle = Write(directory, "game.bundle", UnityFixtureBuilder.Bundle(
			("broken.bundle", UnityFixtureBuilder.TruncatedBundle()),
			("data.resS", new byte[] { 1, 2, 3, 4 })));

		// Classifying the leading "broken.bundle" fails before the trailing "data.resS" is reached,
		// so the failure path must release the entire snapshot it took, not only the current entry.
		Assert.Throws<UnityLoadException>(() => UnitySession.Open(bundle));

		AssertExclusivelyOpenable(bundle);
	}

	[Fact]
	public void BrokenDependencyBundleWithATrailingResourceReleasesBothHandles()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", "payload", dependencies: "side.bundle"));
		string side = Write(directory, "side.bundle", UnityFixtureBuilder.Bundle(
			("CAB-good", UnityFixtureBuilder.SerializedTextAsset("good", "good payload")),
			// A raw resource classified before the broken one is attached to the dependency container,
			// so on failure it is only released by the adapter's own container walk, independently of
			// the snapshot cleanup that frees the trailing raw resource.
			("early.resS", new byte[] { 5, 6, 7, 8 }),
			("broken.bundle", UnityFixtureBuilder.TruncatedBundle()),
			("data.resS", new byte[] { 1, 2, 3, 4 })));

		using (UnitySession session = UnitySession.Open(selected))
		{
			// The broken dependency is skipped, leaving the selected catalogue intact and exporting
			// the exact selected bytes.
			UnityCatalogEntry entry = Assert.Single(session.Entries);
			Assert.Equal("fixture", entry.Name);
			using MemoryStream exported = new();
			Assert.True(session.Export(0, exported));
			Assert.Equal("payload", System.Text.Encoding.UTF8.GetString(exported.ToArray()));
		}

		// The entries classified before and after the broken one, and both root handles, are gone.
		AssertExclusivelyOpenable(selected);
		AssertExclusivelyOpenable(side);
	}

	[Fact]
	public void NestedBundleResourcesAreReleasedOnDispose()
	{
		string directory = Temp.NewDirectory();
		byte[] inner = UnityFixtureBuilder.Bundle(
			("CAB-inner", UnityFixtureBuilder.SerializedTextAsset("deep", "deep payload")),
			("deep.resS", new byte[] { 9, 9, 9, 9 }));
		string outer = Write(directory, "outer.bundle", UnityFixtureBuilder.Bundle(
			("CAB-outer", UnityFixtureBuilder.SerializedTextAsset("outer", "outer payload")),
			("inner.bundle", inner)));

		using (UnitySession session = UnitySession.Open(outer))
		{
			Assert.Contains(session.Entries, entry => entry.Name == "outer");
		}

		// The nested container's own streamed resource only gets released by walking into the child
		// container, so a flat disposal would leave the outer handle open.
		AssertExclusivelyOpenable(outer);
	}
}
