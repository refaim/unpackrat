using System;
using System.Buffers.Binary;
using System.IO;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityGeneration23ExtentTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void PublicMetadataReaderRejectsNonpositiveDescriptorExtents(int size)
	{
		using SmartStream input = SmartStream.CreateMemory(Fixture());
		SerializedFileHeader header = new();
		header.Read(input);
		header.MetadataSize = size;
		SerializedFileMetadata metadata = new();
		Assert.Throws<InvalidDataException>(() => metadata.Read(input, header));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ClaimedMetadataAboveTheResourceLimitIsRejectedBeforeReading(bool compact)
	{
		byte[] bytes = Fixture();
		BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), (64 * 1024 * 1024) + 1);
		AssertInvalid(bytes, compact);
	}

	[Fact]
	public void MetadataResourceCapRejectsAnOtherwiseCoherentLargeExtentWithoutReading()
	{
		const long metadataBytes = (64L * 1024 * 1024) + 1;
		using ZeroExtentStream source = new(48 + metadataBytes) { Position = 48 };
		using SmartStream input = SmartStream.CreateOwned(source);
		Assert.Equal(48, input.Position);
		SerializedFileHeader header = new()
		{
			Version = (FormatVersion)23, MetadataSize = metadataBytes,
			FileSize = source.Length, DataOffset = source.Length,
		};
		SerializedFileMetadata metadata = new();
		Assert.Throws<InvalidDataException>(() => metadata.Read(input, header));
		Assert.Equal(0, source.ReadCalls);
	}

	// A zero-filled seekable extent provides coherent Length/Position without allocating a large
	// backing buffer. Reads remain ordinary successful stream reads if the cap is removed.
	private sealed class ZeroExtentStream(long length) : Stream
	{
		public int ReadCalls { get; private set; }
		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => length;
		public override long Position { get; set; }
		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
		public override int Read(Span<byte> buffer)
		{
			ReadCalls++;
			int count = checked((int)Math.Min(buffer.Length, Math.Max(0, Length - Position)));
			buffer[..count].Clear();
			Position += count;
			return count;
		}
		public override long Seek(long offset, SeekOrigin origin)
		{
			Position = checked((origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length) + offset);
			return Position;
		}
		public override void Flush() { }
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void MetadataCannotStartAfterItsDeclaredDataSection(bool compact)
	{
		byte[] bytes = Fixture();
		BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(32, 8), 47);
		AssertInvalid(bytes, compact);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void MandatoryUserInformationCannotBorrowItsTerminatorFromTheDataSection(bool compact)
	{
		byte[] bytes = Fixture();
		int size = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
		Assert.Equal(0, bytes[48 + size - 1]);
		BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), size - 1);
		AssertInvalid(bytes, compact);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PartialStringTableReadDoesNotPublishAnIncompleteTypeTree(bool compact)
	{
		byte[] bytes = UnityFixtureBuilder.SerializedGeneration23TypedTexture("partial_texture",
			[1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255]);
		int magic = bytes.AsSpan().IndexOf("mhtt"u8);
		Assert.True(magic > 0);
		int nodes = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(magic + 8, 4));
		int strings = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(magic + 12, 4));
		Assert.True(strings > 1);
		using PartialStringTableStream source = new(bytes, magic + 16 + (nodes * 32), strings);
		using SmartStream input = SmartStream.CreateOwned(source);
		InvalidDataException rejection = Assert.Throws<InvalidDataException>(() =>
		{
			using SerializedFile unexpected = (SerializedFile)SchemeReader.ReadFile(input, "partial.assets", "partial.assets",
				compact ? FileReadMode.CompactMetadataOnly : FileReadMode.MetadataOnly);
		});
		Assert.True(source.ReturnedPartialStringTable);
		Assert.Contains("did not consume its exact frame", rejection.Message);
	}

	// A legal seekable source can return fewer bytes than requested without EOF. This exercises
	// the pinned mature type-tree reader's single string-buffer read, without throwing a fault or
	// changing the fixture bytes, reported Length, or metadata bounds.
	private sealed class PartialStringTableStream(byte[] bytes, int stringStart, int stringBytes) : Stream
	{
		private readonly MemoryStream inner = new(bytes, writable: false);
		public bool ReturnedPartialStringTable { get; private set; }
		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => inner.Length;
		public override long Position { get => inner.Position; set => inner.Position = value; }
		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
		public override int Read(Span<byte> buffer)
		{
			if (!ReturnedPartialStringTable && inner.Position == stringStart && buffer.Length == stringBytes)
			{
				ReturnedPartialStringTable = true;
				return inner.Read(buffer[..^1]);
			}
			return inner.Read(buffer);
		}
		public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
		public override void Flush() { }
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		protected override void Dispose(bool disposing)
		{
			if (disposing) inner.Dispose();
			base.Dispose(disposing);
		}
	}

	private static byte[] Fixture()
		=> UnityFixtureBuilder.SerializedGenerationTexture(23, "extent_texture",
			[1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255]);

	private static void AssertInvalid(byte[] bytes, bool compact)
	{
		using SmartStream input = SmartStream.CreateMemory(bytes);
		Assert.Throws<InvalidDataException>(() =>
		{
			using SerializedFile unexpected = (SerializedFile)SchemeReader.ReadFile(input, "extent.assets", "extent.assets",
				compact ? FileReadMode.CompactMetadataOnly : FileReadMode.MetadataOnly);
		});
	}
}
