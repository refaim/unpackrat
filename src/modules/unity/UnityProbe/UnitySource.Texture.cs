using System;
using System.IO;
using System.Runtime.CompilerServices;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.TextureDecoder.Rgb;
using AssetRipper.TextureDecoder.Rgb.Formats;
using BuildTarget = AssetRipper.IO.Files.BuildTarget;

namespace Unpackrat.UnityProbe;

internal static partial class UnitySource
{
	private sealed record TextureFormatAdapter(TexturePixelLayout Layout, Func<DirectBitmap, byte[]?> Encode, bool LegacyScratch = false);
	private static readonly TextureFormatAdapter CompressedTextureAdapter =
		TexturePixels<ColorRGBA<byte>, byte>.Adapter with { LegacyScratch = true };

	// The pinned converter exposes no pre-allocation layout query. Keep its type selection here
	// inside the adapter; parity tests check the real decoder's PixelSize for every mapped format.
	private static TextureFormatAdapter TextureAdapter(TextureFormat format) => format switch
	{
		TextureFormat.Alpha8 => TexturePixels<ColorA<byte>, byte>.Adapter,
		TextureFormat.ARGB4444 => TexturePixels<ColorARGB16, byte>.Adapter,
		TextureFormat.RGB24 => TexturePixels<ColorRGB<byte>, byte>.Adapter,
		TextureFormat.RGBA32 => TexturePixels<ColorRGBA<byte>, byte>.Adapter,
		TextureFormat.ARGB32 => TexturePixels<ColorARGB<byte>, byte>.Adapter,
		TextureFormat.ARGBFloat => TexturePixels<ColorARGB<float>, float>.Adapter,
		TextureFormat.RGB565 => TexturePixels<ColorRGB16, byte>.Adapter,
		TextureFormat.BGR24 => TexturePixels<ColorBGR<byte>, byte>.Adapter,
		TextureFormat.R16 => TexturePixels<ColorR<ushort>, ushort>.Adapter,
		TextureFormat.RGBA4444 => TexturePixels<ColorRGBA16, byte>.Adapter,
		TextureFormat.BGRA32_14 or TextureFormat.BGRA32_37 => TexturePixels<ColorBGRA<byte>, byte>.Adapter,
		TextureFormat.RHalf => TexturePixels<ColorR<Half>, Half>.Adapter,
		TextureFormat.RGHalf => TexturePixels<ColorRG<Half>, Half>.Adapter,
		TextureFormat.RGBAHalf => TexturePixels<ColorRGBA<Half>, Half>.Adapter,
		TextureFormat.RFloat => TexturePixels<ColorR<float>, float>.Adapter,
		TextureFormat.RGFloat => TexturePixels<ColorRG<float>, float>.Adapter,
		TextureFormat.RGBAFloat => TexturePixels<ColorRGBA<float>, float>.Adapter,
		TextureFormat.RGB9e5Float => TexturePixels<ColorRGB9e5, double>.Adapter,
		TextureFormat.RG16 => TexturePixels<ColorRG<byte>, byte>.Adapter,
		TextureFormat.R8 => TexturePixels<ColorR<byte>, byte>.Adapter,
		TextureFormat.RG32 => TexturePixels<ColorRG<ushort>, ushort>.Adapter,
		TextureFormat.RGB48 => TexturePixels<ColorRGB<ushort>, ushort>.Adapter,
		TextureFormat.RGBA64 => TexturePixels<ColorRGBA<ushort>, ushort>.Adapter,
		TextureFormat.R8_SIGNED => TexturePixels<ColorR<sbyte>, sbyte>.Adapter,
		TextureFormat.RG16_SIGNED => TexturePixels<ColorRG<sbyte>, sbyte>.Adapter,
		TextureFormat.RGB24_SIGNED => TexturePixels<ColorRGB<sbyte>, sbyte>.Adapter,
		TextureFormat.RGBA32_SIGNED => TexturePixels<ColorRGBA<sbyte>, sbyte>.Adapter,
		TextureFormat.R16_SIGNED => TexturePixels<ColorR<short>, short>.Adapter,
		TextureFormat.RG32_SIGNED => TexturePixels<ColorRG<short>, short>.Adapter,
		TextureFormat.RGB48_SIGNED => TexturePixels<ColorRGB<short>, short>.Adapter,
		TextureFormat.RGBA64_SIGNED => TexturePixels<ColorRGBA<short>, short>.Adapter,
		_ => CompressedTextureAdapter,
	};

