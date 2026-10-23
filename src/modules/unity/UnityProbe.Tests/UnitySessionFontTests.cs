using System.IO;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Font resource feature slice: a real serialized embedded Font must surface as exactly one
/// exportable catalogue entry whose raw source bytes round-trip byte for byte.
/// </summary>
public sealed class UnitySessionFontTests
{
	/// <summary>A tiny TrueType-signed payload standing in for embedded source font data.</summary>
	private static readonly byte[] FontBytes =
	[
		0x00, 0x01, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x80,
		0x00, 0x03, 0x00, 0x40, 0x00, 0x42, 0x7A, 0x1F,
	];

	[Fact]
	public void EmbeddedFontOpensAsOneExportableFontEntry()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "font.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedFont("legacy", FontBytes));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal((UnityEntryKind)2, entry.Kind);
		Assert.Equal("legacy", entry.Name);
		Assert.Equal((long)FontBytes.Length, entry.SourceSize);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(FontBytes, exported.ToArray());
	}

	[Theory]
	[InlineData("otto", "otf")]
	[InlineData("ttcf", "ttc")]
	[InlineData("truetype", "ttf")]
	[InlineData("short", "ttf")]
	public void FontOutputExtensionFollowsTheFontFamilyAndStaysInTheAbiAlphabet(string family, string expected)
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "font.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedFont("family", FamilyBytes(family)));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(expected, entry.OutputExtension);
	}

	[Fact]
	public void FontWithoutEmbeddedBytesIsNotCatalogued()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "font.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedFont("empty", []));

		using UnitySession session = UnitySession.Open(selected);

		Assert.Empty(session.Entries);
	}

	private static byte[] FamilyBytes(string family) => family switch
	{
		"otto" => [0x4F, 0x54, 0x54, 0x4F, 0x00, 0x01, 0x02, 0x03],
		"ttcf" => [0x74, 0x74, 0x63, 0x66, 0x00, 0x01, 0x00, 0x00],
		"truetype" => [0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00],
		_ => [0x00, 0x01],
	};
}
