using System;
using System.Buffers.Binary;
using System.IO;
using AssetRipper.SourceGenerated.Classes.ClassID_128;
using AssetRipper.SourceGenerated.Classes.ClassID_152;
using AssetRipper.SourceGenerated.Classes.ClassID_329;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.IO.Files.ResourceFiles;
using Fmod5Sharp;
using Fmod5Sharp.FmodTypes;

namespace Unpackrat.UnityProbe;

/// <summary>
/// Adapters for the additional resource kinds (Font, VideoClip, MovieTexture). Like the texture and
/// text adapters they are the only place these AssetRipper types are touched, and every one of them
/// resolves its catalogue metadata from declared fields alone while deferring the raw byte copy to
/// an explicit export.
/// </summary>
internal static partial class UnitySource
{
	private sealed class FontAsset(IFont font) : IProbeResource
	{
		public UnityEntryKind Kind => UnityEntryKind.Font;
		public long PathId => font.PathID;
		public string Name => font.Name.ToString();
		public string? OriginalPath => font.OriginalPath;

		// Metadata only: the embedded byte array is already present after deserialisation, so its
		// length is read without any conversion or re-encoding.
		public long SourceSize => font.FontData.LongLength;

		public string OutputExtension => FontOutputExtension(font);

		public byte[]? ExportData() => font.FontData;
	}

	private sealed class VideoClipAsset(IVideoClip clip) : IProbeResource
	{
		public UnityEntryKind Kind => UnityEntryKind.VideoClip;
		public long PathId => clip.PathID;
		public string Name => clip.Name.ToString();
		public string? OriginalPath => clip.OriginalPath;

		// Metadata only: the declared external-resource size is read without opening or reading the
		// streamed media range, so building the catalogue never touches the payload.
		public long SourceSize => DeclaredSourceSize.Narrow(clip.ExternalResources.Size);

		public string OutputExtension => OutputExtensions.Video(clip.TryGetExtensionFromPath());

		public byte[]? ExportData()
		{
			// Refuse an oversized clip from its declared length before the content is resolved or
			// allocated; the actual media range is only read here, on request.
			if (clip.ExternalResources.Size > Budgets.MaxVideoBytes)
			{
				return null;
			}

			return clip.TryGetContent(out byte[]? data) ? data : null;
		}
	}

	private sealed class MovieTextureAsset(IMovieTexture movie) : IProbeResource
	{
		public UnityEntryKind Kind => UnityEntryKind.MovieTexture;
		public long PathId => movie.PathID;
		public string Name => movie.Name.ToString();
		public string? OriginalPath => movie.OriginalPath;

		// Metadata only: the inline movie byte array is already present after deserialisation.
		public long SourceSize => movie.MovieData_C152.LongLength;

		public string OutputExtension => OutputExtensions.MovieOgv;

		public byte[]? ExportData() => movie.MovieData_C152;
	}

	/// <summary>
	/// Reports from metadata alone whether a VideoClip declares an external media payload: a set
	/// resource path with a non-zero declared size. Such a payload may still be missing or
	/// unresolvable, which only surfaces as an export failure, never as an empty catalogue entry.
	/// </summary>
	private static bool HasDeclaredVideoData(IVideoClip clip)
	{
		return clip.ExternalResources.IsSet() && clip.ExternalResources.Size != 0;
	}

	/// <summary>
	/// An AudioClip whose stored sound payload is either copied verbatim or, for a supported
	/// single-sample modern FSB bank, decoded to WAV/OGG on demand. Both the output extension and
	/// the declared source length are resolved at construction from bounded metadata, so listing an
	/// entry never copies or decodes the whole payload.
	/// </summary>
	private sealed class AudioClipAsset : IProbeResource
	{
		private readonly IAudioClip clip;
		private readonly AudioPayload payload;

		private AudioClipAsset(IAudioClip clip, AudioPayload payload)
		{
			this.clip = clip;
			this.payload = payload;
		}

		public UnityEntryKind Kind => UnityEntryKind.AudioClip;
		public long PathId => clip.PathID;
		public string Name => clip.Name.ToString();
		public string? OriginalPath => clip.OriginalPath;
		public long SourceSize => payload.SourceSize;
		public string OutputExtension => payload.Extension;

		public byte[]? ExportData() => payload.Materialise(clip);

		/// <summary>
		/// Builds the catalogue view only when the clip actually declares a sound payload: a non-empty
		/// inline buffer or a named streamed resource with a non-zero declared size. A clip with
		/// neither is not an exportable audio entry.
		/// </summary>
		public static AudioClipAsset? TryCreate(IAudioClip clip)
		{
			if (!AudioPayload.TryDescribe(clip, out AudioPayload? payload))
			{
				return null;
			}

			return new AudioClipAsset(clip, payload!);
		}
	}

