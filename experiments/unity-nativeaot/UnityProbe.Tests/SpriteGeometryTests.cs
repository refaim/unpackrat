using System;
using System.Linq;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Direct coverage of the pure sprite geometry: the bottom-left to top-down rect mapping, the
/// uprighting flips and rotations, the transparent-canvas placement and the bounded tight-mesh
/// mask, including every refusal of a hostile or malformed input.
/// </summary>
public sealed class SpriteGeometryTests
{
	private static byte[] Grid(int width, int height, params int[] indices)
	{
		byte[] data = new byte[width * height * 4];
		for (int index = 0; index < indices.Length; index++)
		{
			data[index * 4] = (byte)(1 + (indices[index] * 7));
			data[index * 4 + 1] = (byte)(100 + (indices[index] * 5));
			data[index * 4 + 2] = (byte)(250 - (indices[index] * 3));
			data[index * 4 + 3] = 255;
		}

		return data;
	}

	private static int[] Indices(byte[] rgba)
	{
		int[] indices = new int[rgba.Length / 4];
		for (int i = 0; i < indices.Length; i++)
		{
			indices[i] = rgba[i * 4] / 7;
		}

		return indices;
	}

	[Fact]
	public void CropRectMapsABottomLeftRectToTopDownRows()
	{
		Assert.True(SpriteGeometry.TryCropRect(4, 4, 1f, 1f, 2f, 3f, out int left, out int top, out int width, out int height));
		Assert.Equal(1, left);
		Assert.Equal(0, top);
		Assert.Equal(2, width);
		Assert.Equal(3, height);
	}

	[Theory]
	[InlineData(1f, 1f, 0f, 2f)]
	[InlineData(1f, 1f, 2f, 0f)]
	[InlineData(float.NaN, 1f, 2f, 2f)]
	[InlineData(1f, float.NaN, 2f, 2f)]
	[InlineData(1f, 1f, float.NaN, 2f)]
	[InlineData(1f, 1f, 2f, float.NaN)]
	[InlineData(-1f, 1f, 2f, 2f)]
	[InlineData(1f, -1f, 2f, 2f)]
	[InlineData(1f, 1f, 4f, 2f)]
	[InlineData(1f, 1f, 2f, 4f)]
	public void CropRectRefusesADegenerateOrOutOfBoundsRect(float x, float y, float width, float height)
		=> Assert.False(SpriteGeometry.TryCropRect(4, 4, x, y, width, height, out _, out _, out _, out _));

	[Fact]
	public void CanvasIsTheRoundedRectAndTransparent()
	{
		Assert.True(SpriteGeometry.TryCanvas(2f, 3f, 1024, out int width, out int height, out byte[] canvas));
		Assert.Equal(2, width);
		Assert.Equal(3, height);
		Assert.Equal(24, canvas.Length);
		Assert.All(canvas, value => Assert.Equal(0, value));
	}

	[Theory]
	[InlineData(0f, 3f)]
	[InlineData(2f, 0f)]
	[InlineData(float.NaN, 3f)]
	[InlineData(3f, float.NaN)]
	public void CanvasRefusesANonPositiveRect(float width, float height)
		=> Assert.False(SpriteGeometry.TryCanvas(width, height, 1024, out _, out _, out _));

	[Theory]
	[InlineData(1e30f, 3f)]
	[InlineData(3f, 1e30f)]
	public void CanvasRefusesAnUnrepresentableRect(float width, float height)
		=> Assert.False(SpriteGeometry.TryCanvas(width, height, long.MaxValue, out _, out _, out _));

	[Fact]
	public void CanvasRefusesARectOverTheByteCap()
		=> Assert.False(SpriteGeometry.TryCanvas(2f, 2f, 4, out _, out _, out _));

	[Fact]
	public void CanvasRefusesARectOverTheArrayLimit()
		=> Assert.False(SpriteGeometry.TryCanvas(26000f, 26000f, long.MaxValue, out _, out _, out _));

	[Fact]
	public void UprightWithoutRotationKeepsTheImage()
	{
		byte[] source = Grid(3, 2, 0, 1, 2, 3, 4, 5);
		Assert.True(SpriteGeometry.TryUpright(3, 2, source, SpriteGeometry.RotationNone, out int width, out int height, out byte[] upright));
		Assert.Equal(3, width);
		Assert.Equal(2, height);
		Assert.Same(source, upright);
	}

