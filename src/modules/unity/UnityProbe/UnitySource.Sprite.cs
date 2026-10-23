using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using AssetRipper.Assets.Collections;
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
	/// The declared geometry needed to decide whether two selected Sprite rows render the same
	/// image. Floats stay as their raw IEEE payload so a NaN, a signed zero or a fractional value
	/// can never be confused with a missing field or a rounded integer.
	/// </summary>
	private readonly record struct SpriteSourceFacts(
		bool Present,
		uint TextureX,
		uint TextureY,
		uint TextureWidth,
		uint TextureHeight,
		uint OffsetX,
		uint OffsetY,
		uint Downscale,
		uint SettingsRaw);

	/// <summary>The Sprite's own rect and the offset that places the crop on that canvas.</summary>
	private readonly record struct SpriteCanvasFacts(
		bool Present,
		uint RectWidth,
		uint RectHeight);

	private struct SpriteFacts
	{
		public SpriteSourceFacts Source;
		public SpriteCanvasFacts Canvas;
		public AssetCollection? SourceCollection;
		public long SourcePath;
		public int SourceWidth;
		public int SourceHeight;
		public bool Resolved;

		public readonly bool Comparable =>
			Resolved && SourceCollection is not null && Source.Present && Canvas.Present
			&& SpriteFactsReader.IsFinite(Source.TextureX) && SpriteFactsReader.IsFinite(Source.TextureY)
			&& SpriteFactsReader.IsFinite(Source.TextureWidth) && SpriteFactsReader.IsFinite(Source.TextureHeight)
			&& SpriteFactsReader.IsFinite(Source.OffsetX) && SpriteFactsReader.IsFinite(Source.OffsetY)
			&& SpriteFactsReader.IsFinite(Source.Downscale)
			&& SpriteFactsReader.IsFinite(Canvas.RectWidth) && SpriteFactsReader.IsFinite(Canvas.RectHeight)
			&& SpriteFactsReader.Positive(Source.TextureWidth) && SpriteFactsReader.Positive(Source.TextureHeight)
			&& SpriteFactsReader.Positive(Canvas.RectWidth) && SpriteFactsReader.Positive(Canvas.RectHeight)
			&& SpriteFactsReader.IsExportableRectangle(Source, Canvas, SourceWidth, SourceHeight);

		public readonly SpriteKey Key => new(SourceCollection!, SourcePath, Source, Canvas);

		public readonly bool IsFullTexture(int width, int height)
		{
			if (!Comparable || width <= 0 || height <= 0) return false;
			if (!SpriteFactsReader.IsIdentityRotation(Source.SettingsRaw)) return false;
			if (Source.TextureX != 0 || Source.TextureY != 0) return false;
			if (Source.OffsetX != 0 || Source.OffsetY != 0) return false;
			if (Source.TextureWidth != SpriteFactsReader.Bits((float)width)
				|| Source.TextureHeight != SpriteFactsReader.Bits((float)height)) return false;
			if (Canvas.RectWidth != SpriteFactsReader.Bits((float)width)
				|| Canvas.RectHeight != SpriteFactsReader.Bits((float)height)) return false;
			return true;
		}
	}

	private readonly struct SpriteKey : IEquatable<SpriteKey>
	{
		public SpriteKey(AssetCollection sourceCollection, long sourcePath,
			SpriteSourceFacts source, SpriteCanvasFacts canvas)
			=> (SourceCollection, SourcePath, Source, Canvas) = (sourceCollection, sourcePath, source, canvas);

		private AssetCollection SourceCollection { get; }
		private long SourcePath { get; }
		private SpriteSourceFacts Source { get; }
		private SpriteCanvasFacts Canvas { get; }

		public bool Equals(SpriteKey other) =>
			ReferenceEquals(SourceCollection, other.SourceCollection)
			&& SourcePath == other.SourcePath && Source == other.Source && Canvas == other.Canvas;

		public override bool Equals(object? obj) => obj is SpriteKey other && Equals(other);

		public override int GetHashCode()
		{
			HashCode hash = new();
			hash.Add(RuntimeHelpers.GetHashCode(SourceCollection));
			hash.Add(SourcePath);
			hash.Add(Source);
			hash.Add(Canvas);
			return hash.ToHashCode();
		}
	}

	/// <summary>
	/// Reads the bounded Sprite geometry from the already-projected header. A path that is absent
	/// from the schema leaves the corresponding facts absent; it is never defaulted to zero.
	/// </summary>
	private static class SpriteFactsReader
	{
		private const uint OneBits = 0x3F800000u;
		private const uint KnownSettingsMask = 0x7Fu;

		public static SpriteSourceFacts ReadSource(UnityProjection fields, string prefix)
		{
			if (!TryFloat32(fields, prefix + "textureRect/x", out uint textureX)
				|| !TryFloat32(fields, prefix + "textureRect/y", out uint textureY)
				|| !TryFloat32(fields, prefix + "textureRect/width", out uint textureWidth)
				|| !TryFloat32(fields, prefix + "textureRect/height", out uint textureHeight)
				|| !TryFloat32(fields, prefix + "textureRectOffset/x", out uint offsetX)
				|| !TryFloat32(fields, prefix + "textureRectOffset/y", out uint offsetY)
				|| !TryFloat32(fields, prefix + "downscaleMultiplier", out uint downscale)
				|| !TrySettings(fields, prefix, out uint settings))
			{
				return default;
			}
			return new SpriteSourceFacts(true, textureX, textureY, textureWidth, textureHeight,
				offsetX, offsetY, downscale, settings);
		}

		public static SpriteSourceFacts ReadLocalSource(UnityProjection fields)
		{
			if (!TryFloat32(fields, "m_RD/textureRect/x", out uint textureX)
				|| !TryFloat32(fields, "m_RD/textureRect/y", out uint textureY)
				|| !TryFloat32(fields, "m_RD/textureRect/width", out uint textureWidth)
				|| !TryFloat32(fields, "m_RD/textureRect/height", out uint textureHeight)
				|| !TryFloat32(fields, "m_RD/textureRectOffset/x", out uint offsetX)
				|| !TryFloat32(fields, "m_RD/textureRectOffset/y", out uint offsetY)
				|| !TryFloat32(fields, "m_RD/downscaleMultiplier", out uint downscale)
				|| !TryLocalSettings(fields, out uint settings))
			{
				return default;
			}
			return new SpriteSourceFacts(true, textureX, textureY, textureWidth, textureHeight,
				offsetX, offsetY, downscale, settings);
		}

		public static SpriteCanvasFacts ReadCanvas(UnityProjection fields)
		{
			if (!TryFloat32(fields, "m_Rect/width", out uint rectWidth)
				|| !TryFloat32(fields, "m_Rect/height", out uint rectHeight))
			{
				return default;
			}
			return new SpriteCanvasFacts(true, rectWidth, rectHeight);
		}

		private static bool TryLocalSettings(UnityProjection fields, out uint settings)
		{
			if (fields.TryNumber("m_RD/settingsRaw", out long raw))
			{
				settings = unchecked((uint)raw);
				return true;
			}
			if (fields.TryNumber("m_RD/packingMode", out long mode)
				&& fields.TryNumber("m_RD/packingRotation", out long rotation))
			{
				long packed = fields.TryNumber("m_RD/packed", out long packedValue) && packedValue != 0 ? 1 : 0;
				settings = unchecked((uint)((packed << 0) | ((mode & 0x1) << 1) | ((rotation & 0xF) << 2)));
				return true;
			}
			settings = 0;
			return false;
		}

		private static bool TrySettings(UnityProjection fields, string prefix, out uint settings)
		{
			if (fields.TryNumber(prefix + "settingsRaw", out long raw))
			{
				settings = unchecked((uint)raw);
				return true;
			}
			if (fields.TryNumber(prefix + "packingMode", out long mode)
				&& fields.TryNumber(prefix + "packingRotation", out long rotation))
			{
				long packed = fields.TryNumber(prefix + "packed", out long packedValue) && packedValue != 0 ? 1 : 0;
				settings = unchecked((uint)((packed << 0) | ((mode & 0x1) << 1) | ((rotation & 0xF) << 2)));
				return true;
			}
			settings = 0;
			return false;
		}

		private static bool TryFloat32(UnityProjection fields, string path, out uint bits)
		{
			if (fields.TryFloat32(path, out bits)) return true;
			bits = 0;
			return false;
		}

		public static uint Bits(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));

		public static bool IsFinite(uint bits) => float.IsFinite(BitConverter.Int32BitsToSingle(unchecked((int)bits)));

		public static bool Positive(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits)) > 0f;

		public static bool IsUnitDownscale(uint bits) => bits is 0u or OneBits;

		public static bool IsKnownRectangleSettings(uint settings)
		{
			if ((settings & ~KnownSettingsMask) != 0) return false;
			uint mode = (settings >> 1) & 0x1u;
			uint rotation = (settings >> 2) & 0xFu;
			return mode == (uint)SpriteGeometry.ModeRectangle && rotation <= SpriteGeometry.RotationRotate90;
		}

		public static bool IsIdentityRotation(uint settings) => ((settings >> 2) & 0xFu) == 0;

		public static bool IsExportableRectangle(SpriteSourceFacts source, SpriteCanvasFacts canvas, int textureWidth, int textureHeight)
		{
			if (!source.Present || !canvas.Present || textureWidth <= 0 || textureHeight <= 0) return false;
			if (!IsKnownRectangleSettings(source.SettingsRaw) || !IsUnitDownscale(source.Downscale)) return false;
			if (!SpriteGeometry.TryCropRect(textureWidth, textureHeight,
				BitConverter.Int32BitsToSingle(unchecked((int)source.TextureX)),
				BitConverter.Int32BitsToSingle(unchecked((int)source.TextureY)),
				BitConverter.Int32BitsToSingle(unchecked((int)source.TextureWidth)),
				BitConverter.Int32BitsToSingle(unchecked((int)source.TextureHeight)),
				out _, out _, out int cropWidth, out int cropHeight)) return false;
			if (!SpriteGeometry.TryCanvasSize(BitConverter.Int32BitsToSingle(unchecked((int)canvas.RectWidth)),
				BitConverter.Int32BitsToSingle(unchecked((int)canvas.RectHeight)), Budgets.MaxTextureBytes,
				out int canvasWidth, out int canvasHeight)) return false;
			uint rotation = (source.SettingsRaw >> 2) & 0xFu;
			int effectiveRotation = (source.SettingsRaw & 1u) != 0 ? (int)rotation : SpriteGeometry.RotationNone;
			int uprightWidth = effectiveRotation == SpriteGeometry.RotationRotate90 ? cropHeight : cropWidth;
			int uprightHeight = effectiveRotation == SpriteGeometry.RotationRotate90 ? cropWidth : cropHeight;
			if (!SpriteGeometry.TryPlacement(BitConverter.Int32BitsToSingle(unchecked((int)source.OffsetX)),
				BitConverter.Int32BitsToSingle(unchecked((int)source.OffsetY)), canvasHeight, uprightHeight,
				out int x, out int y)) return false;
			return x >= 0 && y >= 0 && (long)x + uprightWidth <= canvasWidth
				&& (long)y + uprightHeight <= canvasHeight;
		}
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
