using System;
using System.Buffers.Binary;
using System.IO;
using Fmod5Sharp.FmodTypes;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// The adapter's decode policy in isolation: the widened metadata bound, the per-codec allocation
/// model, the channel and capability gates, and the FSB5 envelope validation. Every case is decided
/// from numbers and a small bounded prefix, so no giant buffer or intentional allocation is needed.
/// </summary>
public sealed class AudioDecodePolicyTests
{
	private const int FixedHeaderV0 = 0x40;
	private const int FixedHeaderV1 = 0x3C;

	[Fact]
	public void IsFsb5RequiresTheFourMagicBytes()
	{
		Assert.True(AudioDecodePolicy.IsFsb5("FSB5"u8));
		Assert.False(AudioDecodePolicy.IsFsb5("FSB"u8));
		Assert.False(AudioDecodePolicy.IsFsb5("FSB6"u8));
	}

	[Fact]
	public void DecodedPcmUpperBoundWidensBeforeMultiplying()
	{
		// 536870913 * 8 * 4 = 17179869216; a 32-bit multiply would wrap to 32.
		Assert.Equal(17179869216UL, AudioDecodePolicy.DecodedPcmUpperBound(536870913u, 8u));
	}

	[Theory]
	[InlineData(FmodAudioType.VORBIS, 2000L)]
	[InlineData(FmodAudioType.PCM8, 2000L)]
	[InlineData(FmodAudioType.PCM16, 2000L)]
	[InlineData(FmodAudioType.PCM32, 2000L)]
	[InlineData(FmodAudioType.GCADPCM, 4000L)]
	[InlineData(FmodAudioType.IMAADPCM, 4000L)]
	[InlineData(FmodAudioType.FADPCM, 4000L)]
	public void PredictedRebuildBytesOverEstimatesEachSupportedCodec(FmodAudioType codec, long scaled)
		=> Assert.Equal(scaled + 4096L, AudioDecodePolicy.PredictedRebuildBytes(codec, 1000));

	[Fact]
	public void PredictedRebuildBytesRejectsAnUnsupportedCodec()
		=> Assert.Equal(long.MaxValue, AudioDecodePolicy.PredictedRebuildBytes((FmodAudioType)0x7FFFFFFF, 1000));

	[Fact]
	public void PredictedRebuildBytesRejectsANegativeLength()
		=> Assert.Equal(long.MaxValue, AudioDecodePolicy.PredictedRebuildBytes(FmodAudioType.PCM16, -1));

	[Fact]
	public void PredictedRebuildBytesRejectsAnOverflowingLength()
		=> Assert.Equal(long.MaxValue, AudioDecodePolicy.PredictedRebuildBytes(FmodAudioType.FADPCM, long.MaxValue));

	[Theory]
	[InlineData(1u, true)]
	[InlineData(2u, true)]
	[InlineData(6u, false)]
	[InlineData(8u, false)]
	public void CanRebuildGatesOnTheChannelLayout(uint channels, bool expected)
		=> Assert.Equal(expected, AudioDecodePolicy.CanRebuild(FmodAudioType.PCM16, 100, channels, 100));

	[Fact]
	public void CanRebuildKeepsMultichannelVorbis()
		=> Assert.True(AudioDecodePolicy.CanRebuild(FmodAudioType.VORBIS, 100, 6, 100));

	[Fact]
	public void CanRebuildRejectsAnUnsupportedCodec()
		=> Assert.False(AudioDecodePolicy.CanRebuild((FmodAudioType)0x7FFFFFFF, 1, 1, 100));

	[Fact]
	public void CanRebuildRejectsAZeroDecodedBound()
		=> Assert.False(AudioDecodePolicy.CanRebuild(FmodAudioType.PCM16, 0, 1, 100));

	[Fact]
	public void CanRebuildRejectsADecodedBoundBeyondTheCeiling()
		=> Assert.False(AudioDecodePolicy.CanRebuild(FmodAudioType.PCM16, uint.MaxValue, 2, 100));

	[Fact]
	public void CanRebuildRejectsAnAllocationModelBeyondTheCeiling()
		=> Assert.False(AudioDecodePolicy.CanRebuild(FmodAudioType.FADPCM, 1, 1, 300L * 1024 * 1024));

