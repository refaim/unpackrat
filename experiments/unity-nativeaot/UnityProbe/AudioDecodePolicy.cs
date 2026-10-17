using System;
using System.Buffers.Binary;
using Fmod5Sharp.FmodTypes;
using Fmod5Sharp.Util;

namespace Unpackrat.UnityProbe;

/// <summary>
/// The adapter's compatibility and budget policy around the pinned Fmod5Sharp decoder. It decides,
/// from a bounded FSB5 header prefix and the bank's declared length alone, whether the mature
/// library can rebuild one supported sample without an allocation the adapter cannot bound. The
/// codec capability itself comes from the pinned public helpers (<see cref="FmodAudioTypeExtensions.IsSupported"/>
/// and <see cref="FmodAudioTypeExtensions.FileExtension"/>); this type only adds our envelope and
/// budget guarantees. Planning never reads the sample body; the pre-rebuild guard only scans the
/// single sample payload, and never copies or decodes it.
/// </summary>
internal static class AudioDecodePolicy
{
	private const int Fsb5MagicLength = 4;
	private const uint Fsb5Magic = 0x35425346;
	private const int FsbVersionOffset = 0x04;
	private const int FsbNumSamplesOffset = 0x08;
	private const int FsbSizeOfSampleHeadersOffset = 0x0C;
	private const int FsbSizeOfNameTableOffset = 0x10;
	private const int FsbSizeOfDataOffset = 0x14;
	private const int FsbAudioTypeOffset = 0x18;
	private const int FsbFixedHeaderV0 = 0x40;
	private const int FsbFixedHeaderV1 = 0x3C;
	private const int FsbHeaderFieldEnd = 0x1C;
	private const int FsbSampleMetadataLength = 8;
	private const int FsbChunkControlLength = 4;
	private const uint FsbChunkChannels = 1;
	private const int VorbisPacketLengthField = 2;
	private const int MaxVorbisPackets = 262144;
	private const int RebuildOverhead = 4096;
	private const int FsbNameOffsetWord = 4;
	private const int MaxFsbNameBytes = 4096;

	/// <summary>True when the bytes begin with the four ASCII bytes "FSB5".</summary>
	internal static bool IsFsb5(ReadOnlySpan<byte> bytes)
		=> bytes.Length >= Fsb5MagicLength && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Fsb5Magic;

	/// <summary>
	/// Widened upper bound on the decoded PCM bytes implied by public sample metadata: sample count
	/// times channel count times the widest supported sample width. The first operand is cast before
	/// multiplying so a hostile count cannot wrap in 32-bit arithmetic.
	/// </summary>
	internal static ulong DecodedPcmUpperBound(uint sampleCount, uint channels)
		=> (ulong)sampleCount * channels * (ulong)Budgets.AudioDecodedBytesPerSample;

	/// <summary>
	/// Upper bound on the bytes the pinned rebuilder can allocate for a compressed sample payload,
	/// derived from the compressed length with a per-codec factor and a fixed container overhead. The
	/// factors over-estimate the pinned models: PCM copies its compressed bytes into a WAV container,
	/// Vorbis is a packet remux, and the ADPCM rebuilders expand into 16-bit PCM. The pinned FADPCM
	/// decoder sizes a <c>short[(length / 140) * 256]</c> buffer (3.66x the compressed length) and the
	/// pinned GCADPCM decoder expands eight compressed bytes into fourteen samples, so its
	/// <c>short[count]</c> buffer is 3.5x the compressed length; both are bounded by a factor of four,
	/// so a bank the adapter decodes can never drive a larger allocation than the decoded ceiling.
	/// </summary>
	internal static long PredictedRebuildBytes(FmodAudioType codec, long sampleBytes)
	{
		int factor = codec switch
		{
			FmodAudioType.VORBIS => 2,
			FmodAudioType.PCM8 or FmodAudioType.PCM16 or FmodAudioType.PCM32 => 2,
			FmodAudioType.GCADPCM => 4,
			FmodAudioType.IMAADPCM => 4,
			FmodAudioType.FADPCM => 4,
			_ => 0,
		};

		if (factor == 0 || sampleBytes < 0 || sampleBytes > (long.MaxValue - RebuildOverhead) / factor)
		{
			return long.MaxValue;
		}

		return (sampleBytes * factor) + RebuildOverhead;
	}

