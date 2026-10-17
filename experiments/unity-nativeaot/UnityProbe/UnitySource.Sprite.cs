using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_687078895;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.ChannelInfo;
using AssetRipper.SourceGenerated.Subclasses.SpriteAtlasData;
using AssetRipper.SourceGenerated.Subclasses.SpriteRenderData;
using AssetRipper.SourceGenerated.Subclasses.StreamInfo;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using AssetRipper.TextureDecoder.Rgb.Formats;
using StbImageSharp;

namespace Unpackrat.UnityProbe;

/// <summary>
/// Sprite adapter for the additional resource kinds. Like the font, media and mesh adapters it is
/// one of the only places these AssetRipper types are touched: it resolves its catalogue metadata
/// from declared fields alone and defers the texture decode, crop and PNG write to an explicit
/// export. A Unity Sprite is a rectangle (or a tight mesh) inside a texture, so the export is a
/// standalone PNG of the sprite's own pixels, never the whole atlas.
/// </summary>
internal static partial class UnitySource
{
	private sealed class SpriteAsset(ISprite sprite, SpriteSource source) : IProbeResource
	{
		public UnityEntryKind Kind => UnityEntryKind.Sprite;
		public long PathId => sprite.PathID;
		public string Name => sprite.Name.ToString();
		public string? OriginalPath => sprite.OriginalPath;

		// Metadata only: an atlas texture is shared with other sprites, so its contribution is
		// unknown; a sprite whose own render data names its texture reports that declared length.
		public long SourceSize => source.FromAtlas ? 0 : DeclaredTextureSourceSize(source.Texture);

		public string OutputExtension => OutputExtensions.SpritePng;

		public byte[]? ExportData() => RenderSprite(sprite, source);
	}

	/// <summary>
	/// The metadata a sprite's image is cropped from: the texture, its atlas-space rect measured
	/// from the bottom-left, the trimmed offset, and the packing settings. Every value comes from
	/// one selected record, either the sprite's own render data or the atlas entry its render-data
	/// key selects.
	/// </summary>
	private readonly struct SpriteSource
	{
		public SpriteSource(
			ITexture2D texture,
			bool fromAtlas,
			float rectX,
			float rectY,
			float rectWidth,
			float rectHeight,
			float offsetX,
			float offsetY,
			float downscale,
			bool packed,
			int packingMode,
			int packingRotation)
		{
			Texture = texture;
			FromAtlas = fromAtlas;
			RectX = rectX;
			RectY = rectY;
			RectWidth = rectWidth;
			RectHeight = rectHeight;
			OffsetX = offsetX;
			OffsetY = offsetY;
			Downscale = downscale;
			Packed = packed;
			PackingMode = packingMode;
			PackingRotation = packingRotation;
		}

		public ITexture2D Texture { get; }
		public bool FromAtlas { get; }
		public float RectX { get; }
		public float RectY { get; }
		public float RectWidth { get; }
		public float RectHeight { get; }
		public float OffsetX { get; }
		public float OffsetY { get; }
		public float Downscale { get; }
		public bool Packed { get; }
		public int PackingMode { get; }
		public int PackingRotation { get; }
	}

