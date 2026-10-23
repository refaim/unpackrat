using System;
using System.IO;
using AssetRipper.Export.Modules.Models;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.CompressedMesh;
using SharpGLTF.Schema2;
using SharpGLTF.Scenes;

namespace Unpackrat.UnityProbe;

/// <summary>
/// Mesh adapter for the additional resource kinds. Like the font and media adapters it is one of
/// the only places these AssetRipper types are touched: it resolves catalogue metadata from declared
/// fields alone and defers the GLB conversion to an explicit export.
/// </summary>
internal static partial class UnitySource
{
	private sealed class MeshAsset(IMesh mesh) : IProbeResource
	{
		public UnityEntryKind Kind => UnityEntryKind.Mesh;
		public long PathId => mesh.PathID;
		public string Name => mesh.Name.ToString();
		public string? OriginalPath => mesh.OriginalPath;

		// Metadata only: neither the compressed channel bodies nor a streamed resource are read or
		// decompressed here, so the declared length never converts the mesh.
		public long SourceSize => DeclaredMeshSourceSize(mesh);

		public string OutputExtension => OutputExtensions.MeshGlb;

		public byte[]? ExportData()
		{
			// Refuse a hostile or absurd mesh from declared metadata before anything is decompressed
			// or a GLB buffer is sized by it.
			if (!IsMeshWithinBudget(mesh))
			{
				return null;
			}

			// GlbMeshBuilder.Build yields the upstream scene graph; writing it straight to GLB is
			// the mature SharpGLTF path. AssetRipper's own GlbWriter convenience wrapper maps a real
			// OutOfMemoryException to a "false", so it is deliberately bypassed to keep a memory
			// shortage fatal instead of silently reported as a failed export.
			SceneBuilder sceneBuilder = GlbMeshBuilder.Build(mesh);
			ModelRoot model = sceneBuilder.ToGltf2();
			if (model.LogicalMeshes.Count == 0)
			{
				// The mesh declared geometry but none of it could be materialised into a GLB mesh;
				// report a failed export rather than writing an empty model.
				return null;
			}

			using MemoryStream stream = new();
			model.WriteGLB(stream);
			return stream.ToArray();
		}
	}

	/// <summary>
	/// Reports from metadata alone whether a mesh declares geometry. A provably empty placeholder
	/// (no vertex data at all) is excluded; a present mesh whose payload cannot be materialised
	/// stays visible so it fails at export instead of disappearing silently.
	/// </summary>
	private static bool HasMeshGeometry(IMesh mesh) => mesh.HasAnyVertices();

	/// <summary>
	/// The declared inline source payload: the mesh's own vertex-channel bytes plus its index
	/// buffer, both read from metadata alone. A compressed or streamed channel body is not
	/// decompressed or read while the catalogue is built, so its contribution stays unknown (zero)
	/// here rather than being claimed as a GLB length.
	/// </summary>
	private static long DeclaredMeshSourceSize(IMesh mesh)
	{
		return mesh.VertexData.Data.LongLength + mesh.IndexBuffer.LongLength;
	}

	/// <summary>
	/// Refuses a hostile or absurd mesh from declared metadata before anything is decompressed or a
	/// GLB buffer is sized by it. Every declared count and byte length is read without converting
	/// or decompressing a single channel.
	/// </summary>
	internal static bool IsMeshWithinBudget(IMesh mesh)
	{
		return Budgets.IsWithinMeshBudget(
			mesh.SubMeshes.Count,
			DeclaredMeshVertexCount(mesh),
			DeclaredMeshDecodedBytes(mesh),
			DeclaredMeshIndexBytes(mesh),
			DeclaredMeshSourceBytes(mesh));
	}

	/// <summary>The declared vertex count, from the inline vertex data or the compressed triple vector.</summary>
	private static long DeclaredMeshVertexCount(IMesh mesh)
	{
		return Math.Max((long)mesh.VertexData.VertexCount, (long)mesh.CompressedMesh.Vertices.NumItems / 3);
	}

	/// <summary>
	/// A safe upper bound on the decoded bytes every packed channel and every unpacked list
	/// materialises, estimated from the declared counts alone. The pinned decoder allocates one
	/// array per declared item on each compressed channel and independently copies the unpacked
	/// <c>Skin</c> and <c>BindPose</c> lists (including modern uncompressed bind poses) with
	/// <c>Select(...).ToArray()</c>, so summing every count and applying
	/// <see cref="Budgets.MeshDecodedBytesPerItem"/> bounds the whole decode before it runs.
	/// </summary>
	private static long DeclaredMeshDecodedBytes(IMesh mesh)
	{
		ICompressedMesh compressed = mesh.CompressedMesh;
		long items = (long)compressed.Vertices.NumItems
			+ compressed.UV.NumItems
			+ compressed.Normals.NumItems
			+ compressed.NormalSigns.NumItems
			+ compressed.Tangents.NumItems
			+ compressed.TangentSigns.NumItems
			+ compressed.Weights.NumItems
			+ compressed.BoneIndices.NumItems
			+ compressed.Triangles.NumItems;
		// The unpacked lists are copied on their own, even when no compressed channel carries them:
		// the decoder converts <c>mesh.Skin</c> and <c>mesh.BindPose</c> item by item. The skin
		// field only exists before 2018.2, so an absent list contributes nothing.
		items += mesh.Skin?.Count ?? 0;
		items += mesh.BindPose.Count;
		if (compressed.Has_BindPoses())
		{
			items += compressed.BindPoses.NumItems;
		}

		if (compressed.Has_FloatColors())
		{
			items += compressed.FloatColors.NumItems;
		}

		if (compressed.Has_Colors())
		{
			items += compressed.Colors.NumItems;
		}

		return checked(items * Budgets.MeshDecodedBytesPerItem);
	}

	/// <summary>
	/// A safe upper bound on the decoded index buffer, in bytes. The pinned decoder always
	/// materialises the indices as a <c>uint[]</c>; the compressed triangle vector already has that
	/// shape, and an inline buffer carries at most one index per two bytes, so halving its length is
	/// a safe upper bound on the index count.
	/// </summary>
	private static long DeclaredMeshIndexBytes(IMesh mesh)
	{
		long indexCount = Math.Max((long)mesh.CompressedMesh.Triangles.NumItems, mesh.IndexBuffer.LongLength / sizeof(ushort));
		return checked(indexCount * sizeof(uint));
	}

	/// <summary>
	/// The declared source payload the decoder would copy, matching the actual inline-first source
	/// choice: <c>GetVertexDataBytes</c> returns the mesh's own vertex-channel bytes whenever they
	/// are nonempty and only falls back to an active streamed resource when those bytes are empty.
	/// The inline index buffer is always added. Read from metadata alone.
	/// </summary>
	private static long DeclaredMeshSourceBytes(IMesh mesh)
	{
		long channels;
		if (mesh.VertexData.Data.Length > 0)
		{
			channels = mesh.VertexData.Data.LongLength;
		}
		else if (mesh.Has_StreamData() && mesh.StreamData.IsSet())
		{
			channels = mesh.StreamData.Size;
		}
		else
		{
			channels = 0;
		}

		return channels + mesh.IndexBuffer.LongLength;
	}
}
