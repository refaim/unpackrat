using System;

namespace Unpackrat.UnityProbe;

internal readonly record struct TexturePixelLayout(int DecodedBytesPerPixel, int PngBytesPerPixel, bool RequiresConversion);

/// <summary>Metadata-only admission for the pinned decoder and ordinary FastPng path.</summary>
internal static class TextureMemoryBudget
{
	// Per operation, including native encoder vectors. Pixel buffers remain <=256 MiB;
	// PNG buffers are separately bounded by the filtered-row/stored-deflate envelope below.
	public const long MaxWorkingBytes = 1024L * 1024 * 1024;
	// Deflate tables, allocator rounding and fixed codec state, separate from image-sized buffers.
	public const long EncoderFixedBytes = 1024 * 1024;
	public const long LegacySourceBytes = 128L * 1024 * 1024;

	public static long PngUpperBound(long pngInput, long rows)
	{
		long filtered = pngInput + rows;
		return filtered + 5 * ((filtered + 65534) / 65535) + 80;
	}

	public static bool TryGetWorkingBytes(int width, int height, int depth, TexturePixelLayout layout,
		ulong sourceBytes, out long bytes)
	{
		bytes = 0;
		if (sourceBytes > (ulong)Budgets.MaxTextureSourceBytes
			|| !Budgets.TryGetTextureBytes(width, height, depth, layout.DecodedBytesPerPixel, out long decoded)
			|| !Budgets.TryGetTextureBytes(width, height, depth, layout.PngBytesPerPixel, out long pngInput))
			return false;

		long rows = (long)height * depth;
		if (width > ushort.MaxValue || rows > ushort.MaxValue) return false;
		long conversion = layout.RequiresConversion ? pngInput : 0;
		long source = (long)sourceBytes;
		// FastPng output capacity is reserved once, so no 1.5x relocation or hidden ArrayPool
		// memmove buffer occurs. Filtering and managed output copying occupy separate phases.
		long filtered = pngInput + rows + 7;
		long png = PngUpperBound(pngInput, rows);
		long encoder = png + Math.Max(filtered, png);
		long encoding = source + decoded + conversion + encoder + EncoderFixedBytes;
		long decoding = 3 * source + 2 * decoded;
		long working = Math.Max(decoding, encoding);
		if (working > MaxWorkingBytes) return false;
		bytes = working;
		return true;
	}
}