	/// <summary>
	/// Resolves the selected record a sprite renders from. An atlas sprite must resolve its
	/// render-data key through the atlas it names, against the atlas's own collection; a missing
	/// key or an unresolvable texture never silently falls back to the sprite's local render data
	/// or the whole atlas. A sprite with no atlas resolves its own render data.
	/// </summary>
	private static bool TryResolveSpriteSource(ISprite sprite, out SpriteSource source)
	{
		source = default;
		// A pre-2017 sprite serializes no atlas pointer at all; a present pointer with a zero path
		// id is an unset reference.
		if (sprite.SpriteAtlas is { } atlasPointer && !atlasPointer.IsNull())
		{
			// The sprite claims an atlas, so it must resolve one. A sprite with a sprite atlas
			// reference always serializes a render-data key (both arrived in Unity 2017), so the
			// keyed lookup is the only resolution path and a miss is a refusal, not a fallback.
			if (sprite.SpriteAtlasP is not { } atlas)
			{
				return false;
			}

			if (!atlas.RenderDataMap.TryGetValue(sprite.RenderDataKey, out ISpriteAtlasData? atlasData))
			{
				return false;
			}

			ITexture2D? atlasTexture = atlasData.Texture.TryGetAsset(atlas.Collection);
			if (atlasTexture is null)
			{
				return false;
			}

			source = new SpriteSource(
				atlasTexture,
				fromAtlas: true,
				atlasData.TextureRect.X,
				atlasData.TextureRect.Y,
				atlasData.TextureRect.Width,
				atlasData.TextureRect.Height,
				atlasData.TextureRectOffset.X,
				atlasData.TextureRectOffset.Y,
				atlasData.DownscaleMultiplier,
				atlasData.IsPacked,
				(int)atlasData.PackingMode,
				(int)atlasData.PackingRotation);
			return true;
		}

		ITexture2D? localTexture = sprite.RD.Texture.TryGetAsset(sprite.Collection);
		if (localTexture is null)
		{
			return false;
		}

		source = new SpriteSource(
			localTexture,
			fromAtlas: false,
			sprite.RD.TextureRect.X,
			sprite.RD.TextureRect.Y,
			sprite.RD.TextureRect.Width,
			sprite.RD.TextureRect.Height,
			sprite.RD.TextureRectOffset.X,
			sprite.RD.TextureRectOffset.Y,
			sprite.RD.DownscaleMultiplier,
			sprite.RD.IsPacked,
			(int)sprite.RD.PackingMode,
			(int)sprite.RD.PackingRotation);
		return true;
	}

	/// <summary>
	/// A downscale multiplier of exactly one (or the unused zero default) leaves the rect in the
	/// texture's own pixel space. Any other value, including a negative or non-finite one, stores
	/// the rect in downscaled space, which needs a resampler this adapter does not have, so it is
	/// refused rather than mapped wrong.
	/// </summary>
	private static bool IsUnitDownscale(float downscale)
	{
		return downscale is 0f or 1f;
	}