	[Theory]
	[InlineData(SpriteGeometry.RotationFlipHorizontal, new[] { 2, 1, 0, 5, 4, 3 })]
	[InlineData(SpriteGeometry.RotationFlipVertical, new[] { 3, 4, 5, 0, 1, 2 })]
	[InlineData(SpriteGeometry.RotationRotate180, new[] { 5, 4, 3, 2, 1, 0 })]
	public void UprightAppliesAFlip(int rotation, int[] expected)
	{
		byte[] source = Grid(3, 2, 0, 1, 2, 3, 4, 5);
		Assert.True(SpriteGeometry.TryUpright(3, 2, source, rotation, out int width, out int height, out byte[] upright));
		Assert.Equal(3, width);
		Assert.Equal(2, height);
		Assert.Equal(expected, Indices(upright));
	}

	[Fact]
	public void UprightRotate90TurnsTheTopDownImageClockwise()
	{
		// Top-down DEF / ABC turned clockwise scans as AD / BE / CF.
		byte[] source = Grid(3, 2, 3, 4, 5, 0, 1, 2);
		Assert.True(SpriteGeometry.TryUpright(3, 2, source, SpriteGeometry.RotationRotate90, out int width, out int height, out byte[] upright));
		Assert.Equal(2, width);
		Assert.Equal(3, height);
		Assert.Equal([0, 3, 1, 4, 2, 5], Indices(upright));
	}

	[Fact]
	public void UprightRefusesAnUnknownRotation()
	{
		byte[] source = Grid(2, 2, 0, 1, 2, 3);
		Assert.False(SpriteGeometry.TryUpright(2, 2, source, 15, out _, out _, out _));
	}

	[Fact]
	public void UprightRefusesASourceTooShortForItsDimensions()
	{
		byte[] source = new byte[1];
		Assert.False(SpriteGeometry.TryUpright(2, 2, source, SpriteGeometry.RotationNone, out _, out _, out _));
	}

	[Fact]
	public void UprightRefusesAnUnrepresentableRotatedSize()
	{
		byte[] source = new byte[1];
		Assert.False(SpriteGeometry.TryUpright(int.MaxValue, 2, source, SpriteGeometry.RotationRotate90, out _, out _, out _));
	}

	[Fact]
	public void PlacementPutsATrimmedImageAtTheRecordedOffset()
	{
		Assert.True(SpriteGeometry.TryPlacement(1f, 1f, 4, 2, out int x, out int y));
		Assert.Equal(1, x);
		Assert.Equal(1, y);
	}

	[Theory]
	[InlineData(float.NaN, 0f)]
	[InlineData(0f, float.NaN)]
	[InlineData(-1e30f, 0f)]
	[InlineData(1e30f, 0f)]
	[InlineData(0f, 1e30f)]
	[InlineData(0f, -1e30f)]
	public void PlacementRefusesANonFiniteOrUnrepresentableOffset(float offsetX, float offsetY)
		=> Assert.False(SpriteGeometry.TryPlacement(offsetX, offsetY, 4, 2, out _, out _));

	[Fact]
	public void PlaceCopiesTheCropIntoTheCanvas()
	{
		byte[] crop = Grid(2, 2, 0, 1, 2, 3);
		byte[] canvas = new byte[4 * 4 * 4];
		Assert.True(SpriteGeometry.TryPlace(4, 4, 1, 1, 2, 2, crop, canvas));

		byte[] expected = new byte[4 * 4 * 4];
		System.Array.Copy(crop, 0, expected, (((1 * 4) + 1) * 4), 2 * 4);
		System.Array.Copy(crop, 2 * 4, expected, (((2 * 4) + 1) * 4), 2 * 4);
		Assert.Equal(expected, canvas);
	}

	[Theory]
	[InlineData(-1, 0, 2, 2, 4, 4)]
	[InlineData(0, -1, 2, 2, 4, 4)]
	[InlineData(0, 0, 0, 2, 4, 4)]
	[InlineData(0, 0, 2, 0, 4, 4)]
	[InlineData(0, 0, 2, 2, 0, 4)]
	[InlineData(0, 0, 2, 2, 4, 0)]
	[InlineData(3, 0, 2, 2, 4, 4)]
	[InlineData(0, 3, 2, 2, 4, 4)]
	public void PlaceRefusesAnIllFittingPlacement(int x, int y, int cropWidth, int cropHeight, int canvasWidth, int canvasHeight)
		=> Assert.False(SpriteGeometry.TryPlace(canvasWidth, canvasHeight, x, y, cropWidth, cropHeight, new byte[64], new byte[64]));

	[Fact]
	public void PlaceRefusesAnUndersizedCanvas()
		=> Assert.False(SpriteGeometry.TryPlace(4, 4, 0, 0, 2, 2, new byte[16], new byte[1]));