	/// <summary>How an AudioClip's declared payload becomes bytes on export.</summary>
	private enum AudioMode
	{
		/// <summary>The stored payload is copied verbatim (legacy raw, raw FSB, or unknown bin).</summary>
		Copy,

		/// <summary>The stored payload is a supported single-sample FSB bank decoded to WAV/OGG.</summary>
		Decode,
	}

	/// <summary>
	/// The immutable export policy of one AudioClip: its output extension, its declared source
	/// length, and how its payload is materialised. It is derived once from metadata alone (plus at
	/// most a bounded FSB prefix read), never from the whole payload.
	/// </summary>
	private sealed record AudioPayload(string Extension, long SourceSize, ulong DeclaredLength, AudioMode Mode)
	{
		/// <summary>The bounded prefix of an FSB5 header: magic, version, sample count, sizes and codec.</summary>
		private const int PrefixBytes = 1084;

		public static bool TryDescribe(IAudioClip clip, out AudioPayload? payload)
		{
			payload = null;

			byte[]? inline = clip.Has_AudioData() ? clip.AudioData : null;
			ulong inlineLength = inline is { Length: > 0 } ? (ulong)inline.Length : 0;
			// A named streamed resource is a declared payload even when its recorded size is zero:
			// a zero size is an unknown length, not an absent payload, so it stays a listed entry
			// whose export then fails rather than silently disappearing from the catalogue.
			bool streamed = clip.Has_Resource() && clip.Resource.IsSet();
			if (inlineLength == 0 && !streamed)
			{
				return false;
			}

			// An inline buffer is the legacy shape and always carries its own sound type, so by the
			// time this is false the payload is a named streamed resource and the descriptor exists.
			ulong declared = inlineLength != 0 ? inlineLength : clip.Resource!.Size;
			long sourceSize = DeclaredSourceSize.Narrow(declared);

			// A legacy clip carries its original sound type directly; that raw extension wins and no
			// payload is read while building the catalogue.
			if (clip.Has_Type())
			{
				payload = new AudioPayload(OutputExtensions.Normalise(clip.GetSoundType().ToRawExtension()), sourceSize, declared, AudioMode.Copy);
				return true;
			}

			// Inline bytes (a legacy clip's shape) carry their own type above, so a modern clip is
			// always a named streamed resource whose bounded header prefix decides its extension.
			byte[]? prefix = ReadResourcePrefix(clip);
			payload = DescribeModern(prefix, sourceSize, declared);
			return true;
		}

		/// <summary>
		/// Chooses the immutable extension and export mode of a modern clip from a bounded header
		/// prefix: a single-sample FSB bank with a supported codec decodes to WAV/OGG, any other FSB
		/// bank stays raw under the true FSB container extension, and any non-FSB payload is a safe
		/// bin. A payload's compression-format enum is a codec hint, not container proof, so it never
		/// selects an extension on its own.
		/// </summary>
		private static AudioPayload DescribeModern(byte[]? prefix, long sourceSize, ulong declared)
		{
			if (prefix is { } header && AudioDecodePolicy.IsFsb5(header))
			{
				if (AudioDecodePolicy.TryPlanFsb5Decode(header, declared, out string decodedExtension))
				{
					return new AudioPayload(decodedExtension, sourceSize, declared, AudioMode.Decode);
				}

				return new AudioPayload(OutputExtensions.Fsb, sourceSize, declared, AudioMode.Copy);
			}

			return new AudioPayload(OutputExtensions.FallbackBin, sourceSize, declared, AudioMode.Copy);
		}

		/// <summary>
		/// Reads at most <see cref="PrefixBytes"/> bytes of the clip's declared streamed resource,
		/// clamped to the declared size, the physically remaining bytes and the prefix cap. The
		/// declared interval is validated with subtraction, so a short declared resource can never
		/// read into whatever follows it in the physical file; a zero or absent declared size reads
		/// nothing. The stream position is always restored.
		/// </summary>
		private static byte[]? ReadResourcePrefix(IAudioClip clip)
		{
			var resource = clip.Resource!;
			ResourceFile? file = clip.Collection.Bundle.ResolveResource(resource.Source.String);
			if (file is null)
			{
				return null;
			}

			return ReadPrefix(file.Stream, resource.Offset, resource.Size, PrefixBytes);
		}

