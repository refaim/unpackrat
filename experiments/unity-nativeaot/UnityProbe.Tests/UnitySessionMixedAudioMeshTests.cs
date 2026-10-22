using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using SharpGLTF.Schema2;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Combined AudioClip + Mesh feature slice: one modern selected .assets file that really carries a
/// streamed AudioClip (ClassID 83) and a triangle Mesh (ClassID 43) must expose BOTH resources, and
/// its lazy exports must preserve a real PCM16-to-WAV clip and a valid triangle GLB. The catalogue
/// assertion uses the reserved numeric audio kind (5, external 6) because the audio enum member is
/// owned by a separate change and is not part of the current production tree.
/// </summary>
public sealed class UnitySessionMixedAudioMeshTests
{
	private const float Tolerance = 1e-5f;

	/// <summary>ClassID 83, the AudioClip (m_Resource streamed sound) record in the mixed fixture.</summary>
	private const int AudioClipClassId = 83;

	/// <summary>ClassID 43, the Mesh record in the mixed fixture.</summary>
	private const int MeshClassId = 43;

	private const string AudioName = "tone";
	private const string MeshName = "tri";
	private const string ResourceName = "mixed.resS";

	private readonly ITestOutputHelper output;

	public UnitySessionMixedAudioMeshTests(ITestOutputHelper output)
	{
		this.output = output;
	}

