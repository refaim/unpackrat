using System;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class TextureMemoryBudgetTests
{
	[Theory]
	[InlineData(5000, 5000, 4, 100_000_000L)]
	[InlineData(8192, 8192, 1, 67_108_864L)]
	[InlineData(4096, 4096, 16, 268_435_456L)]
	public void DecodedLimitUsesTheActualPixelSize(int width, int height, int pixelBytes, long expected)
	{
		Assert.True(Budgets.TryGetTextureBytes(width, height, 1, pixelBytes, out long bytes));
		Assert.Equal(expected, bytes);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(17)]
	public void InvalidPixelSizeIsRejected(int pixelBytes)
	{
		Assert.False(Budgets.TryGetTextureBytes(1, 1, 1, pixelBytes, out long bytes));
		Assert.Equal(0, bytes);
	}

	[Theory]
	[InlineData(10440, 3960, 4, 4, false, 165_369_600UL)]
	[InlineData(5000, 5000, 4, 4, false, 100_000_000UL)]
	[InlineData(4800, 7200, 4, 4, false, 138_240_000UL)]
	[InlineData(5000, 3500, 4, 4, false, 70_000_000UL)]
	[InlineData(6720, 2800, 4, 4, false, 75_264_000UL)]
	[InlineData(8192, 8192, 1, 4, true, 67_108_864UL)]
	public void KnownGameTexturesFitTheCompletePngPlan(int width, int height, int pixelBytes,
		int pngPixelBytes, bool convert, ulong sourceBytes)
	{
		Assert.True(TextureMemoryBudget.TryGetWorkingBytes(width, height, 1,
			new(pixelBytes, pngPixelBytes, convert), sourceBytes, out long bytes));
		Assert.InRange(bytes, (long)sourceBytes + (long)width * height * pixelBytes,
			TextureMemoryBudget.MaxWorkingBytes);
	}

	[Theory]
	[InlineData(0, 1, 1, 4, 4, false, 4UL)]
	[InlineData(1, 0, 1, 4, 4, false, 4UL)]
	[InlineData(1, 1, 0, 4, 4, false, 4UL)]
	[InlineData(int.MaxValue, int.MaxValue, int.MaxValue, 4, 4, false, 4UL)]
	[InlineData(1, 1, 1, 4, 4, false, ulong.MaxValue)]
	[InlineData(1, 1, 1, 4, 4, false, 268_435_457UL)]
	[InlineData(8193, 8192, 1, 4, 4, false, 4UL)]
	[InlineData(8193, 8192, 1, 1, 4, true, 4UL)]
	[InlineData(65536, 1, 1, 4, 4, false, 4UL)]
	[InlineData(1, 32768, 2, 4, 4, false, 4UL)]
	[InlineData(8192, 8192, 1, 4, 4, false, 268_435_456UL)]
	[InlineData(1, 1, 1, 4, 0, false, 4UL)]
	public void InvalidOrOversizedMetadataIsRefusedWithoutAllocating(int width, int height, int depth,
		int pixelBytes, int pngPixelBytes, bool convert, ulong sourceBytes)
	{
		Assert.False(TextureMemoryBudget.TryGetWorkingBytes(width, height, depth,
			new(pixelBytes, pngPixelBytes, convert), sourceBytes, out long bytes));
		Assert.Equal(0, bytes);
	}

	[Fact]
	public void FpngPlanIncludesReservedOutputFilterAndManagedCopy()
	{
		const int width = 8, height = 4;
		long filtered = (width * 4L + 1) * height;
		long png = filtered + 5 * ((filtered + 65534) / 65535) + 80;
		long encoder = png + Math.Max(filtered + 7, png);
		Assert.True(TextureMemoryBudget.TryGetWorkingBytes(width, height, 1, new(1, 4, true), 32, out long bytes));
		Assert.Equal(32 + 32 + 128 + encoder + TextureMemoryBudget.EncoderFixedBytes, bytes);
	}

	[Fact]
	public void FlattenedLayersAreBudgetedTogether()
	{
		Assert.True(TextureMemoryBudget.TryGetWorkingBytes(8, 2, 2, new(4, 4, false), 128, out long layered));
		Assert.True(TextureMemoryBudget.TryGetWorkingBytes(8, 4, 1, new(4, 4, false), 128, out long flat));
		Assert.Equal(flat, layered);
	}

	[Fact]
	public void SourceAtItsLimitFitsWhenTheOtherBuffersAreTiny()
	{
		Assert.True(TextureMemoryBudget.TryGetWorkingBytes(1, 1, 1, new(4, 4, false),
			(ulong)Budgets.MaxTextureSourceBytes, out long bytes));
		Assert.Equal(3 * Budgets.MaxTextureSourceBytes + 8, bytes);
	}
}
