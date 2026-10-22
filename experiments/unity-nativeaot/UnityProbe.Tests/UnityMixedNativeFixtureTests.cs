using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Regression for the persisted combined AudioClip + Mesh native fixture: the pair the generator
/// writes must be self-contained (the sidecar it names sits beside it) and must really export both
/// a WAV clip and a triangle GLB, without depending on any earlier test run's directory.
/// </summary>
public sealed class UnityMixedNativeFixtureTests
{
	[Fact]
	public void PersistedMixedPairIsSelfContainedAndExportsWavAndGlb()
	{
		// A fresh directory with no earlier run's artifacts: the persistence must produce the pair
		// from the shared factory and the public PCM16 bank alone.
		string directory = Temp.NewDirectory();
		string selected = UnitySpriteNativeFixtureGenerator.PersistMixedPair(directory);
		Assert.Single(Directory.GetFiles(directory, "mixed*.assets"));

		using UnitySession session = UnitySession.Open(selected);
		Assert.Equal(2, session.Entries.Count);
		UnityCatalogEntry audio = Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.AudioClip);
		UnityCatalogEntry mesh = Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Mesh);
		Assert.NotEqual(audio.PathId, mesh.PathId);

		// RED before the fix: the saved pair's streamed resource was renamed, so the clip is a bin
		// and its export fails instead of producing the real PCM16 WAV.
		Assert.Equal("wav", audio.OutputExtension);
		Assert.Equal("glb", mesh.OutputExtension);

		using MemoryStream exportedAudio = new();
		Assert.True(session.Export(audio.Index, exportedAudio));
		byte[] wav = exportedAudio.ToArray();
		Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
		Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
		Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)));
		Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
		Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
		uint fmtSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(16));
		int dataTag = 20 + (int)fmtSize;
		Assert.Equal("data", Encoding.ASCII.GetString(wav, dataTag, 4));
		Assert.Equal(wav.Length - (dataTag + 8), BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(dataTag + 4)));

		using MemoryStream exportedMesh = new();
		Assert.True(session.Export(mesh.Index, exportedMesh));
		byte[] glb = exportedMesh.ToArray();
		Assert.True(glb.Length > 12);
		Assert.Equal([0x67, 0x6C, 0x54, 0x46], glb[..4]);

		// Self-containment: the sidecar the clip's embedded resource names must sit beside it, so
		// the pair never depends on an earlier run's directory.
		Assert.True(
			File.Exists(Path.Combine(directory, "mixed.resS")),
			"the persisted mixed pair must carry the sidecar its embedded resource names (mixed.resS)");
	}
}