	private static byte[]? RenderSprite(ISprite sprite, SpriteSource source)
	{
		// The sprite's pixels are a crop of its source texture, so the source must decode through
		// the same metadata guards an ordinary texture uses.
		if (!TryDecodeTexture(source.Texture, out DirectBitmap atlasBitmap))
		{
			return null;
		}

		if (!IsUnitDownscale(source.Downscale))
		{
			return null;
		}

		if (!SpriteGeometry.TryCropRect(
			atlasBitmap.Width,
			atlasBitmap.Height,
			source.RectX,
			source.RectY,
			source.RectWidth,
			source.RectHeight,
			out int left,
			out int top,
			out int cropWidth,
			out int cropHeight))
		{
			return null;
		}

		// Crop the small region first, then normalise it to RGBA through the mature writer and
		// reader, so no pixel-type switch of our own is needed.
		DirectBitmap cropped = atlasBitmap.Crop(new Range(left, left + cropWidth), new Range(top, top + cropHeight));
		byte[] raw = ToRgba(cropped, out int rawWidth, out int rawHeight);

		if (!SpriteGeometry.TryUpright(rawWidth, rawHeight, raw, SpriteRotation(source), out int uprightWidth, out int uprightHeight, out byte[] upright))
		{
			return null;
		}

		if (!SpriteGeometry.TryCanvas(sprite.Rect.Width, sprite.Rect.Height, Budgets.MaxTextureBytes, out int canvasWidth, out int canvasHeight, out byte[] canvas))
		{
			return null;
		}

		if (!SpriteGeometry.TryPlacement(source.OffsetX, source.OffsetY, canvasHeight, uprightHeight, out int placedX, out int placedY))
		{
			return null;
		}

		if (!SpriteGeometry.TryPlace(canvasWidth, canvasHeight, placedX, placedY, uprightWidth, uprightHeight, upright, canvas))
		{
			return null;
		}

		// A tight sprite's rectangular crop can hold neighbouring pixels inside the bounding box.
		// Only the pixels inside the mesh triangles are kept, so a tight sprite whose mesh is
		// absent, malformed, non-triangular or out of range cannot produce a truthful mask and the
		// export fails rather than leaking the bounding box's neighbouring pixels.
		if (source.PackingMode == SpriteGeometry.ModeTight)
		{
			if (!TryReadSpriteTriangles(sprite, out float[] trianglePoints))
			{
				return null;
			}

			// A serialized pivot is used directly. A sprite that serializes none - the pre-5.6
			// shape - derives it from the rect centre plus its own offset, the pinned
			// SpriteExtensions contract: pivot = (Rect.Size / 2 + Offset) / Rect.Size. No serialized
			// pivot therefore does not mean a centred pivot.
			float pivotX;
			float pivotY;
			if (sprite.Pivot is { } pivot)
			{
				pivotX = pivot.X;
				pivotY = pivot.Y;
			}
			else
			{
				float rectWidth = sprite.Rect.Width;
				float rectHeight = sprite.Rect.Height;
				pivotX = ((rectWidth / 2f) + sprite.Offset.X) / rectWidth;
				pivotY = ((rectHeight / 2f) + sprite.Offset.Y) / rectHeight;
			}

			SpriteGeometry.ToCanvasPixels(trianglePoints, sprite.PixelsToUnits, pivotX, pivotY, sprite.Rect.Width, sprite.Rect.Height);
			if (!SpriteGeometry.TryMaskTriangles(canvasWidth, canvasHeight, canvas, trianglePoints, MaxSpriteMaskOperations))
			{
				return null;
			}
		}

		using MemoryStream stream = new();
		new DirectBitmap<ColorRGBA<byte>, byte>(canvasWidth, canvasHeight, 1, canvas).SaveAsPng(stream);
		return stream.ToArray();
	}

	/// <summary>
	/// The packing rotation, reported as a geometry value. The pinned <c>SpritePackingRotation</c>
	/// values are the Unity ones, so the cast preserves them; a coverage-checked test pins that
	/// agreement. An unpacked sprite ignores the rotation field entirely.
	/// </summary>
	private static int SpriteRotation(SpriteSource source) => source.Packed ? source.PackingRotation : SpriteGeometry.RotationNone;

	/// <summary>
	/// Decodes a cropped bitmap to a top-down, four-byte RGBA buffer by writing it as a PNG with
	/// the mature writer and reading that PNG back. The small crop round-trips losslessly and the
	/// fixed four-byte layout keeps the geometry free of any pixel-format switch.
	/// </summary>
	private static byte[] ToRgba(DirectBitmap bitmap, out int width, out int height)
	{
		using MemoryStream stream = new();
		bitmap.SaveAsPng(stream);
		ImageResult image = ImageResult.FromMemory(stream.ToArray(), ColorComponents.RedGreenBlueAlpha);
		width = image.Width;
		height = image.Height;
		return image.Data;
	}

