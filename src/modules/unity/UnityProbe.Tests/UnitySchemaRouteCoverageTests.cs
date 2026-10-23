using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

// Stage 17 coverage completion. These cases use only the projection entry points and
// independently encoded bodies; no route-private member is called or inspected.
public sealed class UnitySchemaRouteCoverageTests
{
	private const int StepLimit = 65_536;
	private const int StringLimit = 1_048_576;

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UnalignedTextThenEmptyAlignedNoneAndWidthMatchMature(bool bigEndian)
	{
		AssetTypeTemplateField text = Field("m_Name", AssetValueType.String, Field("data", AssetValueType.UInt8));
		text.Children[0].IsAligned = false;
		text.IsAligned = false;
		AssetTypeTemplateField emptyAligned = Field("ignoredEmptyGroup", AssetValueType.None);
		emptyAligned.IsAligned = true;
		AssetTypeTemplateField template = Root(text, emptyAligned, Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);

		List<byte> body = [];
		AddString(body, bigEndian, "abc", align: false);
		Align4(body);
		AddScalar(body, bigEndian, AssetValueType.Int32, 123456);

		using CountingStream routeInput = new(body.ToArray());
		UnityProjection route = UnityProjection.ReadBorrowed(
			routeInput, 0, body.Count, template, bigEndian, state);
		using CountingStream matureInput = new(body.ToArray());
		UnityProjection mature = UnityProjection.ReadBorrowed(
			matureInput, 0, body.Count, template, bigEndian, new UnityProjection.State(template),
			validateLayout: true);

		Assert.Equal("abc", route.Text("m_Name"));
		Assert.Equal(123456, route.Number("m_Width"));
		Assert.Equal(mature.Text("m_Name"), route.Text("m_Name"));
		Assert.Equal(mature.Number("m_Width"), route.Number("m_Width"));
		Assert.Contains(routeInput.Reads, read => read.Offset == 8 && read.Count == 4);
		Assert.Equal(body.Count, routeInput.Position);
		Assert.Equal(body.Count, matureInput.Position);
	}

	[Theory]
	[InlineData(0, false, false)]
	[InlineData(0, true, true)]
	[InlineData(1, false, true)]
	[InlineData(1, true, true)]
	[InlineData(-1, false, true)]
	public void StringAlignmentUsesChildOrFieldAndKeepsTheMatureCursor(
		int childAlignment, bool fieldAligned, bool expectedAligned)
	{
		AssetTypeTemplateField text = childAlignment < 0
			? Field("m_Name", AssetValueType.String)
			: Field("m_Name", AssetValueType.String, Field("data", AssetValueType.UInt8));
		if (childAlignment >= 0) text.Children[0].IsAligned = childAlignment != 0;
		text.IsAligned = fieldAligned;
		AssetTypeTemplateField template = Root(text, Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);

		List<byte> body = [];
		AddString(body, false, "xyz", align: expectedAligned);
		int widthOffset = body.Count;
		AddScalar(body, false, AssetValueType.Int32, -77);

		using CountingStream routeInput = new(body.ToArray());
		UnityProjection route = UnityProjection.ReadBorrowed(routeInput, 0, body.Count, template, false, state);
		using CountingStream matureInput = new(body.ToArray());
		UnityProjection mature = UnityProjection.ReadBorrowed(
			matureInput, 0, body.Count, template, false, new UnityProjection.State(template),
			validateLayout: true);

		Assert.Equal("xyz", route.Text("m_Name"));
		Assert.Equal(-77, route.Number("m_Width"));
		Assert.Equal(mature.Number("m_Width"), route.Number("m_Width"));
		Assert.Contains(routeInput.Reads, read => read.Offset == widthOffset && read.Count == 4);
		Assert.Equal(body.Count, routeInput.Position);
		Assert.Equal(body.Count, matureInput.Position);
	}

