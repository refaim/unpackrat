using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class ResourceExporterTests
{
	private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

	[Fact]
	public void ExportsFirstTextureAndTextAndSkipsUnsupported()
	{
		string dir = Temp.NewDirectory();
		IProbeAsset[] assets =
		[
			new FakeAsset(1),
			new FakeTexture(10, PngBytes, "2021.3.1"),
			new FakeText(20, [9, 8, 7]),
		];

		Assert.Equal(2, ResourceExporter.ExportAssets(assets, dir));
		Assert.Equal(PngBytes, File.ReadAllBytes(Path.Combine(dir, "texture_10.png")));
		Assert.Equal([9, 8, 7], File.ReadAllBytes(Path.Combine(dir, "text_20.bin")));

		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, Manifest.FileName)));
		JsonElement root = document.RootElement;
		Assert.Equal(Manifest.Schema, root.GetProperty("schema").GetString());
		Assert.Equal("2021.3.1", root.GetProperty("version").GetString());
		Assert.Equal(2, root.GetProperty("exported").GetInt32());
		Assert.Equal(1, root.GetProperty("textures").GetInt32());
		Assert.Equal(1, root.GetProperty("texts").GetInt32());
		Assert.Equal(2, root.GetProperty("resources").GetArrayLength());
	}

	[Fact]
	public void TextureConversionFailureDoesNotConsumeTheTextureSlot()
	{
		string dir = Temp.NewDirectory();
		FakeTexture failing = new(10, null);
		FakeTexture good = new(11, PngBytes, "2021.3.1");

		Assert.Equal(1, ResourceExporter.ExportAssets(new IProbeAsset[] { failing, good }, dir));
		Assert.Equal(1, failing.RenderCalls);
		Assert.False(File.Exists(Path.Combine(dir, "texture_10.png")));
		Assert.True(File.Exists(Path.Combine(dir, "texture_11.png")));
	}

	[Fact]
	public void SecondTextureIsIgnoredOnceOneIsExported()
	{
		string dir = Temp.NewDirectory();
		FakeTexture second = new(2, PngBytes);
		IProbeAsset[] assets = [new FakeTexture(1, PngBytes), second, new FakeText(3, [4])];

		Assert.Equal(2, ResourceExporter.ExportAssets(assets, dir));
		Assert.Equal(0, second.RenderCalls);
	}

	[Fact]
	public void UnsupportedAssetAfterTextIsIgnored()
	{
		string dir = Temp.NewDirectory();
		Assert.Equal(1, ResourceExporter.ExportAssets(new IProbeAsset[] { new FakeText(1, [1]), new FakeAsset(2) }, dir));
	}

	[Fact]
	public void StopsScanningOnceBothKindsAreExported()
	{
		string dir = Temp.NewDirectory();
		FakeTexture trailing = new(99, PngBytes);
		IProbeAsset[] assets = [new FakeTexture(1, PngBytes), new FakeText(2, [1]), trailing];

		Assert.Equal(2, ResourceExporter.ExportAssets(assets, dir));
		Assert.Equal(0, trailing.RenderCalls);
	}

	[Fact]
	public void UnsupportedAssetsProduceEmptyManifest()
	{
		string dir = Temp.NewDirectory();

		Assert.Equal(0, ResourceExporter.ExportAssets(new IProbeAsset[] { new FakeAsset(1), new FakeAsset(2) }, dir));

		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, Manifest.FileName)));
		JsonElement root = document.RootElement;
		Assert.Equal("unknown", root.GetProperty("version").GetString());
		Assert.Equal(0, root.GetProperty("exported").GetInt32());
	}

	[Fact]
	public void ScanLimitBoundsExaminedAssets()
	{
		string dir = Temp.NewDirectory();
		FakeTexture texture = new(5, PngBytes);

		Assert.Equal(0, ResourceExporter.ExportAssets(new IProbeAsset[] { new FakeAsset(1), texture }, dir, scanLimit: 1));
		Assert.Equal(0, texture.RenderCalls);
	}

	[Fact]
	public void CreatesOutputDirectoryWhenMissing()
	{
		string dir = Path.Combine(Temp.NewDirectory(), "nested", "out");

		Assert.Equal(1, ResourceExporter.ExportAssets(new IProbeAsset[] { new FakeTexture(3, PngBytes) }, dir));
		Assert.True(File.Exists(Path.Combine(dir, "texture_3.png")));
	}

	[Fact]
	public void UnsupportedTextDoesNotConsumeTheTextSlot()
	{
		string dir = Temp.NewDirectory();
		FakeText failing = new(10, null);
		FakeText good = new(11, [5, 6]);

		Assert.Equal(1, ResourceExporter.ExportAssets(new IProbeAsset[] { failing, good }, dir));
		Assert.False(File.Exists(Path.Combine(dir, "text_10.bin")));
		Assert.Equal([5, 6], File.ReadAllBytes(Path.Combine(dir, "text_11.bin")));
	}

	[Fact]
	public void NullArgumentsAreRejected()
	{
		string dir = Temp.NewDirectory();
		Assert.Throws<ArgumentNullException>(() => ResourceExporter.ExportAssets(null!, dir));
		Assert.Throws<ArgumentNullException>(() => ResourceExporter.ExportAssets(Array.Empty<IProbeAsset>(), null!));
	}
}
