using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityTextureNoiseFixtureTests
{
	[Fact]
	public void SavedNoiseTextureIsDeterministicSelfContainedAndExportsItsDeclaredSize()
	{
		string first = Temp.NewDirectory();
		string second = Temp.NewDirectory();
		string selected = UnityTextureNoiseFixtureGenerator.Save(first, 16, 8);
		string repeated = UnityTextureNoiseFixtureGenerator.Save(second, 16, 8);
		Assert.Equal("noise.assets", Path.GetFileName(selected));
		Assert.Equal(File.ReadAllBytes(selected), File.ReadAllBytes(repeated));
		byte[] pixels = File.ReadAllBytes(Path.Combine(first, "noise.resS"));
		Assert.Equal(128, pixels.Length);
		Assert.Equal(pixels, File.ReadAllBytes(Path.Combine(second, "noise.resS")));
		Assert.Equal("69335EC9970E7F6A1E59545675567D396A6E51CC9D1202FB7060415F08ED5B5C",
			Convert.ToHexString(SHA256.HashData(pixels)));

		using UnitySession session = UnitySession.Open(selected);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Texture, entry.Kind);
		Assert.Equal(128, entry.SourceSize);
		Assert.Equal("png", entry.OutputExtension);
		using MemoryStream output = new();
		Assert.True(session.Export(entry.Index, output));
		byte[] png = output.ToArray();
		Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
		Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
		Assert.Equal(8, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
	}
}