	[Fact]
	public void NegativeStringAndByteArrayLengthsRejectTheBorrowedRange()
	{
		AssetTypeTemplateField stringTemplate = Root(
			Field("m_Name", AssetValueType.String),
			Field("m_Width", AssetValueType.Int32));
		AssertNegativeLength(stringTemplate, "Invalid Unity byte range.");

		AssetTypeTemplateField image = Field("image data", AssetValueType.ByteArray,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		AssetTypeTemplateField byteTemplate = Root(image, Field("m_Width", AssetValueType.Int32));
		AssertNegativeLength(byteTemplate, "Invalid Unity byte range.");
	}

	[Fact]
	public void CapturedStringAboveTheMatureLimitFailsBeforePayloadIsRead()
	{
		AssetTypeTemplateField template = Root(
			Field("m_Name", AssetValueType.String),
			Field("m_Width", AssetValueType.Int32));
		int length = StringLimit + 1;
		byte[] body = new byte[checked(4 + length)];
		BinaryPrimitives.WriteInt32LittleEndian(body, length);

		using CountingStream routeInput = new(body, protectedStart: 4, protectedLength: length);
		InvalidDataException routeError = Assert.Throws<InvalidDataException>(() =>
			UnityProjection.ReadBorrowed(routeInput, 0, body.Length, template, false,
				new UnityProjection.State(template)));
		Assert.Equal("Unity metadata string is too long.", routeError.Message);
		Assert.Equal(4, routeInput.Position);
		Assert.Single(routeInput.Reads);
		Assert.Equal(new CountingStream.ReadRange(0, 4), routeInput.Reads[0]);
		Assert.Equal(0, routeInput.ProtectedReads);

		using CountingStream matureInput = new(body, protectedStart: 4, protectedLength: length);
		InvalidDataException matureError = Assert.Throws<InvalidDataException>(() =>
			UnityProjection.ReadBorrowed(matureInput, 0, body.Length, template, false,
				new UnityProjection.State(template), validateLayout: true));
		Assert.Equal(routeError.Message, matureError.Message);
		Assert.Equal(4, matureInput.Position);
		Assert.Equal(0, matureInput.ProtectedReads);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void NonLastUnalignedByteArrayKeepsItsPayloadBorrowedAndNextFieldAlignedByBytes(bool bigEndian)
	{
		AssetTypeTemplateField image = Field("image data", AssetValueType.ByteArray,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		image.IsAligned = false;
		AssetTypeTemplateField template = Root(image, Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);

		byte[] payload = [0xA1, 0xB2, 0xC3];
		List<byte> body = [];
		AddScalar(body, bigEndian, AssetValueType.Int32, payload.Length);
		body.AddRange(payload);
		int widthOffset = body.Count;
		AddScalar(body, bigEndian, AssetValueType.Int32, 0x10203040);

		using CountingStream routeInput = new(body.ToArray(), protectedStart: 4, protectedLength: payload.Length);
		UnityProjection route = UnityProjection.ReadBorrowed(routeInput, 0, body.Count, template, bigEndian, state);
		using CountingStream matureInput = new(body.ToArray(), protectedStart: 4, protectedLength: payload.Length);
		UnityProjection mature = UnityProjection.ReadBorrowed(
			matureInput, 0, body.Count, template, bigEndian, new UnityProjection.State(template),
			validateLayout: true);

		Assert.Equal(new UnityProjection.ByteRange(4, payload.Length), route.Bytes("image data"));
		Assert.Equal(mature.Bytes("image data"), route.Bytes("image data"));
		Assert.Equal(0x10203040, route.Number("m_Width"));
		Assert.Equal(mature.Number("m_Width"), route.Number("m_Width"));
		Assert.Equal(0, routeInput.ProtectedReads);
		Assert.Equal(0, matureInput.ProtectedReads);
		Assert.Contains(routeInput.Reads, read => read.Offset == widthOffset && read.Count == 4);
		Assert.Equal(body.Count, routeInput.Position);
		Assert.Equal(body.Count, matureInput.Position);
	}

	[Fact]
	public void NoneMarkedAsArrayDeclinesThePlanAndKeepsMatureValues()
	{
		AssetTypeTemplateField oddArray = Field("ignoredOddArray", AssetValueType.None,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.Int32));
		oddArray.IsArray = true;
		AssetTypeTemplateField template = Root(oddArray, Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);

		List<byte> body = [];
		AddScalar(body, false, AssetValueType.Int32, 2);
		AddScalar(body, false, AssetValueType.Int32, 11);
		AddScalar(body, false, AssetValueType.Int32, 22);
		int widthOffset = body.Count;
		AddScalar(body, false, AssetValueType.Int32, 0x55667788);

		using CountingStream routeInput = new(body.ToArray());
		UnityProjection route = UnityProjection.ReadBorrowed(routeInput, 0, body.Count, template, false, state);
		using CountingStream matureInput = new(body.ToArray());
		UnityProjection mature = UnityProjection.ReadBorrowed(
			matureInput, 0, body.Count, template, false, new UnityProjection.State(template),
			validateLayout: true);

		Assert.Equal(0x55667788, route.Number("m_Width"));
		Assert.Equal(mature.Number("m_Width"), route.Number("m_Width"));
		Assert.Equal(new[] { "m_Width" }, route.Keys);
		Assert.Contains(routeInput.Reads, read => read.Offset == widthOffset && read.Count == 4);
		Assert.Equal(body.Count, routeInput.Position);
		Assert.Equal(body.Count, matureInput.Position);
	}

	[Fact]
	public void AddStepLimitFallsBackToMatureAndKeepsTrailingValues()
	{
		AssetTypeTemplateField ignored = Field("ignoredAligned", AssetValueType.Int32);
		ignored.IsAligned = true;
		List<AssetTypeTemplateField> fields = new(StepLimit + 3);
		for (int i = 0; i <= StepLimit; i++) fields.Add(ignored);
		fields.Add(Field("m_Width", AssetValueType.Int32));
		fields.Add(Field("m_Height", AssetValueType.Int32));
		AssetTypeTemplateField template = Root(fields.ToArray());
		UnityProjection.State state = new(template);

		byte[] body = new byte[checked((StepLimit + 1) * 4 + 8)];
		for (int i = 0; i <= StepLimit; i++)
			BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(i * 4), i);
		int widthOffset = (StepLimit + 1) * 4;
		int heightOffset = widthOffset + 4;
		BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(widthOffset), 0x13572468);
		BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(heightOffset), -24681357);