	/// <summary>
	/// Reads the sprite's tight-mesh triangles in sprite-local units, three points per triangle.
	/// The modern vertex blob or the older vertex/indices list supply the positions; the pinned
	/// <c>TriangleEnumerable</c> turns the safely decoded triangle-list indices into triangles.
	/// Every index is validated against the vertex count, only the triangle-list topology is
	/// accepted, and the index range is bounded, so no hostile mesh can drive unbounded work or a
	/// wrong mask.
	/// </summary>
	private static bool TryReadSpriteTriangles(ISprite sprite, out float[] trianglePoints)
	{
		trianglePoints = [];
		ISpriteRenderData renderData = sprite.RD;

		// Metadata-only preflight before the vertex blob is decoded, before any index or vertex
		// array is copied and before the triangle list grows.
		if (!TryDeclaredSpriteMesh(sprite, out _))
		{
			return false;
		}

		Vector3[] vertices;
		if (renderData.VertexData is { Data.Length: > 0 } vertexData)
		{
			vertices = VertexDataBlob.Create(vertexData, sprite.Collection.Version, sprite.Collection.EndianType).ToMeshData().Vertices;
		}
		else
		{
			// The preflight proved the legacy shape carries at least one vertex, so this copy is a
			// known-small allocation.
			var legacyVertices = renderData.Vertices!;
			Vector3[] legacy = new Vector3[legacyVertices.Count];
			for (int i = 0; i < legacy.Length; i++)
			{
				var position = legacyVertices[i].Pos;
				legacy[i] = new Vector3(position.X, position.Y, position.Z);
			}

			vertices = legacy;
		}

		if (!TryReadSpriteIndices(renderData, out uint[] indices, out MeshTopology topology, out int firstIndex, out int indexCount))
		{
			return false;
		}

		return TryBuildTrianglePoints(vertices, indices, topology, firstIndex, indexCount, out trianglePoints);
	}

	/// <summary>
	/// Metadata-only preflight for a sprite's tight mesh: the declared vertex count, the bytes the
	/// vertex decoder would materialise across every channel, the highest source byte it would read,
	/// and the declared index, submesh and triangle counts all come from the pinned vertex-blob or
	/// legacy list metadata; nothing is decoded, copied or iterated here. A sprite whose metadata is
	/// absent or outside the explicit practical limits is refused before the decode runs.
	/// </summary>
	private static bool TryDeclaredSpriteMesh(ISprite sprite, out SpriteGeometry.DeclaredMesh mesh)
	{
		mesh = default;
		ISpriteRenderData renderData = sprite.RD;

		if (renderData.VertexData is { Data.Length: > 0 } vertexData)
		{
			// Bound the raw channel list before the pinned Create walks it: it converts the channels
			// into streams with a stream-major nested loop over every channel, so an untrusted
			// channel-list length must be refused before that walk rather than discovered inside it.
			// A sprite vertex blob always serializes its channel list.
			if (!SpriteGeometry.TryDeclaredChannelCount(vertexData.Channels!.Count))
			{
				return false;
			}

			VertexDataBlob blob;
			try
			{
				blob = VertexDataBlob.Create(vertexData, sprite.Collection.Version, sprite.Collection.EndianType);
			}
			catch (ArgumentOutOfRangeException)
			{
				// The pinned format helpers throw for an unknown channel format, which has no valid
				// component width, so that descriptor is refused here instead of inside the decoder.
				return false;
			}

			// The streams are derived from the bounded channels, but a single hostile channel stream
			// index would still materialise one stream per index, so the derived count is bounded too.
			if (!SpriteGeometry.TryDeclaredStreamCount(blob.Streams.Count))
			{
				return false;
			}

			if (!TryDeclaredVertexChannels(blob, out long channelBytes, out long requiredSourceBytes))
			{
				return false;
			}

			// The decoder copies and iterates the complete index buffer whatever submesh is
			// selected, so its whole length is bounded, not only the selected submesh's count; it
			// also materialises one four-byte index per entry. The selected count is validated
			// separately through the triangle work it drives.
			// The inline index buffer is a byte array like the mesh's own, read through the same
			// null-tolerant span the index decoder uses.
			ReadOnlySpan<byte> indexBuffer = renderData.IndexBuffer;
			long indexBufferCount = indexBuffer.Length / sizeof(ushort);
			int declaredIndexCount = DeclaredSpriteIndexCount(renderData, out int subMeshCount);
			mesh = new SpriteGeometry.DeclaredMesh(
				blob.VertexCount,
				channelBytes + (indexBufferCount * sizeof(uint)),
				requiredSourceBytes,
				blob.Data.Length,
				(int)Math.Min(indexBufferCount, int.MaxValue),
				declaredIndexCount,
				subMeshCount,
				declaredIndexCount / 3);
			return SpriteGeometry.TryDeclaredMesh(mesh);
		}

		if (renderData.Vertices is { Count: > 0 } legacyVertices)
		{
			// The legacy shape always serializes its index list (empty when the sprite carries no
			// indices), so the declared count is read directly.
			int declaredIndexCount = renderData.Indices!.Count;
			// The legacy shape holds one three-float position per vertex, so its own list is the
			// source and the decoded size is exact. The index list is copied in full and
			// materialised as one four-byte index per entry.
			long legacyBytes = (long)legacyVertices.Count * 12;
			mesh = new SpriteGeometry.DeclaredMesh(
				legacyVertices.Count,
				legacyBytes + ((long)declaredIndexCount * sizeof(uint)),
				legacyBytes,
				(int)Math.Min(legacyBytes, int.MaxValue),
				declaredIndexCount,
				declaredIndexCount,
				0,
				declaredIndexCount / 3);
			return SpriteGeometry.TryDeclaredMesh(mesh);
		}

		return false;
	}

