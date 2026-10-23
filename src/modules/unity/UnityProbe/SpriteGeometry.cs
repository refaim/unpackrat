using System;

namespace Unpackrat.UnityProbe;

/// <summary>
/// Pure, third-party-free geometry for a Sprite's standalone PNG. It maps the sprite's rect to the
/// top-down region of a decoded (Y-flipped) atlas, undoes the packed orientation so the image is
/// upright, and places the trimmed image on a transparent canvas the size of the sprite's own rect.
/// </summary>
/// <remarks>
/// Every pixel buffer is plain four-byte RGBA and every dimension is validated before it is used,
/// so a hostile rect or offset is refused rather than turned into a wrong or oversized image. The
/// packing values mirror Unity's serialized <c>settingsRaw</c> bits and the pinned
/// <c>SpritePackingMode</c>/<c>SpritePackingRotation</c> enums; they are plain integers so the
/// helper stays free of third-party types.
/// </remarks>
internal static class SpriteGeometry
{
	/// <summary>The <c>packingMode</c> field value for a tight (mesh-trimmed) sprite.</summary>
	internal const int ModeTight = 0;

	/// <summary>The <c>packingMode</c> field value for a rectangle (untrimmed) sprite.</summary>
	internal const int ModeRectangle = 1;

	internal const int RotationNone = 0;
	internal const int RotationFlipHorizontal = 1;
	internal const int RotationFlipVertical = 2;
	internal const int RotationRotate180 = 3;
	internal const int RotationRotate90 = 4;

	/// <summary>Bytes per pixel of the RGBA buffers this helper works with.</summary>
	private const int BytesPerPixel = 4;

	/// <summary>
	/// The declared size of a sprite's tight mesh, checked from metadata alone before any decoding,
	/// copy, list or loop runs. <see cref="DecodedBytes"/> bounds the bytes the vertex decoder would
	/// materialise across every channel plus the four-byte index array the index decoder copies,
	/// <see cref="RequiredSourceBytes"/> the highest source byte the decode would read, and
	/// <see cref="SourceLength"/> the bytes actually available. <see cref="IndexBufferCount"/> is the
	/// complete index buffer the decoder copies whatever submesh is selected, while
	/// <see cref="IndexCount"/> is only the selected submesh's index count.
	/// </summary>
	internal readonly record struct DeclaredMesh(
		int VertexCount,
		long DecodedBytes,
		long RequiredSourceBytes,
		int SourceLength,
		int IndexBufferCount,
		int IndexCount,
		int SubMeshCount,
		int TriangleCount);

	/// <summary>
	/// Practical upper bound on the vertices one sprite mesh may declare. A sprite mesh is a tight
	/// trim of a single rectangle, so anything near this bound is already pathological; the limit
	/// keeps the decoded vertex buffer small without excluding a real sprite.
	/// </summary>
	internal const int MaxMeshVertexCount = 1 << 20;

	/// <summary>Practical upper bound on the declared and decoded bytes of one sprite mesh.</summary>
	internal const long MaxMeshBytes = 64L << 20;

	/// <summary>Practical upper bound on the indices one sprite mesh may declare.</summary>
	internal const int MaxMeshIndexCount = 1 << 22;

	/// <summary>Practical upper bound on the submeshes one sprite render data may declare.</summary>
	internal const int MaxMeshSubMeshCount = 32;

	/// <summary>
	/// Practical upper bound on the vertex channels one sprite mesh may declare. A sprite mesh is a
	/// tight trim with a handful of channels at most; the limit only stops a hostile channel list
	/// whose length would otherwise drive the pinned channel-to-stream conversion.
	/// </summary>
	internal const int MaxMeshChannelCount = 64;

	/// <summary>
	/// Practical upper bound on the vertex streams a sprite mesh may end up carrying. A real mesh
	/// uses a single stream; the limit stops a hostile channel stream index from materialising one
	/// stream per index.
	/// </summary>
	internal const int MaxMeshStreamCount = 16;

	/// <summary>
	/// The largest channel vector dimension the pinned vertex decoder supports. A channel that
	/// declares a wider dimension cannot be expanded into a <c>Vector2/3/4</c> and is refused.
	/// </summary>
	internal const int MaxMeshChannelDimension = 4;

	/// <summary>
	/// Practical upper bound on the triangles one sprite mesh may declare, counted before the mesh
	/// is enumerated or any triangle list grows. It bounds the total work regardless of how many
	/// triangles fall outside the visible rect.
	/// </summary>
	internal const int MaxMeshTriangleCount = 1 << 20;

