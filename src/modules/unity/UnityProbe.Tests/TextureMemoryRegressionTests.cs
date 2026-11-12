using System;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

// The fixtures exercise real decoder allocations; keep them separate from concurrent spill tests.
[Collection("AssetRipper temporary file ownership")]
public sealed class TextureMemoryRegressionTests
{
	[Theory]
	[InlineData(4097, 4096, TextureFormat.RGBA32, 4)]
	[InlineData(8193, 4096, TextureFormat.RGBA32, 4)]
	[InlineData(8192, 8192, TextureFormat.Alpha8, 1)]
	public void DecodesTheActualPixelBufferInsteadOfChargingEveryPixelForFourFloats(
		int width, int height, TextureFormat format, int bytesPerPixel)
	{
		ITexture2D texture = AssetCreator.CreateTexture2D(AssetCreator.CreateCollection(UnityVersion.V_2022));
		texture.Width_C28 = width;
		texture.Height_C28 = height;
		texture.ImageCount_C28 = 1;
		texture.Format_C28E = format;
		int rowBytes = checked(width * bytesPerPixel);
		byte[] data = new byte[checked(rowBytes * height)];
		data[0] = 0x25;
		data[(height - 1) * rowBytes] = 0xC4;
		texture.ImageData_C28 = data;

		Assert.True(UnitySource.TryDecodeTexture(texture, out DirectBitmap bitmap));
		Assert.Equal(data.LongLength, bitmap.ByteSize);
		Assert.Equal(width, bitmap.Width);
		Assert.Equal(height, bitmap.Height);
		Assert.Equal(0xC4, bitmap.Bits[0]);
		Assert.Equal(0x25, bitmap.Bits[(height - 1) * rowBytes]);
	}
}