	[Fact]
	public void CanRebuildRejectsAGcadpcmAllocationBeyondTheCeiling()
	{
		// The pinned GCADPCM rebuilder expands eight compressed bytes into fourteen samples, so its
		// "new short[count]" buffer is 3.5x the compressed length. A 192 MiB payload therefore needs a
		// 672 MiB intermediate array, past the 512 MiB decoded ceiling, even though the declared
		// sample count alone stays tiny. The length-based model must refuse it from metadata alone.
		Assert.False(AudioDecodePolicy.CanRebuild(FmodAudioType.GCADPCM, 1000u, 1u, 192L * 1024 * 1024));
	}

	[Fact]
	public void CanRebuildAcceptsAWellFormedSmallBank()
		=> Assert.True(AudioDecodePolicy.CanRebuild(FmodAudioType.PCM16, 100, 1, 100));

	[Fact]
	public void VersionZeroHeaderDecodesToItsCodecExtension()
	{
		byte[] header = Fsb5Header(0, 1, 8, 0, 100, FmodAudioType.PCM16, SampleWord(10, 0, 0), FixedHeaderV0 + 8);

		Assert.True(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV0 + 8u + 100u, out string extension));
		Assert.Equal("wav", extension);
	}

	[Fact]
	public void VersionOneStereoVorbisHeaderDecodesToOgg()
	{
		byte[] header = Fsb5Header(1, 1, 8, 0, 100, FmodAudioType.VORBIS, SampleWord(10, 1, 0), FixedHeaderV1 + 8);

		Assert.True(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 8u + 100u, out string extension));
		Assert.Equal("ogg", extension);
	}

	[Fact]
	public void ZeroDeclaredLengthCannotDecode()
	{
		byte[] header = Fsb5Header(1, 1, 0, 0, 100, FmodAudioType.PCM16, SampleWord(10, 0, 0), FixedHeaderV1 + 8);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, 0, out _));
	}

	[Fact]
	public void UnrepresentableDeclaredLengthCannotDecode()
	{
		byte[] header = Fsb5Header(1, 1, 0, 0, 100, FmodAudioType.PCM16, SampleWord(10, 0, 0), FixedHeaderV1 + 8);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, ulong.MaxValue, out _));
	}

	[Fact]
	public void PrefixShorterThanTheFixedHeaderCannotDecode()
	{
		byte[] header = new byte[20];
		"FSB5"u8.CopyTo(header);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, 100, out _));
	}

	[Fact]
	public void PrefixShorterThanTheWholeFixedHeaderCannotDecode()
	{
		// The prefix passes the 28-byte field check but is shorter than the full fixed header plus the
		// sample metadata word, so the sample metadata cannot be addressed.
		byte[] header = new byte[40];
		"FSB5"u8.CopyTo(header);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x08), 1);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, 100, out _));
	}

	[Fact]
	public void MultiSampleBankCannotDecode()
	{
		byte[] header = Fsb5Header(1, 2, 0, 0, 100, FmodAudioType.PCM16, SampleWord(10, 0, 0), FixedHeaderV1 + 8);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 100u, out _));
	}

	[Fact]
	public void SampleHeaderSectionSmallerThanTheMetadataWordCannotDecode()
	{
		// The section size is zero, so it cannot hold the single 8-byte sample metadata word, even
		// though the bytes exist and every other field would let the decode proceed.
		byte[] header = Fsb5Header(1, 1, 0, 0, 100, FmodAudioType.PCM16, SampleWord(10, 0, 0), FixedHeaderV1 + 8);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 8u + 100u, out _));
	}

	[Fact]
	public void SectionArithmeticBeyondTheBankCannotDecode()
	{
		byte[] header = Fsb5Header(1, 1, 100, 0, 100, FmodAudioType.PCM16, SampleWord(10, 0, 0), FixedHeaderV1 + 8);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, 100, out _));
	}

	[Fact]
	public void DataSectionBeyondTheBankCannotDecode()
	{
		byte[] header = Fsb5Header(1, 1, 8, 0, 100, FmodAudioType.PCM16, SampleWord(10, 0, 0), FixedHeaderV1 + 8);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 50u, out _));
	}

	[Theory]
	[InlineData(2u)]
	[InlineData(3u)]
	public void MultichannelHeaderCannotDecode(uint channelBits)
	{
		byte[] header = Fsb5Header(1, 1, 8, 0, 100, FmodAudioType.PCM16, SampleWord(10, channelBits, 0), FixedHeaderV1 + 8);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 8u + 100u, out _));
	}

	[Fact]
	public void DataOffsetBeyondTheDataSectionCannotDecode()
	{
		byte[] header = Fsb5Header(1, 1, 8, 0, 100, FmodAudioType.PCM16, SampleWord(10, 0, 10), FixedHeaderV1 + 8);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 8u + 200u, out _));
	}

	[Fact]
	public void VorbisPacketsBeyondThePacketBoundCannotDecode()
	{
		// 262145 one-byte packets make a ~786 KiB payload: the compressed length alone is far below
		// the rebuild ceiling, so the length-based model accepts it, but the pinned remux would build
		// a packet table past the approved bound, so the pre-rebuild scan must refuse it.
		byte[] sample = VorbisPackets(262145, payloadLength: 1);

		Assert.True(AudioDecodePolicy.CanRebuild(FmodAudioType.VORBIS, 1u, 1u, sample.Length));
		Assert.False(AudioDecodePolicy.CanRebuild(FmodAudioType.VORBIS, 1u, 1u, (ReadOnlySpan<byte>)sample));
	}

	[Theory]
	[InlineData(262143, true)]
	[InlineData(262144, true)]
	[InlineData(262145, false)]
	public void VorbisPacketCountIsBoundedAtTheCap(int packets, bool expected)
	{
		byte[] sample = VorbisPackets(packets, payloadLength: 1);

		Assert.Equal(expected, AudioDecodePolicy.CanRebuild(FmodAudioType.VORBIS, 1u, 1u, (ReadOnlySpan<byte>)sample));
	}

	[Fact]
	public void VorbisPacketLengthBeyondThePayloadCannotDecode()
	{
		// A record declaring sixteen payload bytes with only three remaining runs past the payload.
		byte[] sample = [0x10, 0x00, 0x01, 0x02, 0x03];

		Assert.False(AudioDecodePolicy.CanRebuild(FmodAudioType.VORBIS, 1u, 1u, (ReadOnlySpan<byte>)sample));
	}

	[Fact]
	public void VorbisPacketTooShortForALengthFieldCannotDecode()
	{
		// Two bytes cannot hold a whole two-byte length plus any payload, so no packet table is built.
		Assert.True(AudioDecodePolicy.CanRebuild(FmodAudioType.VORBIS, 1u, 1u, (ReadOnlySpan<byte>)new byte[2]));
	}

	[Theory]
	[InlineData((ushort)0)]
	[InlineData((ushort)0xFFFF)]
	public void VorbisPacketTerminatorEndsTheScan(ushort terminator)
	{
		byte[] sample = new byte[7];
		BinaryPrimitives.WriteUInt16LittleEndian(sample.AsSpan(0), 2);
		sample[2] = 1;
		sample[3] = 2;
		BinaryPrimitives.WriteUInt16LittleEndian(sample.AsSpan(4), terminator);

		Assert.True(AudioDecodePolicy.CanRebuild(FmodAudioType.VORBIS, 1u, 1u, (ReadOnlySpan<byte>)sample));
	}

	[Fact]
	public void NonVorbisSpanSkipsThePacketScan()
		=> Assert.True(AudioDecodePolicy.CanRebuild(FmodAudioType.PCM16, 100u, 1u, (ReadOnlySpan<byte>)new byte[3]));

	[Fact]
	public void SpanRebuildAppliesTheLengthModelFirst()
		=> Assert.False(AudioDecodePolicy.CanRebuild(FmodAudioType.PCM16, uint.MaxValue, 2u, (ReadOnlySpan<byte>)new byte[3]));

	[Fact]
	public void ChannelsChunkMatchingTheFixedLayoutCanDecode()
	{
		byte[] chunks = Chunks((1u, [6]));
		byte[] header = ChunkedHeader(100, FmodAudioType.VORBIS, SampleWord(10, 2, 0), 8 + chunks.Length, chunks, FixedHeaderV1 + 8 + chunks.Length);

		Assert.True(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 8u + (uint)chunks.Length + 100u, out string extension));
		Assert.Equal("ogg", extension);
	}

	[Fact]
	public void ChannelsChunkContradictingTheFixedLayoutCannotDecode()
	{
		// A fixed-mono word with a CHANNELS 6 chunk would be labelled mono by the pinned rebuilder,
		// which ignores the CHANNELS chunk; the mismatch must keep the truthful raw FSB.
		byte[] chunks = Chunks((1u, [6]));
		byte[] header = ChunkedHeader(100, FmodAudioType.VORBIS, SampleWord(10, 0, 0), 8 + chunks.Length, chunks, FixedHeaderV1 + 8 + chunks.Length);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 8u + (uint)chunks.Length + 100u, out _));
	}

	[Fact]
	public void EmptyChannelsChunkIsIgnored()
	{
		byte[] chunks = Chunks((1u, []));
		byte[] header = ChunkedHeader(100, FmodAudioType.PCM16, SampleWord(10, 0, 0), 8 + chunks.Length, chunks, FixedHeaderV1 + 8 + chunks.Length);

		Assert.True(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 8u + (uint)chunks.Length + 100u, out string extension));
		Assert.Equal("wav", extension);
	}

	[Fact]
	public void UnknownChunkBeforeTheChannelsChunkIsSkipped()
	{
		byte[] chunks = Chunks((2u, [0, 0, 0, 0]), (1u, [1]));
		byte[] header = ChunkedHeader(100, FmodAudioType.PCM16, SampleWord(10, 0, 0), 8 + chunks.Length, chunks, FixedHeaderV1 + 8 + chunks.Length);

		Assert.True(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 8u + (uint)chunks.Length + 100u, out string extension));
		Assert.Equal("wav", extension);
	}

	[Fact]
	public void ChunkSectionBeyondThePrefixCannotDecode()
	{
		// The declared section is far longer than the bounded prefix, so the chunk list cannot be
		// inspected; the layout is refused rather than assumed compatible.
		byte[] header = ChunkedHeader(100, FmodAudioType.PCM16, SampleWord(10, 0, 0), 100, [], FixedHeaderV1 + 8);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 100u + 100u, out _));
	}

	[Fact]
	public void PartialChunkControlCannotDecode()
	{
		// Two section bytes after the metadata word cannot hold a four-byte chunk control.
		byte[] header = ChunkedHeader(100, FmodAudioType.PCM16, SampleWord(10, 0, 0), 10, [0, 0], FixedHeaderV1 + 10);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 10u + 100u, out _));
	}

	[Fact]
	public void ChunkSizeBeyondTheDeclaredSectionCannotDecode()
	{
		byte[] chunks = Control(more: 0, size: 40, type: 0);
		byte[] header = ChunkedHeader(100, FmodAudioType.PCM16, SampleWord(10, 0, 0), 13, chunks, FixedHeaderV1 + 13);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 13u + 100u, out _));
	}

	[Fact]
	public void ChunkListClaimingMoreWithNoRoomCannotDecode()
	{
		byte[] chunks = Control(more: 1, size: 0, type: 0);
		byte[] header = ChunkedHeader(100, FmodAudioType.PCM16, SampleWord(10, 0, 0), 12, chunks, FixedHeaderV1 + 12);

		Assert.False(AudioDecodePolicy.TryPlanFsb5Decode(header, FixedHeaderV1 + 12u + 100u, out _));
	}

	[Fact]
	public void AcceptDecodedRejectsNothingAndAcceptsAPresentBuffer()
	{
		byte[] buffer = [1, 2, 3];

		Assert.Null(AudioDecodePolicy.AcceptDecoded(null));
		Assert.Same(buffer, AudioDecodePolicy.AcceptDecoded(buffer));
	}

	[Theory]
	[InlineData(0L, false)]
	[InlineData(1L, true)]
	[InlineData(Budgets.MaxAudioDecodedBytes, true)]
	[InlineData(Budgets.MaxAudioDecodedBytes + 1, false)]
	public void AcceptDecodedLengthGatesOnTheDecodedCeiling(long length, bool accepted)
	{
		byte[] buffer = [1];

		byte[]? result = AudioDecodePolicy.AcceptDecodedLength(length, buffer);
		if (accepted)
		{
			Assert.Same(buffer, result);
		}
		else
		{
			Assert.Null(result);
		}
	}

	[Fact]
	public void NameGuardAcceptsABankTooShortForTheFixedHeader()
	{
		// Fewer than the 28 fixed-header bytes: no name table can be addressed through the header, so
		// the guard leaves the bank to the loader's own end-of-stream handling.
		Assert.True(AudioDecodePolicy.FsbNameTableWithinBound(new byte[27]));
	}

	[Fact]
	public void NameGuardAcceptsABankWithoutANameTable()
	{
		Assert.True(AudioDecodePolicy.FsbNameTableWithinBound(NameTableBank()));
	}

	[Fact]
	public void NameGuardRefusesANameTableOnANonSingleSampleBank()
	{
		// The adapter only decodes a single-sample bank, so the guard cannot bound a table shaped for
		// any other sample count.
		byte[] bank = NameTableBank(numSamples: 2, declaredNameTable: 8, nameTable: [4, 0, 0, 0, 0, 0, 0, 0]);

		Assert.False(AudioDecodePolicy.FsbNameTableWithinBound(bank));
	}

	[Fact]
	public void NameGuardRefusesANameTableSmallerThanItsOffsetWord()
		=> Assert.False(AudioDecodePolicy.FsbNameTableWithinBound(NameTableBank(declaredNameTable: 2, nameTable: [0, 0])));

	[Fact]
	public void NameGuardRefusesANameTableStartingBeyondTheBank()
	{
		// The declared sample-header section alone pushes the table base past the physical bank.
		byte[] bank = NameTableBank(declaredSampleHeaders: 0xFFFF, declaredNameTable: 4, nameTable: [4, 0, 0, 0]);

		Assert.False(AudioDecodePolicy.FsbNameTableWithinBound(bank));
	}

	[Fact]
	public void NameGuardRefusesANameTableExtendingBeyondTheBank()
	{
		// The table base is addressable but the declared table length runs past the physical bank.
		byte[] bank = NameTableBank(declaredNameTable: 0x1000, nameTable: [4, 0, 0, 0]);

		Assert.False(AudioDecodePolicy.FsbNameTableWithinBound(bank));
	}

	[Fact]
	public void NameGuardRefusesANameOffsetInsideTheOffsetWord()
		=> Assert.False(AudioDecodePolicy.FsbNameTableWithinBound(NameTableBank(nameTable: [0, 0, 0, 0])));

	[Fact]
	public void NameGuardRefusesANameOffsetAtTheTableEnd()
		=> Assert.False(AudioDecodePolicy.FsbNameTableWithinBound(NameTableBank(nameTable: [4, 0, 0, 0])));

	[Fact]
	public void NameGuardAcceptsAnEmptyNameAtTheOffsetWordBoundary()
	{
		// Offset four is the first byte after the offset word; a NUL there is a valid empty name. The
		// same table on a version-zero header exercises the wider fixed-header base.
		byte[] table = new byte[8];
		BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0), 4);

		Assert.True(AudioDecodePolicy.FsbNameTableWithinBound(NameTableBank(nameTable: table)));
		Assert.True(AudioDecodePolicy.FsbNameTableWithinBound(NameTableBank(version: 0, nameTable: table)));
	}

	[Fact]
	public void NameGuardAcceptsANameTerminatedInsideTheTable()
		=> Assert.True(AudioDecodePolicy.FsbNameTableWithinBound(NameTableBank(nameTable: [4, 0, 0, 0, 0x61, 0x62, 0, 0])));

	[Fact]
	public void NameGuardRefusesANameWithoutANulInsideTheTable()
		=> Assert.False(AudioDecodePolicy.FsbNameTableWithinBound(NameTableBank(nameTable: [4, 0, 0, 0, 1, 1, 1, 1])));

	[Fact]
	public void NameGuardAcceptsANameExactlyAtTheByteCap()
	{
		// A 4096-byte name terminated by a NUL is the largest name the guard follows.
		byte[] table = new byte[4 + 4096 + 1];
		BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0), 4);
		table.AsSpan(4, 4096).Fill(0x61);

		Assert.True(AudioDecodePolicy.FsbNameTableWithinBound(NameTableBank(nameTable: table)));
	}

	[Fact]
	public void NameGuardRefusesANameOneByteOverTheByteCap()
	{
		byte[] table = new byte[4 + 4097 + 1];
		BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0), 4);
		table.AsSpan(4, 4097).Fill(0x61);

		Assert.False(AudioDecodePolicy.FsbNameTableWithinBound(NameTableBank(nameTable: table)));
	}

	/// <summary>
	/// A single-sample FSB5 bank whose name table and data sit exactly where the pinned loader reads
	/// them, with the declared section sizes independently settable so a test can describe a table
	/// that does not match its physical bytes.
	/// </summary>
	private static byte[] NameTableBank(
		uint version = 1,
		uint? declaredSampleHeaders = null,
		uint? declaredNameTable = null,
		byte[]? nameTable = null,
		int dataLength = 32,
		uint numSamples = 1)
	{
		nameTable ??= [];
		const int physicalSampleHeaders = 8;
		int fixedHeader = version == 0 ? FixedHeaderV0 : FixedHeaderV1;
		byte[] bank = new byte[fixedHeader + physicalSampleHeaders + nameTable.Length + dataLength];
		"FSB5"u8.CopyTo(bank);
		BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(0x04), version);
		BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(0x08), numSamples);
		BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(0x0C), declaredSampleHeaders ?? physicalSampleHeaders);
		BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(0x10), declaredNameTable ?? (uint)nameTable.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(0x14), (uint)dataLength);
		BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(0x18), (uint)FmodAudioType.PCM16);
		BinaryPrimitives.WriteUInt64LittleEndian(bank.AsSpan(fixedHeader), 8UL << 34);
		nameTable.CopyTo(bank, fixedHeader + physicalSampleHeaders);
		return bank;
	}

	private static byte[] Fsb5Header(
		uint version,
		uint numSamples,
		uint sizeOfSampleHeaders,
		uint sizeOfNameTable,
		uint sizeOfData,
		FmodAudioType codec,
		ulong encoded,
		int length)
	{
		byte[] header = new byte[length];
		"FSB5"u8.CopyTo(header);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x04), version);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x08), numSamples);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x0C), sizeOfSampleHeaders);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10), sizeOfNameTable);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14), sizeOfData);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), (uint)codec);
		int metadataOffset = version == 0 ? FixedHeaderV0 : FixedHeaderV1;
		if (length >= metadataOffset + 8)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(metadataOffset), encoded);
		}

		return header;
	}

	/// <summary>The 64-bit encoded sample metadata word: channel bits 5-6, data offset bits 7-33 (in 32-byte units), sample count bits 34-63.</summary>
	private static ulong SampleWord(uint sampleCount, uint channelBits, uint dataOffsetUnits)
		=> ((ulong)sampleCount << 34) | ((ulong)dataOffsetUnits << 7) | ((ulong)channelBits << 5);

	/// <summary>A synthetic Vorbis payload of <paramref name="count"/> records, each a two-byte length followed by its payload.</summary>
	private static byte[] VorbisPackets(int count, int payloadLength)
	{
		byte[] sample = new byte[count * (2 + payloadLength)];
		for (int i = 0; i < count; i++)
		{
			BinaryPrimitives.WriteUInt16LittleEndian(sample.AsSpan(i * (2 + payloadLength)), (ushort)payloadLength);
		}

		return sample;
	}

	/// <summary>The chunk controls and payloads of a sample-header section: more bit0, size bits 1-24, type bits 25-31.</summary>
	private static byte[] Chunks(params (uint Type, byte[] Data)[] chunks)
	{
		using MemoryStream stream = new();
		using BinaryWriter writer = new(stream);
		for (int i = 0; i < chunks.Length; i++)
		{
			uint more = i < chunks.Length - 1 ? 1u : 0u;
			writer.Write(more | ((uint)chunks[i].Data.Length << 1) | (chunks[i].Type << 25));
			writer.Write(chunks[i].Data);
		}

		return stream.ToArray();
	}

	/// <summary>One raw four-byte chunk control word.</summary>
	private static byte[] Control(uint more, uint size, uint type)
	{
		using MemoryStream stream = new();
		using BinaryWriter writer = new(stream);
		writer.Write((more & 1u) | (size << 1) | (type << 25));
		return stream.ToArray();
	}

	/// <summary>
	/// A version-1 FSB5 header whose sample-header section carries the given metadata word (with the
	/// has-chunks bit forced on) and chunk bytes, declared as <paramref name="sizeOfSampleHeaders"/>
	/// bytes even when the physical prefix is shorter.
	/// </summary>
	private static byte[] ChunkedHeader(
		uint sizeOfData,
		FmodAudioType codec,
		ulong metadata,
		int sizeOfSampleHeaders,
		byte[] chunks,
		int prefixLength)
	{
		byte[] header = new byte[prefixLength];
		"FSB5"u8.CopyTo(header);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x04), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x08), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x0C), (uint)sizeOfSampleHeaders);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10), 0);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14), sizeOfData);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), (uint)codec);
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(FixedHeaderV1), metadata | 1UL);
		chunks.CopyTo(header, FixedHeaderV1 + 8);
		return header;
	}
}
