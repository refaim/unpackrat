using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class LegacyProbeResourceTests
{
	[Fact]
	public void TextOnlyFixtureExportsExactBytesManifestAndReleasesInput()
	{
		string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
		byte[] expected = File.ReadAllBytes(Path.Combine(fixtures, "text-only.expected.txt"));
		string input = Path.Combine(Temp.NewDirectory(), "text-only.assets");
		File.Copy(Path.Combine(fixtures, "text-only.assets"), input);
		string output = Temp.NewDirectory();

		int count = UnitySource.LoadAndExport(new[] { input }, output);

		Assert.Equal(1, count);
		Assert.Equal(expected, File.ReadAllBytes(Path.Combine(output, "text_1.bin")));

		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, Manifest.FileName)));
		JsonElement root = document.RootElement;
		Assert.Equal(1, root.GetProperty("exported").GetInt32());
		Assert.Equal(1, root.GetProperty("texts").GetInt32());
		Assert.Equal(0, root.GetProperty("textures").GetInt32());
		Assert.Equal("2021.3.0f1", root.GetProperty("version").GetString());

		using FileStream exclusive = new(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		Assert.Equal(new FileInfo(input).Length, exclusive.Length);
	}

	[Theory]
	[InlineData("directory")]
	[InlineData("zip")]
	[InlineData("multiple")]
	public void CollectiveInputsPreserveTheTextExportContract(string inputKind)
	{
		string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
		string fixture = Path.Combine(fixtures, "text-only.assets");
		byte[] expected = File.ReadAllBytes(Path.Combine(fixtures, "text-only.expected.txt"));
		(string[] paths, string[] handles) = CreateCollectiveInput(inputKind, fixture);
		string output = Temp.NewDirectory();

		int count = UnitySource.LoadAndExport(paths, output);

		Assert.Equal(1, count);
		Assert.Equal(expected, File.ReadAllBytes(Path.Combine(output, "text_1.bin")));
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, Manifest.FileName)));
		JsonElement root = document.RootElement;
		Assert.Equal(1, root.GetProperty("exported").GetInt32());
		Assert.Equal(1, root.GetProperty("texts").GetInt32());
		Assert.Equal(0, root.GetProperty("textures").GetInt32());
		Assert.Equal("2021.3.0f1", root.GetProperty("version").GetString());

		foreach (string handle in handles)
		{
			using FileStream exclusive = new(handle, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
			Assert.Equal(new FileInfo(handle).Length, exclusive.Length);
		}
	}

	private static (string[] Paths, string[] Handles) CreateCollectiveInput(string inputKind, string fixture)
	{
		if (inputKind == "directory")
		{
			string directory = Temp.NewDirectory();
			string directoryFixture = CopyFixture(fixture, directory);
			return (new[] { directory }, new[] { directoryFixture });
		}

		if (inputKind == "zip")
		{
			string contents = Temp.NewDirectory();
			CopyFixture(fixture, contents);
			string archive = Path.Combine(Temp.NewDirectory(), "text-only.zip");
			ZipFile.CreateFromDirectory(contents, archive);
			return (new[] { archive }, new[] { archive });
		}

		if (inputKind == "multiple")
		{
			string first = CopyFixture(fixture, Temp.NewDirectory());
			string second = CopyFixture(fixture, Temp.NewDirectory());
			return (new[] { first, second }, new[] { first, second });
		}

		throw new ArgumentOutOfRangeException(nameof(inputKind));
	}

	private static string CopyFixture(string fixture, string directory)
	{
		string input = Path.Combine(directory, "text-only.assets");
		File.Copy(fixture, input);
		return input;
	}
}
