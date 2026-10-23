using System.IO;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// VideoClip and legacy MovieTexture resource slices: a real serialized clip that streams its media
/// from a sibling resource and a real inline movie texture must each surface as one exportable
/// catalogue entry whose raw source bytes round-trip byte for byte, with the raw range untouched
/// while the catalogue is built.
/// </summary>
public sealed class UnitySessionMediaTests
{
	private static readonly byte[] Media = [0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02, 0x03, 0x04];
	private static readonly byte[] Movie = [0x4F, 0x67, 0x67, 0x53, 0x11, 0x22, 0x33];

	private static string Write(string directory, string name, byte[] bytes)
	{
		string path = Path.Combine(directory, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	[Fact]
	public void StreamedVideoClipOpensAsOneExportableEntryWithItsRawBytes()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "clip.assets",
			UnityFixtureBuilder.SerializedVideoClip("intro", "clip.mp4", "intro.resource", (uint)Media.Length));
		string resource = Write(directory, "intro.resource", Media);

		// Hold the media exclusively open across Open: building the catalogue must not read the
		// streamed video range.
		UnitySession session;
		using (FileStream locked = new(resource, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			session = UnitySession.Open(selected);
		}

		using (session)
		{
			UnityCatalogEntry entry = Assert.Single(session.Entries);
			Assert.Equal(UnityEntryKind.VideoClip, entry.Kind);
			Assert.Equal("intro", entry.Name);
			Assert.Equal((long)Media.Length, entry.SourceSize);
			Assert.Equal("mp4", entry.OutputExtension);

			using MemoryStream exported = new();
			Assert.True(session.Export(0, exported));
			Assert.Equal(Media, exported.ToArray());
		}
	}

	[Fact]
	public void VideoClipWithAMissingSiblingResourceStillListsButExportsNothing()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "clip.assets",
			UnityFixtureBuilder.SerializedVideoClip("intro", "clip.mp4", "absent.resource", (uint)Media.Length));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal((long)Media.Length, entry.SourceSize);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void VideoClipDeclaringAnOversizedPayloadIsRefusedFromMetadataAlone()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "clip.assets",
			UnityFixtureBuilder.SerializedVideoClip("intro", "clip.mp4", "huge.resource", (uint)(Budgets.MaxVideoBytes + 1)));

		using UnitySession session = UnitySession.Open(selected);

		// The oversized declared length is metadata, so the entry still lists with its declared size
		// but the actual content is refused before any allocation.
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(Budgets.MaxVideoBytes + 1, entry.SourceSize);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void VideoClipWithoutDeclaredExternalDataIsNotCatalogued()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "clip.assets",
			UnityFixtureBuilder.SerializedVideoClip("empty", "clip.mp4", "", 0));

		using UnitySession session = UnitySession.Open(selected);

		Assert.Empty(session.Entries);
	}

	[Fact]
	public void VideoClipWithANamedResourceButZeroDeclaredSizeIsNotCatalogued()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "clip.assets",
			UnityFixtureBuilder.SerializedVideoClip("zero", "clip.mp4", "zero.resource", 0));

		using UnitySession session = UnitySession.Open(selected);

		Assert.Empty(session.Entries);
	}

	[Fact]
	public void VideoClipWithAnUnknownContainerExtensionFallsBackToBin()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "clip.assets",
			UnityFixtureBuilder.SerializedVideoClip("intro", "payload.exe", "intro.resource", (uint)Media.Length));
		Write(directory, "intro.resource", Media);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("bin", entry.OutputExtension);
	}

	[Fact]
	public void LegacyMovieTextureOpensAsOneExportableEntryWithItsRawBytes()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "movie.assets", UnityFixtureBuilder.SerializedMovieTexture("logo", Movie));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.MovieTexture, entry.Kind);
		Assert.Equal("logo", entry.Name);
		Assert.Equal((long)Movie.Length, entry.SourceSize);
		Assert.Equal("ogv", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(Movie, exported.ToArray());
	}

	[Fact]
	public void MovieTextureWithoutInlineDataIsNotCatalogued()
	{
		string directory = Temp.NewDirectory();
		string selected = Write(directory, "movie.assets", UnityFixtureBuilder.SerializedMovieTexture("empty", []));

		using UnitySession session = UnitySession.Open(selected);

		Assert.Empty(session.Entries);
	}
}
