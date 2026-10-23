using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

// Behavioral differentials for the schema route. These tests use only the public/internal
// projection entry points; no private implementation state is replaced or inspected.
public sealed class UnitySchemaRouteExtraTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void BorrowedRouteDecodesEveryFixedIntegralKind(bool bigEndian)
	{
		AssetTypeTemplateField template = Root(Field("m_StreamData", AssetValueType.None,
			Field("aBool", AssetValueType.Bool),
			Field("bInt8", AssetValueType.Int8),
			Field("cUInt8", AssetValueType.UInt8),
			Field("dInt16", AssetValueType.Int16),
			Field("eUInt16", AssetValueType.UInt16),
			Field("fInt32", AssetValueType.Int32),
			Field("gUInt32", AssetValueType.UInt32),
			Field("hInt64", AssetValueType.Int64),
			Field("iUInt64", AssetValueType.UInt64)));
		UnityProjection.State state = new(template);
		long signed64 = -0x123456789ABCDEFL;
		ulong unsigned64 = 0xFEDCBA9876543210UL;
		List<byte> body = [];
		AddScalar(body, bigEndian, AssetValueType.Bool, 1);
		AddScalar(body, bigEndian, AssetValueType.Int8, -7);
		AddScalar(body, bigEndian, AssetValueType.UInt8, 250);
		AddScalar(body, bigEndian, AssetValueType.Int16, -12345);
		AddScalar(body, bigEndian, AssetValueType.UInt16, 54321);
		AddScalar(body, bigEndian, AssetValueType.Int32, -123456789);
		AddScalar(body, bigEndian, AssetValueType.UInt32, 0xDEADBEEF);
		AddScalar(body, bigEndian, AssetValueType.Int64, signed64);
		AddScalar(body, bigEndian, AssetValueType.UInt64, unchecked((long)unsigned64));

		int offset = 17;
		byte[] source = Embed(body.ToArray(), offset, 11);
		using CountingStream input = new(source);
		UnityProjection result = UnityProjection.ReadBorrowed(input, offset, body.Count, template, bigEndian, state);

		Assert.Equal(1, result.Number("m_StreamData/aBool"));
		Assert.Equal(-7, result.Number("m_StreamData/bInt8"));
		Assert.Equal(250, result.Number("m_StreamData/cUInt8"));
		Assert.Equal(-12345, result.Number("m_StreamData/dInt16"));
		Assert.Equal(54321, result.Number("m_StreamData/eUInt16"));
		Assert.Equal(-123456789, result.Number("m_StreamData/fInt32"));
		Assert.Equal(0xDEADBEEFUL, result.Unsigned("m_StreamData/gUInt32"));
		Assert.Equal(signed64, result.Number("m_StreamData/hInt64"));
		Assert.Equal(unsigned64, result.Unsigned("m_StreamData/iUInt64"));
		Assert.All(input.Reads, read => Assert.InRange(read.Offset, offset, offset + body.Count - read.Count));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(256)]
	[InlineData(257)]
	[InlineData(1_048_576)]
	public void CapturedTextAndBorrowedPayloadsRespectTheirExactRanges(int nameLength)
	{
		AssetTypeTemplateField image = Field("image data", AssetValueType.ByteArray,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		image.IsAligned = true;
		AssetTypeTemplateField template = Root(
			Field("m_Name", AssetValueType.String),
			image,
			Field("m_StreamData", AssetValueType.None,
				Field("path", AssetValueType.String),
				Field("offset", AssetValueType.UInt64),
				Field("size", AssetValueType.UInt64)));
		UnityProjection.State state = new(template);
		string name = new('n', nameLength);
		byte[] imageBytes = Enumerable.Range(0, 37).Select(i => (byte)(i * 3 + 1)).ToArray();
		List<byte> body = [];
		AddString(body, false, name);
		AddScalar(body, false, AssetValueType.Int32, imageBytes.Length);
		int imageOffset = body.Count;
		body.AddRange(imageBytes);
		Align4(body);
		AddString(body, false, "resS/hero.resS");
		AddScalar(body, false, AssetValueType.UInt64, 0x1122334455667788L);
		AddScalar(body, false, AssetValueType.UInt64, 0x0011223344556677L);

		int offset = 29;
		byte[] source = Embed(body.ToArray(), offset, 13);
		using CountingStream input = new(source, offset + imageOffset, imageBytes.Length);
		UnityProjection result = UnityProjection.ReadBorrowed(input, offset, body.Count, template, false, state);

		Assert.Equal(name, result.Text("m_Name"));
		Assert.Equal(new UnityProjection.ByteRange(imageOffset, imageBytes.Length), result.Bytes("image data"));
		Assert.Equal("resS/hero.resS", result.Text("m_StreamData/path"));
		Assert.Equal(0x1122334455667788UL, result.Unsigned("m_StreamData/offset"));
		Assert.Equal(0x0011223344556677UL, result.Unsigned("m_StreamData/size"));
		Assert.Equal(0, input.ProtectedReads);
		Assert.All(input.Reads, read => Assert.InRange(read.Offset, offset, offset + body.Count - read.Count));
	}

	[Fact]
	public void IgnoredFloatsAndParentAndFieldAlignmentPreserveTheCursor()
	{
		AssetTypeTemplateField height = Field("m_Height", AssetValueType.Int16);
		height.IsAligned = true;
		AssetTypeTemplateField parent = Field("parent", AssetValueType.None,
			Field("paddingA", AssetValueType.Int32),
			Field("ignoredText", AssetValueType.String),
			Field("paddingB", AssetValueType.Int16));
		parent.IsAligned = true;
		AssetTypeTemplateField template = Root(
			Field("m_Width", AssetValueType.Int32),
			Field("ignoredFloat", AssetValueType.Float),
			Field("ignoredDouble", AssetValueType.Double),
			height,
			Field("m_Type", AssetValueType.UInt8),
			parent,
			Field("m_SpriteAtlas", AssetValueType.None, Field("m_PathID", AssetValueType.UInt64)));
		UnityProjection.State state = new(template);
		List<byte> body = [];
		AddScalar(body, false, AssetValueType.Int32, 321);
		AddScalar(body, false, AssetValueType.Float, BitConverter.SingleToInt32Bits(1.5f));
		AddScalar(body, false, AssetValueType.Double, BitConverter.DoubleToInt64Bits(2.25));
		AddScalar(body, false, AssetValueType.Int16, -123);
		Align4(body);
		AddScalar(body, false, AssetValueType.UInt8, 200);
		AddScalar(body, false, AssetValueType.Int32, 0x01020304);
		AddString(body, false, "pad");
		AddScalar(body, false, AssetValueType.Int16, 0x0506);
		Align4(body);
		AddScalar(body, false, AssetValueType.UInt64, unchecked((long)0xFEDCBA9876543210UL));

		using CountingStream input = new(Embed(body.ToArray(), 13, 7));
		UnityProjection result = UnityProjection.ReadBorrowed(input, 13, body.Count, template, false, state);

		Assert.Equal(321, result.Number("m_Width"));
		Assert.Equal(-123, result.Number("m_Height"));
		Assert.Equal(200, result.Number("m_Type"));
		Assert.Equal(0xFEDCBA9876543210UL, result.Unsigned("m_SpriteAtlas/m_PathID"));
		Assert.DoesNotContain("ignoredFloat", result.Keys);
		Assert.DoesNotContain("ignoredDouble", result.Keys);
	}

	[Fact]
	public void RawNamesAndReusedStateSwitchInputsAndEndiannessWithoutStaleValues()
	{
		AssetTypeTemplateField template = Root(Field("m_Name", AssetValueType.String), Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		byte[] firstName = Encoding.UTF8.GetBytes("hé中");
		List<byte> firstBody = [];
		AddString(firstBody, false, firstName);
		AddScalar(firstBody, false, AssetValueType.Int32, -123456789);
		using CountingStream firstInput = new(Embed(firstBody.ToArray(), 17, 9));
		UnityProjection first = UnityProjection.ReadBorrowed(firstInput, 17, firstBody.Count, template, false, state,
			captureRawName: true);
		Assert.Equal(firstName, first.RawName.ToArray());
		// Candidate 16 leaves the text slot at its null default on the raw-name path; the
		// accessor returns that slot directly. validateLayout keeps the iterator as its oracle.
		Assert.Null(first.Text("m_Name"));
		Assert.Equal(-123456789, first.Number("m_Width"));
		using (CountingStream matureInput = new(Embed(firstBody.ToArray(), 17, 9)))
		{
			UnityProjection mature = UnityProjection.ReadBorrowed(matureInput, 17, firstBody.Count, template, false,
				new UnityProjection.State(template), validateLayout: true, captureRawName: true);
			Assert.Equal(firstName, mature.RawName.ToArray());
			Assert.Null(mature.Text("m_Name"));
		}

		byte[] secondName = Encoding.UTF8.GetBytes("snow");
		List<byte> secondBody = [];
		AddString(secondBody, true, secondName);
		AddScalar(secondBody, true, AssetValueType.Int32, 0x01020304);
		using CountingStream secondInput = new(Embed(secondBody.ToArray(), 41, 5));
		UnityProjection second = UnityProjection.ReadBorrowed(secondInput, 41, secondBody.Count, template, true, state,
			captureRawName: true);
		Assert.Equal(secondName, second.RawName.ToArray());
		Assert.Null(second.Text("m_Name"));
		Assert.Equal(0x01020304, second.Number("m_Width"));
		using (CountingStream matureInput = new(Embed(secondBody.ToArray(), 41, 5)))
		{
			UnityProjection mature = UnityProjection.ReadBorrowed(matureInput, 41, secondBody.Count, template, true,
				new UnityProjection.State(template), validateLayout: true, captureRawName: true);
			Assert.Equal(secondName, mature.RawName.ToArray());
			Assert.Null(mature.Text("m_Name"));
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void TextureTailAloneDoesNotDisableTheBorrowedRoute(bool textureTail)
	{
		AssetTypeTemplateField template = Root(Field("m_Width", AssetValueType.Int32), Field("m_Height", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		List<byte> body = [];
		AddScalar(body, false, AssetValueType.Int32, 7);
		AddScalar(body, false, AssetValueType.Int32, 9);
		using CountingStream input = new(Embed(body.ToArray(), 5, 3));
		UnityProjection result = UnityProjection.ReadBorrowed(input, 5, body.Count, template, false, state,
			textureTail: textureTail);
		Assert.Equal(7, result.Number("m_Width"));
		Assert.Equal(9, result.Number("m_Height"));
		Assert.Equal(1, input.Reads.Count);
	}

	[Fact]
	public void LayoutValidationKeepsTheMatureTraversalAndTextureExtension()
	{
		AssetTypeTemplateField template = Root(Field("m_Width", AssetValueType.Int32), Field("m_Height", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		List<byte> plain = [];
		AddScalar(plain, false, AssetValueType.Int32, 7);
		AddScalar(plain, false, AssetValueType.Int32, 9);
		using CountingStream validated = new(plain.ToArray());
		Assert.Equal(9, UnityProjection.ReadBorrowed(validated, 0, plain.Count, template, false, state,
			validateLayout: true).Number("m_Height"));
		Assert.True(validated.Reads.Count >= 2, "Layout validation must retain the unchanged iterator traversal.");

		List<byte> extended = [.. plain, .. Enumerable.Repeat((byte)1, 24)];
		using MemoryStream input = new(extended.ToArray(), false);
		UnityProjection texture = UnityProjection.ReadBorrowed(input, 0, extended.Count, template, false, state,
			validateLayout: true, textureTail: true);
		Assert.Equal(7, texture.Number("m_Width"));
	}

	[Fact]
	public void TruncatedFieldsAndTextFailBeforeTheNextReadRecovers()
	{
		AssetTypeTemplateField scalarTemplate = Root(Field("m_Width", AssetValueType.Int32), Field("m_Height", AssetValueType.Int32));
		UnityProjection.State scalarState = new(scalarTemplate);
		using (MemoryStream empty = new([], false))
			Assert.Throws<EndOfStreamException>(() => UnityProjection.ReadBorrowed(empty, 0, 0, scalarTemplate, false, scalarState));
		using (MemoryStream shortRun = new([1, 2, 3, 4], false))
			Assert.Throws<EndOfStreamException>(() => UnityProjection.ReadBorrowed(shortRun, 0, 4, scalarTemplate, false, scalarState));
		using (MemoryStream recovered = new([3, 0, 0, 0, 4, 0, 0, 0], false))
		{
			UnityProjection next = UnityProjection.ReadBorrowed(recovered, 0, 8, scalarTemplate, false, scalarState);
			Assert.Equal(3, next.Number("m_Width"));
			Assert.Equal(4, next.Number("m_Height"));
		}

		AssetTypeTemplateField textTemplate = Root(Field("m_Name", AssetValueType.String), Field("m_Width", AssetValueType.Int32));
		UnityProjection.State textState = new(textTemplate);
		List<byte> truncatedBody = [];
		AddScalar(truncatedBody, false, AssetValueType.Int32, 10);
		truncatedBody.Add(1);
		truncatedBody.Add(2);
		using (MemoryStream truncated = new(truncatedBody.ToArray(), false))
			Assert.Throws<InvalidDataException>(() => UnityProjection.ReadBorrowed(truncated, 0, truncatedBody.Count,
				textTemplate, false, textState));
		List<byte> validBody = [];
		AddString(validBody, false, "ok");
		AddScalar(validBody, false, AssetValueType.Int32, 7);
		using (MemoryStream recovered = new(validBody.ToArray(), false))
		{
			UnityProjection next = UnityProjection.ReadBorrowed(recovered, 0, validBody.Count, textTemplate, false, textState);
			Assert.Equal("ok", next.Text("m_Name"));
			Assert.Equal(7, next.Number("m_Width"));
		}
	}

	[Fact]
	public void UnsupportedArraysAndDynamicCapturesKeepTheMatureValues()
	{
		AssetTypeTemplateField array = Field("ignoredArray", AssetValueType.Array,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.Int32));
		AssetTypeTemplateField template = Root(array, Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		List<byte> body = [];
		AddScalar(body, false, AssetValueType.Int32, 3);
		AddScalar(body, false, AssetValueType.Int32, 10);
		AddScalar(body, false, AssetValueType.Int32, 20);
		AddScalar(body, false, AssetValueType.Int32, 30);
		AddScalar(body, false, AssetValueType.Int32, 77);
		using (MemoryStream input = new(body.ToArray(), false))
		{
			UnityProjection fallback = UnityProjection.ReadBorrowed(input, 0, body.Count, template, false, state);
			Assert.Equal(77, fallback.Number("m_Width"));
			Assert.Equal(new[] { "m_Width" }, fallback.Keys);
		}

		AssetTypeTemplateField repeated = Field("m_RenderDataKey", AssetValueType.Array,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.Int64));
		AssetTypeTemplateField repeatedTemplate = Root(repeated, Field("m_Width", AssetValueType.Int32));
		UnityProjection.State repeatedState = new(repeatedTemplate);
		List<byte> repeatedBody = [];
		AddScalar(repeatedBody, false, AssetValueType.Int32, 2);
		AddScalar(repeatedBody, false, AssetValueType.Int64, 5);
		AddScalar(repeatedBody, false, AssetValueType.Int64, 6);
		AddScalar(repeatedBody, false, AssetValueType.Int32, 9);
		using (MemoryStream input = new(repeatedBody.ToArray(), false))
		{
			UnityProjection captured = UnityProjection.ReadBorrowed(input, 0, repeatedBody.Count,
				repeatedTemplate, false, repeatedState);
			Assert.Equal(6, captured.Number("m_RenderDataKey/data"));
			Assert.Equal(9, captured.Number("m_Width"));
		}

		AssetTypeTemplateField texture = Field("texture", AssetValueType.ByteArray,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		AssetTypeTemplateField atlasTemplate = Root(Field("m_RenderDataMap", AssetValueType.None,
			Field("Array", AssetValueType.Array, Field("size", AssetValueType.Int32),
				Field("data", AssetValueType.None, Field("first", AssetValueType.Int64),
					Field("second", AssetValueType.None, texture)))));
		atlasTemplate.Type = "SpriteAtlas";
		UnityProjection.State atlasState = new(atlasTemplate);
		List<byte> atlasBody = [];
		AddScalar(atlasBody, false, AssetValueType.Int32, 1);
		AddScalar(atlasBody, false, AssetValueType.Int64, 7);
		AddScalar(atlasBody, false, AssetValueType.Int32, 3);
		atlasBody.AddRange(new byte[] { 0xAA, 0xBB, 0xCC });
		using MemoryStream atlasInput = new(atlasBody.ToArray(), false);
		UnityProjection atlas = UnityProjection.ReadBorrowed(atlasInput, 0, atlasBody.Count, atlasTemplate, false, atlasState);
		Assert.Equal(7, atlas.Number("m_RenderDataMap/Array/data#0/first"));
		Assert.Equal(new UnityProjection.ByteRange(16, 3), atlas.Bytes("m_RenderDataMap/Array/data#0/second/texture"));
	}

	[Fact]
	public void AStateDoesNotApplyItsPlanToADifferentTemplateInstance()
	{
		AssetTypeTemplateField planned = Root(Field("m_Width", AssetValueType.Int32));
		AssetTypeTemplateField other = Root(Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(planned);
		using MemoryStream input = new([42, 0, 0, 0], false);
		UnityProjection result = UnityProjection.ReadBorrowed(input, 0, 4, other, false, state);
		Assert.Equal(0, result.Number("m_Width"));
		Assert.Equal(new[] { "m_Width" }, result.Keys);
	}

	[Fact]
	public void ALastByteRangeStopsAtItsLengthAndLeavesThePayloadAndTailUnread()
	{
		AssetTypeTemplateField image = Field("image data", AssetValueType.ByteArray,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		image.IsAligned = true;
		AssetTypeTemplateField template = Root(Field("m_Name", AssetValueType.String), image);
		UnityProjection.State state = new(template);
		byte[] payload = [0x10, 0x20, 0x30, 0x40, 0x50];
		List<byte> body = [];
		AddString(body, false, "last");
		AddScalar(body, false, AssetValueType.Int32, payload.Length);
		int payloadOffset = body.Count;
		body.AddRange(payload);
		body.AddRange(new byte[] { 0x77, 0x77, 0x77 });
		int offset = 9;
		using CountingStream input = new(Embed(body.ToArray(), offset, 4), offset + payloadOffset, payload.Length);
		UnityProjection result = UnityProjection.ReadBorrowed(input, offset, body.Count, template, false, state);
		Assert.Equal("last", result.Text("m_Name"));
		Assert.Equal(new UnityProjection.ByteRange(payloadOffset, payload.Length), result.Bytes("image data"));
		Assert.Equal(0, input.ProtectedReads);
	}

	[Fact]
	public void ALastCapturedTextConsumesItsBytesButNotItsPadding()
	{
		AssetTypeTemplateField template = Root(Field("m_Name", AssetValueType.String));
		UnityProjection.State state = new(template);
		List<byte> body = [];
		AddScalar(body, false, AssetValueType.Int32, 3);
		body.AddRange(Encoding.UTF8.GetBytes("end"));
		body.AddRange(new byte[] { 0xAA, 0xAA, 0xAA });
		using CountingStream input = new(Embed(body.ToArray(), 11, 5));
		UnityProjection result = UnityProjection.ReadBorrowed(input, 11, body.Count, template, false, state);
		Assert.Equal("end", result.Text("m_Name"));
		Assert.Equal(2, input.Reads.Count);
		Assert.Equal(11 + 4 + 3, input.Reads[^1].Offset + input.Reads[^1].Count);
	}

	[Fact]
	public void NoCaptureFloatingCaptureAndDynamicCapturesKeepTheirTypedBehavior()
	{
		AssetTypeTemplateField uncaptured = Root(Field("ignored", AssetValueType.Int32));
		UnityProjection.State uncapturedState = new(uncaptured);
		using (MemoryStream uncapturedInput = new([1, 0, 0, 0], false))
		{
			UnityProjection result = UnityProjection.ReadBorrowed(uncapturedInput, 0, 4, uncaptured, false, uncapturedState);
			Assert.Empty(result.Keys);
			Assert.Equal(0, result.Number("ignored"));
		}

		AssetTypeTemplateField floating = Root(Field("m_Width", AssetValueType.Float));
		UnityProjection.State floatingState = new(floating);
		using MemoryStream input = new([0, 0, 0xC0, 0x3F], false);
		UnityProjection floatingResult = UnityProjection.ReadBorrowed(input, 0, 4, floating, false, floatingState);
		Assert.True(floatingResult.TryBits("m_Width", out long bits));
		Assert.Equal(BitConverter.SingleToInt32Bits(1.5f), bits);
		Assert.False(floatingResult.TryNumber("m_Width", out _));

		AssetTypeTemplateField atlas = Root(Field("m_Width", AssetValueType.Int32));
		atlas.Type = "SpriteAtlas";
		UnityProjection.State atlasState = new(atlas);
		using MemoryStream atlasInput = new([42, 0, 0, 0], false);
		Assert.Equal(42, UnityProjection.ReadBorrowed(atlasInput, 0, 4, atlas, false, atlasState).Number("m_Width"));
	}

	private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] fields)
		=> Field("Base", AssetValueType.None, fields);

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
	}
}
