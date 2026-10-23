using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Minimal PNG reader for the tiny fixtures these tests produce. It inflates the IDAT stream and
/// reverses the per-scanline filters so a test can assert the exact pixels a texture exported,
/// instead of trusting the signature alone.
/// </summary>
internal static class PngDecoder
{
	private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

	/// <summary>Decodes a non-interlaced, 8-bit PNG into top-down RGBA pixels.</summary>
	public static (int Width, int Height, byte[] Rgba) Decode(byte[] png)
	{
		ReadOnlySpan<byte> span = png;
		if (span.Length < Signature.Length || !span[..Signature.Length].SequenceEqual(Signature))
		{
			throw new InvalidDataException("Not a PNG file.");
		}

		int width = 0;
		int height = 0;
		int bitDepth = 0;
		int colorType = 0;
		int interlace = 0;
		using MemoryStream idat = new();
		int offset = Signature.Length;
		while (offset + 12 <= span.Length)
		{
			int length = BinaryPrimitives.ReadInt32BigEndian(span.Slice(offset, 4));
			string type = System.Text.Encoding.ASCII.GetString(span.Slice(offset + 4, 4));
			ReadOnlySpan<byte> data = span.Slice(offset + 8, length);
			if (type == "IHDR")
			{
				width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
				height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
				bitDepth = data[8];
				colorType = data[9];
				interlace = data[12];
			}
			else if (type == "IDAT")
			{
				idat.Write(data);
			}
			else if (type == "IEND")
			{
				break;
			}

			offset += 12 + length;
		}

		if (bitDepth != 8 || interlace != 0)
		{
			throw new InvalidDataException($"Unsupported PNG: bit depth {bitDepth}, interlace {interlace}.");
		}

		int channels = colorType switch
		{
			0 => 1,
			2 => 3,
			4 => 2,
			6 => 4,
			_ => throw new InvalidDataException($"Unsupported PNG color type {colorType}."),
		};

		byte[] raw = Inflate(idat.ToArray());
		int stride = width * channels;
		byte[] rgba = new byte[width * height * 4];
		byte[] previous = new byte[stride];
		byte[] current = new byte[stride];
		int position = 0;
		for (int y = 0; y < height; y++)
		{
			byte filter = raw[position++];
			for (int i = 0; i < stride; i++)
			{
				int left = i >= channels ? current[i - channels] : 0;
				int up = previous[i];
				int upLeft = i >= channels ? previous[i - channels] : 0;
				int value = raw[position + i];
				current[i] = filter switch
				{
					0 => (byte)value,
					1 => (byte)(value + left),
					2 => (byte)(value + up),
					3 => (byte)(value + ((left + up) >> 1)),
					4 => (byte)(value + Paeth(left, up, upLeft)),
					_ => throw new InvalidDataException($"Unsupported PNG filter {filter}."),
				};
			}

			position += stride;
			for (int x = 0; x < width; x++)
			{
				int source = x * channels;
				int target = (y * width + x) * 4;
				rgba[target] = current[source];
				rgba[target + 1] = channels >= 3 ? current[source + 1] : current[source];
				rgba[target + 2] = channels >= 3 ? current[source + 2] : current[source];
				rgba[target + 3] = channels switch
				{
					4 => current[source + 3],
					2 => current[source + 1],
					_ => (byte)255,
				};
			}

			(current, previous) = (previous, current);
		}

		return (width, height, rgba);
	}

	private static byte[] Inflate(byte[] data)
	{
		using MemoryStream input = new(data);
		using ZLibStream zlib = new(input, CompressionMode.Decompress);
		using MemoryStream output = new();
		zlib.CopyTo(output);
		return output.ToArray();
	}

	private static int Paeth(int a, int b, int c)
	{
		int p = a + b - c;
		int pa = Math.Abs(p - a);
		int pb = Math.Abs(p - b);
		int pc = Math.Abs(p - c);
		return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
	}
}
