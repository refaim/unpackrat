using System.IO;
using System.Linq;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Original-path metadata carried from the adapter through the session catalogue: a recorded path is
/// preserved as an owned catalogue value, an absent one becomes the empty string, and a real
/// ResourceManager-container fixture proves AssetRipper's original-path processor actually runs and
/// records a container-metadata path during loading.
/// </summary>
public sealed class UnitySessionPathTests
{
	private static string Write(string directory, string name, byte[] bytes)
	{
		string path = Path.Combine(directory, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	[Fact]
	public void CatalogueCarriesRecordedOriginalPathsAndDefaultsMissingOnesToEmpty()
	{
		FakeGraph graph = new(new FakeCollection(
			"collected",
			new FakeTexture(10, [1], name: "hero", originalPath: "Assets/Resources/hero.png"),
			new FakeText(20, [2], name: "notes", originalPath: "Assets/Resources/notes.txt"),
			new FakeTexture(30, [3], name: "bare"),
			new FakeText(40, [4], name: "plain")));

		using UnitySession session = UnitySession.FromGraph(graph);

		Assert.Equal(
			new[] { "Assets/Resources/hero.png", "Assets/Resources/notes.txt", "", "" },
			session.Entries.Select(entry => entry.OriginalPath));
	}

	[Fact]
	public void AResourceManagerContainerRecordsTheRealOriginalPath()
	{
		// The ResourceManager's serialized container holds a real pointer at the TextAsset, so
		// AssetRipper's original-path processor records an "Assets/Resources/..." path for it. This
		// only holds when the processor actually runs during loading.
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "resources.assets",
			UnityFixtureBuilder.SerializedResourceManagerTextAsset("hero.txt", "fixture", "payload"));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("fixture", entry.Name);
		Assert.False(string.IsNullOrEmpty(entry.OriginalPath));
		Assert.Equal(Path.Join("Assets/Resources", "hero.txt"), entry.OriginalPath);
	}

	[Fact]
	public void ARealSerializedAssetWithoutARecordedPathReportsEmpty()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "selected.assets", UnityFixtureBuilder.SerializedTextAsset("fixture", "payload"));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(string.Empty, entry.OriginalPath);
	}

	[Fact]
	public void ARealBundleWithoutRecordedContainerMetadataReportsEmpty()
	{
		// The tiny bundle fixture declares no asset-container paths, so the original-path processor
		// records nothing and the entry must surface the empty default instead of a synthesised path.
		string directory = Temp.NewDirectory();
		string bundle = Write(directory, "named.bundle", UnityFixtureBuilder.Bundle(
			("Assets/hero.assets", UnityFixtureBuilder.SerializedTextAsset("fixture", "payload"))));

		using UnitySession session = UnitySession.Open(bundle);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(string.Empty, entry.OriginalPath);
	}
}
