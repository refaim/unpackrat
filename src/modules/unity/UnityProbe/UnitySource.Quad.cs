using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AssetRipper.IO.Endian;
using AssetRipper.Numerics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.ChannelInfo;
using AssetRipper.SourceGenerated.Subclasses.StreamInfo;

namespace Unpackrat.UnityProbe;

internal static partial class UnitySource
{
	private const int QuadVertices = 4;
	private const int QuadIndices = 6;
	private const int QuadVertexDataLimit = 4096;
	private const int QuadChannelLimit = 32;
	private const int QuadStreamLimit = 8;

	private static class SpriteQuadProof
	{
		public static bool TryProve(UnityProjection fields, Stream source, long objectOffset,
			UnityVersion version, EndianType endian, int width, int height)
		{
			if (width <= 0 || height <= 0) return false;
			if (!fields.TryNumber("m_RD/settingsRaw", out long settings) || settings != 64) return false;
			if (!TryFloat(fields, "m_RD/downscaleMultiplier", out float downscale)
				|| downscale is not (0f or 1f)) return false;
			if (!TryFloat(fields, "m_PixelsToUnits", out float pixelsToUnits)
				|| !float.IsFinite(pixelsToUnits) || pixelsToUnits <= 0f) return false;
			if (!TryFloat(fields, "m_Rect/width", out float rectWidth)
				|| !TryFloat(fields, "m_Rect/height", out float rectHeight)
				|| rectWidth != width || rectHeight != height) return false;
			if (!TryPivot(fields, rectWidth, rectHeight, out float pivotX, out float pivotY)) return false;

			if (!fields.TryNumber("m_RD/m_VertexData/m_VertexCount", out long vertexCount)
				|| vertexCount != QuadVertices) return false;
			const string subMesh = "m_RD/m_SubMeshes/Array/data#0/";
			if (!fields.TryNumber(subMesh + "firstByte", out long firstByte) || firstByte != 0
				|| !fields.TryNumber(subMesh + "indexCount", out long indexCount) || indexCount != QuadIndices
				|| !fields.TryNumber(subMesh + "topology", out long topology) || topology != 0
				|| !fields.TryNumber(subMesh + "firstVertex", out long firstVertex) || firstVertex != 0
				|| !fields.TryNumber(subMesh + "vertexCount", out long subVertexCount) || subVertexCount != QuadVertices
				|| fields.TryNumber("m_RD/m_SubMeshes/Array/data#1/firstByte", out _)) return false;

			UnityProjection.ByteRange indexRange = LazyAssetFactory.Range(fields, "m_RD/m_IndexBuffer");
			if (indexRange.Length != QuadIndices * sizeof(ushort)) return false;
			Span<byte> indexBytes = stackalloc byte[QuadIndices * sizeof(ushort)];
			if (!TryRead(source, objectOffset, indexRange, indexBytes)) return false;
			ushort[] indices = new ushort[QuadIndices];
			for (int i = 0; i < indices.Length; i++)
				indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(indexBytes[(i * sizeof(ushort))..]);

			UnityProjection.ByteRange dataRange = LazyAssetFactory.Range(fields, "m_RD/m_VertexData/m_DataSize");
			if (dataRange.Length <= 0 || dataRange.Length > QuadVertexDataLimit) return false;
			byte[] data = new byte[dataRange.Length];
			if (!TryRead(source, objectOffset, dataRange, data)) return false;

			if (!TryChannels(fields, out List<ChannelInfo> channels)) return false;
			IReadOnlyList<IStreamInfo> streams = VertexDataBlob.ConvertChannelsToStreams(channels, (uint)QuadVertices, version);
			VertexDataBlob blob = new(channels, streams, data, QuadVertices, version, endian);
			// TryChannels guarantees a decoded position channel with a known format and bounded
			// dimension, so the declared source bound is the only remaining Quad refusal here.
			_ = TryDeclaredVertexChannels(blob, out _, out long requiredSourceBytes);
			if (requiredSourceBytes > data.Length) return false;
			MeshData decoded = blob.ToMeshData();
			if (decoded.Vertices.Length != QuadVertices) return false;

			float[] points = new float[QuadVertices * 2];
			for (int i = 0; i < QuadVertices; i++)
			{
				Vector3 vertex = decoded.Vertices[i];
				points[i * 2] = vertex.X;
				points[(i * 2) + 1] = vertex.Y;
			}
			SpriteGeometry.ToCanvasPixels(points, pixelsToUnits, pivotX, pivotY, rectWidth, rectHeight);
			return Covers(points, indices, width, height);
		}