	[Fact]
	public void PlaceRefusesAnUndersizedCrop()
		=> Assert.False(SpriteGeometry.TryPlace(4, 4, 0, 0, 2, 2, new byte[1], new byte[64]));

	[Fact]
	public void PlaceRefusesAnUnrepresentableCanvas()
		=> Assert.False(SpriteGeometry.TryPlace(26000, 26000, 0, 0, 1, 1, new byte[64], new byte[64]));

	[Fact]
	public void PlaceRefusesAnUnrepresentableCrop()
		=> Assert.False(SpriteGeometry.TryPlace(4, 4, 0, 0, 26000, 26000, new byte[64], new byte[64]));

	[Fact]
	public void MaskKeepsOnlyThePixelsInsideTheTriangle()
	{
		byte[] canvas = [.. Enumerable.Repeat((byte)255, 3 * 3 * 4)];
		float[] triangle = [0f, 0f, 3f, 0f, 0f, 3f];

		Assert.True(SpriteGeometry.TryMaskTriangles(3, 3, canvas, triangle, 1024));

		int[] alpha = new int[9];
		for (int pixel = 0; pixel < 9; pixel++)
		{
			alpha[pixel] = canvas[(pixel * 4) + 3];
		}

		Assert.Equal([255, 0, 0, 255, 255, 0, 255, 255, 255], alpha);
	}

	[Fact]
	public void MaskClearsEverythingWhenNoTriangleTouchesTheCanvas()
	{
		byte[] canvas = [.. Enumerable.Repeat((byte)255, 3 * 3 * 4)];
		// The triangle overlaps the canvas columns but sits entirely below its rows.
		float[] triangle = [0f, 100f, 3f, 100f, 0f, 103f];

		Assert.True(SpriteGeometry.TryMaskTriangles(3, 3, canvas, triangle, 1024));
		Assert.All(canvas, value => Assert.Equal(0, value));
	}

	[Fact]
	public void MaskClearsEverythingWhenNoTriangleOverlapsTheCanvasColumns()
	{
		byte[] canvas = [.. Enumerable.Repeat((byte)255, 3 * 3 * 4)];
		float[] triangle = [100f, 0f, 103f, 0f, 100f, 3f];

		Assert.True(SpriteGeometry.TryMaskTriangles(3, 3, canvas, triangle, 1024));
		Assert.All(canvas, value => Assert.Equal(0, value));
	}

	[Theory]
	[InlineData(0, 3)]
	[InlineData(3, 0)]
	public void MaskRefusesAnEmptyCanvas(int width, int height)
		=> Assert.False(SpriteGeometry.TryMaskTriangles(width, height, new byte[64], [0f, 0f, 3f, 0f, 0f, 3f], 1024));

	[Fact]
	public void MaskRefusesAnEmptyTriangle()
		=> Assert.False(SpriteGeometry.TryMaskTriangles(3, 3, new byte[64], [], 1024));

	[Fact]
	public void MaskRefusesAnUndersizedCanvas()
		=> Assert.False(SpriteGeometry.TryMaskTriangles(3, 3, new byte[1], [0f, 0f, 3f, 0f, 0f, 3f], 1024));

	[Fact]
	public void MaskRefusesAnUnrepresentableCanvas()
		=> Assert.False(SpriteGeometry.TryMaskTriangles(26000, 26000, new byte[64], [0f, 0f, 3f, 0f, 0f, 3f], 1024));

	[Fact]
	public void MaskRefusesANonFiniteTriangle()
		=> Assert.False(SpriteGeometry.TryMaskTriangles(3, 3, new byte[64], [float.NaN, 0f, 3f, 0f, 0f, 3f], 1024));

	[Fact]
	public void MaskRefusesMoreWorkThanItsCap()
		=> Assert.False(SpriteGeometry.TryMaskTriangles(3, 3, new byte[64], [0f, 0f, 3f, 0f, 0f, 3f], 1));

	[Fact]
	public void CanvasPixelsScaleByPixelsPerUnitAndShiftToThePivot()
	{
		float[] points = [-1.5f, -1.5f, 1.5f, -1.5f, -1.5f, 1.5f];
		SpriteGeometry.ToCanvasPixels(points, 1f, 0.5f, 0.5f, 3f, 3f);
		Assert.Equal([0f, 0f, 3f, 0f, 0f, 3f], points);
	}

	/// <summary>A declared mesh just inside every limit, used as the base of the refusal cases.</summary>
	private static SpriteGeometry.DeclaredMesh DeclaredMesh() => new(3, 36, 36, 36, 3, 3, 1, 1);

