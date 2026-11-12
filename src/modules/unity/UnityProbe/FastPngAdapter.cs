using System;
using AssetRipper.Conversions.FastPng;
using AssetRipper.Conversions.FastPng.Structures;

namespace Unpackrat.UnityProbe;

/// <summary>Owns the pinned encoder's vector and reserves its bounded output before encoding.</summary>
internal static unsafe class FastPngAdapter
{
	// The generated encoder registers shared pointer indices during lazy table initialization.
	// Its registry is not thread-safe, so serialize calls from different Unity sessions too.
	private static readonly object Gate = new();
	// The injected encoder exercises failure/ownership at this library boundary; no foreign type
	// or allocator contract escapes the implementation.
	internal delegate bool Encoder(void* pixels, int width, int height, int channels, void* output, int flags);

	public static byte[]? Encode(ReadOnlySpan<byte> pixels, int width, int height)
		=> Encode(pixels, width, height, FPng.fpng_encode_image_to_memory);

	internal static byte[]? Encode(ReadOnlySpan<byte> pixels, int width, int height, Encoder encoder)
	{
		lock (Gate)
		{
			// Called only after the metadata plan has bounded dimensions and complete RGB(A) data.
			int channels = pixels.Length / (width * height);
			int capacity = checked((int)TextureMemoryBudget.PngUpperBound(pixels.Length, height));
			using OutputVector output = new();
			return output.Encode(pixels, width, height, channels, capacity, encoder);
		}
	}

	private ref struct OutputVector
	{
		private std_vector vector;
		public OutputVector()
		{
			vector = default;
			fixed (std_vector* pointer = &vector)
				FPng.vector_unsigned_char_class_std_allocator_unsigned_char_Constructor(pointer);
		}

		public byte[]? Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, int capacity, Encoder encoder)
		{
			fixed (std_vector* pointer = &vector)
			fixed (byte* input = pixels)
			{
				// resize(0) retains capacity. Subsequent encoder resizes therefore never relocate
				// an image-sized vector (the package's memmove rents another full-size array).
				FPng.resize_wwmwtsa(pointer, capacity);
				FPng.resize_wwmwtsa(pointer, 0);
				if (!encoder(input, width, height, channels, pointer, 0)) return null;
				byte* first = (byte*)vector.field_0.field_0.field_0;
				byte* last = (byte*)vector.field_0.field_0.field_1;
				return new ReadOnlySpan<byte>(first, checked((int)(last - first))).ToArray();
			}
		}

		public void Dispose()
		{
			fixed (std_vector* pointer = &vector)
				FPng.vector_unsigned_char_class_std_allocator_unsigned_char_Destructor(pointer);
		}
	}
}
