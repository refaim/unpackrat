using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Enums;
using Fmod5Sharp.FmodTypes;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// AudioClip resource slice: a real serialized legacy clip whose sound bytes are inline must
/// surface as exactly one exportable catalogue entry whose stored raw bytes round-trip byte for
/// byte.
/// </summary>
public sealed class UnitySessionAudioTests
{
	private const int AudioClipClassId = 83;

	/// <summary>A tiny but structurally valid 8-bit mono PCM WAV file: a 44-byte header followed by four samples.</summary>
	private static readonly byte[] Wav =
	[
		0x52, 0x49, 0x46, 0x46, 0x28, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45,
		0x66, 0x6D, 0x74, 0x20, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
		0x40, 0x1F, 0x00, 0x00, 0x40, 0x1F, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00,
		0x64, 0x61, 0x74, 0x61, 0x04, 0x00, 0x00, 0x00, 0x80, 0x80, 0x80, 0x80,
	];

	[Fact]
	public void LegacyAudioClipOpensAsOneExportableEntryWithItsRawBytes()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "audio.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedLegacyAudioClip("beep", Wav));

		// Prove the fixture really is a legal serialized AudioClip carrying its inline WAV bytes
		// before asking the catalogue for it, so an empty catalogue can only mean the adapter
		// ignores the asset, not that the input failed to parse or never held the payload.
		using (FileBase parsed = UnitySource.ReadFile(selected))
		{
			parsed.ReadContentsRecursively();
			SerializedFile serialized = Assert.IsType<SerializedFile>(parsed);
			ObjectInfo info = Assert.Single(serialized.Objects.ToArray());
			Assert.Equal(AudioClipClassId, info.TypeID);
			Assert.True(info.ObjectData.Length > Wav.Length);

			using GameBundle bundle = new();
			GameAssetFactory factory = new(new BaseManager(static _ => { }));
			SerializedAssetCollection collection = bundle.AddCollectionFromSerializedFile(serialized, factory);
			IAudioClip clip = Assert.Single(collection.OfType<IAudioClip>());
			Assert.True(clip.Has_AudioData());
			Assert.True(clip.Has_Type());
			Assert.Equal(Wav, clip.AudioData);
		}

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal((UnityEntryKind)5, entry.Kind);
		Assert.Equal("beep", entry.Name);
		Assert.Equal((long)Wav.Length, entry.SourceSize);
		Assert.Equal("wav", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(Wav, exported.ToArray());
	}

