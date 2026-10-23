using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Hermetic end-to-end proof of the managed entry point. The committed
/// <c>fixtures/text-only.assets</c> fixture is copied next to the test binary by the project file
/// and driven through the real <see cref="UnitySource.LoadAndExport"/> pipeline, so no private
/// corpus or environment variable is required.
/// </summary>
public sealed class RealGameIntegrationTests
{
	[Fact]
	public void TextOnlyFixtureExportsExactBytesAndManifestThroughThePipeline()
	{
		string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
		string assetPath = Path.Combine(fixtures, "text-only.assets");
		byte[] expected = File.ReadAllBytes(Path.Combine(fixtures, "text-only.expected.txt"));

		string output = Temp.NewDirectory();
		int count = UnitySource.LoadAndExport(new[] { assetPath }, output);

		// One TextAsset (path id 1) is the only exportable resource in the fixture.
		Assert.Equal(1, count);
		Assert.Equal(expected, File.ReadAllBytes(Path.Combine(output, "text_1.bin")));

		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, Manifest.FileName)));
		JsonElement root = document.RootElement;
		Assert.Equal(1, root.GetProperty("exported").GetInt32());
		Assert.Equal(1, root.GetProperty("texts").GetInt32());
		Assert.Equal(0, root.GetProperty("textures").GetInt32());
		Assert.Equal("2021.3.0f1", root.GetProperty("version").GetString());
	}
}
