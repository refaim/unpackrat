using System;
using System.IO;
using System.Text;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>Persists and verifies a self-contained AudioClip and Mesh native qualification pair.</summary>
internal static class UnityMixedNativeFixtureGenerator
{
	internal static string PersistMixedPair(string target)
	{
		Directory.CreateDirectory(target);
		byte[] bank = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "audio", "pcm16.fsb"));
		const string ResourceName = "mixed.resS";
		string assetsPath = Path.Combine(target, "mixed.assets");
		File.WriteAllBytes(assetsPath, UnityFixtureBuilder.SerializedMixedAudioAndMesh("tone", "tri", ResourceName, (uint)bank.Length));
		File.WriteAllBytes(Path.Combine(target, ResourceName), bank);
		VerifyMixedPair(assetsPath, ResourceName, target);
		return assetsPath;
	}

	private static void VerifyMixedPair(string assetsPath, string resourceName, string target)
	{
		using UnitySession session = UnitySession.Open(assetsPath);
		UnityCatalogEntry audio = Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.AudioClip);
		UnityCatalogEntry mesh = Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Mesh);
		Assert.Equal("wav", audio.OutputExtension);
		Assert.Equal("glb", mesh.OutputExtension);
		Assert.NotEqual(audio.PathId, mesh.PathId);

		using MemoryStream audioStream = new();
		Assert.True(session.Export(audio.Index, audioStream));
		byte[] wav = audioStream.ToArray();
		Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
		Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));

		using MemoryStream meshStream = new();
		Assert.True(session.Export(mesh.Index, meshStream));
		byte[] glb = meshStream.ToArray();
		Assert.True(glb.Length > 12);
		Assert.Equal([0x67, 0x6C, 0x54, 0x46], glb[..4]);

		StringBuilder oracle = new();
		oracle.AppendLine("selected=mixed.assets");
		oracle.AppendLine($"sidecar={resourceName}");
		oracle.AppendLine("entry[0]=AudioClip kind=5 external=6 extension=wav");
		oracle.AppendLine("entry[1]=Mesh kind=6 external=7 extension=glb");
		File.WriteAllText(Path.Combine(target, "mixed.expected.txt"), oracle.ToString());
	}
}
