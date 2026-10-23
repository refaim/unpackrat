using AssetRipper.Assets.Collections;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Decision table for the mesh export budget guard, checked before the GLB builder runs. Every case
/// sets a declared metadata count or size on a real upstream mesh through the factory setters only,
/// so the guard is exercised without decompressing a single channel or allocating a decoder array.
/// </summary>
public sealed class MeshBudgetGuardTests
{
	private const uint AbsurdItems = 1_000_000_000u;

	[Fact]
	public void AMinimalMeshIsAccepted()
	{
		Assert.True(UnitySource.IsMeshWithinBudget(NewMesh()));
	}

	[Theory]
	[InlineData("Vertices")]
	[InlineData("UV")]
	[InlineData("Normals")]
	[InlineData("NormalSigns")]
	[InlineData("Tangents")]
	[InlineData("TangentSigns")]
	[InlineData("Weights")]
	[InlineData("BoneIndices")]
	[InlineData("Triangles")]
	[InlineData("FloatColors")]
	public void ASingleInflatedChannelIsRefused(string channel)
	{
		IMesh mesh = NewMesh();
		Inflate(mesh, channel);

		Assert.False(UnitySource.IsMeshWithinBudget(mesh));
	}

	[Theory]
	[InlineData("BindPoses")]
	[InlineData("Colors")]
	public void ALegacyInflatedChannelIsRefused(string channel)
	{
		IMesh mesh = NewMesh("4.3.0f1");
		Inflate(mesh, channel);

		Assert.False(UnitySource.IsMeshWithinBudget(mesh));
	}

	[Fact]
	public void AnAbsurdDeclaredVertexCountIsRefused()
	{
		IMesh mesh = NewMesh();
		mesh.VertexData.VertexCount = (uint)(Budgets.MaxMeshVertices + 1);

		Assert.False(UnitySource.IsMeshWithinBudget(mesh));
	}

	[Fact]
	public void ATinyInlineMeshKeepsAnUnusedOversizedStreamedSize()
	{
		IMesh mesh = NewMesh();
		mesh.VertexData.Data = [1, 2, 3];
		mesh.StreamData.Path = new Utf8String("mesh.resS");
		mesh.StreamData.Size = (uint)(Budgets.MaxMeshSourceBytes + 1);

		// The real builder path (VertexDataBlob.Create -> GetVertexDataBytes) prefers the nonempty
		// inline vertex bytes over the streamed descriptor, so an unused oversized declaration
		// cannot convert the mesh; it must remain accepted.
		Assert.True(UnitySource.IsMeshWithinBudget(mesh));
	}

	[Fact]
	public void CompressedIndicesAreBoundedAtFourBytesEach()
	{
		// 134217728 indices are exactly the 256 MiB cap at two bytes each, but the pinned decoder
		// always materialises them as a uint[], so the real allocation is 512 MiB and must be refused.
		IMesh mesh = NewMesh();
		mesh.CompressedMesh.Triangles.NumItems = 134_217_728u;

		Assert.False(UnitySource.IsMeshWithinBudget(mesh));
	}

	[Fact]
	public void AnActiveStreamedResourceIsBoundedByItsDeclaredSize()
	{
		IMesh mesh = NewMesh();
		mesh.StreamData.Path = new Utf8String("mesh.resS");
		mesh.StreamData.Size = 1024;

		Assert.True(UnitySource.IsMeshWithinBudget(mesh));

		mesh.StreamData.Size = (uint)(Budgets.MaxMeshSourceBytes + 1);
		Assert.False(UnitySource.IsMeshWithinBudget(mesh));
	}