		using CountingStream routeInput = new(body);
		UnityProjection route = UnityProjection.ReadBorrowed(routeInput, 0, body.Length, template, false, state);
		using CountingStream matureInput = new(body);
		UnityProjection mature = UnityProjection.ReadBorrowed(
			matureInput, 0, body.Length, template, false, new UnityProjection.State(template),
			validateLayout: true);

		Assert.Equal(0x13572468, route.Number("m_Width"));
		Assert.Equal(-24681357, route.Number("m_Height"));
		Assert.Equal(mature.Number("m_Width"), route.Number("m_Width"));
		Assert.Equal(mature.Number("m_Height"), route.Number("m_Height"));
		Assert.Contains(routeInput.Reads, read => read.Offset == widthOffset && read.Count == 4);
		Assert.Contains(routeInput.Reads, read => read.Offset == heightOffset && read.Count == 4);
		Assert.Equal(body.Length, routeInput.Position);
		Assert.Equal(body.Length, matureInput.Position);
	}

	[Fact]
	public void ScalarFlushLimitAfterManyStringsFallsBackToMature()
	{
		AssetTypeTemplateField ignored = Field("ignoredString", AssetValueType.String);
		List<AssetTypeTemplateField> fields = new(StepLimit + 1);
		for (int i = 0; i < StepLimit; i++) fields.Add(ignored);
		fields.Add(Field("m_Width", AssetValueType.Int32));
		AssetTypeTemplateField template = Root(fields.ToArray());
		UnityProjection.State state = new(template);

		byte[] body = new byte[checked(StepLimit * 4 + 4)];
		int widthOffset = StepLimit * 4;
		BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(widthOffset), 0x24681357);

		using MemoryStream routeInput = new(body, writable: false);
		UnityProjection route = UnityProjection.ReadBorrowed(
			routeInput, 0, body.Length, template, false, state);
		using MemoryStream matureInput = new(body, writable: false);
		UnityProjection mature = UnityProjection.ReadBorrowed(
			matureInput, 0, body.Length, template, false, new UnityProjection.State(template),
			validateLayout: true);

		Assert.Equal(0x24681357, route.Number("m_Width"));
		Assert.Equal(mature.Number("m_Width"), route.Number("m_Width"));
		Assert.Equal(new[] { "m_Width" }, route.Keys);
		Assert.Equal(body.Length, routeInput.Position);
		Assert.Equal(body.Length, matureInput.Position);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RawNameCandidateLeavesTheTextSlotNullOnRouteAndMature(bool bigEndian)
	{
		AssetTypeTemplateField template = Root(
			Field("m_Name", AssetValueType.String),
			Field("m_Width", AssetValueType.Int32));
		byte[] name = Encoding.UTF8.GetBytes("raw-name");
		List<byte> body = [];
		AddString(body, bigEndian, name);
		AddScalar(body, bigEndian, AssetValueType.Int32, -1234);

		using CountingStream routeInput = new(body.ToArray());
		UnityProjection route = UnityProjection.ReadBorrowed(
			routeInput, 0, body.Count, template, bigEndian, new UnityProjection.State(template),
			captureRawName: true);
		using CountingStream matureInput = new(body.ToArray());
		UnityProjection mature = UnityProjection.ReadBorrowed(
			matureInput, 0, body.Count, template, bigEndian, new UnityProjection.State(template),
			validateLayout: true, captureRawName: true);

		Assert.Equal(name, route.RawName.ToArray());
		Assert.Equal(mature.RawName.ToArray(), route.RawName.ToArray());
		Assert.Null(route.Text("m_Name"));
		Assert.Null(mature.Text("m_Name"));
		Assert.Equal(-1234, route.Number("m_Width"));
		Assert.Equal(mature.Number("m_Width"), route.Number("m_Width"));
		Assert.Equal(body.Count, routeInput.Position);
		Assert.Equal(body.Count, matureInput.Position);
	}

	private static void AssertNegativeLength(AssetTypeTemplateField template, string expectedMessage)
	{
		byte[] body = [0xFF, 0xFF, 0xFF, 0xFF];
		using CountingStream routeInput = new(body);
		InvalidDataException routeError = Assert.Throws<InvalidDataException>(() =>
			UnityProjection.ReadBorrowed(routeInput, 0, body.Length, template, false,
				new UnityProjection.State(template)));
		Assert.Equal(expectedMessage, routeError.Message);
		Assert.Equal(4, routeInput.Position);
		Assert.Equal(new CountingStream.ReadRange(0, 4), Assert.Single(routeInput.Reads));

		using CountingStream matureInput = new(body);
		InvalidDataException matureError = Assert.Throws<InvalidDataException>(() =>
			UnityProjection.ReadBorrowed(matureInput, 0, body.Length, template, false,
				new UnityProjection.State(template), validateLayout: true));
		Assert.Equal(routeError.Message, matureError.Message);
		Assert.Equal(4, matureInput.Position);
	}

	private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] fields)
		=> Field("Base", AssetValueType.None, fields);

	private static AssetTypeTemplateField Field(string name, AssetValueType kind,
		params AssetTypeTemplateField[] children)
		=> new()
		{
			Name = name,
			Type = name,
			ValueType = kind,
			HasValue = kind != AssetValueType.None,
			IsArray = kind is AssetValueType.Array or AssetValueType.ByteArray,
			Children = children.ToList(),
		};

	private static void AddString(List<byte> bytes, bool bigEndian, string value, bool align)
		=> AddString(bytes, bigEndian, Encoding.UTF8.GetBytes(value), align);

	private static void AddString(List<byte> bytes, bool bigEndian, byte[] value)
		=> AddString(bytes, bigEndian, value, align: true);

	private static void AddString(List<byte> bytes, bool bigEndian, byte[] value, bool align)
	{
		AddScalar(bytes, bigEndian, AssetValueType.Int32, value.Length);
		bytes.AddRange(value);
		if (align) Align4(bytes);
	}

	private static void AddScalar(List<byte> bytes, bool bigEndian, AssetValueType kind, long value)
	{
		Span<byte> encoded = stackalloc byte[8];
		int width = kind switch
		{
			AssetValueType.Bool or AssetValueType.Int8 or AssetValueType.UInt8 => 1,
			AssetValueType.Int16 or AssetValueType.UInt16 => 2,
			AssetValueType.Int32 or AssetValueType.UInt32 or AssetValueType.Float => 4,
			AssetValueType.Int64 or AssetValueType.UInt64 or AssetValueType.Double => 8,
			_ => throw new InvalidOperationException($"Unsupported scalar {kind}."),
		};
		switch (kind)
		{
			case AssetValueType.Bool: encoded[0] = value == 0 ? (byte)0 : (byte)1; break;
			case AssetValueType.Int8: encoded[0] = unchecked((byte)(sbyte)value); break;
			case AssetValueType.UInt8: encoded[0] = (byte)value; break;
			case AssetValueType.Int16 when bigEndian: BinaryPrimitives.WriteInt16BigEndian(encoded, (short)value); break;
			case AssetValueType.Int16: BinaryPrimitives.WriteInt16LittleEndian(encoded, (short)value); break;
			case AssetValueType.UInt16 when bigEndian: BinaryPrimitives.WriteUInt16BigEndian(encoded, (ushort)value); break;
			case AssetValueType.UInt16: BinaryPrimitives.WriteUInt16LittleEndian(encoded, (ushort)value); break;
			case AssetValueType.Int32 when bigEndian: BinaryPrimitives.WriteInt32BigEndian(encoded, (int)value); break;
			case AssetValueType.Int32: BinaryPrimitives.WriteInt32LittleEndian(encoded, (int)value); break;
			case AssetValueType.UInt32 when bigEndian: BinaryPrimitives.WriteUInt32BigEndian(encoded, (uint)value); break;
			case AssetValueType.UInt32: BinaryPrimitives.WriteUInt32LittleEndian(encoded, (uint)value); break;
			case AssetValueType.Int64 when bigEndian: BinaryPrimitives.WriteInt64BigEndian(encoded, value); break;
			case AssetValueType.Int64: BinaryPrimitives.WriteInt64LittleEndian(encoded, value); break;
			case AssetValueType.UInt64 when bigEndian: BinaryPrimitives.WriteUInt64BigEndian(encoded, unchecked((ulong)value)); break;
			case AssetValueType.UInt64: BinaryPrimitives.WriteUInt64LittleEndian(encoded, unchecked((ulong)value)); break;
			case AssetValueType.Float: BinaryPrimitives.WriteInt32LittleEndian(encoded, (int)value); break;
			case AssetValueType.Double: BinaryPrimitives.WriteInt64LittleEndian(encoded, value); break;
			default: throw new InvalidOperationException($"Unsupported scalar {kind}.");
		}
		bytes.AddRange(encoded[..width].ToArray());
	}

	private static void Align4(List<byte> bytes)
	{
		while ((bytes.Count & 3) != 0) bytes.Add(0);
	}

	private sealed class CountingStream : Stream
	{
		private readonly MemoryStream inner;
		private readonly long protectedStart;
		private readonly int protectedLength;

		public CountingStream(byte[] bytes, long protectedStart = -1, int protectedLength = 0)
		{
			inner = new MemoryStream(bytes, writable: false);
			this.protectedStart = protectedStart;
			this.protectedLength = protectedLength;
		}

		public List<ReadRange> Reads { get; } = [];
		public int ProtectedReads { get; private set; }
		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => inner.Length;
		public override long Position { get => inner.Position; set => inner.Position = value; }

		public override int Read(byte[] buffer, int offset, int count)
		{
			Check(count);
			int read = inner.Read(buffer, offset, count);
			Record(read);
			return read;
		}

		public override int Read(Span<byte> buffer)
		{
			Check(buffer.Length);
			int read = inner.Read(buffer);
			Record(read);
			return read;
		}

		public override int ReadByte()
		{
			Check(1);
			int value = inner.ReadByte();
			if (value >= 0) Reads.Add(new ReadRange(inner.Position - 1, 1));
			return value;
		}

		private void Check(int count)
		{
			if (count > 0 && protectedLength > 0 && inner.Position < protectedStart + protectedLength
				&& inner.Position + count > protectedStart)
			{
				ProtectedReads++;
				throw new InvalidOperationException("Protected payload was read.");
			}
		}

		private void Record(int count)
		{
			if (count > 0) Reads.Add(new ReadRange(inner.Position - count, count));
		}

		public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
		public override void Flush() { }
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public readonly record struct ReadRange(long Offset, int Count);

		protected override void Dispose(bool disposing)
		{
			if (disposing) inner.Dispose();
			base.Dispose(disposing);
		}
	}
}
