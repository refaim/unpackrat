using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SharpGLTF.Schema2;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Mesh resource slice: a real serialized mesh must surface as exactly one exportable catalogue
/// entry whose output is a real, parsable binary glTF with the mesh's own geometry. Positions are
/// asserted after the upstream Unity-to-gltf handedness conversion; nothing here inspects the
/// production helper directly, only the exported bytes.
/// </summary>
public sealed class UnitySessionMeshTests
{
	private const float Tolerance = 1e-5f;

	private readonly ITestOutputHelper output;

	public UnitySessionMeshTests(ITestOutputHelper output)
	{
		this.output = output;
	}

	[Fact]
	public void TriangleMeshOpensAsOneExportableMeshEntry()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "mesh.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedTriangleMesh("tri"));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal((UnityEntryKind)6, entry.Kind);
		Assert.Equal("tri", entry.Name);
		Assert.Equal("glb", entry.OutputExtension);
		// The fixture stores its geometry compressed, so the inline source payload is unknown and
		// the catalogue reports the honest zero rather than the GLB length.
		Assert.Equal(0L, entry.SourceSize);
	}

	[Fact]
	public void ExportingATriangleMeshWritesAParsableGlbWithTheConvertedTriangle()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "mesh.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedTriangleMesh("tri"));

		using UnitySession session = UnitySession.Open(selected);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		byte[] payload = exported.ToArray();

		// The bytes are a real GLB container, not merely a magic header.
		Assert.True(payload.Length > 12);
		Assert.Equal([0x67, 0x6C, 0x54, 0x46], payload[..4]);

		ModelRoot model = ModelRoot.ReadGLB(new MemoryStream(payload));
		Mesh mesh = Assert.Single(model.LogicalMeshes);
		MeshPrimitive primitive = Assert.Single(mesh.Primitives);

		IList<Vector3> positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
		var indices = primitive.GetIndexAccessor().AsIndicesArray();

		output.WriteLine($"GLB bytes={payload.Length}");
		output.WriteLine($"positions={positions.Count} indices={indices.Count}");
		for (int i = 0; i < positions.Count; i++)
		{
			output.WriteLine($"  position[{i}]=({positions[i].X},{positions[i].Y},{positions[i].Z})");
		}
		for (int i = 0; i < indices.Count; i++)
		{
			output.WriteLine($"  index[{i}]={indices[i]}");
		}

		// Upstream converts Unity's left-handed space to glTF's right-handed space by negating X,
		// so (0,0,0)-(1,0,0)-(0,1,0) becomes (0,0,0)-(-1,0,0)-(0,1,0). The triangle winding is
		// reversed on export, so the corner set, not the order, is compared.
		Vector3[] expected =
		[
			new(0f, 0f, 0f),
			new(-1f, 0f, 0f),
			new(0f, 1f, 0f),
		];

		Assert.Equal(3, positions.Count);
		Assert.Equal(3, indices.Count);
		HashSet<uint> distinct = [];
		foreach (uint index in indices)
		{
			Assert.InRange(index, 0u, (uint)(positions.Count - 1));
			distinct.Add(index);
		}
		Assert.Equal(3, distinct.Count);

		AssertEquivalentCornerSet(expected, [positions[(int)indices[0]], positions[(int)indices[1]], positions[(int)indices[2]]]);
	}

	[Fact]
	public void MeshWithoutSubMeshesStillListsButExportsNothing()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "mesh.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedMeshWithoutSubMeshes("loose"));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Mesh, entry.Kind);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void MeshDeclaringAnAbsurdVertexCountIsRefusedFromMetadataAlone()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "mesh.assets");
		File.WriteAllBytes(selected,
			UnityFixtureBuilder.SerializedMeshDeclaringVertices("huge", (uint)(Budgets.MaxMeshVertices + 1)));

		using UnitySession session = UnitySession.Open(selected);

		// The absurd declared count is metadata, so the entry still lists, but the geometry is
		// refused before any allocation.
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(UnityEntryKind.Mesh, entry.Kind);
		Assert.Equal("glb", entry.OutputExtension);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void MeshWithoutGeometryIsNotCatalogued()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "mesh.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedEmptyMesh("blank"));

		using UnitySession session = UnitySession.Open(selected);

		Assert.Empty(session.Entries);
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