	/// <summary>
	/// Validates the vertex blob's decoded channels from metadata alone and returns the safe
	/// conservative decoded-byte total the vertex decoder would materialise and the highest source
	/// byte it would read. A channel the decoder skips - one with a zero dimension or a clear stream
	/// mask bit - is neither read nor allocated, so only decoded channels are bounded. A decoded
	/// channel must carry a supported vector dimension and sit wholly inside the decoded body.
	/// </summary>
	private static bool TryDeclaredVertexChannels(VertexDataBlob blob, out long decodedBytes, out long requiredSourceBytes)
	{
		decodedBytes = 0;
		requiredSourceBytes = 0;
		for (int channelIndex = 0; channelIndex < blob.Channels.Count; channelIndex++)
		{
			ChannelInfo channel = blob.Channels[channelIndex];
			bool legacyColorOverride = blob.Version.LessThan(2018) & (channelIndex == 2) & (channel.Format == 2);
			byte dimension = SpriteGeometry.ChannelDimension(channel.GetDataDimension(), legacyColorOverride);
			// The blob's streams are derived from these channels, so every stream index is in range.
			IStreamInfo stream = blob.Streams[channel.Stream];
			if (!SpriteGeometry.IsDecodedChannel(dimension, stream.ChannelMask, channelIndex))
			{
				continue;
			}

			if (dimension > SpriteGeometry.MaxMeshChannelDimension)
			{
				return false;
			}

			// The blob was created with these channels, so every decoded channel's format already
			// resolved to a valid byte width; the same width drives the source span and allocation.
			int componentSize = MeshHelper.GetFormatSize(MeshHelper.ToVertexFormat(channel.Format, blob.Version));
			long vertexCount = blob.VertexCount;
			long stride = stream.GetStride();
			long sourceEnd = (long)stream.Offset + channel.Offset + ((vertexCount - 1) * stride) + ((long)componentSize * dimension);
			requiredSourceBytes = Math.Max(requiredSourceBytes, sourceEnd);

			// componentBytes = n*dimension*componentSize; the expanded float or int array is one
			// four-byte component each; the final Vector2/3/4/Color array is at most sixteen bytes
			// per vertex; a blend channel additionally rents one BoneWeight4[n] of thirty-two bytes
			// per vertex. Summing every path keeps the bound conservative.
			long components = vertexCount * dimension;
			decodedBytes += (components * componentSize) + (components * sizeof(float)) + (vertexCount * 16) + (vertexCount * 32);
		}

		return decodedBytes > 0;
	}