	[Fact]
	public void StreamedPcm16FsbAudioClipDecodesToWav()
	{
		byte[] bank = Bank("pcm16.fsb");
		string selected = StreamedClip("beep", bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal((UnityEntryKind)5, entry.Kind);
		Assert.Equal((long)bank.Length, entry.SourceSize);
		Assert.Equal("wav", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		byte[] wav = exported.ToArray();
		Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
		Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
		Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)));
		Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
		Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
		Assert.True(wav.Length > 44);
	}

	[Fact]
	public void StreamedVorbisFsbAudioClipDecodesToOgg()
	{
		byte[] bank = Bank("short_vorbis.fsb");
		string selected = StreamedClip("song", bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal((long)bank.Length, entry.SourceSize);
		Assert.Equal("ogg", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		byte[] ogg = exported.ToArray();
		Assert.Equal("OggS", Encoding.ASCII.GetString(ogg, 0, 4));
	}

	[Fact]
	public void UnsupportedFsbCodecStaysRawFsb()
	{
		byte[] bank = Bank("pcm16.fsb");
		BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(0x18), 0x7FFFFFFF);
		string selected = StreamedClip("odd", bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("fsb", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(bank, exported.ToArray());
	}

	[Fact]
	public void MultiSampleFsbStaysRawFsb()
	{
		byte[] bank = Bank("pcm16.fsb");
		BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(0x08), 2);
		string selected = StreamedClip("many", bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("fsb", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(bank, exported.ToArray());
	}

	[Fact]
	public void DecodeFailureUnderAChosenExtensionFailsInsteadOfLeakingRawFsb()
	{
		byte[] bank = Bank("pcm16.fsb");
		BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(0x18), (uint)FmodAudioType.VORBIS);
		string selected = StreamedClip("mislabelled", bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("ogg", entry.OutputExtension);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void MissingDeclaredStreamStaysListedButExportsNothing()
	{
		string directory = Temp.NewDirectory();
		byte[] bank = Bank("pcm16.fsb");
		string selected = Path.Combine(directory, "audio.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedStreamedFsbAudioClip("absent", "absent.resource", (uint)bank.Length));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal((long)bank.Length, entry.SourceSize);
		Assert.Equal("bin", entry.OutputExtension);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void OversizedDeclaredAudioPayloadIsRefusedFromMetadataAlone()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "audio.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedStreamedFsbAudioClip("huge", "huge.resource", (uint)(Budgets.MaxAudioSourceBytes + 1)));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(Budgets.MaxAudioSourceBytes + 1, entry.SourceSize);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void AudioClipWithoutAnyDeclaredPayloadIsNotCatalogued()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "audio.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedLegacyAudioClip("silent", []));

		using UnitySession session = UnitySession.Open(selected);

		Assert.Empty(session.Entries);
	}

	[Fact]
	public void NonFsbPayloadWithAnFsbCompressionFormatStaysSafeBin()
	{
		byte[] junk = new byte[40];
		"RIFF"u8.CopyTo(junk);
		string selected = StreamedClip("weird", junk);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("bin", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(junk, exported.ToArray());
	}

	[Fact]
	public void NonFsbPayloadWithAnIndependentCompressionContainerStaysSafeBin()
	{
		// The compression-format enum is a codec hint, not container proof: RIFF junk tagged with
		// the VAG enum is still not a VAG payload and must not be surfaced under a vag extension.
		byte[] junk = new byte[40];
		"RIFF"u8.CopyTo(junk);
		string selected = StreamedClip("weird", junk, compressionFormat: (int)AudioCompressionFormat.VAG);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("bin", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(junk, exported.ToArray());
	}

	[Fact]
	public void StreamedOffsetBeyondTheAddressableRangeStaysSafeBin()
	{
		byte[] bank = Bank("pcm16.fsb");
		string selected = StreamedClip("offset", bank, offset: ulong.MaxValue);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("bin", entry.OutputExtension);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void StreamedOffsetPastTheEndStaysSafeBin()
	{
		byte[] bank = Bank("pcm16.fsb");
		string selected = StreamedClip("past", bank, offset: (ulong)bank.Length + 100);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("bin", entry.OutputExtension);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void StreamedOffsetAtTheEndStaysSafeBin()
	{
		byte[] bank = Bank("pcm16.fsb");
		string selected = StreamedClip("atend", bank, offset: (ulong)bank.Length);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("bin", entry.OutputExtension);
	}

	[Fact]
	public void BankWhoseSampleMetadataExceedsTheDecodeBudgetStaysRawFsb()
	{
		// A sample count that would imply a decoded size beyond the ceiling must select the truthful
		// raw FSB fallback at catalogue time rather than promise a WAV that could not be bounded.
		byte[] bank = Bank("pcm16.fsb");
		BinaryPrimitives.WriteUInt64LittleEndian(bank.AsSpan(0x3C), 0x3FFFFFFFUL << 34);
		string selected = StreamedClip("hugeSamples", bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("fsb", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(bank, exported.ToArray());
	}

	[Fact]
	public void AudioClipWithAnEmptyResourceNameIsNotCatalogued()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "audio.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedStreamedFsbAudioClip("blank", "", 100));

		using UnitySession session = UnitySession.Open(selected);

		Assert.Empty(session.Entries);
	}

	[Fact]
	public void BankWithNoSampleBytesFailsInsteadOfEmittingAnEmptyFile()
	{
		byte[] bank = Bank("pcm16.fsb");
		BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(0x14), 0);
		BinaryPrimitives.WriteUInt64LittleEndian(bank.AsSpan(0x3C), 4UL << 34);
		string selected = StreamedClip("emptyBank", bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("wav", entry.OutputExtension);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void BankDeclaringZeroSamplesStaysRawFsb()
	{
		// A degenerate zero sample count cannot bound a decode, so the truthful raw FSB fallback is
		// selected at catalogue time instead of promising a WAV container.
		byte[] bank = Bank("pcm16.fsb");
		BinaryPrimitives.WriteUInt64LittleEndian(bank.AsSpan(0x3C), 0);
		string selected = StreamedClip("zeroSamples", bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("fsb", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(bank, exported.ToArray());
	}

	[Fact]
	public void FourByteFsb5MagicIsRecognisedAsRawFsb()
	{
		byte[] bank = "FSB5"u8.ToArray();
		string selected = StreamedClip("tiny", bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("fsb", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(bank, exported.ToArray());
	}

	[Theory]
	[InlineData(4)]
	[InlineData(8)]
	[InlineData(20)]
	[InlineData(27)]
	public void TruncatedFsb5BodyStaysRawFsb(int length)
	{
		// A body shorter than the 28-byte fixed header can never be a decodable bank, but the four
		// magic bytes still prove it is an FSB container, so it must stay a raw .fsb rather than a bin
		// or an inferred codec extension.
		byte[] bank = new byte[length];
		"FSB5"u8.CopyTo(bank);
		string selected = StreamedClip("truncated" + length, bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("fsb", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(bank, exported.ToArray());
	}

	[Fact]
	public void ShortNonFsbPayloadStaysSafeBin()
	{
		byte[] bank = [0x52, 0x49];
		string selected = StreamedClip("two", bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("bin", entry.OutputExtension);
	}

	[Fact]
	public void FsbHeaderClaimingMoreDataThanTheBankHoldsStaysRawFsb()
	{
		// A header-only bank that declares a 1 GiB data section must never reach the pinned loader,
		// which allocates the declared sample body before reading it; the declared size is validated
		// against the actual bank length and the truthful raw .fsb is selected instead.
		byte[] bank = Bank("pcm16.fsb")[..96];
		BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(0x14), 0x40000000);
		string selected = StreamedClip("claims", bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("fsb", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(bank, exported.ToArray());
	}

	[Theory]
	[InlineData(2u)]
	[InlineData(3u)]
	public void MultichannelPcmBankStaysRawFsb(uint channelBits)
	{
		// The pinned PCM rebuilder picks mono or stereo from IsStereo and would silently drop the
		// extra channels, so a 6- or 8-channel bank must stay raw .fsb rather than promise a lossy WAV.
		byte[] bank = Bank("pcm16.fsb");
		ulong encoded = BinaryPrimitives.ReadUInt64LittleEndian(bank.AsSpan(0x3C));
		encoded = (encoded & ~(0x3UL << 5)) | ((ulong)channelBits << 5);
		BinaryPrimitives.WriteUInt64LittleEndian(bank.AsSpan(0x3C), encoded);
		string selected = StreamedClip("multi" + channelBits, bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("fsb", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal(bank, exported.ToArray());
	}

	[Fact]
	public void DeclaredPrefixNeverReadsPastTheDeclaredResourceInterval()
	{
		// The asset declares a four-byte resource, while the physical sibling file is a full FSB bank.
		// The bounded prefix must be clamped to the declared interval, so the classification sees only
		// the four magic bytes (raw .fsb) and never the following header fields of the physical file.
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "audio.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedStreamedFsbAudioClip("tiny", "tiny.resource", 4));
		File.WriteAllBytes(Path.Combine(directory, "tiny.resource"), Bank("pcm16.fsb"));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("fsb", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		Assert.Equal("FSB5"u8.ToArray(), exported.ToArray());
	}

	[Fact]
	public void NamedStreamedResourceWithUnknownZeroSizeStaysListedButExportsNothing()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "audio.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedStreamedFsbAudioClip("zero", "zero.resource", 0));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(0L, entry.SourceSize);
		Assert.Equal("bin", entry.OutputExtension);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void DeclaredResourceIntervalPastThePhysicalEndStaysSafeBin()
	{
		// The asset declares a resource 100 bytes longer than the physical sibling file, so the whole
		// declared interval does not fit the stream. The bounded prefix must refuse to classify it
		// from bytes at the physical end rather than clamp into them, and the entry stays a safe bin.
		string directory = Temp.NewDirectory();
		byte[] bank = Bank("pcm16.fsb");
		string selected = Path.Combine(directory, "audio.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedStreamedFsbAudioClip("past", "past.resource", (uint)bank.Length + 100));
		File.WriteAllBytes(Path.Combine(directory, "past.resource"), bank);

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal((long)bank.Length + 100, entry.SourceSize);
		Assert.Equal("bin", entry.OutputExtension);
	}

	[Fact]
	public void ReadPrefixRefusesANonZeroDeclaredIntervalPastThePhysicalEnd()
	{
		byte[] bytes = [1, 2, 3, 4, 5, 6, 7, 8];
		using MemoryStream stream = new(bytes);
		stream.Position = 3;

		Assert.Null(UnitySource.ReadPrefix(stream, offset: 0, size: (ulong)bytes.Length + 1, cap: 1084));
		Assert.Equal(3, stream.Position);
	}

	[Fact]
	public void ReadPrefixReadsTheDeclaredIntervalAndRestoresThePosition()
	{
		byte[] bytes = [10, 11, 12, 13, 14];
		using MemoryStream stream = new(bytes);
		stream.Position = 2;

		Assert.Equal(new byte[] { 11, 12, 13 }, UnitySource.ReadPrefix(stream, offset: 1, size: 3, cap: 1084));
		Assert.Equal(2, stream.Position);
	}

	[Fact]
	public void ReadPrefixReadsNothingFromAZeroSizeInterval()
	{
		// A zero declared size is an unknown length, not a payload; nothing is read or returned.
		byte[] bytes = [1, 2, 3];
		using MemoryStream stream = new(bytes);
		stream.Position = 2;

		Assert.Null(UnitySource.ReadPrefix(stream, offset: 0, size: 0, cap: 1084));
		Assert.Equal(2, stream.Position);
	}

	[Fact]
	public void FsbNameOffsetOutsideTheNameTableFailsInsteadOfDecoding()
	{
		// A four-byte FSB name table whose single offset points past the table makes the pinned loader
		// build the sample name from whatever follows the table. The catalogue still promises the
		// decoded wav extension, but the pre-loader guard must refuse the bank so the export fails
		// instead of decoding through an out-of-table name read.
		byte[] nameTable = new byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(nameTable.AsSpan(0), 0x40);
		string selected = StreamedClip("offsetname", BankWithNameTable(Bank("pcm16.fsb"), nameTable));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("wav", entry.OutputExtension);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void UnterminatedFsbNameInsideTheTableFailsInsteadOfDecoding()
	{
		// The name region inside the declared table has no terminating NUL, so the pinned loader would
		// keep reading past the table into the sample data. The guard must refuse the bank.
		byte[] nameTable = new byte[8192];
		BinaryPrimitives.WriteUInt32LittleEndian(nameTable.AsSpan(0), 4);
		nameTable.AsSpan(4).Fill(0x41);
		string selected = StreamedClip("unterminated", BankWithNameTable(Bank("pcm16.fsb"), nameTable));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("wav", entry.OutputExtension);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void OverlongFsbNameBeyondTheByteCapFailsInsteadOfDecoding()
	{
		// A NUL does terminate this name, but only after 4097 bytes, past the documented 4096-byte cap;
		// the guard must refuse the bank instead of letting the loader materialise the whole string.
		byte[] nameTable = new byte[4102];
		BinaryPrimitives.WriteUInt32LittleEndian(nameTable.AsSpan(0), 4);
		nameTable.AsSpan(4, 4097).Fill(0x41);
		nameTable[4101] = 0;
		string selected = StreamedClip("overlong", BankWithNameTable(Bank("pcm16.fsb"), nameTable));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("wav", entry.OutputExtension);

		using MemoryStream stream = new();
		Assert.False(session.Export(0, stream));
		Assert.Equal(0, stream.Length);
	}

	[Fact]
	public void FsbBankWithAnEmptyInTableNameStillDecodes()
	{
		// A present name table whose single offset points at an immediate NUL is a valid empty name,
		// so the guard must accept it and the single-sample bank must still decode.
		byte[] nameTable = new byte[8];
		BinaryPrimitives.WriteUInt32LittleEndian(nameTable.AsSpan(0), 4);
		string selected = StreamedClip("emptyname", BankWithNameTable(Bank("pcm16.fsb"), nameTable));

		using UnitySession session = UnitySession.Open(selected);

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("wav", entry.OutputExtension);

		using MemoryStream exported = new();
		Assert.True(session.Export(0, exported));
		byte[] wav = exported.ToArray();
		Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
		Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
	}

	private static string StreamedClip(string name, byte[] bank, ulong offset = 0, int compressionFormat = -1)
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "audio.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedStreamedFsbAudioClip(name, name + ".resource", (uint)bank.Length, offset, compressionFormat));
		File.WriteAllBytes(Path.Combine(directory, name + ".resource"), bank);
		return selected;
	}

	/// <summary>
	/// Returns <paramref name="bank"/> with a name table spliced in where the FSB5 layout places it
	/// (directly after the sample headers, before the data) and the declared name-table size updated
	/// to match. The caller supplies the whole table, so a test can choose the followed offset and
	/// the name bytes exactly.
	/// </summary>
	private static byte[] BankWithNameTable(byte[] bank, byte[] nameTable)
	{
		uint version = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(0x04));
		int fixedHeader = version == 0 ? 0x40 : 0x3C;
		int tableStart = fixedHeader + (int)BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(0x0C));
		byte[] result = new byte[bank.Length + nameTable.Length];
		bank.AsSpan(0, tableStart).CopyTo(result);
		nameTable.CopyTo(result, tableStart);
		bank.AsSpan(tableStart).CopyTo(result.AsSpan(tableStart + nameTable.Length));
		BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x10), (uint)nameTable.Length);
		return result;
	}

	private static byte[] Bank(string name)
		=> File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "audio", name));
}
