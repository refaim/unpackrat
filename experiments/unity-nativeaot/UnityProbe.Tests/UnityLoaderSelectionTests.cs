using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityLoaderSelectionTests
{
	[Fact]
	public void PersistTinyScriptAndResourceFixturesForNativeQualification()
	{
		DirectoryInfo? root = new(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "build.ps1")))
		{
			root = root.Parent;
		}
		Assert.NotNull(root);
		string target = Path.Combine(root.FullName, "out", "unity-speed", "fixtures");
		Directory.CreateDirectory(target);
		byte[] assets = UnityFixtureBuilder.SerializedResourceManagerTextAsset("hero.txt", "hero", "payload", 114);
		File.WriteAllBytes(Path.Combine(target, "script.assets"), assets);
		File.WriteAllBytes(Path.Combine(target, "script.unity3d"), UnityFixtureBuilder.CompressedBundle(("CAB-selected", assets)));
		byte[] expected = Encoding.UTF8.GetBytes("payload");
		File.WriteAllBytes(Path.Combine(target, "hero.bin"), expected);
		foreach (string file in new[] { "script.assets", "script.unity3d" })
		{
			using UnitySession session = UnitySession.Open(Path.Combine(target, file));
			UnityCatalogEntry entry = Assert.Single(session.Entries);
			Assert.Equal(Path.Join("Assets", "Resources", "hero.txt"), entry.OriginalPath);
			using MemoryStream output = new();
			Assert.True(session.Export(0, output));
			Assert.Equal(expected, output.ToArray());
		}
	}

	[Theory]
	[InlineData(false, 114)]
	[InlineData(true, 114)]
	[InlineData(false, -1)]
	[InlineData(true, -1)]
	public void ScriptIdentitiesAndResourcePathsAndPayloadsSurvive(bool bundled, int scriptClassId)
	{
		string directory = Temp.NewDirectory();
		byte[] selectedBytes = UnityFixtureBuilder.SerializedResourceManagerTextAsset("hero.txt", "hero", "payload", scriptClassId);
		string selected = Path.Combine(directory, bundled ? "selected.unity3d" : "selected.assets");
		File.WriteAllBytes(selected, bundled ? UnityFixtureBuilder.CompressedBundle(("CAB-selected", selectedBytes)) : selectedBytes);

		IUnityGraph graph = UnitySource.OpenGraph(selected);
		// Keep the script's identity placeholder, so the upstream collection still checks path IDs.
		Assert.Equal(3, Assert.Single(graph.Collections).Assets.Count);
		using UnitySession session = UnitySession.FromGraph(graph);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("hero", entry.Name);
		Assert.Equal(Path.Join("Assets", "Resources", "hero.txt"), entry.OriginalPath);
		using MemoryStream output = new();
		Assert.True(session.Export(entry.Index, output));
		Assert.Equal("payload", Encoding.UTF8.GetString(output.ToArray()));
	}

	[Theory]
	[InlineData(false, 0)]
	[InlineData(true, 0)]
	[InlineData(false, 1)]
	[InlineData(true, 1)]
	public void InvalidScriptIdentitiesStillRejectTheSelectedInput(bool bundled, long pathId)
	{
		string directory = Temp.NewDirectory();
		byte[] bytes = UnityFixtureBuilder.SerializedResourceManagerTextAsset("hero.txt", "hero", "payload", 114, pathId);
		string selected = Path.Combine(directory, bundled ? "bad.unity3d" : "bad.assets");
		File.WriteAllBytes(selected, bundled ? UnityFixtureBuilder.Bundle(("CAB-bad", bytes)) : bytes);
		Assert.Throws<UnityLoadException>(() => UnitySession.Open(selected));
		using FileStream exclusive = new(selected, FileMode.Open, FileAccess.Read, FileShare.None);
	}

	[Theory]
	[InlineData(false, 0)]
	[InlineData(true, 0)]
	[InlineData(false, 1)]
	[InlineData(true, 1)]
	public void InvalidScriptIdentitiesStillDiscardADeclaredDependency(bool bundled, long pathId)
	{
		string directory = Temp.NewDirectory();
		string dependencyName = bundled ? "side.unity3d" : "side.assets";
		byte[] dependency = UnityFixtureBuilder.SerializedResourceManagerTextAsset("side.txt", "side", "side payload", 114, pathId);
		File.WriteAllBytes(Path.Combine(directory, dependencyName), bundled
			? UnityFixtureBuilder.Bundle(("CAB-side", dependency)) : dependency);
		string selected = Path.Combine(directory, "selected.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedTextAsset("selected", "selected payload", dependencies: dependencyName));

		UnityGraph graph = (UnityGraph)UnitySource.OpenGraph(selected);
		// A standalone collection is registered before its body fails; a bundle is attached only
		// after all its collections succeed. Preserve that existing loader behavior as well.
		Assert.Equal(bundled ? 0 : 1, graph.DependencyCollectionCount);
		using UnitySession session = UnitySession.FromGraph(graph);
		Assert.Equal("selected", Assert.Single(session.Entries).Name);
		using MemoryStream output = new();
		Assert.True(session.Export(0, output));
		Assert.Equal("selected payload", Encoding.UTF8.GetString(output.ToArray()));
	}
}
