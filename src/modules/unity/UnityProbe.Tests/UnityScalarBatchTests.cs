using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

// Stage 17 artifact. This test defines the observable benefit of the metadata-run route without
// depending on the route implementation: it counts reads through the real ReadBorrowed entry.
public sealed class UnityScalarBatchTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void KnownTextureScalarRunsAreBatchedWithoutTouchingOpaquePayload(bool bigEndian)
	{
		AssetTypeTemplateField template = TextureTemplate();
		UnityProjection.State state = new(template);

		int firstOffset = 61;
		byte[] firstImage = [0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33];
		(byte[] firstObject, long firstWidthRun, long firstStreamRun, long firstPayload) =
			TextureObject(bigEndian, "hero", 1920, 1080, firstImage, "resS/hero.resS",
				0x1122334455667788UL, 0x99AABBCCUL);
		using BatchCountingStream firstInput = new(Embed(firstObject, firstOffset));
		UnityProjection first = UnityProjection.ReadBorrowed(firstInput, firstOffset, firstObject.Length,
			template, bigEndian, state);

		Assert.Equal("hero", first.Text("m_Name"));
		Assert.Equal(1920, first.Number("m_Width"));
		Assert.Equal(1080, first.Number("m_Height"));
		Assert.Equal("resS/hero.resS", first.Text("m_StreamData/path"));
		Assert.Equal(0x1122334455667788UL, first.Unsigned("m_StreamData/offset"));
		Assert.Equal(0x99AABBCCUL, first.Unsigned("m_StreamData/size"));
		Assert.Equal(new UnityProjection.ByteRange(firstPayload, firstImage.Length), first.Bytes("image data"));
		AssertReadsStayInWindowAndOutOfPayload(firstInput, firstOffset, firstObject.Length,
			[new Run(firstOffset + firstWidthRun, 8), new Run(firstOffset + firstStreamRun, 16)],
			firstOffset + firstPayload, firstImage.Length, expectedRunReads: 2);

		int secondOffset = 113;
		byte[] secondImage = [0x7E, 0x7F];
		(byte[] secondObject, long secondWidthRun, long secondStreamRun, long secondPayload) =
			TextureObject(bigEndian, "sprite", 16, 8, secondImage, "r",
				0x0102030405060708UL, 0x0A0B0C0DUL);
		using BatchCountingStream secondInput = new(Embed(secondObject, secondOffset));
		UnityProjection second = UnityProjection.ReadBorrowed(secondInput, secondOffset, secondObject.Length,
			template, bigEndian, state);

		Assert.Equal("sprite", second.Text("m_Name"));
		Assert.Equal(16, second.Number("m_Width"));
		Assert.Equal(8, second.Number("m_Height"));
		Assert.Equal("r", second.Text("m_StreamData/path"));
		Assert.Equal(0x0102030405060708UL, second.Unsigned("m_StreamData/offset"));
		Assert.Equal(0x0A0B0C0DUL, second.Unsigned("m_StreamData/size"));
		Assert.Equal(new UnityProjection.ByteRange(secondPayload, secondImage.Length), second.Bytes("image data"));
		AssertReadsStayInWindowAndOutOfPayload(secondInput, secondOffset, secondObject.Length,
			[new Run(secondOffset + secondWidthRun, 8), new Run(secondOffset + secondStreamRun, 16)],
			secondOffset + secondPayload, secondImage.Length, expectedRunReads: 2);
	}

	private static void AssertReadsStayInWindowAndOutOfPayload(BatchCountingStream input, long objectOffset,
		int objectLength, Run[] scalarRuns, long payloadStart, int payloadLength, int expectedRunReads)
	{
		long objectEnd = objectOffset + objectLength;
		Assert.All(input.Reads, read =>
		{
			Assert.True(read.Offset >= objectOffset,
				$"read at {read.Offset} starts before object {objectOffset}.");
			Assert.True(read.Offset + read.Count <= objectEnd,
				$"read ending at {read.Offset + read.Count} exceeds object end {objectEnd}.");
		});
		Assert.DoesNotContain(input.Reads, read => Intersects(read, payloadStart, payloadLength));
		int runReads = input.Reads.Count(read => scalarRuns.Any(run => Intersects(read, run.Start, run.Length)));
		Assert.Equal(expectedRunReads, runReads);
	}

	private static bool Intersects(ReadWindow read, long start, int length)
		=> read.Count > 0 && length > 0 && read.Offset < start + length && read.Offset + read.Count > start;

	private static AssetTypeTemplateField TextureTemplate()
	{
		AssetTypeTemplateField Field(string name, AssetValueType kind, params AssetTypeTemplateField[] children)
			=> new()
			{
				Name = name, Type = name, ValueType = kind, HasValue = kind != AssetValueType.None,
				IsArray = kind is AssetValueType.Array or AssetValueType.ByteArray,
				Children = children.ToList(),
			};

		AssetTypeTemplateField image = Field("image data", AssetValueType.ByteArray,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		image.IsAligned = true;
		AssetTypeTemplateField root = Field("Base", AssetValueType.None,
			Field("m_Name", AssetValueType.String),
			Field("m_Width", AssetValueType.Int32),
			Field("m_Height", AssetValueType.Int32),
			image,
			Field("m_StreamData", AssetValueType.None,
				Field("path", AssetValueType.String),
				Field("offset", AssetValueType.UInt64),
				Field("size", AssetValueType.UInt64)));
		root.Type = "Texture2D";
		return root;
	}

	private static (byte[] Bytes, long WidthRun, long StreamRun, long PayloadStart) TextureObject(
		bool bigEndian, string name, int width, int height, byte[] image, string resource,
		ulong streamOffset, ulong streamSize)
	{
		List<byte> bytes = [];

		void Int32(int value)
		{
			Span<byte> encoded = stackalloc byte[4];
			if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(encoded, value);
			else BinaryPrimitives.WriteInt32LittleEndian(encoded, value);
			bytes.AddRange(encoded.ToArray());
		}

		void UInt64(ulong value)
		{
			Span<byte> encoded = stackalloc byte[8];
			if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(encoded, value);
			else BinaryPrimitives.WriteUInt64LittleEndian(encoded, value);
			bytes.AddRange(encoded.ToArray());
		}

		void Pad4()
		{
			while ((bytes.Count & 3) != 0) bytes.Add(0);
		}

		byte[] nameBytes = Encoding.UTF8.GetBytes(name);
		Int32(nameBytes.Length);
		bytes.AddRange(nameBytes);
		Pad4();

		long widthRun = bytes.Count;
		Int32(width);
		Int32(height);

		Int32(image.Length);
		long payloadStart = bytes.Count;
		bytes.AddRange(image);
		Pad4();

		byte[] resourceBytes = Encoding.UTF8.GetBytes(resource);
		Int32(resourceBytes.Length);
		bytes.AddRange(resourceBytes);
		Pad4();

		long streamRun = bytes.Count;
		UInt64(streamOffset);
		UInt64(streamSize);
		return (bytes.ToArray(), widthRun, streamRun, payloadStart);
	}

	private static byte[] Embed(byte[] objectBytes, int offset)
	{
		const int Suffix = 97;
		byte[] file = new byte[checked(offset + objectBytes.Length + Suffix)];
		file.AsSpan().Fill(0xEE);
		file.AsSpan(file.Length - Suffix).Fill(0xDD);
		Array.Copy(objectBytes, 0, file, offset, objectBytes.Length);
		return file;
	}

	private readonly record struct Run(long Start, int Length);
	private readonly record struct ReadWindow(long Offset, int Count);

	private sealed class BatchCountingStream(byte[] bytes) : Stream
	{
		private readonly MemoryStream inner = new(bytes, writable: false);
		private readonly List<ReadWindow> reads = [];

		public IReadOnlyList<ReadWindow> Reads => reads;
		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => inner.Length;
		public override long Position { get => inner.Position; set => inner.Position = value; }

		// Count each public read exactly once. Calling base.Read(Span) would route through
		// Read(byte[], int, int) and double-count the compatibility path.
		public override int Read(byte[] buffer, int offset, int count)
		{
			int read = inner.Read(buffer, offset, count);
			Record(read);
			return read;
		}

		public override int Read(Span<byte> buffer)
		{
			int read = inner.Read(buffer);
			Record(read);
			return read;
		}

		public override int ReadByte()
		{
			int value = inner.ReadByte();
			if (value >= 0) reads.Add(new(inner.Position - 1, 1));
			return value;
		}

		private void Record(int count)
		{
			if (count > 0) reads.Add(new(inner.Position - count, count));
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
}