		public byte[]? Materialise(IAudioClip clip)
		{
			// Refuse an over-large payload from its declared length before the body is copied, and
			// re-check the materialised array so an under-declared stream cannot slip past the cap.
			if (DeclaredLength > (ulong)Budgets.MaxAudioSourceBytes)
			{
				return null;
			}

			byte[] data = clip.GetAudioData();
			if (data.Length == 0)
			{
				return null;
			}

			return Mode == AudioMode.Decode ? Decode(data) : data;
		}

		/// <summary>
		/// Decodes a supported single-sample FSB bank through the mature Fmod5Sharp library. A bank
		/// that fails the rebuild or exceeds the decoded ceiling yields nothing: a clip whose
		/// catalogue extension promises WAV/OGG must never fall back to raw FSB bytes under that
		/// extension. The header envelope and the codec allocation model were already bounded from
		/// metadata before this is reached.
		/// </summary>
		private static byte[]? Decode(byte[] bankBytes)
		{
			// The pinned loader follows a present FSB name table before any rebuild guard can run, by
			// reading a NUL-terminated string whose length nothing bounds; validate the table here,
			// at the pre-loader boundary, so a bank whose name read cannot be bounded is refused
			// rather than materialised. The catalogue extension stays immutable, so this only turns a
			// promised wav/ogg into an export failure, never into raw FSB bytes under that extension.
			if (!AudioDecodePolicy.FsbNameTableWithinBound(bankBytes))
			{
				return null;
			}

			try
			{
				FmodSoundBank bank = FsbLoader.LoadFsbFromByteArray(bankBytes);
				FmodSample sample = bank.Samples[0];

				// Re-run the decode guard over the actual single-sample payload before the rebuilder
				// allocates: the metadata envelope and codec allocation model were bounded at
				// catalogue time, but only the real payload can bound the Vorbis packet table.
				if (!AudioDecodePolicy.CanRebuild(bank.Header.AudioType, sample.Metadata.SampleCount, sample.Metadata.Channels, sample.SampleBytes))
				{
					return null;
				}

				// A supported single-sample bank rebuilds to a standard container; the library signals a
				// malformed bank by throwing, which the handler below turns into "no audio". The
				// rebuilt length is checked once more against the decoded ceiling so an under-declared
				// bank cannot slip a larger buffer past the metadata bound.
				sample.RebuildAsStandardFileFormat(out byte[]? decoded, out _);
				return AudioDecodePolicy.AcceptDecoded(decoded);
			}
			// Fmod5Sharp signals a malformed or unsupported bank by throwing, so a rebuild failure is
			// translated to "no decoded audio". A real memory shortage is deliberately rethrown and
			// stays fatal rather than being reported as a decode failure.
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				return null;
			}
		}

	}

	/// <summary>
	/// The Font output extension: AssetRipper's own helper decides ttf versus otf, the TrueType
	/// collection tag is additionally recognised as ttc, and the result is normalised to the ABI
	/// alphabet so nothing hostile can escape.
	/// </summary>
	/// <remarks>
	/// The audio prefix reader is exposed at the adapter level so the bounded-resource-interval
	/// contract can be exercised directly through a real <see cref="Stream"/> without a decoded asset
	/// graph. The whole declared interval must fit inside the stream: a nonzero size that runs past
	/// the physical end is refused rather than clamped, so a short file can never be classified from
	/// bytes outside the resource. The size is clamped to the prefix cap before narrowing so a hostile
	/// size cannot overflow the subtraction or the read; a zero or absent size reads nothing.
	/// </remarks>
	internal static byte[]? ReadPrefix(Stream stream, ulong offset, ulong size, int cap)
	{
		if (offset > long.MaxValue)
		{
			return null;
		}

		long start = (long)offset;
		long streamLength = stream.Length;
		if (start > streamLength || size > (ulong)(streamLength - start))
		{
			return null;
		}

		long declared = size > (ulong)cap ? cap : (long)size;
		int count = (int)declared;
		if (count <= 0)
		{
			return null;
		}

		byte[] prefix = new byte[count];
		long saved = stream.Position;
		try
		{
			stream.Position = start;
			stream.ReadExactly(prefix);
		}
		finally
		{
			stream.Position = saved;
		}

		return prefix;
	}

	private static string FontOutputExtension(IFont font)
	{
		byte[] data = font.FontData;
		string extension = data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == TtcTag
			? "ttc"
			: font.GetFontExtension();
		return OutputExtensions.Normalise(extension);
	}

	/// <summary>The big-endian ASCII tag "ttcf" read as a little-endian 32-bit value, as the font helper does.</summary>
	private const uint TtcTag = 0x66637474;
}
