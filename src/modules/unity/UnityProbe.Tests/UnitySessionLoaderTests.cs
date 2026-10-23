using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Drives the real <see cref="UnitySource.OpenGraph"/> loader over tiny legal fixtures generated at
/// test time: a serialized TextAsset file with an unrelated neighbour, a declared sidecar
/// dependency, and a UnityFS bundle with two internal collections that share a path id.
/// </summary>
public sealed class UnitySessionLoaderTests
{
	private const string Payload = "unpackrat unity fixture\n";

	private static string Write(string directory, string name, byte[] bytes)
	{
		string path = Path.Combine(directory, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	[Fact]
	public void SerializedTextAssetSessionListsAndRepeatedlyExportsTheSelectedBytes()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets", UnityFixtureBuilder.SerializedTextAsset("fixture", Payload));
		Write(directory, "unrelated.assets", UnityFixtureBuilder.SerializedTextAsset("other", "other payload"));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(0, entry.Index);
		Assert.Equal(UnityEntryKind.Text, entry.Kind);
		Assert.Equal(0, entry.CollectionIndex);
		Assert.Equal(1, entry.PathId);
		Assert.Equal("fixture", entry.Name);

		using MemoryStream first = new();
		Assert.True(session.Export(0, first));
		Assert.Equal(Payload, System.Text.Encoding.UTF8.GetString(first.ToArray()));

		using MemoryStream second = new();
		Assert.True(session.Export(0, second));
		Assert.Equal(first.ToArray(), second.ToArray());
	}

	[Fact]
	public void AnUnrelatedAdjacentFileNeverEntersTheCatalogue()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets", UnityFixtureBuilder.SerializedTextAsset("fixture", Payload));
		Write(directory, "unrelated.assets", UnityFixtureBuilder.SerializedTextAsset("other", "other payload"));

		using UnitySession session = UnitySession.Open(selected);

		Assert.Single(session.Entries);
		Assert.DoesNotContain(session.Entries, entry => entry.Name == "other");
	}

