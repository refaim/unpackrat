using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnitySchemaRouteArraySkipTests
{
	private const int FieldBudget = 16_000_000;

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void BorrowedRouteSkipsFixedArraysBeforeSpriteGeometry(bool bigEndian)
	{
		AssetTypeTemplateField image = Field("image data", AssetValueType.ByteArray,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		image.IsAligned = true;
		AssetTypeTemplateField subMeshes = ArrayField("m_SubMeshes",
			Field("data", AssetValueType.None,
				Field("firstByte", AssetValueType.UInt32),
				Field("indexCount", AssetValueType.UInt32),
				Field("topology", AssetValueType.Int32)));
		AssetTypeTemplateField channels = ArrayField("m_Channels",
			Field("data", AssetValueType.None,
				Field("stream", AssetValueType.UInt8),
				Field("offset", AssetValueType.UInt8),
				Field("format", AssetValueType.UInt8),
				Field("dimension", AssetValueType.UInt8)));
		AssetTypeTemplateField template = Root(
			image, subMeshes, channels,
			Field("m_RD", AssetValueType.None,
				Field("textureRect", AssetValueType.None, Field("x", AssetValueType.Float))));
		UnityProjection.State state = new(template);
		Assert.NotNull(state.Route);

		byte[] imageBytes = [0x11, 0x22, 0x33, 0x44, 0x55];
		List<byte> body = [];
		AddBytes(body, bigEndian, imageBytes);
		int imageOffset = 4;
		Align4(body);
		AddScalar(body, bigEndian, AssetValueType.Int32, 2);
		int subOffset = body.Count;
		for (int index = 0; index < 2; index++)
		{
			AddScalar(body, bigEndian, AssetValueType.UInt32, index + 1);
			AddScalar(body, bigEndian, AssetValueType.UInt32, index + 11);
			AddScalar(body, bigEndian, AssetValueType.Int32, index - 2);
		}
		int subLength = body.Count - subOffset;
		AddScalar(body, bigEndian, AssetValueType.Int32, 3);
		int channelOffset = body.Count;
		for (int index = 0; index < 3; index++)
		{
			AddScalar(body, bigEndian, AssetValueType.UInt8, index);
			AddScalar(body, bigEndian, AssetValueType.UInt8, index + 4);
			AddScalar(body, bigEndian, AssetValueType.UInt8, index + 8);
			AddScalar(body, bigEndian, AssetValueType.UInt8, index + 12);
		}
		int channelLength = body.Count - channelOffset;
		AddFloat(body, bigEndian, 1.5f);

		int offset = 7;
		using CountingStream input = new(Embed(body.ToArray(), offset, 9));
		UnityProjection result = UnityProjection.ReadBorrowed(input, offset, body.Count, template, bigEndian, state);

		Assert.Equal(new UnityProjection.ByteRange(imageOffset, imageBytes.Length), result.Bytes("image data"));
		Assert.True(result.TryBits("m_RD/textureRect/x", out long bits));
		Assert.Equal(0x3FC00000L, bits);
		Assert.DoesNotContain(input.Reads, read => Overlaps(read, offset + subOffset, subLength));
		Assert.DoesNotContain(input.Reads, read => Overlaps(read, offset + channelOffset, channelLength));
		Assert.DoesNotContain(input.Reads, read => Overlaps(read, offset + imageOffset, imageBytes.Length));
	}

	[Fact]
	public void EmptyAndOuterAlignedArraysPreserveTheFollowingField()
	{
		AssetTypeTemplateField channels = ArrayField("m_Channels",
			Field("data", AssetValueType.None,
				Field("stream", AssetValueType.UInt8), Field("offset", AssetValueType.UInt8)));
		AssetTypeTemplateField subMeshes = ArrayField("m_SubMeshes",
			Field("data", AssetValueType.None,
				Field("firstByte", AssetValueType.UInt32), Field("indexCount", AssetValueType.UInt16)));
		subMeshes.IsAligned = true;
		AssetTypeTemplateField template = Root(
			channels, subMeshes,
			Field("m_RD", AssetValueType.None,
				Field("textureRect", AssetValueType.None, Field("x", AssetValueType.Float))));
		UnityProjection.State state = new(template);
		Assert.NotNull(state.Route);

		List<byte> body = [];
		AddScalar(body, false, AssetValueType.Int32, 0);
		AddScalar(body, false, AssetValueType.Int32, 1);
		int subOffset = body.Count;
		AddScalar(body, false, AssetValueType.UInt32, 42);
		AddScalar(body, false, AssetValueType.UInt16, 7);
		int subLength = body.Count - subOffset;
		Align4(body);
		AddFloat(body, false, 1.5f);

		int offset = 11;
		using CountingStream input = new(Embed(body.ToArray(), offset, 5));
		UnityProjection result = UnityProjection.ReadBorrowed(input, offset, body.Count, template, false, state);

		Assert.True(result.TryBits("m_RD/textureRect/x", out long bits));
		Assert.Equal(0x3FC00000L, bits);
		Assert.DoesNotContain(input.Reads, read => Overlaps(read, offset + subOffset, subLength));
	}

	[Fact]
	public void ArrayElementVisitsShareTheFieldBudget()
	{
		AssetTypeTemplateField template = Root(
			ArrayField("ignored", Field("data", AssetValueType.None)),
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State underState = new(template);
		Assert.NotNull(underState.Route);
		List<byte> underBody = [];
		AddScalar(underBody, false, AssetValueType.Int32, FieldBudget - 2);
		AddScalar(underBody, false, AssetValueType.Int32, 77);
		using (MemoryStream input = new(underBody.ToArray(), false))
		{
			UnityProjection result = UnityProjection.ReadBorrowed(input, 0, underBody.Count, template, false, underState);
			Assert.Equal(77, result.Number("m_Width"));
		}

		UnityProjection.State overState = new(template);
		List<byte> overBody = [];
		AddScalar(overBody, false, AssetValueType.Int32, FieldBudget - 1);
		using MemoryStream overInput = new(overBody.ToArray(), false);
		InvalidDataException failure = Assert.Throws<InvalidDataException>(() => UnityProjection.ReadBorrowed(
			overInput, 0, overBody.Count, template, false, overState));
		Assert.Equal("Unity projection exceeded its field budget.", failure.Message);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(16_000_001)]
	public void MalformedArrayCountsFailAfterTheCountPrefix(int count)
	{
		AssetTypeTemplateField template = Root(
			ArrayField("ignored", Field("data", AssetValueType.Int32)),
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		Assert.NotNull(state.Route);
		List<byte> body = [];
		AddScalar(body, false, AssetValueType.Int32, count);
		AddScalar(body, false, AssetValueType.Int32, 9);
		int offset = 3;
		using CountingStream input = new(Embed(body.ToArray(), offset, 2));
		InvalidDataException failure = Assert.Throws<InvalidDataException>(() => UnityProjection.ReadBorrowed(
			input, offset, body.Count, template, false, state));
		Assert.Equal("Invalid Unity array count.", failure.Message);
		Assert.Equal(offset + 4, input.Position);
	}

	[Fact]
	public void TruncatedFixedArrayMatchesTheIteratorFailure()
	{
		AssetTypeTemplateField template = Root(
			ArrayField("ignored", Field("data", AssetValueType.Int32)),
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State routeState = new(template);
		UnityProjection.State matureState = new(template);
		Assert.NotNull(routeState.Route);
		List<byte> body = [];
		AddScalar(body, false, AssetValueType.Int32, 2);
		body.AddRange(new byte[] { 1, 0, 0, 0, 2, 0 });
		int offset = 5;
		using CountingStream routeInput = new(Embed(body.ToArray(), offset, 7));
		using CountingStream matureInput = new(Embed(body.ToArray(), offset, 7));
		Assert.Throws<EndOfStreamException>(() => UnityProjection.ReadBorrowed(
			routeInput, offset, body.Count, template, false, routeState));
		Assert.Throws<EndOfStreamException>(() => UnityProjection.ReadBorrowed(
			matureInput, offset, body.Count, template, false, matureState, validateLayout: true));
		Assert.Equal(matureInput.Position, routeInput.Position);
		Assert.DoesNotContain(routeInput.Reads, read => Overlaps(read, offset + 4, body.Count - 4));
	}

	[Fact]
	public void VariableElementArraysKeepTheMatureFallback()
	{
		AssetTypeTemplateField template = Root(
			ArrayField("ignored", Field("data", AssetValueType.String)),
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		Assert.NotNull(state.Route);
		List<byte> body = [];
		AddScalar(body, false, AssetValueType.Int32, 1);
		AddString(body, false, "abc");
		AddScalar(body, false, AssetValueType.Int32, 9);
		using MemoryStream input = new(body.ToArray(), false);
		UnityProjection result = UnityProjection.ReadBorrowed(input, 0, body.Count, template, false, state);
		Assert.Equal(9, result.Number("m_Width"));
	}

	[Fact]
	public void CapturedArrayElementsKeepTheMatureFallback()
	{
		AssetTypeTemplateField template = Root(
			ArrayField("m_RenderDataKey", Field("data", AssetValueType.Int64)),
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		Assert.Null(state.Route);
		List<byte> body = [];
		AddScalar(body, false, AssetValueType.Int32, 2);
		AddScalar(body, false, AssetValueType.Int64, 5);
		AddScalar(body, false, AssetValueType.Int64, 6);
		AddScalar(body, false, AssetValueType.Int32, 9);
		using MemoryStream input = new(body.ToArray(), false);
		UnityProjection result = UnityProjection.ReadBorrowed(input, 0, body.Count, template, false, state);
		Assert.Equal(6, result.Number("m_RenderDataKey/data"));
		Assert.Equal(9, result.Number("m_Width"));
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void ScalarBatchBudgetFailureMatchesTheMatureCursor(bool truncated, bool bigEndian)
	{
		AssetTypeTemplateField routeTemplate = ScalarBudgetTemplate();
		AssetTypeTemplateField matureTemplate = ScalarBudgetTemplate();
		matureTemplate.Type = "SpriteAtlas";
		UnityProjection.State routeState = new(routeTemplate);
		UnityProjection.State matureState = new(matureTemplate);
		Assert.NotNull(routeState.Route);
		Assert.Null(matureState.Route);

		List<byte> body = [];
		AddScalar(body, bigEndian, AssetValueType.Int32, FieldBudget - 2);
		AddScalar(body, bigEndian, AssetValueType.Int32, 77);
		if (truncated) body.RemoveRange(body.Count - 2, 2);
		else AddScalar(body, bigEndian, AssetValueType.Int32, 88);

		int offset = 7;
		using CountingStream routeInput = new(Embed(body.ToArray(), offset, 5));
		using CountingStream matureInput = new(Embed(body.ToArray(), offset, 5));
		Exception? routeFailure = Record.Exception(() => UnityProjection.ReadBorrowed(
			routeInput, offset, body.Count, routeTemplate, bigEndian, routeState));
		Exception? matureFailure = Record.Exception(() => UnityProjection.ReadBorrowed(
			matureInput, offset, body.Count, matureTemplate, bigEndian, matureState, validateLayout: true));

		Assert.NotNull(routeFailure);
		Assert.NotNull(matureFailure);
		if (truncated) Assert.IsType<EndOfStreamException>(routeFailure);
		else Assert.IsType<InvalidDataException>(routeFailure);
		Assert.Equal(matureFailure.GetType(), routeFailure.GetType());
		Assert.Equal(matureInput.Position, routeInput.Position);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void MergedSkipBudgetFailureMatchesTheMatureCursor(bool bigEndian)
	{
		AssetTypeTemplateField routeTemplate = MergedSkipTemplate();
		AssetTypeTemplateField matureTemplate = MergedSkipTemplate();
		matureTemplate.Type = "SpriteAtlas";
		UnityProjection.State routeState = new(routeTemplate);
		UnityProjection.State matureState = new(matureTemplate);
		Assert.NotNull(routeState.Route);
		Assert.Null(matureState.Route);

		List<byte> body = [];
		AddScalar(body, bigEndian, AssetValueType.Int32, FieldBudget - 2);
		AddScalar(body, bigEndian, AssetValueType.Int32, 1);
		AddScalar(body, bigEndian, AssetValueType.Int32, 2);
		AddScalar(body, bigEndian, AssetValueType.Int32, 9);

		int offset = 3;
		using CountingStream routeInput = new(Embed(body.ToArray(), offset, 5));
		using CountingStream matureInput = new(Embed(body.ToArray(), offset, 5));
		Exception? routeFailure = Record.Exception(() => UnityProjection.ReadBorrowed(
			routeInput, offset, body.Count, routeTemplate, bigEndian, routeState));
		Exception? matureFailure = Record.Exception(() => UnityProjection.ReadBorrowed(
			matureInput, offset, body.Count, matureTemplate, bigEndian, matureState, validateLayout: true));

		Assert.NotNull(routeFailure);
		Assert.NotNull(matureFailure);
		Assert.IsType<InvalidDataException>(routeFailure);
		Assert.Equal(matureFailure.GetType(), routeFailure.GetType());
		Assert.Equal(matureInput.Position, routeInput.Position);
	}

	private static AssetTypeTemplateField ScalarBudgetTemplate()
		=> Root(
			ArrayField("ignored", Field("data", AssetValueType.None)),
			Field("m_Width", AssetValueType.Int32),
			Field("m_Height", AssetValueType.Int32));

	private static AssetTypeTemplateField MergedSkipTemplate()
		=> Root(
			ArrayField("ignored", Field("data", AssetValueType.None)),
			Field("first", AssetValueType.Int32),
			Field("second", AssetValueType.Int32),
			Field("m_Width", AssetValueType.Int32));

	private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] fields)
		=> Field("Base", AssetValueType.None, fields);

	private static AssetTypeTemplateField ArrayField(string name, AssetTypeTemplateField element)
		=> Field(name, AssetValueType.Array, Field("size", AssetValueType.Int32), element);

	private static AssetTypeTemplateField Field(string name, AssetValueType kind, params AssetTypeTemplateField[] children)
		=> new()
		{
			Name = name,
			Type = name,
			ValueType = kind,
			HasValue = kind != AssetValueType.None,
			IsArray = kind is AssetValueType.Array or AssetValueType.ByteArray,
			Children = children.ToList(),
		};

	private static void AddString(List<byte> bytes, bool bigEndian, string value)
		=> AddString(bytes, bigEndian, Encoding.UTF8.GetBytes(value));

	private static void AddString(List<byte> bytes, bool bigEndian, byte[] value)
	{
		AddScalar(bytes, bigEndian, AssetValueType.Int32, value.Length);
		bytes.AddRange(value);
		Align4(bytes);
	}

	private static void AddBytes(List<byte> bytes, bool bigEndian, byte[] value)
	{
		AddScalar(bytes, bigEndian, AssetValueType.Int32, value.Length);
		bytes.AddRange(value);
	}

	private static void AddFloat(List<byte> bytes, bool bigEndian, float value)
	{
		Span<byte> encoded = stackalloc byte[4];
		int bits = BitConverter.SingleToInt32Bits(value);
		if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(encoded, bits);
		else BinaryPrimitives.WriteInt32LittleEndian(encoded, bits);
		bytes.AddRange(encoded.ToArray());
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

	private static byte[] Embed(byte[] body, int offset, int suffix)
	{
		byte[] source = new byte[checked(offset + body.Length + suffix)];
		Array.Fill(source, (byte)0xA5, 0, offset);
		body.CopyTo(source, offset);
		Array.Fill(source, (byte)0x5A, offset + body.Length, suffix);
		return source;
	}

	private static bool Overlaps(CountingStream.ReadRange read, long start, int length)
		=> read.Count > 0 && read.Offset < start + length && start < read.Offset + read.Count;

	private sealed class CountingStream : Stream
	{
		private readonly MemoryStream inner;

		public CountingStream(byte[] bytes) => inner = new MemoryStream(bytes, writable: false);

		public List<ReadRange> Reads { get; } = [];
		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => inner.Length;
		public override long Position { get => inner.Position; set => inner.Position = value; }

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
			if (value >= 0) Reads.Add(new ReadRange(inner.Position - 1, 1));
			return value;
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
	}
}
