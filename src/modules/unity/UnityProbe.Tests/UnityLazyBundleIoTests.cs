using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.BundleFiles.RawWeb;
using AssetRipper.IO.Files.CompressedFiles;
using AssetRipper.IO.Files.CompressedFiles.GZip;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.WebFiles;
using AssetRipper.IO.Files.Streams.Smart;
using K4os.Compression.LZ4;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityLazyBundleIoTests(ITestOutputHelper output)
{
	[Theory]
	[InlineData(CompressionType.Lz4)]
	[InlineData(CompressionType.Lz4HC)]
	public void DistinctLz4BlocksReuseDecoderBuffersWithinABoundedAllocationBudget(CompressionType compression)
	{
		const int blockSize = 128 * 1024;
		byte[][] blocks = Enumerable.Range(0, 64).Select(index =>
		{
			byte[] bytes = new byte[blockSize];
			new Random(1702 + index).NextBytes(bytes);
			return bytes;
		}).ToArray();
		long length = (long)blocks.Length * blockSize;
		var fixture = Bundle(blocks, [("bounded.resS", 0L, length)], compression);
		TrackingStream physical = new(fixture.Bytes, long.MaxValue);
		using SmartStream input = SmartStream.CreateOwned(physical);
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "budget.bundle", "budget.bundle", FileReadMode.MetadataOnly);
		using SmartStream selected = Assert.Single(container.ResourceFiles).Stream.CreateReference();
		byte[] buffer = new byte[blockSize];
		selected.ReadExactly(buffer);
		Assert.True(buffer.AsSpan().SequenceEqual(blocks[0]));
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int pass = 0; pass < 2; pass++)
			for (int index = 1; index < blocks.Length; index++)
			{
				selected.Position = (long)index * blockSize;
				selected.ReadExactly(buffer);
				Assert.True(buffer.AsSpan().SequenceEqual(blocks[index]));
			}
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		output.WriteLine($"Repeated block reads allocated {allocated} bytes.");
		Assert.True(allocated < 2 * 1024 * 1024, $"Repeated block reads allocated {allocated:N0} bytes; decoder scratch must be reused.");
		Assert.Equal(length, selected.Position);
		Assert.Equal(-1, selected.ReadByte());
		selected.Dispose();
		container.ResourceFiles[0].Dispose();
		Assert.Equal(1, input.RefCount);
		input.Dispose();
		Assert.True(physical.Closed);
	}

	[Fact]
	public void GrowingAndShrinkingCompressedBlocksDecodeOnlyTheirDeclaredRanges()
	{
		byte[][] blocks = [new byte[17], new byte[65 * 1024], new byte[3]];
		for (int i = 0; i < blocks.Length; i++) new Random(i + 441).NextBytes(blocks[i]);
		var fixture = Bundle(blocks, [("varying.resS", 0L, blocks.Sum(b => b.LongLength))]);
		using SmartStream input = SmartStream.CreateMemory(fixture.Bytes);
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "varying.bundle", "varying.bundle", FileReadMode.MetadataOnly);
		Assert.Equal(blocks.SelectMany(b => b).ToArray(), container.ResourceFiles.Single().ToByteArray());
		foreach (var resource in container.ResourceFiles) resource.Dispose();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FailedNextBlockNeverPoisonsPreviouslyDecodedBytes(bool truncate)
	{
		byte[] first = new byte[128 * 1024], second = new byte[first.Length];
		new Random(641).NextBytes(first); new Random(642).NextBytes(second);
		var fixture = Bundle([first, second], [("retry.resS", 0L, first.LongLength + second.Length)]);
		byte[] bytes = fixture.Bytes.ToArray();
		if (!truncate) Array.Fill(bytes, (byte)255, (int)fixture.SecondBlock, bytes.Length - (int)fixture.SecondBlock);
		TrackingStream physical = new(bytes, long.MaxValue);
		using SmartStream input = SmartStream.CreateOwned(physical);
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "retry.bundle", "retry.bundle", FileReadMode.MetadataOnly);
		SmartStream stream = container.ResourceFiles.Single().Stream;
		byte[] buffer = new byte[first.Length];
		stream.ReadExactly(buffer);
		Assert.Equal(first, buffer);
		if (truncate) physical.ReadLimit = bytes.Length - 1;
		Assert.ThrowsAny<Exception>(() => stream.ReadExactly(buffer));
		Assert.Equal(first.Length, stream.Position);
		stream.Position = 0;
		stream.ReadExactly(buffer);
		Assert.Equal(first, buffer);
		foreach (var resource in container.ResourceFiles) resource.Dispose();
		Assert.Equal(1, input.RefCount);
		input.Dispose(); Assert.True(physical.Closed);
	}

	[Fact]
	public void LargeLz4BlocksKeepTheExistingTemporaryStreamFallback()
	{
		byte[] bytes = new byte[50 * 1024 * 1024 + 1];
		bytes[0] = 113; bytes[^1] = 219;
		var fixture = Bundle([bytes], [("large.resS", 0L, bytes.LongLength)]);
		using SmartStream input = SmartStream.CreateMemory(fixture.Bytes);
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "large.bundle", "large.bundle", FileReadMode.MetadataOnly);
		SmartStream stream = container.ResourceFiles.Single().Stream;
		Assert.Equal(113, stream.ReadByte());
		stream.Position = stream.Length - 1;
		Assert.Equal(219, stream.ReadByte());
		Assert.Equal(-1, stream.ReadByte());
		foreach (var resource in container.ResourceFiles) resource.Dispose();
		Assert.Equal(1, input.RefCount);
	}
	[Fact]
	public void FailedRawDirectoryReleasesPreviouslyAddedEntryRangesImmediately()
	{
		var fixture = LegacyBundle(new byte[80], false, invalidSecond: true);
		TrackingStream physical = new(fixture.Bytes, long.MaxValue);
		using SmartStream input = SmartStream.CreateOwned(physical);
		Assert.Throws<ArgumentOutOfRangeException>(() => SchemeReader.ReadFile(input, "bad.raw", "bad.raw", FileReadMode.MetadataOnly));
		Assert.Equal(1, input.RefCount);
		input.Dispose();
		Assert.True(physical.Closed);
	}

	[Fact]
	public void FailedWebDataDirectoryReleasesPreviouslyAddedEntryRangesImmediately()
	{
		using WebFile original = new();
		original.AddResourceFile(new ResourceFile(new byte[80], "web", "valid.resS"));
		original.AddResourceFile(new ResourceFile(new byte[80], "web", "invalid.resS"));
		using MemoryStream output = new(); original.Write(output);
		byte[] bytes = output.ToArray();
		int firstNameLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28, 4));
		int secondEntry = 32 + firstNameLength;
		System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(secondEntry, 4), int.MaxValue);
		TrackingStream physical = new(bytes, long.MaxValue);
		using SmartStream input = SmartStream.CreateOwned(physical);
		Assert.Throws<ArgumentOutOfRangeException>(() => SchemeReader.ReadFile(input, "bad.web", "bad.web", FileReadMode.MetadataOnly));
		Assert.Equal(1, input.RefCount);
		input.Dispose(); Assert.True(physical.Closed);
		foreach (var resource in original.ResourceFiles) resource.Dispose();
	}

	[Fact]
	public void FatalBundleAllocationFailurePropagatesTheOriginalExceptionAndReleasesOwners()
	{
		var fixture = Bundle([new byte[80], new byte[80]], [("first.resS", 0L, 80L), ("second.resS", 80L, 80L)]);
		OutOfMemoryException failure = new("injected physical input failure");
		TrackingStream physical = new(fixture.Bytes, fixture.SecondBlock) { Failure = failure };
		using SmartStream input = SmartStream.CreateOwned(physical);
		Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => SchemeReader.ReadFile(input, "bad.bundle", "bad.bundle")));
		Assert.Equal(1, input.RefCount);
		input.Dispose(); Assert.True(physical.Closed);
	}
	[Fact]
	public void ReclassifyingAnOuterCompressedResourceReleasesTheOriginalLease()
	{
		TrackingStream physical = new(new byte[80], long.MaxValue);
		SmartStream source = SmartStream.CreateOwned(physical);
		using GZipFile wrapper = new() { UncompressedFile = new ResourceFile(source, "outer", "outer") };
		Assert.Equal(2, source.RefCount);
		wrapper.ReadContents();
		ResourceFile classified = Assert.IsType<ResourceFile>(wrapper.UncompressedFile);
		Assert.Equal(2, source.RefCount);
		source.Dispose();
		Assert.False(physical.Closed);
		classified.Dispose();
		Assert.True(physical.Closed);
	}

	[Fact]
	public void FailedReclassificationReleasesTheOriginalLease()
	{
		byte[] bytes = UnityFixtureBuilder.SerializedTextAsset("invalid", "body");
		System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(32), -1);
		TrackingStream physical = new(bytes, long.MaxValue);
		SmartStream source = SmartStream.CreateOwned(physical);
		using GZipFile wrapper = new() { UncompressedFile = new ResourceFile(source, "outer", "outer") };
		Assert.Equal(2, source.RefCount);
		Assert.Throws<InvalidDataException>(() => wrapper.ReadContents());
		Assert.Equal(1, source.RefCount);
		Assert.False(physical.Closed);
		source.Dispose();
		Assert.True(physical.Closed);
	}
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void LegacyRawAndWebDefaultEagerStillLoadObjectBodies(bool web)
	{
		var fixture = LegacyBundle(UnityFixtureBuilder.SerializedTextAsset("eager", "body"), web);
		using SmartStream input = SmartStream.CreateMemory(fixture.Bytes);
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "eager.bundle", "eager.bundle");
		container.ReadContentsRecursively();
		SerializedFile file = Assert.Single(container.SerializedFiles);
		Assert.True(file.Objects[0].HasObjectData);
		Assert.True(file.Objects[0].ObjectData.Length > 0);
		file.Dispose();
	}

	[Fact]
	public void WebDataDefaultEagerStillLoadsObjectBodies()
	{
		using WebFile original = new();
		original.AddResourceFile(new ResourceFile(UnityFixtureBuilder.SerializedTextAsset("web", "body"), "web", "web.assets"));
		using MemoryStream output = new();
		original.Write(output);
		using SmartStream input = SmartStream.CreateMemory(output.ToArray());
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "web", "web");
		container.ReadContentsRecursively();
		SerializedFile file = Assert.Single(container.SerializedFiles);
		Assert.True(file.Objects[0].HasObjectData);
		file.Dispose();
		foreach (var resource in original.ResourceFiles) resource.Dispose();
	}

	[Fact]
	public void InvalidBlockExtentsFailWithoutReadingAnyBlockData()
	{
		var fixture = Bundle([[1, 2, 3, 4]], [("entry.resS", 0L, 4L)], CompressionType.None);
		foreach (bool truncate in new[] { false, true })
		{
			byte[] bytes = truncate ? fixture.Bytes[..^1] : fixture.Bytes.ToArray();
			if (!truncate)
			{
				// Header alignment is 16; the first storage block follows hash(16), count(4).
				using MemoryStream headerInput = new(bytes, false);
				FileStreamBundleHeader header = new(); header.Read(headerInput);
				int metadata = (int)((headerInput.Position + 15) & ~15L);
				System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(metadata + 20), 5);
			}
			using TrackingStream physical = new(bytes, fixture.DataOffset);
			using SmartStream input = SmartStream.CreateOwned(physical);
			Assert.Throws<InvalidDataException>(() => SchemeReader.ReadFile(input, "bad.bundle", "bad.bundle", FileReadMode.MetadataOnly));
			Assert.Equal(0, physical.ForbiddenReads);
			Assert.Equal(1, input.RefCount);
		}
	}

	[Fact]
	public void CorruptCompressedBlockCanBeRetriedWithoutUsingAPoisonedCache()
	{
		var fixture = Bundle([[1, 2, 3, 4]], [("entry.resS", 0L, 4L)]);
		byte[] corrupt = fixture.Bytes.ToArray();
		Array.Fill(corrupt, (byte)255, (int)fixture.DataOffset, corrupt.Length - (int)fixture.DataOffset);
		using SmartStream input = SmartStream.CreateMemory(corrupt);
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "bad.bundle", "bad.bundle", FileReadMode.MetadataOnly);
		SmartStream entry = container.ResourceFiles.Single().Stream;
		Assert.ThrowsAny<Exception>(() => entry.ReadByte());
		Assert.Equal(0, entry.Position);
		Assert.ThrowsAny<Exception>(() => entry.ReadByte());
		Assert.Equal(0, entry.Position);
		foreach (var resource in container.ResourceFiles) resource.Dispose();
	}

	[Fact]
	public void BundleLogicalCursorIsBoundedAndReleasesItsPhysicalSourceOnce()
	{
		var fixture = Bundle([[1, 2, 3, 4]], [("entry.resS", 0L, 4L)]);
		TrackingStream physical = new(fixture.Bytes, long.MaxValue);
		SmartStream input = SmartStream.CreateOwned(physical);
		FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "cursor.bundle", "cursor.bundle", FileReadMode.MetadataOnly);
		// Obtain the real internal stream to exercise its complete Stream contract independently of entry slices.
		var streamProperty = typeof(SmartStream).GetProperty("Stream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
		Stream range = (Stream)streamProperty.GetValue(container.ResourceFiles.Single().Stream)!;
		SmartStream dataOwner = (SmartStream)range.GetType().GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single(f => f.FieldType == typeof(SmartStream)).GetValue(range)!;
		Stream data = (Stream)streamProperty.GetValue(dataOwner)!;
		Assert.True(data.CanRead); Assert.True(data.CanSeek); Assert.False(data.CanWrite);
		Assert.Equal(4, data.Length);
		Assert.Equal(0, data.Seek(0, SeekOrigin.Begin));
		Assert.Equal(1, data.ReadByte());
		Assert.Equal(2, data.Seek(1, SeekOrigin.Current));
		Assert.Equal(3, data.Seek(-1, SeekOrigin.End));
		Assert.Equal(1, data.Read(new byte[3], 0, 3));
		Assert.Equal(-1, data.ReadByte());
		Assert.Equal(0, data.Read(new byte[1], 0, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => data.Position = -1);
		Assert.Throws<ArgumentOutOfRangeException>(() => data.Position = 5);
		Assert.Throws<ArgumentOutOfRangeException>(() => data.Seek(long.MaxValue, SeekOrigin.Current));
		Assert.Throws<ArgumentException>(() => data.Seek(0, (SeekOrigin)99));
		Assert.Throws<NotSupportedException>(() => data.SetLength(0));
		Assert.Throws<NotSupportedException>(() => data.Write([9], 0, 1));
		data.Flush();
		input.Dispose(); container.Dispose();
		Assert.False(physical.Closed);
		foreach (var resource in container.ResourceFiles) resource.Dispose();
		Assert.True(physical.Closed);
		Assert.False(data.CanRead); Assert.False(data.CanSeek);
		Assert.Throws<ObjectDisposedException>(() => data.ReadByte());
		Assert.Throws<ObjectDisposedException>(() => data.Position);
		Assert.Throws<ObjectDisposedException>(() => data.Length);
		Assert.Throws<ObjectDisposedException>(() => data.Flush());
		data.Dispose();
	}
	[Fact]
	public void WebDataDirectoryDoesNotCopyEntryBodies()
	{
		byte[] payload = UnityFixtureBuilder.SerializedTextAsset("webdata", "body");
		using WebFile original = new();
		original.AddResourceFile(new ResourceFile(payload, "web.data", "web.assets"));
		using MemoryStream output = new();
		original.Write(output);
		byte[] bytes = output.ToArray();
		long dataStart = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4));
		using TrackingStream physical = new(bytes, dataStart);
		using SmartStream input = SmartStream.CreateOwned(physical);
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "web.data", "web.data", FileReadMode.MetadataOnly);
		Assert.Single(container.ResourceFiles);
		Assert.Equal(0, physical.ForbiddenReads);
		physical.ForbiddenStart = long.MaxValue;
		container.ReadContentsRecursively();
		SerializedFile file = Assert.Single(container.SerializedFiles);
		Assert.False(file.Objects[0].HasObjectData);
		using SmartStream body = file.Objects[0].OpenDataStream();
		Assert.True(body.ToArray().Length > 0);
		file.Dispose();
		foreach (var resource in original.ResourceFiles) resource.Dispose();
	}
	[Theory]
	[InlineData(CompressionType.None)]
	[InlineData(CompressionType.Lz4)]
	[InlineData(CompressionType.Lz4HC)]
	[InlineData(CompressionType.Lzma)]
	public void LazyAndDefaultEagerEntriesMatchForExistingCodecs(CompressionType compression)
	{
		byte[] bytes = Enumerable.Range(0, 130).Select(i => (byte)i).ToArray();
		var fixture = Bundle([bytes[..64], bytes[64..]], [("entry.resS", 12L, 99L)], compression);
		foreach (FileReadMode mode in new[] { FileReadMode.Eager, FileReadMode.MetadataOnly })
		{
			using SmartStream input = SmartStream.CreateMemory(fixture.Bytes);
			using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "codec.bundle", "codec.bundle", mode);
			Assert.True(container.FailedFiles.Count == 0, string.Join("\n", container.FailedFiles.Select(f => f.StackTrace)));
			Assert.Equal(bytes.Skip(12).Take(99).ToArray(), container.ResourceFiles.Single().ToByteArray());
			foreach (var entry in container.ResourceFiles) entry.Dispose();
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void LegacyRawAndWebEntriesUseOwnedRangesAndPropagateMetadataOnly(bool web)
	{
		byte[] assets = UnityFixtureBuilder.SerializedTextAsset("legacy", "body");
		var fixture = LegacyBundle(assets, web);
		using TrackingStream physical = new(fixture.Bytes, web ? long.MaxValue : fixture.DataOffset);
		using SmartStream input = SmartStream.CreateOwned(physical);
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "legacy.bundle", "legacy.bundle", FileReadMode.MetadataOnly);
		Assert.Single(container.ResourceFiles);
		if (!web) Assert.Equal(0, physical.DataReads(fixture.DataOffset));
		physical.ForbiddenStart = long.MaxValue;
		container.ReadContentsRecursively();
		SerializedFile file = Assert.Single(container.SerializedFiles);
		Assert.False(file.Objects[0].HasObjectData);
		input.Dispose();
		using SmartStream body = file.Objects[0].OpenDataStream();
		Assert.True(body.Length > 0);
		Assert.True(body.ToArray().Length > 0);
		file.Dispose();
	}

	[Fact]
	public void NestedAndGzipContainersKeepMetadataOnlyModeThroughRecursion()
	{
		byte[] assets = UnityFixtureBuilder.SerializedTextAsset("nested", "body");
		byte[] inner = Bundle([assets], [("inner.assets", 0L, assets.LongLength)]).Bytes;
		byte[] outer = Bundle([inner], [("inner.bundle", 0L, inner.LongLength)]).Bytes;
		using MemoryStream wrapped = new();
		using (GZipStream gzip = new(wrapped, CompressionLevel.Fastest, true)) gzip.Write(outer);
		using SmartStream input = SmartStream.CreateMemory(wrapped.ToArray());
		using CompressedFile compressed = (CompressedFile)SchemeReader.ReadFile(input, "nested.gz", "nested.gz", FileReadMode.MetadataOnly);
		compressed.ReadContentsRecursively();
		FileContainer container = Assert.IsAssignableFrom<FileContainer>(compressed.UncompressedFile);
		SerializedFile file = Assert.Single(container.FetchSerializedFiles());
		Assert.False(file.Objects[0].HasObjectData);
		using SmartStream body = file.Objects[0].OpenDataStream();
		Assert.True(body.ToArray().Length > 0);
		file.Dispose();
	}

	[Fact]
	public void EmptyBlocksAndInvalidEntryRangesCannotReadOutsideTheBundle()
	{
		var fixture = Bundle([[], [1, 2, 3], []], [("valid.resS", 0L, 3L), ("negative.resS", -1L, 1L),
			("overflow.resS", long.MaxValue, 2L), ("past.resS", 2L, 2L), ("empty.resS", 3L, 0L)], CompressionType.None);
		using SmartStream input = SmartStream.CreateMemory(fixture.Bytes);
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "invalid.bundle", "invalid.bundle", FileReadMode.MetadataOnly);
		Assert.Equal(3, container.FailedFiles.Count);
		Assert.Equal(new byte[] { 1, 2, 3 }, container.ResourceFiles[0].ToByteArray());
		Assert.Empty(container.ResourceFiles[1].ToByteArray());
		foreach (var entry in container.ResourceFiles) entry.Dispose();
	}
	[Theory]
	[InlineData(CompressionType.None)]
	[InlineData(CompressionType.Lz4)]
	[InlineData(CompressionType.Lz4HC)]
	[InlineData(CompressionType.Lzma)]
	public void OpeningADirectoryAndReadingOneEntryDoesNotTouchAnUnrelatedCompressedBlock(CompressionType compression)
	{
		byte[] first = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
		byte[] second = Enumerable.Repeat((byte)177, 128).ToArray();
		var fixture = Bundle([first, second], [("first.resS", 0L, 128L), ("second.resS", 128L, 128L)], compression);
		using TrackingStream physical = new(fixture.Bytes, fixture.SecondBlock);
		using SmartStream input = SmartStream.CreateOwned(physical);
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "ranges.bundle", "ranges.bundle", FileReadMode.MetadataOnly);
		Assert.Empty(container.FailedFiles);
		Assert.Equal(0, physical.DataReads(fixture.DataOffset));
		Assert.Equal(2, container.ResourceFiles.Count);
		using SmartStream selected = container.ResourceFiles[0].Stream.CreateReference();
		Assert.Equal(first, selected.ToArray());
		Assert.Equal(0, physical.ForbiddenReads);
		Assert.Equal(0, container.ResourceFiles[1].Stream.Position);
		foreach (var entry in container.ResourceFiles) entry.Dispose();
	}

	[Fact]
	public void CrossBlockAndSharedBlockRangesAreExactAndEmptyEntriesReadNothing()
	{
		byte[] first = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
		byte[] second = Enumerable.Range(64, 64).Select(i => (byte)i).ToArray();
		var fixture = Bundle([first, second], [("cross.resS", 16L, 80L), ("shared.resS", 20L, 8L), ("empty.resS", 128L, 0L)]);
		using TrackingStream physical = new(fixture.Bytes, long.MaxValue);
		using SmartStream input = SmartStream.CreateOwned(physical);
		using FileContainer container = (FileContainer)SchemeReader.ReadFile(input, "ranges.bundle", "ranges.bundle", FileReadMode.MetadataOnly);
		Assert.Empty(container.FailedFiles);
		Assert.Equal(0, physical.DataReads(fixture.DataOffset));
		using SmartStream cross = container.ResourceFiles[0].Stream.CreateReference();
		using SmartStream shared = container.ResourceFiles[1].Stream.CreateReference();
		Assert.Equal(first.Concat(second).Skip(16).Take(80).ToArray(), cross.ToArray());
		Assert.Equal(first.Skip(20).Take(8).ToArray(), shared.ToArray());
		using SmartStream empty = container.ResourceFiles[2].Stream.CreateReference();
		int reads = physical.Reads.Count;
		Assert.Equal(-1, empty.ReadByte());
		Assert.Equal(0, empty.Read(new byte[1], 0, 1));
		Assert.Equal(reads, physical.Reads.Count);
		cross.Position = cross.Length - 1;
		byte[] tail = new byte[10];
		Assert.Equal(1, cross.Read(tail, 0, tail.Length));
		Assert.Equal(95, tail[0]);
		Assert.Equal(-1, cross.ReadByte());
		foreach (var entry in container.ResourceFiles) entry.Dispose();
	}

	private static (byte[] Bytes, long DataOffset, long SecondBlock) Bundle(
		byte[][] blocks, (string Name, long Offset, long Size)[] entries, CompressionType compression = CompressionType.Lz4)
	{
		byte[][] compressed = blocks.Select(bytes =>
		{
			if (compression == CompressionType.None) return bytes;
			if (compression == CompressionType.Lzma)
			{
				using MemoryStream source = new(bytes, false);
				using MemoryStream result = new();
				LzmaCompression.CompressLzmaStream(source, source.Length, result);
				return result.ToArray();
			}
			byte[] target = new byte[LZ4Codec.MaximumOutputSize(bytes.Length)];
			int count = LZ4Codec.Encode(bytes, target, LZ4Level.L00_FAST);
			return target.AsSpan(0, count).ToArray();
		}).ToArray();
		StorageBlock[] metadataBlocks = blocks.Select((bytes, index) =>
		{
			using MemoryStream serialized = new();
			using (EndianWriter writer = new(serialized, EndianType.BigEndian))
			{
				writer.Write((uint)bytes.Length);
				writer.Write((uint)compressed[index].Length);
				writer.Write((ushort)compression);
			}
			serialized.Position = 0;
			using EndianReader reader = new(serialized, EndianType.BigEndian);
			return StorageBlock.Read(reader);
		}).ToArray();
		byte[] metadata;
		using (MemoryStream serialized = new())
		{
			using (EndianWriter writer = new(serialized, EndianType.BigEndian))
			{
				new BlocksInfo(new Hash128(), metadataBlocks).Write(writer);
				new DirectoryInfo<FileStreamNode>
				{
					Nodes = entries.Select(e => new FileStreamNode { Path = e.Name, Offset = e.Offset, Size = e.Size }).ToArray(),
				}.Write(writer);
			}
			metadata = serialized.ToArray();
		}
		FileStreamBundleHeader header = new()
		{
			Version = BundleVersion.BF_2022_2,
			CompressedBlocksInfoSize = metadata.Length,
			UncompressedBlocksInfoSize = metadata.Length,
			Flags = BundleFlags.BlocksAndDirectoryInfoCombined,
		};
		byte[] headerBytes = HeaderBytes(header);
		int padding = (16 - headerBytes.Length % 16) % 16;
		header.Size = headerBytes.Length + padding + metadata.Length + compressed.Sum(b => b.Length);
		headerBytes = HeaderBytes(header);
		using MemoryStream result = new();
		result.Write(headerBytes);
		result.Write(new byte[padding]);
		result.Write(metadata);
		long dataOffset = result.Position;
		foreach (byte[] bytes in compressed) result.Write(bytes);
		return (result.ToArray(), dataOffset, dataOffset + compressed[0].Length);
	}

	private static (byte[] Bytes, long DataOffset) LegacyBundle(byte[] payload, bool web, bool invalidSecond = false)
	{
		RawWebNode[] Nodes(int offset) => invalidSecond
			? [new RawWebNode { Path = "legacy.assets", Offset = offset, Size = payload.Length }, new RawWebNode { Path = "invalid.resS", Offset = int.MaxValue, Size = 1 }]
			: [new RawWebNode { Path = "legacy.assets", Offset = offset, Size = payload.Length }];
		byte[] metadata;
		using (MemoryStream output = new())
		{
			using EndianWriter writer = new(output, EndianType.BigEndian);
			new DirectoryInfo<RawWebNode> { Nodes = Nodes(0) }.Write(writer);
			writer.AlignStream();
			metadata = output.ToArray();
			output.Position = 0;
			new DirectoryInfo<RawWebNode> { Nodes = Nodes(metadata.Length) }.Write(writer);
			writer.AlignStream();
			metadata = output.ToArray();
		}
		byte[] body = metadata.Concat(payload).ToArray();
		byte[] compressed = body;
		if (web)
		{
			using MemoryStream source = new(body, false);
			using MemoryStream target = new();
			LzmaCompression.CompressLzmaSizeStream(source, source.Length, target);
			compressed = target.ToArray();
		}
		byte[] Header(int size)
		{
			using MemoryStream output = new();
			using EndianWriter writer = new(output, EndianType.BigEndian);
			writer.WriteStringZeroTerm(web ? "UnityWeb" : "UnityRaw");
			writer.Write((int)BundleVersion.BF_350_4x);
			writer.WriteStringZeroTerm("4.x.x");
			writer.WriteStringZeroTerm("4.7.2f1");
			writer.Write((uint)(size + compressed.Length));
			writer.Write(size);
			writer.Write(1);
			writer.Write(1);
			writer.Write((uint)compressed.Length);
			writer.Write((uint)body.Length);
			writer.Write((uint)(size + compressed.Length));
			writer.Write((uint)metadata.Length);
			writer.AlignStream();
			return output.ToArray();
		}
		int headerSize = Header(0).Length;
		return (Header(headerSize).Concat(compressed).ToArray(), headerSize + metadata.LongLength);
	}

	private static byte[] HeaderBytes(FileStreamBundleHeader header)
	{
		using MemoryStream stream = new();
		header.Write(stream);
		return stream.ToArray();
	}

	private sealed class TrackingStream(byte[] bytes, long forbiddenStart) : Stream
	{
		private readonly MemoryStream source = new(bytes, false);
		public List<(long Start, int Count)> Reads { get; } = [];
		public int ForbiddenReads { get; private set; }
		public bool Closed { get; private set; }
		public Exception? Failure { get; set; }
		public long ReadLimit { get; set; } = long.MaxValue;
		public long ForbiddenStart { get; set; } = forbiddenStart;
		public int DataReads(long dataOffset) => Reads.Count(r => r.Start + r.Count > dataOffset);
		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => source.Length;
		public override long Position { get => source.Position; set => source.Position = value; }
		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
		public override int Read(Span<byte> buffer)
		{
			int count = (int)Math.Min(buffer.Length, Math.Max(0, Math.Min(Length, ReadLimit) - Position));
			Reads.Add((Position, count));
			if (Position + count > ForbiddenStart)
			{
				ForbiddenReads++;
				throw Failure ?? new InvalidOperationException("Unrelated compressed block was read");
			}
			return source.Read(buffer[..count]);
		}
		public override int ReadByte()
		{
			Span<byte> one = stackalloc byte[1];
			return Read(one) == 0 ? -1 : one[0];
		}
		public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
		public override void Flush() { }
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		protected override void Dispose(bool disposing) { Closed = true; source.Dispose(); base.Dispose(disposing); }
	}
}