	/// <summary>
	/// Decides whether one supported sample of <paramref name="codec"/> can be rebuilt within the
	/// decoded ceiling: the codec must be supported, the channel layout must be one the pinned
	/// rebuilder preserves, and both the metadata bound and the compressed-length allocation model
	/// must stay under the ceiling.
	/// </summary>
	internal static bool CanRebuild(FmodAudioType codec, uint sampleCount, uint channels, long sampleBytes)
	{
		if (!codec.IsSupported())
		{
			return false;
		}

		// The pinned PCM/ADPCM rebuilders label their output mono or stereo from IsStereo and would
		// silently drop the extra channels of a 6- or 8-channel bank; only Vorbis (a packet remux)
		// keeps an arbitrary channel layout.
		if (codec != FmodAudioType.VORBIS && channels != 1 && channels != 2)
		{
			return false;
		}

		ulong decoded = DecodedPcmUpperBound(sampleCount, channels);
		if (decoded == 0 || decoded > (ulong)Budgets.MaxAudioDecodedBytes)
		{
			return false;
		}

		return PredictedRebuildBytes(codec, sampleBytes) <= Budgets.MaxAudioDecodedBytes;
	}

	/// <summary>
	/// The pre-rebuild guard over the actual single-sample payload. It applies the same metadata and
	/// compressed-length allocation model as the length-based overload and then inspects the payload
	/// itself, so an allocation the declared length alone cannot bound (the Vorbis packet table the
	/// pinned remux materialises before it honours the sample-count stop) is refused before the
	/// pinned rebuilder runs. The sample span is only scanned, never copied or decoded.
	/// </summary>
	internal static bool CanRebuild(FmodAudioType codec, uint sampleCount, uint channels, ReadOnlySpan<byte> sampleBytes)
	{
		if (!CanRebuild(codec, sampleCount, channels, sampleBytes.Length))
		{
			return false;
		}

		// The pinned Vorbis remux reads every packet into a List<ushort>, a List<byte[]> and one byte
		// array per packet before it honours the sample-count stop, so a payload of tiny records can
		// drive a packet table far larger than the compressed length suggests. Only the actual payload
		// can bound that table; every other codec allocates solely from the compressed length above.
		return codec != FmodAudioType.VORBIS || VorbisPacketTableWithinBound(sampleBytes);
	}