	/// <summary>
	/// Preflight for a sprite mesh: every declared count, byte size and source range must be finite,
	/// non-negative and inside an explicit practical limit before any allocation or iteration. Values
	/// arrive as plain numbers so the helper stays free of third-party types; the adapter derives
	/// them from the pinned vertex-blob metadata.
	/// </summary>
	internal static bool TryDeclaredMesh(DeclaredMesh mesh)
	{
		if (mesh.VertexCount <= 0 || mesh.VertexCount > MaxMeshVertexCount)
		{
			return false;
		}

		if (mesh.DecodedBytes <= 0 || mesh.DecodedBytes > MaxMeshBytes)
		{
			return false;
		}

		if (mesh.RequiredSourceBytes < 0 || mesh.RequiredSourceBytes > MaxMeshBytes || mesh.RequiredSourceBytes > mesh.SourceLength)
		{
			return false;
		}

		if (mesh.IndexBufferCount < 0 || mesh.IndexBufferCount > MaxMeshIndexCount)
		{
			return false;
		}

		if (mesh.IndexCount < 0 || mesh.IndexCount > MaxMeshIndexCount)
		{
			return false;
		}

		if (mesh.SubMeshCount < 0 || mesh.SubMeshCount > MaxMeshSubMeshCount)
		{
			return false;
		}

		return mesh.TriangleCount >= 0 && mesh.TriangleCount <= MaxMeshTriangleCount;
	}

	/// <summary>
	/// Bounds the raw vertex channel list before the pinned <c>VertexDataBlob.Create</c> walks it: it
	/// converts the channels into streams with a stream-major nested loop over every channel, so an
	/// untrusted channel-list length must be refused before that walk rather than discovered in it.
	/// </summary>
	internal static bool TryDeclaredChannelCount(int channelCount)
	{
		return channelCount >= 0 && channelCount <= MaxMeshChannelCount;
	}

	/// <summary>
	/// Bounds the streams the decoded vertex blob ends up carrying. A sprite vertex blob serializes
	/// channels and no stream list, so the streams only exist once the pinned Create has derived
	/// them from the channels; each channel's stream index selects one of them, so this bounds that
	/// selection too.
	/// </summary>
	internal static bool TryDeclaredStreamCount(int streamCount)
	{
		return streamCount >= 0 && streamCount <= MaxMeshStreamCount;
	}

	/// <summary>
	/// The effective vector dimension of a vertex channel: its low nibble, except that the pinned
	/// decoder forces the third channel to four when it carries the pre-2018 colour format.
	/// </summary>
	internal static byte ChannelDimension(byte rawDimension, bool legacyColorOverride)
	{
		return legacyColorOverride ? (byte)4 : rawDimension;
	}

	/// <summary>
	/// Whether the pinned vertex decoder decodes a channel at all: it is decoded only when it
	/// declares a nonzero dimension and its stream's channel mask selects it. Every other channel is
	/// neither read nor allocated.
	/// </summary>
	internal static bool IsDecodedChannel(int dimension, uint channelMask, int channelIndex)
	{
		return dimension != 0 && (channelMask & (1u << channelIndex)) != 0;
	}

	/// <summary>
	/// Maps an atlas rect measured from the bottom-left onto the top-down region of a decoded
	/// (Y-flipped) bitmap. The rect's columns are <c>x..x+width</c> and its top-down rows are
	/// <c>atlasHeight - y - height .. atlasHeight - y</c>. A non-finite, non-positive or
	/// out-of-bounds rect is refused rather than clamped, so a wrong rectangle never silently
	/// selects adjacent pixels.
	/// </summary>
	internal static bool TryCropRect(
		int atlasWidth,
		int atlasHeight,
		float x,
		float y,
		float width,
		float height,
		out int left,
		out int top,
		out int cropWidth,
		out int cropHeight)
	{
		left = 0;
		top = 0;
		cropWidth = 0;
		cropHeight = 0;

		if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(width) || !float.IsFinite(height))
		{
			return false;
		}

		if (width <= 0f || height <= 0f)
		{
			return false;
		}

		double leftEdge = Math.Floor((double)x);
		double bottomEdge = Math.Floor((double)y);
		double rightEdge = Math.Ceiling((double)x + width);
		double topEdge = Math.Ceiling((double)y + height);
		if (leftEdge < 0d || bottomEdge < 0d || rightEdge > atlasWidth || topEdge > atlasHeight)
		{
			return false;
		}

