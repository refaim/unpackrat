using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class ManifestWriterTests
{
	[Fact]
	public void WritesSchemaCountsAndResources()
	{
		string dir = Temp.NewDirectory();
		ManifestWriter.Write(dir, "2020.1.0", 1, 1, new List<ManifestResource>
		{
			new(ManifestResource.TextureKind, 7, "texture_7.png", 3),
			new(ManifestResource.TextKind, 8, "text_8.bin", 4),
		});

		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, Manifest.FileName)));
		JsonElement root = document.RootElement;
		Assert.Equal(Manifest.Schema, root.GetProperty("schema").GetString());
		Assert.Equal("2020.1.0", root.GetProperty("version").GetString());
		Assert.Equal(2, root.GetProperty("exported").GetInt32());
		Assert.Equal(1, root.GetProperty("textures").GetInt32());
		Assert.Equal(1, root.GetProperty("texts").GetInt32());

		JsonElement first = root.GetProperty("resources")[0];
		Assert.Equal("texture", first.GetProperty("kind").GetString());
		Assert.Equal(7, first.GetProperty("pathId").GetInt64());
		Assert.Equal("texture_7.png", first.GetProperty("file").GetString());
		Assert.Equal(3, first.GetProperty("bytes").GetInt32());
	}
}