		private static bool TryPivot(UnityProjection fields, float rectWidth, float rectHeight,
			out float pivotX, out float pivotY)
		{
			bool hasX = TryFloat(fields, "m_Pivot/x", out pivotX);
			bool hasY = TryFloat(fields, "m_Pivot/y", out pivotY);
			if (hasX != hasY) return false;
			if (hasX) return float.IsFinite(pivotX) && float.IsFinite(pivotY);
			if (!TryFloat(fields, "m_Offset/x", out float offsetX)
				|| !TryFloat(fields, "m_Offset/y", out float offsetY)) return false;
			pivotX = ((rectWidth / 2f) + offsetX) / rectWidth;
			pivotY = ((rectHeight / 2f) + offsetY) / rectHeight;
			return float.IsFinite(pivotX) && float.IsFinite(pivotY);
		}

		private static bool TryChannels(UnityProjection fields, out List<ChannelInfo> channels)
		{
			channels = new(QuadVertices);
			for (int i = 0; i < QuadChannelLimit; i++)
			{
				string prefix = $"m_RD/m_VertexData/m_Channels/Array/data#{i}/";
				bool hasStream = fields.TryNumber(prefix + "stream", out long stream);
				bool hasOffset = fields.TryNumber(prefix + "offset", out long offset);
				bool hasFormat = fields.TryNumber(prefix + "format", out long format);
				bool hasDimension = fields.TryNumber(prefix + "dimension", out long dimension);
				if (!hasStream && !hasOffset && !hasFormat && !hasDimension) break;
				if (!hasStream || !hasOffset || !hasFormat || !hasDimension) return false;
				if (stream is < 0 or >= QuadStreamLimit
					|| offset is < 0 or > byte.MaxValue
					|| format is < 0 or > byte.MaxValue
					|| dimension is < 0 or > 4) return false;
				bool active = dimension != 0;
				if (active && !IsKnownVertexFormat(format)) return false;
				// This bounded proof covers basic geometry only. Active blend channels (12 = weights,
				// 13 = indices) can reach SDK skin decoding that indexes an empty component array.
				if (active && i is 12 or 13) return false;
				if (active)
				{
					foreach (ChannelInfo existing in channels)
						if (existing.Dimension != 0 && existing.Stream == stream && existing.Offset == offset) return false;
				}
				channels.Add(new ChannelInfo
				{
					Stream = (byte)stream,
					Offset = (byte)offset,
					Format = (byte)format,
					Dimension = (byte)dimension,
				});
			}
			if (channels.Count == 0 || channels[0].Dimension != 3) return false;
			const string beyond = "m_RD/m_VertexData/m_Channels/Array/data#32/";
			if (fields.TryNumber(beyond + "stream", out _) || fields.TryNumber(beyond + "offset", out _)
				|| fields.TryNumber(beyond + "format", out _) || fields.TryNumber(beyond + "dimension", out _)) return false;
			return true;
		}

		// TryChannels rejects a negative format before this check; the private predicate only
		// needs the supported upper bound.
		private static bool IsKnownVertexFormat(long format) => format <= 6;

		private static bool TryFloat(UnityProjection fields, string path, out float value)
		{
			if (!fields.TryFloat32(path, out uint bits)) { value = 0f; return false; }
			value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
			return true;
		}

		private static bool TryRead(Stream source, long objectOffset, UnityProjection.ByteRange range, Span<byte> destination)
		{
			if (range.Offset < 0 || range.Length != destination.Length) return false;
			long offset = checked(objectOffset + range.Offset);
			if (offset < 0 || offset > source.Length || destination.Length > source.Length - offset) return false;
			source.Position = offset;
			return source.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false) == destination.Length;
		}

		private static bool Covers(float[] points, ushort[] indices, int width, int height)
		{
			if (points.Length != QuadVertices * 2 || indices.Length != QuadIndices) return false;
			if (width < 1 || height < 1 || (long)width * height > 8_388_608) return false;
			int[] corners = new int[QuadVertices];
			bool[] used = new bool[QuadVertices];
			for (int i = 0; i < QuadVertices; i++)
			{
				float x = points[i * 2];
				float y = points[(i * 2) + 1];
				int corner;
				if (y == 0f)
				{
					if (x == 0f) corner = 0;
					else if (x == width) corner = 1;
					else return false;
				}
				else if (y == height)
				{
					if (x == 0f) corner = 2;
					else if (x == width) corner = 3;
					else return false;
				}
				else return false;
				if (used[corner]) return false;
				used[corner] = true;
				corners[i] = corner;
			}

			int first = FaceMask(corners, indices, 0);
			int second = FaceMask(corners, indices, 3);
			if (first < 0 || second < 0 || (first | second) != 0b1111) return false;
			int shared = first & second;
			return shared is 0b1001 or 0b0110;
		}

		private static int FaceMask(int[] corners, ushort[] indices, int start)
		{
			int mask = 0;
			for (int i = start; i < start + 3; i++)
			{
				if (indices[i] >= QuadVertices) return -1;
				mask |= 1 << corners[indices[i]];
			}
			return BitOperations.PopCount((uint)mask) == 3 ? mask : -1;
		}
	}
}