	/// <summary>
	/// Fixture self-check (expected to pass once compiled): the bytes really deserialize into a
	/// legal modern AudioClip carrying its streamed resource metadata and a legal triangle Mesh, so
	/// a later product failure can never be misreported as a broken fixture.
	/// </summary>
	[Fact]
	public void MixedFixtureDeserializesToRealAudioClipAndMesh()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "mixed.assets");
		byte[] bank = Bank();
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedMixedAudioAndMesh(AudioName, MeshName, ResourceName, (uint)bank.Length));

		using FileBase parsed = UnitySource.ReadFile(selected);
		parsed.ReadContentsRecursively();
		SerializedFile serialized = Assert.IsType<SerializedFile>(parsed);

		ObjectInfo[] objects = serialized.Objects.ToArray();
		Assert.Equal(2, objects.Length);
		Assert.Contains(objects, info => info.TypeID == AudioClipClassId);
		Assert.Contains(objects, info => info.TypeID == MeshClassId);

		using GameBundle bundle = new();
		GameAssetFactory factory = new(new BaseManager(static _ => { }));
		SerializedAssetCollection collection = bundle.AddCollectionFromSerializedFile(serialized, factory);

		IAudioClip clip = Assert.Single(collection.OfType<IAudioClip>());
		Assert.Equal(AudioName, clip.Name.ToString());
		Assert.True(clip.Has_Resource());
		Assert.Equal(ResourceName, clip.Resource!.Source.ToString());
		Assert.Equal((ulong)bank.Length, (ulong)clip.Resource!.Size);

		AssetRipper.SourceGenerated.Classes.ClassID_43.IMesh mesh = Assert.Single(collection.OfType<AssetRipper.SourceGenerated.Classes.ClassID_43.IMesh>());
		Assert.Equal(MeshName, mesh.Name.ToString());
		// The fill helper stores the geometry in the compressed channels: three vertices become nine
		// float coordinate items and the index buffer becomes three triangle items, while the unpacked
		// inline counts stay zero. Verify the packed payload the exporter actually decodes, not the
		// unused inline VertexCount.
		Assert.Equal(9u, mesh.CompressedMesh.Vertices.NumItems);
		Assert.Equal(3u, mesh.CompressedMesh.Triangles.NumItems);
		Assert.NotEmpty(mesh.CompressedMesh.Vertices.Data);
		Assert.NotEmpty(mesh.CompressedMesh.Triangles.Data);
	}

	/// <summary>
	/// The real regression: opening the one mixed selected file must expose exactly two resources by
	/// distinct PathID and correct kinds, then export a real WAV and a real triangle GLB. The first
	/// assertion is the catalogue, so on a tree without the Audio adapter the failure is the missing
	/// audio entry, not a fixture or setup exception.
	/// </summary>
	[Fact]
	public void MixedAudioAndMeshFileExposesBothResourcesAndExportsRealWavAndGlb()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "mixed.assets");
		byte[] bank = Bank();
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedMixedAudioAndMesh(AudioName, MeshName, ResourceName, (uint)bank.Length));
		File.WriteAllBytes(Path.Combine(directory, ResourceName), bank);

		// Persist the exact serialized fixture (and its sibling resource) beside the repository out
		// tree for the deferred native qualification. This runs before the catalogue assertion, so
		// the artifact exists even when the run stops at the expected RED.
		PersistFixture(selected, Path.Combine(directory, ResourceName));

		using UnitySession session = UnitySession.Open(selected);

		// RED on a tree with no Audio adapter: the mixed file exposes only the mesh entry.
		Assert.Equal(2, session.Entries.Count);

		UnityCatalogEntry meshEntry = Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Mesh);
		UnityCatalogEntry audioEntry = Assert.Single(session.Entries, entry => entry.Kind == (UnityEntryKind)5);
		Assert.NotEqual(meshEntry.PathId, audioEntry.PathId);

		output.WriteLine($"audio entry kind={(int)audioEntry.Kind} ext={audioEntry.OutputExtension} pathId={audioEntry.PathId} size={audioEntry.SourceSize}");
		output.WriteLine($"mesh entry kind={(int)meshEntry.Kind} ext={meshEntry.OutputExtension} pathId={meshEntry.PathId}");

		Assert.Equal("wav", audioEntry.OutputExtension);
		Assert.Equal("glb", meshEntry.OutputExtension);

		// Audio: the streamed PCM16 bank must export as a real RIFF/WAVE container with 16-bit PCM
		// samples, following the existing Audio decode assertions.
		using MemoryStream exportedAudio = new();
		Assert.True(session.Export(audioEntry.Index, exportedAudio));
		byte[] wav = exportedAudio.ToArray();
		Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
		Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
		Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)));
		Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
		Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
		// The pinned decoder emits a WAVEFORMATEX header with an 18-byte fmt chunk, so the data chunk
		// does not sit at the canonical 44-byte offset. Locate it from the declared fmt size and
		// assert the real PCM data chunk spans the remaining bytes.
		uint fmtSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(16));
		int dataTag = 20 + (int)fmtSize;
		Assert.Equal("data", Encoding.ASCII.GetString(wav, dataTag, 4));
		Assert.Equal(wav.Length - (dataTag + 8), BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(dataTag + 4)));
		Assert.True(wav.Length > dataTag + 8);

		// Mesh: the triangle must export as a real GLB whose geometry is the converted triangle,
		// following the existing Mesh GLB assertions.
		using MemoryStream exportedMesh = new();
		Assert.True(session.Export(meshEntry.Index, exportedMesh));
		byte[] glb = exportedMesh.ToArray();
		Assert.True(glb.Length > 12);
		Assert.Equal([0x67, 0x6C, 0x54, 0x46], glb[..4]);

		ModelRoot model = ModelRoot.ReadGLB(new MemoryStream(glb));
		Mesh gltfMesh = Assert.Single(model.LogicalMeshes);
		MeshPrimitive primitive = Assert.Single(gltfMesh.Primitives);
		IList<Vector3> positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
		var indices = primitive.GetIndexAccessor().AsIndicesArray();

		Assert.Equal(3, positions.Count);
		Assert.Equal(3, indices.Count);
		HashSet<uint> distinct = [];
		foreach (uint index in indices)
		{
			Assert.InRange(index, 0u, (uint)(positions.Count - 1));
			distinct.Add(index);
		}

		Assert.Equal(3, distinct.Count);

		Vector3[] expected =
		[
			new(0f, 0f, 0f),
			new(-1f, 0f, 0f),
			new(0f, 1f, 0f),
		];
		AssertEquivalentCornerSet(expected, [positions[(int)indices[0]], positions[(int)indices[1]], positions[(int)indices[2]]]);
	}

	private static byte[] Bank()
		=> File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "audio", "pcm16.fsb"));

	/// <summary>
	/// Copies the serialized mixed fixture and its sibling resource into
	/// <c>out/unity-features/combined-red</c> under the repository root, if that root can be found
	/// from the test output directory. A missing root is logged and skipped, never failed.
	/// </summary>
	private void PersistFixture(string assetsPath, string resourcePath)
	{
		string? root = RepositoryRoot();
		if (root is null)
		{
			output.WriteLine("Repository root not found; mixed fixture not persisted.");
			return;
		}

		string target = Path.Combine(root, "out", "unity-features", "combined-red");
		Directory.CreateDirectory(target);
		File.Copy(assetsPath, Path.Combine(target, "mixed.assets"), overwrite: true);
		File.Copy(resourcePath, Path.Combine(target, Path.GetFileName(resourcePath)), overwrite: true);
		output.WriteLine($"Persisted mixed fixture to {target}");
	}

	private static string? RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (Directory.Exists(Path.Combine(directory.FullName, "experiments")) && Directory.Exists(Path.Combine(directory.FullName, "out")))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		return null;
	}

	private static void AssertEquivalentCornerSet(IReadOnlyList<Vector3> expected, IReadOnlyList<Vector3> actual)
	{
		Assert.Equal(expected.Count, actual.Count);
		foreach (Vector3 wanted in expected)
		{
			bool found = false;
			foreach (Vector3 candidate in actual)
			{
				if (Vector3.Distance(wanted, candidate) <= Tolerance)
				{
					found = true;
					break;
				}
			}

			Assert.True(found, $"Expected a corner at {wanted}.");
		}
	}
}