	[Fact]
	public void DeclaredMeshAcceptsAWithinLimitsMesh()
		=> Assert.True(SpriteGeometry.TryDeclaredMesh(DeclaredMesh()));

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void DeclaredMeshRefusesANonPositiveVertexCount(int vertexCount)
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { VertexCount = vertexCount }));

	[Fact]
	public void DeclaredMeshRefusesMoreVerticesThanItsLimit()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { VertexCount = SpriteGeometry.MaxMeshVertexCount + 1 }));

	[Theory]
	[InlineData(0L)]
	[InlineData(-1L)]
	public void DeclaredMeshRefusesANonPositiveDecodedSize(long decodedBytes)
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { DecodedBytes = decodedBytes }));

	[Fact]
	public void DeclaredMeshRefusesMoreDecodedBytesThanItsLimit()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { DecodedBytes = SpriteGeometry.MaxMeshBytes + 1 }));

	[Fact]
	public void DeclaredMeshRefusesANegativeSourceRange()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { RequiredSourceBytes = -1 }));

	[Fact]
	public void DeclaredMeshRefusesASourceRangeBeyondTheByteLimit()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { RequiredSourceBytes = SpriteGeometry.MaxMeshBytes + 1, SourceLength = int.MaxValue }));

	[Fact]
	public void DeclaredMeshRefusesASourceRangeBeyondTheBody()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { RequiredSourceBytes = 37 }));

	[Fact]
	public void DeclaredMeshRefusesANegativeIndexCount()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { IndexCount = -1 }));

	[Fact]
	public void DeclaredMeshRefusesMoreIndicesThanItsLimit()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { IndexCount = SpriteGeometry.MaxMeshIndexCount + 1 }));

	[Fact]
	public void DeclaredMeshRefusesANegativeIndexBufferCount()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { IndexBufferCount = -1 }));

	[Fact]
	public void DeclaredMeshRefusesMoreIndexBufferEntriesThanItsLimit()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { IndexBufferCount = SpriteGeometry.MaxMeshIndexCount + 1 }));

	[Fact]
	public void DeclaredMeshRefusesANegativeSubMeshCount()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { SubMeshCount = -1 }));

	[Fact]
	public void DeclaredMeshRefusesMoreSubMeshesThanItsLimit()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { SubMeshCount = SpriteGeometry.MaxMeshSubMeshCount + 1 }));

	[Fact]
	public void DeclaredMeshRefusesANegativeTriangleCount()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { TriangleCount = -1 }));

	[Fact]
	public void DeclaredMeshRefusesMoreTrianglesThanItsLimit()
		=> Assert.False(SpriteGeometry.TryDeclaredMesh(DeclaredMesh() with { TriangleCount = SpriteGeometry.MaxMeshTriangleCount + 1 }));

	[Theory]
	[InlineData(0, true)]
	[InlineData(SpriteGeometry.MaxMeshChannelCount, true)]
	[InlineData(SpriteGeometry.MaxMeshChannelCount + 1, false)]
	[InlineData(-1, false)]
	public void DeclaredChannelCountIsBounded(int channelCount, bool expected)
		=> Assert.Equal(expected, SpriteGeometry.TryDeclaredChannelCount(channelCount));

	[Theory]
	[InlineData(0, true)]
	[InlineData(SpriteGeometry.MaxMeshStreamCount, true)]
	[InlineData(SpriteGeometry.MaxMeshStreamCount + 1, false)]
	[InlineData(-1, false)]
	public void DeclaredStreamCountIsBounded(int streamCount, bool expected)
		=> Assert.Equal(expected, SpriteGeometry.TryDeclaredStreamCount(streamCount));

	[Theory]
	[InlineData(3, false, 3)]
	[InlineData(1, false, 1)]
	[InlineData(1, true, 4)]
	public void ChannelDimensionUsesTheLegacyColorOverride(byte rawDimension, bool legacyColorOverride, int expected)
		=> Assert.Equal(expected, SpriteGeometry.ChannelDimension(rawDimension, legacyColorOverride));

	[Theory]
	[InlineData(0, 1u, 0, false)]
	[InlineData(3, 0u, 0, false)]
	[InlineData(3, 1u, 0, true)]
	[InlineData(3, 4u, 2, true)]
	public void IsDecodedChannelNeedsADimensionAndItsMaskBit(int dimension, uint channelMask, int channelIndex, bool expected)
		=> Assert.Equal(expected, SpriteGeometry.IsDecodedChannel(dimension, channelMask, channelIndex));
}