		left = (int)leftEdge;
		top = atlasHeight - (int)topEdge;
		cropWidth = (int)(rightEdge - leftEdge);
		cropHeight = (int)(topEdge - bottomEdge);
		return true;
	}

	/// <summary>
	/// The trimmed sprite's canvas in pixels: the rounded size of the sprite's own rect, with a
	/// fully transparent pixel buffer. A non-finite, non-positive or oversized rect is refused.
	/// </summary>
	internal static bool TryCanvasSize(float rectWidth, float rectHeight, long maxBytes, out int width, out int height)
	{
		width = 0;
		height = 0;
		if (!float.IsFinite(rectWidth) || !float.IsFinite(rectHeight) || rectWidth <= 0f || rectHeight <= 0f)
		{
			return false;
		}

		double roundedWidth = Math.Round((double)rectWidth);
		double roundedHeight = Math.Round((double)rectHeight);
		if (roundedWidth > int.MaxValue || roundedHeight > int.MaxValue)
		{
			return false;
		}

		int canvasWidth = (int)roundedWidth;
		int canvasHeight = (int)roundedHeight;
		if (!TryPixelBuffer(canvasWidth, canvasHeight, out int length) || length > maxBytes)
		{
			return false;
		}

		width = canvasWidth;
		height = canvasHeight;
		return true;
	}

	internal static bool TryCanvas(float rectWidth, float rectHeight, long maxBytes, out int width, out int height, out byte[] canvas)
	{
		canvas = [];
		if (!TryCanvasSize(rectWidth, rectHeight, maxBytes, out width, out height)
			|| !TryPixelBuffer(width, height, out int length))
		{
			return false;
		}

		canvas = new byte[length];
		return true;
	}

	/// <summary>
	/// Uprights a top-down RGBA crop for the given packing rotation. A horizontal or vertical flip
	/// (and their combination, a 180-degree rotation) keeps the dimensions; a 90-degree packed
	/// rotation swaps them with a clockwise turn of the top-down image. An unknown rotation is
	/// refused.
	/// </summary>
	internal static bool TryUpright(
		int width,
		int height,
		byte[] source,
		int rotation,
		out int uprightWidth,
		out int uprightHeight,
		out byte[] upright)
	{
		uprightWidth = width;
		uprightHeight = height;
		upright = source;
		if (!TryPixelBuffer(width, height, out int length) || source.Length < length)
		{
			return false;
		}

		switch (rotation)
		{
			case RotationNone:
				return true;
			case RotationFlipHorizontal:
				FlipHorizontal(width, height, source);
				return true;
			case RotationFlipVertical:
				FlipVertical(width, height, source);
				return true;
			case RotationRotate180:
				FlipHorizontal(width, height, source);
				FlipVertical(width, height, source);
				return true;
			case RotationRotate90:
				return TryRotateClockwise90(width, height, source, length, out uprightWidth, out uprightHeight, out upright);
			default:
				return false;
		}
	}

	/// <summary>
	/// The top-left corner an upright trimmed image is placed at so it sits on the canvas at the
	/// recorded trim offset, measured from the canvas's bottom-left: <c>x = offsetX</c> and
	/// <c>y = canvasHeight - offsetY - uprightHeight</c>.
	/// </summary>
	internal static bool TryPlacement(float offsetX, float offsetY, int canvasHeight, int uprightHeight, out int x, out int y)
	{
		x = 0;
		y = 0;
		if (!float.IsFinite(offsetX) || !float.IsFinite(offsetY))
		{
			return false;
		}

		double placedX = Math.Round((double)offsetX);
		double placedY = (double)canvasHeight - Math.Round((double)offsetY) - uprightHeight;
		if (placedX < int.MinValue || placedX > int.MaxValue || placedY < int.MinValue || placedY > int.MaxValue)
		{
			return false;
		}

		x = (int)placedX;
		y = (int)placedY;
		return true;
	}

	/// <summary>
	/// Copies an upright RGBA crop into a transparent canvas at the given top-left corner. A crop
	/// that would not fit entirely inside the canvas is refused rather than clipped.
	/// </summary>
	internal static bool TryPlace(int canvasWidth, int canvasHeight, int x, int y, int cropWidth, int cropHeight, byte[] crop, byte[] canvas)
	{
		if (x < 0 || y < 0 || cropWidth <= 0 || cropHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0)
		{
			return false;
		}

		if ((long)x + cropWidth > canvasWidth || (long)y + cropHeight > canvasHeight)
		{
			return false;
		}

		if (!TryPixelBuffer(canvasWidth, canvasHeight, out int canvasLength) || canvas.Length < canvasLength)
		{
			return false;
		}

		// The crop is no larger than the canvas in either dimension, and the canvas length is
		// already representable, so the crop's four-byte length cannot overflow.
		if (crop.Length < cropWidth * cropHeight * BytesPerPixel)
		{
			return false;
		}

		int cropStride = cropWidth * BytesPerPixel;
		int canvasStride = canvasWidth * BytesPerPixel;
		for (int row = 0; row < cropHeight; row++)
		{
			Array.Copy(crop, row * cropStride, canvas, ((y + row) * canvasStride) + (x * BytesPerPixel), cropStride);
		}

		return true;
	}

	/// <summary>
	/// Keeps only the pixels of an upright trimmed image that fall inside at least one triangle of
	/// its tight mesh, clearing everything else to transparent. The triangle points are canvas
	/// pixels in bottom-up coordinates, three points per triangle. A pixel is retained when its
	/// centre lies inside a triangle, so a deliberately coloured neighbour inside the rectangular
	/// bounding box but outside the mesh is removed. The total marked area is bounded before any
	/// pixel is touched, so a hostile mesh cannot drive unbounded work.
	/// </summary>
	internal static bool TryMaskTriangles(int canvasWidth, int canvasHeight, byte[] canvas, float[] trianglePoints, long maxOperations)
	{
		int triangleCount = trianglePoints.Length / 6;
		if (canvasWidth <= 0 || canvasHeight <= 0 || triangleCount == 0)
		{
			return false;
		}

		if (!TryPixelBuffer(canvasWidth, canvasHeight, out int canvasLength) || canvas.Length < canvasLength)
		{
			return false;
		}

		byte[] marked = new byte[canvasWidth * canvasHeight];
		long markedArea = 0;
		for (int triangle = 0; triangle < triangleCount; triangle++)
		{
			int point = triangle * 6;
			float ax = trianglePoints[point];
			float ay = trianglePoints[point + 1];
			float bx = trianglePoints[point + 2];
			float by = trianglePoints[point + 3];
			float cx = trianglePoints[point + 4];
			float cy = trianglePoints[point + 5];

			// A non-finite coordinate makes every derived bound non-finite too, so the components
			// are all inspected rather than short-circuited, and a single refusal covers them.
			if (!(float.IsFinite(ax) & float.IsFinite(ay) & float.IsFinite(bx) & float.IsFinite(by) & float.IsFinite(cx) & float.IsFinite(cy)))
			{
				return false;
			}

			float minimumX = MathF.Min(ax, MathF.Min(bx, cx));
			float maximumX = MathF.Max(ax, MathF.Max(bx, cx));
			float minimumY = MathF.Min(ay, MathF.Min(by, cy));
			float maximumY = MathF.Max(ay, MathF.Max(by, cy));

			// The pixel-centre test only visits pixels whose centre can fall in the bounding box.
			double firstColumn = Math.Max(0d, Math.Ceiling((double)minimumX - 0.5d));
			double lastColumn = Math.Min(canvasWidth - 1d, Math.Floor((double)maximumX - 0.5d));
			double firstRow = Math.Max(0d, Math.Ceiling((double)canvasHeight - 0.5d - maximumY));
			double lastRow = Math.Min(canvasHeight - 1d, Math.Floor((double)canvasHeight - 0.5d - minimumY));
			if (firstColumn > lastColumn || firstRow > lastRow)
			{
				continue;
			}

			markedArea += (long)(lastColumn - firstColumn + 1d) * (long)(lastRow - firstRow + 1d);
			if (markedArea > maxOperations)
			{
				return false;
			}

			for (int row = (int)firstRow; row <= (int)lastRow; row++)
			{
				float centreY = canvasHeight - row - 0.5f;
				int rowStart = row * canvasWidth;
				for (int column = (int)firstColumn; column <= (int)lastColumn; column++)
				{
					float centreX = column + 0.5f;
					if (InsideTriangle(centreX, centreY, ax, ay, bx, by, cx, cy))
					{
						marked[rowStart + column] = 1;
					}
				}
			}
		}

		for (int pixel = 0; pixel < marked.Length; pixel++)
		{
			if (marked[pixel] == 0)
			{
				int index = pixel * BytesPerPixel;
				canvas[index] = 0;
				canvas[index + 1] = 0;
				canvas[index + 2] = 0;
				canvas[index + 3] = 0;
			}
		}

		return true;
	}

	/// <summary>Reports whether a point lies inside a triangle, its edges included.</summary>
	private static bool InsideTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
	{
		float first = ((px - bx) * (ay - by)) - ((ax - bx) * (py - by));
		float second = ((px - cx) * (by - cy)) - ((bx - cx) * (py - cy));
		float third = ((px - ax) * (cy - ay)) - ((cx - ax) * (py - ay));
		bool hasNegative = (first < 0f) | (second < 0f) | (third < 0f);
		bool hasPositive = (first > 0f) | (second > 0f) | (third > 0f);
		return !(hasNegative && hasPositive);
	}

	/// <summary>
	/// Converts tight-mesh triangle points from sprite-local units into canvas pixels: each point
	/// is scaled by the sprite's pixels-per-unit and shifted so the sprite pivot lands at
	/// <c>rect * pivot</c> from the canvas's bottom-left corner. The points are rewritten in place.
	/// </summary>
	internal static void ToCanvasPixels(float[] points, float pixelsToUnits, float pivotX, float pivotY, float rectWidth, float rectHeight)
	{
		float pivotOffsetX = rectWidth * pivotX;
		float pivotOffsetY = rectHeight * pivotY;
		for (int i = 0; i < points.Length; i += 2)
		{
			points[i] = (points[i] * pixelsToUnits) + pivotOffsetX;
			points[i + 1] = (points[i + 1] * pixelsToUnits) + pivotOffsetY;
		}
	}

	/// <summary>
	/// The four-byte length of a <paramref name="width"/> x <paramref name="height"/> RGBA buffer,
	/// or <see langword="false"/> when the product cannot be an array.
	/// </summary>
	private static bool TryPixelBuffer(int width, int height, out int length)
	{
		length = 0;
		long bytes = (long)width * height * BytesPerPixel;
		if (bytes > Array.MaxLength)
		{
			return false;
		}

		length = (int)bytes;
		return true;
	}

	private static void FlipHorizontal(int width, int height, byte[] rgba)
	{
		int stride = width * BytesPerPixel;
		for (int row = 0; row < height; row++)
		{
			int rowStart = row * stride;
			int left = rowStart;
			int right = rowStart + ((width - 1) * BytesPerPixel);
			while (left < right)
			{
				for (int channel = 0; channel < BytesPerPixel; channel++)
				{
					(rgba[left + channel], rgba[right + channel]) = (rgba[right + channel], rgba[left + channel]);
				}

				left += BytesPerPixel;
				right -= BytesPerPixel;
			}
		}
	}

	private static void FlipVertical(int width, int height, byte[] rgba)
	{
		int stride = width * BytesPerPixel;
		byte[] scratch = new byte[stride];
		for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
		{
			int topStart = top * stride;
			int bottomStart = bottom * stride;
			Array.Copy(rgba, topStart, scratch, 0, stride);
			Array.Copy(rgba, bottomStart, rgba, topStart, stride);
			Array.Copy(scratch, 0, rgba, bottomStart, stride);
		}
	}

	/// <summary>
	/// Turns a top-down RGBA crop a quarter turn clockwise. <paramref name="length"/> is the
	/// already-validated four-byte length of the crop, which the rotated image shares.
	/// </summary>
	private static bool TryRotateClockwise90(int width, int height, byte[] source, int length, out int rotatedWidth, out int rotatedHeight, out byte[] rotated)
	{
		rotatedWidth = height;
		rotatedHeight = width;
		rotated = new byte[length];
		for (int row = 0; row < height; row++)
		{
			for (int column = 0; column < width; column++)
			{
				int sourceIndex = ((row * width) + column) * BytesPerPixel;
				int destination = ((column * rotatedWidth) + (height - 1 - row)) * BytesPerPixel;
				rotated[destination] = source[sourceIndex];
				rotated[destination + 1] = source[sourceIndex + 1];
				rotated[destination + 2] = source[sourceIndex + 2];
				rotated[destination + 3] = source[sourceIndex + 3];
			}
		}

		return true;
	}
}