	[Fact]
	public void AnUnrepresentableStreamedSizeIsRefusedRatherThanTreatedAsUnknown()
	{
		IMesh mesh = NewMesh();
		mesh.StreamData.Path = new Utf8String("mesh.resS");
		mesh.StreamData.Size = uint.MaxValue;

		Assert.False(UnitySource.IsMeshWithinBudget(mesh));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	public void TheDecodedChannelByteCapIsExact(int overBy)
	{
		long itemsAtCap = Budgets.MaxMeshDecodedBytes / Budgets.MeshDecodedBytesPerItem;
		IMesh mesh = NewMesh();
		mesh.CompressedMesh.Normals.NumItems = (uint)(itemsAtCap + overBy);

		Assert.Equal(overBy == 0, UnitySource.IsMeshWithinBudget(mesh));
	}

	[Fact]
	public void AnUncompressedSkinListCrossesTheDecodedCapAtTheBoundary()
	{
		// Versions before 2018.2 carry an unpacked skin list the decoder copies on its own. The
		// compressed channels already sit exactly on the cap, so the mesh is otherwise accepted;
		// one copied skin item must push the aggregate over it.
		long itemsAtCap = Budgets.MaxMeshDecodedBytes / Budgets.MeshDecodedBytesPerItem;
		IMesh mesh = NewMesh("5.0.0f1");
		mesh.CompressedMesh.Normals.NumItems = (uint)itemsAtCap;
		mesh.Skin.AddNew();

		Assert.False(UnitySource.IsMeshWithinBudget(mesh));
	}

	[Fact]
	public void AnUncompressedBindPoseListCrossesTheDecodedCapAtTheBoundary()
	{
		// Modern meshes still carry an unpacked bind-pose list even though the skin field is gone;
		// one copied bind pose must push the otherwise accepted mesh over the cap.
		long itemsAtCap = Budgets.MaxMeshDecodedBytes / Budgets.MeshDecodedBytesPerItem;
		IMesh mesh = NewMesh();
		mesh.CompressedMesh.Normals.NumItems = (uint)itemsAtCap;
		mesh.BindPose.AddNew();

		Assert.False(UnitySource.IsMeshWithinBudget(mesh));
	}

	[Theory]
	[InlineData(1)]
	[InlineData(256)]
	public void PracticalUncompressedListsAreAccepted(int count)
	{
		IMesh mesh = NewMesh("5.0.0f1");
		for (int i = 0; i < count; i++)
		{
			mesh.Skin.AddNew();
			mesh.BindPose.AddNew();
		}

		Assert.True(UnitySource.IsMeshWithinBudget(mesh));
	}

	[Theory]
	[InlineData(0, true)]
	[InlineData(1, false)]
	public void TheInlineIndexBufferStillCountsAgainstTheStreamedSourceCap(int indexBytes, bool expected)
	{
		// The streamed vertex payload sits exactly on the source cap; the inline index byte is
		// always added, so a single byte pushes the combined copy over it and must be refused.
		IMesh mesh = NewMesh();
		mesh.StreamData.Path = new Utf8String("mesh.resS");
		mesh.StreamData.Size = (uint)Budgets.MaxMeshSourceBytes;
		mesh.IndexBuffer = new byte[indexBytes];

		Assert.Equal(expected, UnitySource.IsMeshWithinBudget(mesh));
	}

	private static IMesh NewMesh(string version = "2022.3.0f1")
	{
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(UnityVersion.Parse(version));
		return collection.CreateMesh();
	}

	private static void Inflate(IMesh mesh, string channel)
	{
		switch (channel)
		{
			case "Vertices":
				mesh.CompressedMesh.Vertices.NumItems = AbsurdItems;
				break;
			case "UV":
				mesh.CompressedMesh.UV.NumItems = AbsurdItems;
				break;
			case "Normals":
				mesh.CompressedMesh.Normals.NumItems = AbsurdItems;
				break;
			case "NormalSigns":
				mesh.CompressedMesh.NormalSigns.NumItems = AbsurdItems;
				break;
			case "Tangents":
				mesh.CompressedMesh.Tangents.NumItems = AbsurdItems;
				break;
			case "TangentSigns":
				mesh.CompressedMesh.TangentSigns.NumItems = AbsurdItems;
				break;
			case "Weights":
				mesh.CompressedMesh.Weights.NumItems = AbsurdItems;
				break;
			case "BoneIndices":
				mesh.CompressedMesh.BoneIndices.NumItems = AbsurdItems;
				break;
			case "Triangles":
				mesh.CompressedMesh.Triangles.NumItems = AbsurdItems;
				break;
			case "FloatColors":
				mesh.CompressedMesh.FloatColors.NumItems = AbsurdItems;
				break;
			case "BindPoses":
				mesh.CompressedMesh.BindPoses.NumItems = AbsurdItems;
				break;
			case "Colors":
				mesh.CompressedMesh.Colors.NumItems = AbsurdItems;
				break;
			default:
				throw new System.ArgumentOutOfRangeException(nameof(channel), channel, "Unknown channel.");
		}
	}
}
