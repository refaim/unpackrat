using System;
using System.IO;
using System.Runtime.InteropServices;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

[Collection("AssetRipper temporary file ownership")]
public sealed class UnityTexturePixelLayoutTests
{
	public static TheoryData<TextureFormat, int, int, bool> Formats => new()
	{
		{ TextureFormat.Alpha8, 1, 4, true },
		{ TextureFormat.ARGB4444, 2, 4, true },
		{ TextureFormat.RGB24, 3, 3, false },
		{ TextureFormat.RGBA32, 4, 4, false },
		{ TextureFormat.ARGB32, 4, 4, true },
		{ TextureFormat.ARGBFloat, 16, 4, true },
		{ TextureFormat.RGB565, 2, 3, true },
		{ TextureFormat.BGR24, 3, 3, true },
		{ TextureFormat.R16, 2, 3, true },
		{ TextureFormat.RGBA4444, 2, 4, true },
		{ TextureFormat.BGRA32_14, 4, 4, true },
		{ TextureFormat.BGRA32_37, 4, 4, true },
		{ TextureFormat.RHalf, 2, 3, true },
		{ TextureFormat.RGHalf, 4, 3, true },
		{ TextureFormat.RGBAHalf, 8, 4, true },
		{ TextureFormat.RFloat, 4, 3, true },
		{ TextureFormat.RGFloat, 8, 3, true },
		{ TextureFormat.RGBAFloat, 16, 4, true },
		{ TextureFormat.RGB9e5Float, 4, 3, true },
		{ TextureFormat.RG16, 2, 3, true },
		{ TextureFormat.R8, 1, 3, true },
		{ TextureFormat.RG32, 4, 3, true },
		{ TextureFormat.RGB48, 6, 3, true },
		{ TextureFormat.RGBA64, 8, 4, true },
		{ TextureFormat.R8_SIGNED, 1, 3, true },
		{ TextureFormat.RG16_SIGNED, 2, 3, true },
		{ TextureFormat.RGB24_SIGNED, 3, 3, true },
		{ TextureFormat.RGBA32_SIGNED, 4, 4, true },
		{ TextureFormat.R16_SIGNED, 2, 3, true },
		{ TextureFormat.RG32_SIGNED, 4, 3, true },
		{ TextureFormat.RGB48_SIGNED, 6, 3, true },
		{ TextureFormat.RGBA64_SIGNED, 8, 4, true },
		{ TextureFormat.DXT1, 4, 4, false },
	};

	[Theory]
	[MemberData(nameof(Formats))]
	public void DeclaredPixelLayoutAndPngBytesMatchThePinnedDecoder(
		TextureFormat format, int decodedBytesPerPixel, int pngBytesPerPixel, bool requiresConversion)
	{
		byte[] data = PixelData(format, format == TextureFormat.DXT1 ? 8 : 4 * 4 * decodedBytesPerPixel);
		ITexture2D texture = Texture(4, 4, 1, format, data);
		Assert.Equal(new TexturePixelLayout(decodedBytesPerPixel, pngBytesPerPixel, requiresConversion),
			UnitySource.GetTexturePixelLayout(format));
		Assert.True(TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap));
		Assert.Equal(decodedBytesPerPixel, bitmap.PixelSize);
		Assert.Equal(4 * 4 * decodedBytesPerPixel, bitmap.ByteSize);
		AssertPngParity(texture, bitmap, 4, 4);
	}

	[Theory]
	[InlineData(65536, 1, 1)]
	[InlineData(1, 32768, 2)]
	[InlineData(4, 4, 2)]
	public void WideAndLayeredTexturesKeepThePinnedPngLayout(int width, int height, int depth)
	{
		byte[] data = PixelData(TextureFormat.RGBA32, checked(width * height * depth * 4));
		ITexture2D texture = Texture(width, height, depth, TextureFormat.RGBA32, data);
		Assert.True(TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap));
		Assert.Equal(depth, bitmap.Depth);
		AssertPngParity(texture, bitmap, width, height * depth);
	}

	[Fact]
	public void UnknownFormatHasTheDefaultPixelPlanAndFailsWithoutRenderingPng()
	{
		TextureFormat unknown = (TextureFormat)int.MaxValue;
		Assert.Equal(new TexturePixelLayout(4, 4, false), UnitySource.GetTexturePixelLayout(unknown));
		ITexture2D texture = Texture(4, 4, 1, unknown, new byte[64]);
		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(Assert.Single(UnitySource.Wrap([texture])));
		Assert.Null(wrapped.RenderPng());
	}

	private static ITexture2D Texture(int width, int height, int depth, TextureFormat format, byte[] data)
	{
		ITexture2D texture = AssetCreator.CreateTexture2D(AssetCreator.CreateCollection(UnityVersion.V_2022));
		texture.Width_C28 = width;
		texture.Height_C28 = height;
		texture.ImageCount_C28 = depth;
		texture.Format_C28E = format;
		texture.ImageData_C28 = data;
		return texture;
	}

	private static void AssertPngParity(ITexture2D texture, DirectBitmap bitmap, int width, int rows)
	{
		using MemoryStream expected = new();
		bitmap.SaveAsPng(expected);
		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(Assert.Single(UnitySource.Wrap([texture])));
		byte[] actual = Assert.IsType<byte[]>(wrapped.RenderPng());
		Assert.Equal(expected.ToArray(), actual);
		(int actualWidth, int actualRows, byte[] pixels) = PngDecoder.Decode(actual);
		Assert.Equal(width, actualWidth);
		Assert.Equal(rows, actualRows);
		Assert.Equal(width * rows * 4, pixels.Length);
	}

	private static byte[] PixelData(TextureFormat format, int byteCount)
	{
		byte[] data = new byte[byteCount];
		if (format is TextureFormat.ARGBFloat or TextureFormat.RFloat or TextureFormat.RGFloat or TextureFormat.RGBAFloat)
		{
			Span<float> channels = MemoryMarshal.Cast<byte, float>(data.AsSpan());
			for (int i = 0; i < channels.Length; i++) channels[i] = (i % 5) / 4f;
		}
		else if (format is TextureFormat.RHalf or TextureFormat.RGHalf or TextureFormat.RGBAHalf)
		{
			Span<Half> channels = MemoryMarshal.Cast<byte, Half>(data.AsSpan());
			for (int i = 0; i < channels.Length; i++) channels[i] = (Half)((i % 5) / 4f);
		}
		else if (format != TextureFormat.DXT1)
		{
			for (int i = 0; i < data.Length; i++) data[i] = unchecked((byte)(i * 17 + 3));
		}
		return data;
	}
}
