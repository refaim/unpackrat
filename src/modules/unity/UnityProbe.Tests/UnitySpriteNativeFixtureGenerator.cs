using System;
using System.IO;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Test-only persistence for the deferred native qualification: it writes a few already-covered
/// serialized Sprite fixtures (and the combined AudioClip+Mesh fixture) plus their independent
/// expected pixel/dimension oracle under out/unity-features/final-managed/native-fixtures, so the
/// native Observer DLL can be pixel-compared later. It reuses the shared fixture builder and a
/// local copy of the pixel oracle and adds no product API, format or infrastructure.
/// </summary>
public sealed class UnitySpriteNativeFixtureGenerator
{
	private readonly ITestOutputHelper output;

	public UnitySpriteNativeFixtureGenerator(ITestOutputHelper output)
	{
		this.output = output;
	}

	/// <summary>A unique colour per pixel index, the same oracle the Sprite slice uses.</summary>
	private static byte[] Color(int index) =>
		[(byte)(1 + (index * 7)), (byte)(100 + (index * 5)), (byte)(250 - (index * 3)), 255];

	private static byte[] RawPixels(int width, int height)
	{
		byte[] data = new byte[width * height * 4];
		for (int index = 0; index < width * height; index++)
		{
			Color(index).CopyTo(data, index * 4);
		}

		return data;
	}

	/// <summary>
	/// The decoded crop oracle: the upstream reader flips Y and a texture rect is measured from the
	/// bottom-left, so raw row <c>row</c> becomes top-down row <c>height-1-row</c>.
	/// </summary>
	private static byte[] ExpectedCrop(int textureWidth, int textureHeight, int rectX, int rectY, int cropWidth, int cropHeight)
	{
		byte[] expected = new byte[cropWidth * cropHeight * 4];
		int topDownTop = textureHeight - rectY - cropHeight;
		for (int y = 0; y < cropHeight; y++)
		{
			int rawRow = textureHeight - 1 - (topDownTop + y);
			for (int x = 0; x < cropWidth; x++)
			{
				Color((rawRow * textureWidth) + rectX + x).CopyTo(expected, (((y * cropWidth) + x) * 4));
			}
		}

		return expected;
	}

	/// <summary>The right-triangle sprite-local points of the tight fixtures.</summary>
	private static readonly float[] Triangle = [-1.5f, -1.5f, 1.5f, -1.5f, -1.5f, 1.5f];

	/// <summary>The three-by-three tight bounding box with the lower-left half retained.</summary>
	private static readonly int[] TightKeep = [0, 3, 4, 6, 7, 8];

	[Fact]
	public void PersistSpriteNativeFixtures()
	{
		string? root = RepositoryRoot();
		Assert.NotNull(root);
		string target = Path.Combine(root!, "out", "unity-features", "final-managed", "native-fixtures");
		Directory.CreateDirectory(target);

		// 1. Basic crop: a 2x3 region of a 4x4 texture, exported as its own standalone PNG.
		Persist(
			target,
			"sprite-crop",
			UnityFixtureBuilder.SerializedSprite("hero", RawPixels(4, 4), 4, 4, 1, 1, 2, 3, 1, 1, 2, 3, 0, 0),
			"hero",
			2,
			3,
			ExpectedCrop(4, 4, 1, 1, 2, 3));

		// 2. Tight sprite: only the pixels inside the mesh triangle survive the rectangular crop.
		byte[] tightFile = UnityFixtureBuilder.SerializedSprite(
			"hero", RawPixels(4, 4), 4, 4, 0, 0, 3, 3, 0, 0, 3, 3, 0, 0,
			settingsRaw: 0u, pixelsToUnits: 1f, tightMesh: new UnityFixtureBuilder.TightMeshOptions(Triangle));
		byte[] tightCrop = ExpectedCrop(4, 4, 0, 0, 3, 3);
		byte[] tightExpected = new byte[3 * 3 * 4];
		foreach (int index in TightKeep)
		{
			Array.Copy(tightCrop, index * 4, tightExpected, index * 4, 4);
		}

		Persist(target, "sprite-tight", tightFile, "hero", 3, 3, tightExpected);

		// 3. Atlas sprite: the render-data key resolves a 2x3 region of the dependency atlas.
		(byte[] spriteFile, byte[] atlasFile) = UnityFixtureBuilder.SerializedAtlasSprite(
			"hero", RawPixels(2, 2), 2, 2, RawPixels(4, 4), 4, 4, 1, 1, 2, 3, 0, 0, 2u, 7, 7);
		File.WriteAllBytes(Path.Combine(target, UnityFixtureBuilder.AtlasDependencyName), atlasFile);
		Persist(target, "sprite-atlas", spriteFile, "hero", 2, 3, ExpectedCrop(4, 4, 1, 1, 2, 3));

		// 4. The combined AudioClip + Mesh fixture, built directly so the persisted pair is
		// self-contained and never depends on an earlier run's directory.
		PersistMixedPair(target);

		Assert.True(File.Exists(Path.Combine(target, "sprite-crop.assets")));
		Assert.True(File.Exists(Path.Combine(target, "sprite-tight.assets")));
		Assert.True(File.Exists(Path.Combine(target, "sprite-atlas.assets")));
		output.WriteLine($"Persisted sprite native fixtures to {target}");
	}