	[Fact]
	public void GraphKeepsOnlyTheSelectedCollectionAndReportsNoDependencies()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets", UnityFixtureBuilder.SerializedTextAsset("fixture", Payload));

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		UnityGraphCollection collection = Assert.Single(graph.Collections.Cast<UnityGraphCollection>());
		Assert.Contains("selected.assets", collection.Identity, StringComparison.Ordinal);
		Assert.Equal(0, graph.DependencyCollectionCount);
	}

	[Fact]
	public void DeclaredSerializedDependencyLoadsYetStaysOutOfTheCatalogue()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", Payload, dependencies: "side.assets"));
		Write(directory, "side.assets", UnityFixtureBuilder.SerializedTextAsset("side", "side payload"));

		UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);
		Assert.Equal(1, graph.DependencyCollectionCount);

		using UnitySession session = UnitySession.FromGraph(graph);
		Assert.Single(session.Entries);
		Assert.Equal("fixture", session.Entries[0].Name);
	}

	[Fact]
	public void MissingSidecarIsIgnored()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", Payload, dependencies: "absent.assets"));

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		Assert.Single(graph.Collections);
		Assert.Equal(0, graph.DependencyCollectionCount);
	}

	[Fact]
	public void DependencyOutsideTheSelectedDirectoryIsIgnored()
	{
		string directory = Temp.NewDirectory();
		string parent = Path.GetDirectoryName(directory)!;
		string escapeName = "../" + Path.GetFileName(directory) + ".escape.assets";
		Write(parent, Path.GetFileName(directory) + ".escape.assets", UnityFixtureBuilder.SerializedTextAsset("escape", "escape"));

		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", Payload, dependencies: escapeName));

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		Assert.Equal(0, graph.DependencyCollectionCount);
	}

	[Fact]
	public void CyclicDependencyBackToTheSelectedFileIsNotLoadedTwice()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", Payload, dependencies: "side.assets"));
		Write(directory, "side.assets",
			UnityFixtureBuilder.SerializedTextAsset("side", "side payload", dependencies: "selected.assets"));

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		Assert.Equal(1, graph.DependencyCollectionCount);
	}

	[Fact]
	public void DependencyLoadingIsBoundedByTheExplicitLimit()
	{
		string directory = Temp.NewDirectory();
		string[] names = new string[65];
		for (int i = 0; i < names.Length; i++)
		{
			names[i] = $"dep{i:D2}.assets";
			Write(directory, names[i], UnityFixtureBuilder.SerializedTextAsset($"dep{i:D2}", "payload"));
		}

		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", Payload, dependencies: names));

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		Assert.Equal(64, graph.DependencyCollectionCount);
	}

	[Fact]
	public void MissingDependenciesConsumeTheExaminedBudget()
	{
		string directory = Temp.NewDirectory();
		string[] names = new string[65];
		for (int i = 0; i < 64; i++)
		{
			// Declared but never written: each still consumes one slot of the walk budget.
			names[i] = $"absent{i:D2}.assets";
		}

		names[64] = "present.assets";
		Write(directory, names[64], UnityFixtureBuilder.SerializedTextAsset("present", "present payload"));

		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", Payload, dependencies: names));

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		// The 64 missing names exhaust the budget, so the valid 65th is never examined.
		Assert.Equal(0, graph.DependencyCollectionCount);
	}

	[Fact]
	public void DependencyWalkStopsAtTheBoundWithQueuedWork()
	{
		string directory = Temp.NewDirectory();
		string[] names = new string[64];
		for (int i = 0; i < names.Length; i++)
		{
			names[i] = $"dep{i:D2}.assets";
		}

		Write(directory, "tail.assets", UnityFixtureBuilder.SerializedTextAsset("tail", "tail payload"));
		for (int i = 0; i < names.Length - 1; i++)
		{
			Write(directory, names[i], UnityFixtureBuilder.SerializedTextAsset($"dep{i:D2}", "payload"));
		}

		// The last examined dependency declares one more dependency, which must stay queued and
		// unexamined once the bound is reached.
		Write(directory, names[63],
			UnityFixtureBuilder.SerializedTextAsset("dep63", "payload", dependencies: "tail.assets"));

		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", Payload, dependencies: names));

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		Assert.Equal(64, graph.DependencyCollectionCount);
	}

	[Fact]
	public void DuplicateDependenciesAreBoundedAndLoadedOnce()
	{
		string directory = Temp.NewDirectory();
		Write(directory, "dup.assets", UnityFixtureBuilder.SerializedTextAsset("dup", "dup payload"));

		string[] duplicates = Enumerable.Repeat("dup.assets", 100).ToArray();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", Payload, dependencies: duplicates));

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		Assert.Equal(1, graph.DependencyCollectionCount);
	}

	[Fact]
	public void BundleCollectionIdentityUsesTheProvidedInnerPath()
	{
		string directory = Temp.NewDirectory();
		string bundle = Write(directory, "named.bundle", UnityFixtureBuilder.Bundle(
			("CAB-provided", UnityFixtureBuilder.SerializedTextAsset("inner", "inner payload"))));

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(bundle);

		UnityGraphCollection collection = Assert.Single(graph.Collections.Cast<UnityGraphCollection>());
		Assert.Contains("CAB-provided", collection.Identity, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void SelectedBundleWinsOverADependencyBundleWithTheSameInnerName()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.bundle", UnityFixtureBuilder.Bundle(
			("CAB-shared", UnityFixtureBuilder.SerializedTextAsset("selectedInner", "selected payload", dependencies: "side.bundle"))));
		Write(directory, "side.bundle", UnityFixtureBuilder.Bundle(
			("CAB-shared", UnityFixtureBuilder.SerializedTextAsset("depInner", "dep payload"))));

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		// Both bundles carry an inner collection with the very same name; only the selected one is
		// catalogued.
		Assert.Equal(1, graph.DependencyCollectionCount);
		using UnitySession session = UnitySession.FromGraph(graph);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("selectedInner", entry.Name);

		using MemoryStream stream = new();
		Assert.True(session.Export(0, stream));
		Assert.Equal("selected payload", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
	}

	[Fact]
	public void ContainerDependencyIsLoadedAsABundle()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", Payload, dependencies: "side.bundle"));
		Write(directory, "side.bundle",
			UnityFixtureBuilder.Bundle(("CAB-inner", UnityFixtureBuilder.SerializedTextAsset("inner", "inner payload"))));

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		Assert.Equal(1, graph.DependencyCollectionCount);
	}

	[Fact]
	public void ResourceDependencyContributesNoCollections()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets",
			UnityFixtureBuilder.SerializedTextAsset("fixture", Payload, dependencies: "junk.resource"));
		Write(directory, "junk.resource", [1, 2, 3, 4]);

		using UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);

		Assert.Equal(0, graph.DependencyCollectionCount);
	}

	[Fact]
	public void BundleEnumeratesEveryInternalCollectionWithDuplicatePathIds()
	{
		string directory = Temp.NewDirectory();
		string bundle = Write(directory, "two.bundle", UnityFixtureBuilder.Bundle(
			("CAB-first", UnityFixtureBuilder.SerializedTextAsset("first", "first payload")),
			("CAB-second", UnityFixtureBuilder.SerializedTextAsset("second", "second payload"))));

		using UnitySession session = UnitySession.Open(bundle);

		Assert.Equal(2, session.Entries.Count);
		Assert.Equal(1, session.Entries[0].PathId);
		Assert.Equal(1, session.Entries[1].PathId);
		Assert.Equal(0, session.Entries[0].CollectionIndex);
		Assert.Equal(1, session.Entries[1].CollectionIndex);
		Assert.Equal("first", session.Entries[0].Name);
		Assert.Equal("second", session.Entries[1].Name);

		using MemoryStream first = new();
		Assert.True(session.Export(0, first));
		using MemoryStream second = new();
		Assert.True(session.Export(1, second));
		Assert.Equal("first payload", System.Text.Encoding.UTF8.GetString(first.ToArray()));
		Assert.Equal("second payload", System.Text.Encoding.UTF8.GetString(second.ToArray()));
	}

	[Fact]
	public void TruncatedBundleSignatureIsRejected()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "broken.bundle", UnityFixtureBuilder.TruncatedBundle());

		Assert.Throws<UnityLoadException>(() => UnitySession.Open(selected));
	}

	[Fact]
	public void BundleWithAnUnreadableInternalEntryIsRejected()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "mixed.bundle", UnityFixtureBuilder.Bundle(
			("CAB-good", UnityFixtureBuilder.SerializedTextAsset("good", "good payload")),
			("CAB-bad", UnityFixtureBuilder.TruncatedBundle())));

		Assert.Throws<UnityLoadException>(() => UnitySession.Open(selected));
	}

	[Fact]
	public void NonUnityFileIsRejected()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "plain.txt", System.Text.Encoding.ASCII.GetBytes("just some text, not a unity file"));

		Assert.Throws<UnityLoadException>(() => UnitySession.Open(selected));
	}

	[Fact]
	public void MissingInputFileIsRejected()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "absent.assets");

		Assert.Throws<UnityLoadException>(() => UnitySession.Open(selected));
	}

	[Fact]
	public void UnopenableInputIsRejected()
	{
		string directory = Temp.NewDirectory();
		string illegalPath = directory + Path.DirectorySeparatorChar + "na" + '\0' + "me.assets";

		Assert.Throws<UnityLoadException>(() => UnitySession.Open(string.Empty));
		Assert.Throws<UnityLoadException>(() => UnitySession.Open(directory));
		Assert.Throws<UnityLoadException>(() => UnitySession.Open(illegalPath));
	}

	[Fact]
	public void InputFileHeldOpenExclusivelyIsRejected()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "locked.assets", UnityFixtureBuilder.SerializedTextAsset("fixture", Payload));

		using FileStream exclusive = new(selected, FileMode.Open, FileAccess.Read, FileShare.None);
		Assert.Throws<UnityLoadException>(() => UnitySession.Open(selected));
	}

	[Fact]
	public void CollectionWithAZeroPathIdIsRejected()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "zero.assets", UnityFixtureBuilder.SerializedTextAsset("zero", Payload, pathId: 0));

		Assert.Throws<UnityLoadException>(() => UnitySession.Open(selected));
	}

	[Fact]
	public void RealSessionRejectsExportAfterDispose()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets", UnityFixtureBuilder.SerializedTextAsset("fixture", Payload));
		UnitySession session = UnitySession.Open(selected);
		session.Dispose();

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
	}
}