	internal static TexturePixelLayout GetTexturePixelLayout(TextureFormat format) => TextureAdapter(format).Layout;

	private static class TexturePixels<TColor, TChannel>
		where TColor : unmanaged, IColor<TChannel>
		where TChannel : unmanaged
	{
		public static readonly TextureFormatAdapter Adapter = new(
			new(Unsafe.SizeOf<TColor>(), TColor.HasAlphaChannel ? 4 : 3,
				typeof(TColor) != typeof(ColorRGBA<byte>) && typeof(TColor) != typeof(ColorRGB<byte>)), Encode);

		private static byte[]? Encode(DirectBitmap bitmap)
		{
			int rows = bitmap.Height * bitmap.Depth;
			if (bitmap.Width > ushort.MaxValue || rows > ushort.MaxValue)
			{
				using MemoryStream stream = new();
				bitmap.SaveAsPng(stream);
				return stream.ToArray();
			}

			if (!Adapter.Layout.RequiresConversion)
				return FastPngAdapter.Encode(bitmap.Bits, bitmap.Width, rows);
			byte[] converted;
			if (TColor.HasAlphaChannel)
				RgbConverter.Convert<TColor, TChannel, ColorRGBA<byte>, byte>(bitmap.Bits, bitmap.Width, rows, out converted);
			else
				RgbConverter.Convert<TColor, TChannel, ColorRGB<byte>, byte>(bitmap.Bits, bitmap.Width, rows, out converted);
			return FastPngAdapter.Encode(converted, bitmap.Width, rows);
		}
	}

	private static bool TryDecodeTexture(ITexture2D texture, TextureFormatAdapter adapter, out DirectBitmap bitmap)
	{
		bitmap = DirectBitmap.Empty;
		long inlineLength = texture.ImageData_C28.LongLength;
		ulong source = inlineLength != 0 ? (ulong)inlineLength : texture.StreamData_C28?.Size ?? 0;
		long rows = (long)texture.Height_C28 * texture.ImageCount_C28;
		// Preserve the old admission corridor for paths with a different, unaudited workspace.
		bool legacy = adapter.LegacyScratch || texture.Width_C28 > ushort.MaxValue || rows > ushort.MaxValue
			|| texture.Collection.Platform == BuildTarget.Xbox360
			|| (texture.Collection.Platform == BuildTarget.Switch && texture.PlatformBlob_C28 is { Length: >= 12 });
		if (legacy)
		{
			if (source > TextureMemoryBudget.LegacySourceBytes
				|| !Budgets.TryGetTextureBytes(texture.Width_C28, texture.Height_C28, texture.ImageCount_C28, out _))
				return false;
		}
		else if (!TextureMemoryBudget.TryGetWorkingBytes(texture.Width_C28, texture.Height_C28,
			texture.ImageCount_C28, adapter.Layout, source, out _))
			return false;
		return TextureConverter.TryConvertToBitmap(texture, out bitmap);
	}

	internal static bool TryDecodeTexture(ITexture2D texture, out DirectBitmap bitmap)
		=> TryDecodeTexture(texture, TextureAdapter(texture.Format_C28E), out bitmap);

	private static byte[]? RenderTexturePng(ITexture2D texture)
	{
		TextureFormatAdapter adapter = TextureAdapter(texture.Format_C28E);
		return TryDecodeTexture(texture, adapter, out DirectBitmap bitmap) ? adapter.Encode(bitmap) : null;
	}
}
