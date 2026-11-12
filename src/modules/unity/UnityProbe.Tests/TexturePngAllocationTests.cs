using System;
using System.Diagnostics.Tracing;
using System.Linq;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

[Collection("AssetRipper temporary file ownership")]
public sealed class TexturePngAllocationTests
{
	[Fact]
	public void IncompressiblePngDoesNotRentAnImageSizedVectorRelocationBuffer()
	{
		const int width = 128, height = 128;
		byte[] pixels = new byte[width * height * 4];
		uint value = 0x17459481;
		for (int i = 0; i < pixels.Length; i++)
		{
			value ^= value << 13;
			value ^= value >> 17;
			value ^= value << 5;
			pixels[i] = (byte)value;
		}
		ITexture2D texture = AssetCreator.CreateTexture2D(AssetCreator.CreateCollection(UnityVersion.V_2022));
		texture.Width_C28 = width; texture.Height_C28 = height; texture.ImageCount_C28 = 1;
		texture.Format_C28E = TextureFormat.RGBA32; texture.ImageData_C28 = pixels;
		IProbeTexture resource = Assert.IsAssignableFrom<IProbeTexture>(UnitySource.Wrap([texture]).Single());
		using PayloadRentalListener rentals = new(pixels.Length);

		byte[] png = Assert.IsType<byte[]>(resource.RenderPng());

		Assert.True(png.Length > pixels.Length); // Exercises the encoder's stored/raw fallback.
		Assert.Equal(0, rentals.ImageSizedRentals);
	}

	private sealed class PayloadRentalListener(int minimumBytes) : EventListener
	{
		private readonly int threadId = Environment.CurrentManagedThreadId;
		public int ImageSizedRentals { get; private set; }
		protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
		{
			if (source.Name == "System.Buffers.ArrayPoolEventSource")
				EnableEvents(source, EventLevel.Verbose);
		}
		protected override void OnEventWritten(EventWrittenEventArgs data)
		{
			if (data.EventName == "BufferRented" && Environment.CurrentManagedThreadId == threadId
				&& data.Payload is { Count: >= 2 } && data.Payload[1] is int size && size >= minimumBytes)
				ImageSizedRentals++;
		}
	}
}