	/// <summary>
	/// Writes the combined AudioClip + Mesh pair directly from the shared factory and the public
	/// PCM16 bank: the selected file plus the sidecar named exactly as its embedded streamed
	/// resource, an entry/oracle mapping, and a real WAV + GLB export verification of both entries.
	/// It reads no earlier run's directory, so the persisted pair is always self-contained. Returns
	/// the persisted selected file path.
	/// </summary>
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

	/// <summary>
	/// Verifies a persisted combined AudioClip + Mesh pair by opening it and really exporting both
	/// entries, so a mis-named sidecar can never be persisted silently, and writes the entry/oracle
	/// mapping beside the pair.
	/// </summary>
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

	/// <summary>
	/// Writes one serialized selected file plus its expected-entry and pixel-dimension oracle. The
	/// oracle is the local independent crop computation, not the production export.
	/// </summary>
	private void Persist(string target, string stem, byte[] serialized, string entryName, int width, int height, byte[] expected)
	{
		string assetsPath = Path.Combine(target, stem + ".assets");
		File.WriteAllBytes(assetsPath, serialized);
		StringBuilder oracle = new();
		oracle.AppendLine($"selected={stem}.assets");
		oracle.AppendLine("kind=Sprite");
		oracle.AppendLine("extension=png");
		oracle.AppendLine($"entry={entryName}");
		oracle.AppendLine($"width={width}");
		oracle.AppendLine($"height={height}");
		oracle.Append("rgba=").AppendLine(Convert.ToHexString(expected));
		File.WriteAllText(Path.Combine(target, stem + ".expected.txt"), oracle.ToString());

		// Verify the persisted artifact in-process against the same independent oracle, so a broken
		// fixture is caught here rather than by the deferred native pixel comparison.
		using UnitySession session = UnitySession.Open(assetsPath);
		UnityCatalogEntry entry = Assert.Single(session.Entries, candidate => candidate.Kind == UnityEntryKind.Sprite);
		Assert.Equal(entryName, entry.Name);
		Assert.Equal("png", entry.OutputExtension);
		using MemoryStream exported = new();
		Assert.True(session.Export(entry.Index, exported));
		(int decodedWidth, int decodedHeight, byte[] rgba) = PngDecoder.Decode(exported.ToArray());
		Assert.Equal(width, decodedWidth);
		Assert.Equal(height, decodedHeight);
		Assert.Equal(expected, rgba);
		output.WriteLine($"{stem}: {decodedWidth}x{decodedHeight} sprite entry verified");
	}

	private static string? RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (Directory.Exists(Path.Combine(directory.FullName, "src", "modules", "unity")) && Directory.Exists(Path.Combine(directory.FullName, "out")))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		return null;
	}
}