	/// <summary>
	/// Turns safely decoded triangle-list indices into sprite-local triangle points, three points per
	/// triangle. The topology, the three-at-a-time index count and the submesh's index range are all
	/// validated, every index is bounded by the vertex count, and only the triangle-list topology is
	/// accepted, so no hostile mesh can drive an out-of-range read or a wrong mask.
	/// </summary>
	private static bool TryBuildTrianglePoints(
		Vector3[] vertices,
		uint[] indices,
		MeshTopology topology,
		int firstIndex,
		int indexCount,
		out float[] trianglePoints)
	{
		trianglePoints = [];
		if (topology != MeshTopology.Triangles || indexCount % 3 != 0 || (long)firstIndex + indexCount > indices.Length)
		{
			return false;
		}

		List<float> points = [];
		foreach ((uint first, uint second, uint third) in new TriangleEnumerable(topology, new ArraySegment<uint>(indices, firstIndex, indexCount)))
		{
			if (first >= vertices.Length || second >= vertices.Length || third >= vertices.Length)
			{
				return false;
			}

			AddPoint(points, vertices[(int)first]);
			AddPoint(points, vertices[(int)second]);
			AddPoint(points, vertices[(int)third]);
		}

		trianglePoints = points.ToArray();
		return true;
	}

	/// <summary>
	/// The declared index count of the modern shape: the selected submesh's own index count, or the
	/// whole decoded index buffer when no submesh is present. Also reports the declared submesh count.
	/// </summary>
	private static int DeclaredSpriteIndexCount(ISpriteRenderData renderData, out int subMeshCount)
	{
		if (renderData.SubMeshes is { Count: > 0 } subMeshes)
		{
			subMeshCount = subMeshes.Count;
			return (int)subMeshes[0].IndexCount;
		}

		subMeshCount = 0;
		ReadOnlySpan<byte> buffer = renderData.IndexBuffer;
		return buffer.Length / sizeof(ushort);
	}

	private static void AddPoint(List<float> points, Vector3 vertex)
	{
		points.Add(vertex.X);
		points.Add(vertex.Y);
	}

	/// <summary>
	/// Decodes the sprite mesh indices into a <c>uint</c> array together with its topology and the
	/// submesh's index range. The modern path reads the little-endian <c>ushort</c> index buffer
	/// and the first submesh; the older path reads the per-vertex index list as a single triangle
	/// list.
	/// </summary>
	private static bool TryReadSpriteIndices(
		ISpriteRenderData renderData,
		out uint[] indices,
		out MeshTopology topology,
		out int firstIndex,
		out int indexCount)
	{
		indices = [];
		topology = MeshTopology.Triangles;
		firstIndex = 0;
		indexCount = 0;

		if (renderData.VertexData is { Data.Length: > 0 })
		{
			ReadOnlySpan<byte> buffer = renderData.IndexBuffer;
			if (buffer.Length == 0 || (buffer.Length & 1) != 0)
			{
				return false;
			}

			uint[] decoded = new uint[buffer.Length / sizeof(ushort)];
			for (int i = 0; i < decoded.Length; i++)
			{
				decoded[i] = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(i * sizeof(ushort), sizeof(ushort)));
			}

			indices = decoded;
			if (renderData.SubMeshes is { Count: > 0 } subMeshes)
			{
				ISubMesh subMesh = subMeshes[0];
				topology = subMesh.TopologyE;
				firstIndex = (int)(subMesh.FirstByte / sizeof(ushort));
				indexCount = (int)subMesh.IndexCount;
			}
			else
			{
				indexCount = decoded.Length;
			}

			return true;
		}

		if (renderData.Indices is { Count: > 0 } indexList)
		{
			uint[] decoded = new uint[indexList.Count];
			for (int i = 0; i < decoded.Length; i++)
			{
				decoded[i] = indexList[i];
			}

			indices = decoded;
			indexCount = decoded.Length;
			return true;
		}

		return false;
	}

	/// <summary>Upper bound on the pixel-centre tests one tight-mesh mask may perform.</summary>
	private const long MaxSpriteMaskOperations = 268_435_456L;
}