	/// <summary>
	/// Scans a Vorbis payload exactly as the pinned reader does, without allocating any packet, and
	/// rejects a packet table the adapter cannot bound. Each record is a two-byte little-endian length
	/// followed by that many payload bytes; a zero or 0xFFFF length ends the list, as does a trailing
	/// fragment too short for a whole length field. A record whose length runs past the payload, or a
	/// packet count beyond <see cref="MaxVorbisPackets"/>, is refused so the caller keeps the truthful
	/// raw FSB instead of driving an unbounded per-packet list.
	/// </summary>
	private static bool VorbisPacketTableWithinBound(ReadOnlySpan<byte> sampleBytes)
	{
		int position = 0;
		int packets = 0;
		while (position + VorbisPacketLengthField < sampleBytes.Length)
		{
			ushort packetSize = BinaryPrimitives.ReadUInt16LittleEndian(sampleBytes[position..]);
			if (packetSize == 0 || packetSize == 0xFFFF)
			{
				break;
			}

			position += VorbisPacketLengthField;
			if (packetSize > sampleBytes.Length - position)
			{
				return false;
			}

			position += packetSize;
			packets++;
			if (packets > MaxVorbisPackets)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Plans a decode from a bounded FSB5 prefix: the fixed header must be complete, the bank must
	/// hold exactly one sample, every declared section, offset and data size must fit the bank's
	/// declared length, and the codec must be rebuildable within budget. On success the extension is
	/// the pinned library's own <see cref="FmodAudioTypeExtensions.FileExtension"/> value, normalised
	/// to the ABI alphabet.
	/// </summary>
	internal static bool TryPlanFsb5Decode(ReadOnlySpan<byte> prefix, ulong declaredLength, out string extension)
	{
		extension = OutputExtensions.Fsb;
		if (!TryReadFsb5Sample(prefix, declaredLength, out FmodAudioType codec, out uint sampleCount, out uint channels, out long sampleBytes))
		{
			return false;
		}

		if (!CanRebuild(codec, sampleCount, channels, sampleBytes))
		{
			return false;
		}

		extension = OutputExtensions.Normalise(codec.FileExtension());
		return true;
	}

	/// <summary>
	/// Accepts a rebuilt container only when it is present, non-empty and within the decoded ceiling;
	/// anything else is "no audio" rather than a payload of unproven size.
	/// </summary>
	internal static byte[]? AcceptDecoded(byte[]? decoded)
		=> decoded is null ? null : AcceptDecodedLength(decoded.LongLength, decoded);

	/// <summary>
	/// The rebuilt-length half of <see cref="AcceptDecoded"/>, split out so the empty and over-ceiling
	/// decisions can be exercised from lengths alone without allocating a buffer of that size.
	/// </summary>
	internal static byte[]? AcceptDecodedLength(long length, byte[] decoded)
		=> length > 0 && length <= Budgets.MaxAudioDecodedBytes ? decoded : null;

	/// <summary>
	/// Pre-loader guard over the whole bank bytes for the one shape the adapter decodes: a
	/// single-sample FSB5 bank. The pinned loader materialises the sample's name by taking a 32-bit
	/// offset from the declared name table, adding the table base, and reading a NUL-terminated
	/// string with no bound on its length, all before the rebuild guard can run. A four-byte table
	/// can therefore point into the sample data and drive a string far larger than the whole bank,
	/// so this runs before <c>FsbLoader</c>: a present table must contain its own four-byte offset,
	/// the offset must stay inside the table and clear of that offset word, and a NUL must terminate
	/// the name inside the table within the documented <see cref="MaxFsbNameBytes"/> cap. A bank
	/// with no name table reads no name and is accepted unchanged, and the scan allocates nothing.
	/// </summary>
	internal static bool FsbNameTableWithinBound(ReadOnlySpan<byte> bankBytes)
	{
		// A bank too short for the fixed header fields cannot expose a name table through them; the
		// loader's own end-of-stream handling then applies, so there is nothing to bound here.
		if (bankBytes.Length < FsbHeaderFieldEnd)
		{
			return true;
		}

		uint version = BinaryPrimitives.ReadUInt32LittleEndian(bankBytes[FsbVersionOffset..]);
		uint numSamples = BinaryPrimitives.ReadUInt32LittleEndian(bankBytes[FsbNumSamplesOffset..]);
		uint sizeOfSampleHeaders = BinaryPrimitives.ReadUInt32LittleEndian(bankBytes[FsbSizeOfSampleHeadersOffset..]);
		uint sizeOfNameTable = BinaryPrimitives.ReadUInt32LittleEndian(bankBytes[FsbSizeOfNameTableOffset..]);
		if (sizeOfNameTable == 0)
		{
			return true;
		}

		// Only a single-sample bank is decoded, so the table holds exactly one offset word; a name
		// table on any other sample count is not a shape this guard can bound.
		if (numSamples != 1)
		{
			return false;
		}

		int fixedHeader = version == 0 ? FsbFixedHeaderV0 : FsbFixedHeaderV1;
		ulong tableStart = (ulong)fixedHeader + sizeOfSampleHeaders;

		// A present table must contain its own offset word and lie wholly inside the bank, checked
		// with subtraction so the table cannot wrap or run past the physical bytes.
		if (sizeOfNameTable < FsbNameOffsetWord)
		{
			return false;
		}

		if (tableStart > (ulong)bankBytes.Length)
		{
			return false;
		}

		if (sizeOfNameTable > (ulong)bankBytes.Length - tableStart)
		{
			return false;
		}

		int tableBase = (int)tableStart;
		uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(bankBytes[tableBase..]);

		// The followed name must start after the offset word it belongs to and stay inside the table.
		if (nameOffset < FsbNameOffsetWord)
		{
			return false;
		}

		if (nameOffset >= sizeOfNameTable)
		{
			return false;
		}

		// Follow only the name region, bounded by the table end and the byte cap, looking for the
		// terminating NUL; nothing is copied or decoded.
		long tableEnd = (long)tableBase + sizeOfNameTable;
		long nameStart = (long)tableBase + nameOffset;
		long scanEnd = Math.Min(tableEnd, nameStart + MaxFsbNameBytes + 1);
		for (long position = nameStart; position < scanEnd; position++)
		{
			if (bankBytes[(int)position] == 0)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Reads the fixed FSB5 header and the single sample's encoded metadata from a bounded prefix and
	/// validates the whole declared envelope against the bank's actual length: the fixed header, name
	/// table, sample headers and data section must all fit inside it, and the sample's data offset
	/// must lie inside the data section. Only the fixed header, the one sample metadata word and the
	/// bounded sample-chunk list inside the declared sample-header section are inspected; no sample
	/// body is touched.
	/// </summary>
	private static bool TryReadFsb5Sample(
		ReadOnlySpan<byte> prefix,
		ulong declaredLength,
		out FmodAudioType codec,
		out uint sampleCount,
		out uint channels,
		out long sampleBytes)
	{
		codec = default;
		sampleCount = 0;
		channels = 0;
		sampleBytes = 0;

		if (declaredLength == 0 || declaredLength > long.MaxValue || prefix.Length < FsbHeaderFieldEnd)
		{
			return false;
		}

		uint version = BinaryPrimitives.ReadUInt32LittleEndian(prefix[FsbVersionOffset..]);
		uint numSamples = BinaryPrimitives.ReadUInt32LittleEndian(prefix[FsbNumSamplesOffset..]);
		uint sizeOfSampleHeaders = BinaryPrimitives.ReadUInt32LittleEndian(prefix[FsbSizeOfSampleHeadersOffset..]);
		uint sizeOfNameTable = BinaryPrimitives.ReadUInt32LittleEndian(prefix[FsbSizeOfNameTableOffset..]);
		uint sizeOfData = BinaryPrimitives.ReadUInt32LittleEndian(prefix[FsbSizeOfDataOffset..]);
		codec = (FmodAudioType)BinaryPrimitives.ReadUInt32LittleEndian(prefix[FsbAudioTypeOffset..]);

		if (numSamples != 1)
		{
			return false;
		}

		int fixedHeader = version == 0 ? FsbFixedHeaderV0 : FsbFixedHeaderV1;
		if (prefix.Length < fixedHeader + FsbSampleMetadataLength)
		{
			return false;
		}

		// The declared sample-header section must cover at least the single 8-byte sample metadata
		// word; a section too small to hold it is not a layout the pinned reader can address.
		if (sizeOfSampleHeaders < FsbSampleMetadataLength)
		{
			return false;
		}

		ulong dataStart = (ulong)fixedHeader + sizeOfNameTable + sizeOfSampleHeaders;
		if (dataStart > declaredLength || sizeOfData > declaredLength - dataStart)
		{
			return false;
		}

		ulong encoded = BinaryPrimitives.ReadUInt64LittleEndian(prefix[fixedHeader..]);
		bool hasAnyChunks = (encoded & 1UL) == 1UL;
		uint channelBits = (uint)((encoded >> 5) & 0x3);
		channels = channelBits switch
		{
			0 => 1,
			1 => 2,
			2 => 6,
			_ => 8,
		};

		// Fixed metadata alone cannot prove the sample's true channel layout: the pinned header
		// ignores a CHANNELS chunk and labels the rebuilt container from these fixed bits. Inspect the
		// bounded chunk list inside the declared section and refuse any layout the rebuilder cannot
		// honour, so a fixed-mono word with a CHANNELS 6/8 chunk is never promised as a mono WAV.
		if (!SampleChunkLayoutIsSupported(prefix, fixedHeader, sizeOfSampleHeaders, hasAnyChunks, channels))
		{
			return false;
		}

		ulong dataOffset = ((encoded >> 7) & 0x7FFFFFF) * 32;
		sampleCount = (uint)((encoded >> 34) & 0x3FFFFFFF);
		if (dataOffset > sizeOfData)
		{
			return false;
		}

		sampleBytes = (long)(sizeOfData - dataOffset);
		return true;
	}

	/// <summary>
	/// Inspects the bounded sample-chunk list inside the declared sample-header section and reports
	/// whether the pinned rebuilder can honour the layout. Only a CHANNELS chunk is interpreted: its
	/// declared channel count must equal the fixed-metadata channel count, because the pinned
	/// rebuilders label their output from the fixed word and silently ignore a CHANNELS chunk that
	/// disagrees. Unknown chunk payloads are skipped, bounded by the section; a chunk list that cannot
	/// be fully inspected inside the prefix, or whose control arithmetic overruns the declared section,
	/// is refused so the caller keeps the truthful raw FSB rather than an assumed-compatible WAV.
	/// </summary>
	private static bool SampleChunkLayoutIsSupported(
		ReadOnlySpan<byte> prefix,
		int sectionStart,
		uint sizeOfSampleHeaders,
		bool hasAnyChunks,
		uint channels)
	{
		if (!hasAnyChunks)
		{
			return true;
		}

		long sectionEnd = (long)sectionStart + sizeOfSampleHeaders;
		if (sectionEnd > prefix.Length)
		{
			return false;
		}

		long position = (long)sectionStart + FsbSampleMetadataLength;
		while (position < sectionEnd)
		{
			if (position + FsbChunkControlLength > sectionEnd)
			{
				return false;
			}

			uint control = BinaryPrimitives.ReadUInt32LittleEndian(prefix[(int)position..]);
			bool more = (control & 1u) == 1u;
			uint chunkSize = (control >> 1) & 0xFFFFFF;
			uint chunkType = (control >> 25) & 0x7F;
			position += FsbChunkControlLength;

			if (chunkSize > (uint)(sectionEnd - position))
			{
				return false;
			}

			if (chunkType == FsbChunkChannels && chunkSize >= 1 && prefix[(int)position] != channels)
			{
				return false;
			}

			position += chunkSize;

			if (!more)
			{
				return true;
			}
		}

		return false;
	}
}
