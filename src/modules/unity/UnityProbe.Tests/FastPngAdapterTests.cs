using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetRipper.Conversions.FastPng;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

[Collection("AssetRipper temporary file ownership")]
public sealed unsafe class FastPngAdapterTests
{
	[Fact]
	public void EncoderCallsFromDifferentThreadsAreSerialized()
	{
		using ManualResetEventSlim firstEntered = new();
		using ManualResetEventSlim secondStarted = new();
		using ManualResetEventSlim secondEntered = new();
		using ManualResetEventSlim release = new();
		byte[] pixels = Pixels();
		Task first = Task.Factory.StartNew(() => FastPngAdapter.Encode(pixels, 4, 4,
			(input, width, height, channels, output, flags) =>
			{
				firstEntered.Set();
				Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
				return false;
			}), TaskCreationOptions.LongRunning);
		Task? second = null;
		bool overlapped = false;
		try
		{
			Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
			second = Task.Factory.StartNew(() =>
			{
				secondStarted.Set();
				return FastPngAdapter.Encode(pixels, 4, 4, (input, width, height, channels, output, flags) =>
				{
					secondEntered.Set();
					return false;
				});
			}, TaskCreationOptions.LongRunning);
			Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10)));
			overlapped = secondEntered.Wait(TimeSpan.FromMilliseconds(500));
		}
		finally
		{
			release.Set();
			first.GetAwaiter().GetResult();
			second?.GetAwaiter().GetResult();
		}
		Assert.False(overlapped);
		Assert.True(secondEntered.IsSet);
	}

	[Fact]
	public void EncoderFailureReturnsNullAndTheNextExportKeepsTheExactPngBytes()
	{
		byte[] pixels = Pixels();
		byte[] expected = FPng.EncodeImageToMemory(pixels, 4, 4);
		bool invoked = false;
		byte[]? actual = FastPngAdapter.Encode(pixels, 4, 4, (input, width, height, channels, output, flags) =>
		{
			Assert.Equal(4, width);
			Assert.Equal(4, height);
			Assert.Equal(4, channels);
			Assert.Equal(0, flags);
			// Populate the owned vector before failure so cleanup must release a real output.
			Assert.True(FPng.fpng_encode_image_to_memory(input, width, height, channels, output, flags));
			invoked = true;
			return false;
		});
		Assert.True(invoked);
		Assert.Null(actual);
		Assert.Equal(expected, FastPngAdapter.Encode(pixels, 4, 4));
	}

	[Fact]
	public void EncoderExceptionPropagatesUnchangedAndTheNextExportKeepsTheExactPngBytes()
	{
		byte[] pixels = Pixels();
		byte[] expected = FPng.EncodeImageToMemory(pixels, 4, 4);
		InvalidDataException failure = new("encoder failed after writing its output");
		InvalidDataException actual = Assert.Throws<InvalidDataException>(() =>
			FastPngAdapter.Encode(pixels, 4, 4, (input, width, height, channels, output, flags) =>
			{
				Assert.True(FPng.fpng_encode_image_to_memory(input, width, height, channels, output, flags));
				throw failure;
			}));
		Assert.Same(failure, actual);
		Assert.Equal(expected, FastPngAdapter.Encode(pixels, 4, 4));
	}

	private static byte[] Pixels()
	{
		byte[] pixels = new byte[4 * 4 * 4];
		for (int i = 0; i < pixels.Length; i++) pixels[i] = unchecked((byte)(i * 37));
		return pixels;
	}
}
