using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.IO.Files.SerializedFiles;
using AssetsTools.NET;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityProjectionTests(ITestOutputHelper output)
{
	private static AssetTypeTemplateField Field(string name, AssetValueType kind, params AssetTypeTemplateField[] children)
		=> new() { Name = name, Type = name, ValueType = kind, HasValue = kind != AssetValueType.None,
			IsArray = kind is AssetValueType.Array or AssetValueType.ByteArray, Children = children.ToList() };
	private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] fields) => Field("Base", AssetValueType.None, fields);
	private static MemoryStream Data(Action<BinaryWriter> write)
	{
		MemoryStream stream = new(); using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) write(writer); stream.Position = 0; return stream;
	}

	[Theory]
	[InlineData(AssetValueType.Bool, 0)]
	[InlineData(AssetValueType.Bool, 1)]
	[InlineData(AssetValueType.Int8, -7)]
	[InlineData(AssetValueType.UInt8, 7)]
	[InlineData(AssetValueType.Int16, -7)]
	[InlineData(AssetValueType.UInt16, 7)]
	[InlineData(AssetValueType.Int32, -7)]
	[InlineData(AssetValueType.UInt32, 7)]
	[InlineData(AssetValueType.Int64, -7)]
	[InlineData(AssetValueType.UInt64, 7)]
	public void IntegerAndBooleanMetadataUseTheMatureScalarReader(AssetValueType type, long expected)
	{
		using MemoryStream stream = Data(writer =>
		{
			switch (type)
			{
				case AssetValueType.Bool: writer.Write(expected != 0); break;
				case AssetValueType.Int8: writer.Write((sbyte)expected); break;
				case AssetValueType.UInt8: writer.Write((byte)expected); break;
				case AssetValueType.Int16: writer.Write((short)expected); break;
				case AssetValueType.UInt16: writer.Write((ushort)expected); break;
				case AssetValueType.Int32: writer.Write((int)expected); break;
				case AssetValueType.UInt32: writer.Write((uint)expected); break;
				case AssetValueType.Int64: writer.Write(expected); break;
				default: writer.Write((ulong)expected); break;
			}
		});
		UnityProjection result = UnityProjection.Read(stream, Root(Field("m_Width", type)), false);
		Assert.Equal(expected, result.Number("m_Width"));
		Assert.Equal(string.Empty, result.Text("m_Width"));
		Assert.Equal(default, result.Bytes("m_Width"));
		Assert.Contains("m_Width", result.Keys);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(32)]
	[InlineData(1_048_577)]
	public void InvalidOrOversizedMetadataStringLengthsAreRefusedBeforeAllocation(int length)
	{
		using MemoryStream stream = Data(w => { w.Write(length); if (length > 1_048_576) w.Write(new byte[length]); });
		Assert.Throws<InvalidDataException>(() => UnityProjection.Read(stream, Root(Field("m_Name", AssetValueType.String)), false));
	}

	[Theory]
	[InlineData(AssetValueType.Array, -1)]
	[InlineData(AssetValueType.Array, 16_000_001)]
	[InlineData(AssetValueType.ByteArray, -1)]
	[InlineData(AssetValueType.ByteArray, 17)]
	public void InvalidArrayRangesAreRefusedBeforeAnElementCanBeMaterialised(AssetValueType type, int count)
	{
		using MemoryStream stream = Data(w => w.Write(count));
		var array = Field("ignored", type, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		Assert.Throws<InvalidDataException>(() => UnityProjection.Read(stream, Root(array), false));
	}

	[Fact]
	public void ManagedReferenceGraphsCannotBeMaterialisedByTheMetadataIterator()
	{
		using MemoryStream stream = new();
		Assert.Throws<InvalidDataException>(() => UnityProjection.Read(stream, Root(Field("ignored", AssetValueType.ManagedReferencesRegistry)), false));
	}

	[Fact]
	public void EmptyAndWrongTypeFieldsReturnDefaultsAndASkippedTailIsNotRead()
	{
		using MemoryStream stream = Data(w => { w.Write(0); w.Write(0xFFFF_FFFF); });
		UnityProjection result = UnityProjection.Read(stream, Root(Field("m_Name", AssetValueType.String), Field("irrelevant", AssetValueType.ByteArray)), false);
		Assert.Equal(string.Empty, result.Text("m_Name"));
		Assert.Equal(0, result.Number("m_Name"));
		Assert.Equal(0UL, result.Unsigned("missing"));
		Assert.Equal(default, result.Bytes("missing"));
		Assert.Equal(4, stream.Position);
	}

	[Fact]
	public void CanonicalTreeConversionPreservesArrayAlignmentAndRejectsUnboundedSchemaDepth()
	{
		TypeTreeNodeStruct leaf = new("UInt8", "data", 1, 0, []);
		TypeTreeNodeStruct array = new("Array", "Array", 1, TransferMetaFlags.AlignBytes, [new("int", "size", 1, 0, []), leaf]);
		AssetTypeTemplateField converted = UnityProjection.Template(array);
		Assert.True(converted.IsArray); Assert.True(converted.IsAligned); Assert.Equal(AssetValueType.ByteArray, converted.ValueType);
		TypeTreeNodeStruct deep = leaf;
		for (int i = 0; i < 130; i++) deep = new("nested", "nested", 1, 0, [deep]);
		Assert.Throws<InvalidDataException>(() => UnityProjection.Template(deep));
		AssetTypeTemplateField deepTemplate = Field("leaf", AssetValueType.None);
		for (int i = 0; i < 131; i++) deepTemplate = Field("nested", AssetValueType.None, deepTemplate);
		using MemoryStream stream = new();
		Assert.Throws<InvalidDataException>(() => UnityProjection.Read(stream, deepTemplate, false));
	}
	[Fact]
	public void ZeroSizedArrayElementsCannotBurnUnboundedProjectionWork()
	{
		using MemoryStream stream = Data(w => w.Write(16_000_000));
		var array = Field("ignored", AssetValueType.Array, Field("size", AssetValueType.Int32), Field("data", AssetValueType.None));
		Assert.Throws<InvalidDataException>(() => UnityProjection.Read(stream, Root(array), false));
	}

	[Fact]
	public void ObjectSizedReadOnlyViewBoundsEverySeekAndReadAndBorrowsItsSource()
	{
		using MemoryStream input = new([1, 2, 3, 4], false);
		Type type = typeof(UnityProjection).GetNestedType("ProjectionStream", System.Reflection.BindingFlags.NonPublic)!;
		using Stream bounded = (Stream)Activator.CreateInstance(type,
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
			null, [input], null)!;
		Assert.True(bounded.CanRead); Assert.True(bounded.CanSeek); Assert.False(bounded.CanWrite);
		Assert.Equal(4, bounded.Length);
		Assert.Equal(0, bounded.Seek(0, SeekOrigin.Begin));
		Assert.Equal(1, bounded.Seek(1, SeekOrigin.Current));
		Assert.Equal(3, bounded.Seek(-1, SeekOrigin.End));
		Assert.Throws<ArgumentOutOfRangeException>(() => bounded.Seek(0, (SeekOrigin)42));
		Assert.Throws<EndOfStreamException>(() => bounded.Position = -1);
		Assert.Throws<EndOfStreamException>(() => bounded.Position = 5);
		Assert.Throws<EndOfStreamException>(() => bounded.Read(new byte[2], 0, 2));
		Assert.Throws<EndOfStreamException>(() => bounded.Read(new byte[2], 0, -1));
		bounded.Position = 0;
		Assert.Equal(2, bounded.Read(new byte[2].AsSpan()));
		bounded.Flush();
		Assert.Throws<NotSupportedException>(() => bounded.SetLength(0));
		Assert.Throws<NotSupportedException>(() => bounded.Write([1], 0, 1));
		bounded.Dispose(); Assert.True(input.CanRead);
	}

	[Theory]
	[InlineData(AssetValueType.Float)]
	[InlineData(AssetValueType.Double)]
	public void FloatingPointSchemaFieldsKeepTheirRawIeeeBitsAndCannotMasqueradeAsIntegers(AssetValueType type)
	{
		long expected = type == AssetValueType.Float
			? BitConverter.SingleToInt32Bits(1.5f)
			: BitConverter.DoubleToInt64Bits(1.5d);
		using MemoryStream stream = Data(w =>
		{
			if (type == AssetValueType.Float) w.Write(1.5f);
			else w.Write(1.5d);
		});
		UnityProjection projection = UnityProjection.Read(stream, Root(Field("m_Width", type)), false);
		Assert.True(projection.TryBits("m_Width", out long bits));
		Assert.Equal(expected, bits);
		Assert.False(projection.TryNumber("m_Width", out _));
		Assert.False(projection.TryUnsigned("m_Width", out _));
		Assert.Equal(0, projection.Number("m_Width"));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void TypedScalarAccessorsDistinguishMissingIntegralAndBitStorage(bool dictionary)
	{
		AssetTypeTemplateField template = Root(
			Field("m_RD", AssetValueType.None,
				Field("settingsRaw", AssetValueType.UInt32),
				Field("textureRect", AssetValueType.None, Field("x", AssetValueType.Float))));
		if (dictionary) template.Type = "SpriteAtlas";
		using MemoryStream stream = Data(w => { w.Write(0x7FC00001u); w.Write(3.25f); });
		UnityProjection projection = UnityProjection.Read(stream, template, false);
		Assert.True(projection.TryUnsigned("m_RD/settingsRaw", out ulong settings));
		Assert.Equal(0x7FC00001UL, settings);
		Assert.True(projection.TryNumber("m_RD/settingsRaw", out long number));
		Assert.Equal(0x7FC00001L, number);
		Assert.False(projection.TryBits("m_RD/settingsRaw", out _));
		Assert.True(projection.TryBits("m_RD/textureRect/x", out long bits));
		Assert.Equal(BitConverter.SingleToInt32Bits(3.25f), bits);
		Assert.False(projection.TryNumber("m_RD/textureRect/x", out _));
		Assert.False(projection.TryUnsigned("m_RD/textureRect/x", out _));
		Assert.False(projection.TryNumber("m_RD/missing", out _));
		Assert.False(projection.TryBits("m_RD/missing", out _));
		Assert.False(projection.TryUnsigned("m_RD/missing", out _));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Float32GetterRejectsDoubleAndIntegerStorage(bool dictionary)
	{
		AssetTypeTemplateField template = Root(
			Field("m_RD", AssetValueType.None,
				Field("textureRect", AssetValueType.None,
					Field("x", AssetValueType.Float), Field("y", AssetValueType.Double)),
				Field("settingsRaw", AssetValueType.UInt32)));
		if (dictionary) template.Type = "SpriteAtlas";
		using MemoryStream stream = Data(w => { w.Write(0x7FC00123u); w.Write(0x000000003F800000UL); w.Write(7u); });
		UnityProjection projection = UnityProjection.Read(stream, template, false);
		bool TryFloat32(UnityProjection value, string path, out uint bits)
		{
			System.Reflection.MethodInfo? method = typeof(UnityProjection).GetMethod(
				"TryFloat32",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
				null,
				new[] { typeof(string), typeof(uint).MakeByRefType() },
				null);
			Assert.NotNull(method);
			object?[] arguments = [path, 0u];
			bool found = (bool)method!.Invoke(value, arguments)!;
			bits = (uint)arguments[1]!;
			return found;
		}
		Assert.True(TryFloat32(projection, "m_RD/textureRect/x", out uint single));
		Assert.Equal(0x7FC00123u, single);
		Assert.False(TryFloat32(projection, "m_RD/textureRect/y", out _));
		Assert.False(TryFloat32(projection, "m_RD/settingsRaw", out _));
		Assert.False(TryFloat32(projection, "m_RD/missing", out _));
		Assert.True(projection.TryBits("m_RD/textureRect/y", out long wide));
		Assert.Equal(unchecked((long)0x000000003F800000UL), wide);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FastRouteAndMatureIteratorAgreeOnFloatingBitPatternsInBothEndiannesses(bool bigEndian)
	{
		uint[] patterns = [0x7FC00123u, 0xFFC00123u, 0x80000000u, 0xBF800000u];
		const ulong wideBits = 0xFFF0000000000042UL;
		foreach (uint bits in patterns)
		{
			AssetTypeTemplateField template = Root(
				Field("m_RD", AssetValueType.None,
					Field("textureRect", AssetValueType.None,
						Field("x", AssetValueType.Float), Field("y", AssetValueType.Double)),
					Field("downscaleMultiplier", AssetValueType.Float)));
			byte[] body = new byte[16];
			if (bigEndian)
			{
				BinaryPrimitives.WriteUInt32BigEndian(body, bits);
				BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(4), wideBits);
				BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12), 0x3FC00000u);
			}
			else
			{
				BinaryPrimitives.WriteUInt32LittleEndian(body, bits);
				BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4), wideBits);
				BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), 0x3FC00000u);
			}

			UnityProjection route;
			using (MemoryStream input = new(body, false))
				route = UnityProjection.ReadBorrowed(input, 0, body.Length, template, bigEndian, new UnityProjection.State(template));
			Assert.True(route.TryBits("m_RD/textureRect/x", out long xBits));
			Assert.True(route.TryBits("m_RD/textureRect/y", out long yBits));
			Assert.True(route.TryBits("m_RD/downscaleMultiplier", out long downscaleBits));
			Assert.Equal(bits, unchecked((uint)xBits));
			Assert.Equal(unchecked((long)wideBits), yBits);
			Assert.Equal(0x3FC00000u, unchecked((uint)downscaleBits));

			template.Type = "SpriteAtlas";
			using MemoryStream fallbackInput = new(body, false);
			UnityProjection mature = UnityProjection.ReadBorrowed(fallbackInput, 0, body.Length, template, bigEndian, new UnityProjection.State(template));
			Assert.True(mature.TryBits("m_RD/textureRect/x", out long fallbackX));
			Assert.True(mature.TryBits("m_RD/textureRect/y", out long fallbackY));
			Assert.True(mature.TryBits("m_RD/downscaleMultiplier", out long fallbackDownscale));
			Assert.Equal(xBits, fallbackX);
			Assert.Equal(yBits, fallbackY);
			Assert.Equal(downscaleBits, fallbackDownscale);
		}
	}

	[Fact]
	public void AtlasProjectionCapturesOnlyTheFieldsTheSpriteResolverConsumes()
	{
		AssetTypeTemplateField Array(string name, AssetTypeTemplateField element)
			=> Field(name, AssetValueType.Array, Field("size", AssetValueType.Int32), element);
		var key = Field("first", AssetValueType.None,
			Field("first", AssetValueType.None, Enumerable.Range(0, 4).Select(i => Field($"data[{i}]", AssetValueType.Int32)).ToArray()),
			Field("second", AssetValueType.Int64));
		var value = Field("second", AssetValueType.None,
			Field("texture", AssetValueType.None, Field("m_FileID", AssetValueType.Int32), Field("m_PathID", AssetValueType.Int64)),
			Field("textureRect", AssetValueType.None,
				Field("x", AssetValueType.Float), Field("y", AssetValueType.Float),
				Field("width", AssetValueType.Float), Field("height", AssetValueType.Float)),
			Field("textureRectOffset", AssetValueType.None, Field("x", AssetValueType.Float), Field("y", AssetValueType.Float)),
			Field("downscaleMultiplier", AssetValueType.Float),
			Field("settingsRaw", AssetValueType.UInt32),
			Field("packed", AssetValueType.Bool),
			Field("packingMode", AssetValueType.Int32),
			Field("packingRotation", AssetValueType.Int32),
			Field("unrelated", AssetValueType.Int32),
			Field("payload", AssetValueType.ByteArray, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8)),
			Array("unrelatedArray", Field("data", AssetValueType.Int32)));
		var template = Root(Field("m_RenderDataMap", AssetValueType.None,
			Array("Array", Field("data", AssetValueType.None, key, value))));
		template.Type = "SpriteAtlas";
		using MemoryStream stream = Data(w =>
		{
			w.Write(1); w.Write(0x11223344); w.Write(0x55667788); w.Write(0x99AABBCC); w.Write(0xDDEEFF00);
			w.Write(7L); w.Write(1); w.Write(9L);
			w.Write(0f); w.Write(0f); w.Write(2f); w.Write(2f); w.Write(0f); w.Write(0f);
			w.Write(1f); w.Write(3u); w.Write(true); w.Write(1); w.Write(4);
			w.Write(123); w.Write(3); w.Write(new byte[] { 1, 2, 3 }); w.Write(2); w.Write(11); w.Write(12);
		});
		UnityProjection projection = UnityProjection.ReadBorrowed(stream, 0, stream.Length, template, false, new UnityProjection.State(template));
		Assert.DoesNotContain(projection.Keys, path => path.EndsWith("/unrelated", StringComparison.Ordinal));
		Assert.DoesNotContain(projection.Keys, path => path.EndsWith("/payload", StringComparison.Ordinal));
		Assert.DoesNotContain(projection.Keys, path => path.Contains("/unrelatedArray", StringComparison.Ordinal));
		Assert.Contains("m_RenderDataMap/Array/data#0/first/first/data[3]", projection.Keys);
		Assert.Contains("m_RenderDataMap/Array/data#0/first/second", projection.Keys);
		Assert.Contains("m_RenderDataMap/Array/data#0/second/texture/m_PathID", projection.Keys);
		Assert.Contains("m_RenderDataMap/Array/data#0/second/textureRect/width", projection.Keys);
		Assert.Contains("m_RenderDataMap/Array/data#0/second/textureRectOffset/y", projection.Keys);
		Assert.Contains("m_RenderDataMap/Array/data#0/second/downscaleMultiplier", projection.Keys);
		Assert.Contains("m_RenderDataMap/Array/data#0/second/settingsRaw", projection.Keys);
	}

	[Theory]
	[InlineData(0, false, false, true)]
	[InlineData(16, false, false, true)]
	[InlineData(16, true, false, false)]
	[InlineData(24, true, false, false)]
	[InlineData(24, true, true, true)]
	[InlineData(16, true, true, false)]
	[InlineData(4097, false, false, false)]
	public void WholeLayoutValidationBoundsPaddingAndPreservesTheTextureExtension(int tail, bool nonzero, bool texture, bool accepted)
	{
		using MemoryStream input = Data(w => { w.Write(7); w.Write(Enumerable.Repeat(nonzero ? (byte)1 : (byte)0, tail).ToArray()); });
		var template = Root(Field("m_Width", AssetValueType.Int32));
		if (accepted) Assert.Equal(7, UnityProjection.Read(input, template, false, validateLayout: true, textureTail: texture).Number("m_Width"));
		else Assert.Throws<InvalidDataException>(() => UnityProjection.Read(input, template, false, validateLayout: true, textureTail: texture));
	}
	[Theory]
	[InlineData(AssetValueType.UInt32, 2d)]
	[InlineData(AssetValueType.UInt8, 1.5d)]
	public void CapturedScalarArraysUseBoundedAllocationsAndNeverReadTheSkippedPayload(AssetValueType type, double budgetMiB)
	{
		const int count = 256;
		const int payloadLength = 1024 * 1024;
		const int marker = 0x1234567;
		long payloadStart = 0;
		using MemoryStream encoded = Data(w =>
		{
			w.Write(count);
			for (uint i = 0; i < count; i++) { if (type == AssetValueType.UInt8) w.Write((byte)i); else w.Write(i); }
			w.Write(payloadLength); payloadStart = w.BaseStream.Position;
			w.Write(new byte[payloadLength]); w.Write(marker);
		});
		byte[] bytes = encoded.ToArray();
		using (ScalarPayloadGuard control = new(bytes, payloadStart, payloadLength))
		{
			control.Position = payloadStart;
			Assert.Throws<InvalidOperationException>(() => control.ReadByte());
		}
		var array = Field("m_RenderDataKey", AssetValueType.Array, Field("size", AssetValueType.Int32), Field("data", type));
		var payload = Field("ignoredPayload", AssetValueType.ByteArray, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		var template = Root(array, payload, Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		using ScalarPayloadGuard input = new(bytes, payloadStart, payloadLength);
		UnityProjection warm = UnityProjection.Read(input, template, false, state);
		Assert.Equal(count - 1, warm.Number("m_RenderDataKey/data"));
		long allocated = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 128; i++)
		{
			input.Position = 0;
			UnityProjection projection = UnityProjection.Read(input, template, false, state);
			Assert.Equal(count - 1, projection.Number("m_RenderDataKey/data"));
			Assert.Equal(marker, projection.Number("m_Width"));
		}
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.Equal(0, input.PayloadReads);
		output.WriteLine($"Captured scalar allocation: {allocated}");
		Assert.True(allocated < budgetMiB * 1024 * 1024, $"32768 captured scalar fields allocated {allocated} bytes; metadata scalars must not build temporary value trees.");
	}

	private sealed class ScalarPayloadGuard(byte[] bytes, long start, long length) : MemoryStream(bytes, false)
	{
		public int PayloadReads { get; private set; }
		private void Check(int count)
		{
			if (count > 0 && Position < start + length && Position + count > start)
			{
				PayloadReads++;
				throw new InvalidOperationException("Skipped inline payload was read.");
			}
		}
		public override int Read(byte[] buffer, int offset, int count) { Check(count); return base.Read(buffer, offset, count); }
		public override int Read(Span<byte> buffer) { Check(buffer.Length); return base.Read(buffer); }
		public override int ReadByte() { Check(1); return base.ReadByte(); }
	}

	[Fact]
	public void ScalarPeeksPreserveBigEndianSignedValuesAndIteratorAlignment()
	{
		byte[] bytes = new byte[16];
		bytes[0] = 1;
		System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), -1234);
		System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), 0xFEDCBA9876543210UL);
		var boolean = Field("m_Width", AssetValueType.Bool); boolean.IsAligned = true;
		var template = Root(boolean, Field("m_Height", AssetValueType.Int32),
			Field("m_SpriteAtlas", AssetValueType.None, Field("m_PathID", AssetValueType.UInt64)));
		using MemoryStream input = new(bytes, false);
		UnityProjection result = UnityProjection.Read(input, template, true, validateLayout: true);
		Assert.Equal(1, result.Number("m_Width"));
		Assert.Equal(-1234, result.Number("m_Height"));
		Assert.Equal(0xFEDCBA9876543210UL, result.Unsigned("m_SpriteAtlas/m_PathID"));
		Assert.Equal(input.Length, input.Position);
	}

	[Fact]
	public void RepeatedSpriteMetadataDoesNotAllocateDictionaryGrowthArrays()
	{
		byte[] bytes = UnityFixtureBuilder.SerializedSprite("hero", [10, 20, 30, 255], 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0);
		using var fileStream = AssetRipper.IO.Files.Streams.Smart.SmartStream.CreateMemory(bytes);
		using var file = (AssetRipper.IO.Files.SerializedFiles.SerializedFile)AssetRipper.IO.Files.SchemeReader.ReadFile(fileStream, "sprite.assets", "sprite.assets");
		Assert.True(TypeTreeNodeStruct.TryMakeFromTpk((AssetRipper.SourceGenerated.ClassIDType)213, file.Version, out var release, out _));
		var template = UnityProjection.Template(release);
		UnityProjection.State state = new(template);
		using MemoryStream body = new(file.Objects[1].ObjectData, false);
		UnityProjection warm = UnityProjection.Read(body, template, false, state);
		Assert.Equal("hero", warm.Text("m_Name"));
		long allocated = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 4096; i++)
		{
			body.Position = 0;
			UnityProjection projection = UnityProjection.Read(body, template, false, state);
			Assert.Equal("hero", projection.Text("m_Name"));
			Assert.Equal(1, projection.Number("m_RD/texture/m_PathID"));
		}
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		output.WriteLine($"Static Sprite allocation: {allocated}");
		Assert.True(allocated < 8L * 1024 * 1024, $"4096 Sprite metadata projections allocated {allocated} bytes; known static fields should not repeatedly grow metadata dictionaries.");
	}

	[Fact]
	public void CapturedStringsUseTheBoundedMatureReaderWithoutTemporaryValueTrees()
	{
		const string expected = "hé中";
		byte[] name = Encoding.UTF8.GetBytes(expected);
		const int count = 256;
		const int payloadLength = 1024 * 1024;
		const int marker = 1234567;
		long payloadStart = 0;
		using MemoryStream encoded = Data(w =>
		{
			w.Write(count);
			for (int i = 0; i < count; i++)
			{
				w.Write(name.Length); w.Write(name);
				while ((w.BaseStream.Position & 3) != 0) w.Write((byte)0);
			}
			w.Write(payloadLength); payloadStart = w.BaseStream.Position;
			w.Write(new byte[payloadLength]); w.Write(marker);
		});
		var template = Root(Field("m_Name", AssetValueType.Array, Field("size", AssetValueType.Int32), Field("data", AssetValueType.String)),
			Field("ignoredPayload", AssetValueType.ByteArray, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8)), Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		using ScalarPayloadGuard input = new(encoded.ToArray(), payloadStart, payloadLength);
		UnityProjection warm = UnityProjection.Read(input, template, false, state);
		Assert.Equal(expected, warm.Text("m_Name/data"));
		long allocated = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 128; i++)
		{
			input.Position = 0;
			UnityProjection projection = UnityProjection.Read(input, template, false, state);
			Assert.Equal(expected, projection.Text("m_Name/data"));
			Assert.Equal(marker, projection.Number("m_Width"));
		}
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		output.WriteLine($"Captured string allocation: {allocated}");
		Assert.Equal(0, input.PayloadReads);
		Assert.True(allocated < 3L * 1024 * 1024, $"32768 captured UTF8 strings allocated {allocated} bytes; bounded metadata reads must not build temporary value trees.");
	}

	[Fact]
	public void StaticScalarCapturesUseTheirSchemaSlotsWithoutHashingPathsDuringRead()
	{
		const int count = 512;
		const int payloadLength = 1024 * 1024;
		const long marker = -123456789;
		long payloadStart = 0;
		using MemoryStream encoded = Data(w =>
		{
			for (int i = 0; i < count; i++) w.Write((long)i * 17);
			w.Write(payloadLength); payloadStart = w.BaseStream.Position;
			w.Write(new byte[payloadLength]); w.Write(marker);
		});
		var template = Root(
			Field("m_RenderDataKey", AssetValueType.None,
				Enumerable.Range(0, count).Select(i => Field($"data[{i}]", AssetValueType.Int64)).ToArray()),
			Field("ignoredPayload", AssetValueType.ByteArray, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8)),
			Field("m_Width", AssetValueType.Int64));
		UnityProjection.State state = new(template);
		OrdinalHashObserver comparer = new();
		var observedSlots = new System.Collections.Generic.Dictionary<string, UnityProjection.Slot>(state.Slots, comparer);
		// Observe the real dictionary's work without a production counter or changing its key semantics.
		var storage = typeof(UnityProjection.State).GetField("<Slots>k__BackingField",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		Assert.NotNull(storage);
		storage.SetValue(state, observedSlots);
		int control = comparer.HashCalls;
		Assert.True(state.Slots.ContainsKey("m_Width"));
		Assert.True(comparer.HashCalls > control);
		using ScalarPayloadGuard input = new(encoded.ToArray(), payloadStart, payloadLength);
		for (int pass = 0; pass < 2; pass++)
		{
			input.Position = 0;
			int before = comparer.HashCalls;
			UnityProjection projection = UnityProjection.Read(input, template, false, state);
			int captureHashes = comparer.HashCalls - before;
			for (int i = 0; i < count; i++) Assert.Equal((long)i * 17, projection.Number($"m_RenderDataKey/data[{i}]"));
			Assert.Equal(marker, projection.Number("m_Width"));
			Assert.Equal(count + 1, projection.Keys.Count());
			Assert.Equal(0, input.PayloadReads);
			Assert.True(captureHashes == 0,
				$"{count + 1} fixed scalar captures hashed their path keys {captureHashes} times; schema slots must be bound before reading object values.");
		}
	}

	private sealed class OrdinalHashObserver : System.Collections.Generic.IEqualityComparer<string>
	{
		public int HashCalls { get; private set; }
		public bool Equals(string? left, string? right) => StringComparer.Ordinal.Equals(left, right);
		public int GetHashCode(string value)
		{
			HashCalls++;
			return StringComparer.Ordinal.GetHashCode(value);
		}
	}

	[Fact]
	public void DynamicAtlasByteRangesKeepPerEntryNamesAndLastWritesWithoutReadingPayloads()
	{
		var payloads = new System.Collections.Generic.List<UnityProjection.ByteRange>();
		var expected = new System.Collections.Generic.List<UnityProjection.ByteRange>();
		using MemoryStream encoded = Data(w =>
		{
			w.Write(2);
			for (int entry = 0; entry < 2; entry++)
			{
				w.Write(11L + entry);
				for (int duplicate = 0; duplicate < 2; duplicate++)
				{
					int length = entry + duplicate + 2;
					w.Write(length);
					UnityProjection.ByteRange range = new(w.BaseStream.Position, length);
					payloads.Add(range);
					if (duplicate == 1) expected.Add(range);
					w.Write(Enumerable.Repeat((byte)(31 + entry + duplicate), length).ToArray());
				}
			}
		});
		AssetTypeTemplateField BytesField() => Field("texture", AssetValueType.ByteArray,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		var template = Root(Field("m_RenderDataMap", AssetValueType.None,
			Field("Array", AssetValueType.Array, Field("size", AssetValueType.Int32),
				Field("data", AssetValueType.None, Field("first", AssetValueType.Int64),
					Field("second", AssetValueType.None, BytesField(), BytesField())))));
		template.Type = "SpriteAtlas";
		UnityProjection.State state = new(template);
		byte[] bytes = encoded.ToArray();
		using (RangePayloadGuard control = new(bytes, payloads))
		{
			control.Position = payloads[0].Offset;
			Assert.Throws<InvalidOperationException>(() => control.ReadByte());
		}
		using RangePayloadGuard input = new(bytes, payloads);
		UnityProjection first = UnityProjection.Read(input, template, false, state);
		using (MemoryStream empty = Data(w => w.Write(0)))
		{
			UnityProjection absent = UnityProjection.Read(empty, template, false, state);
			Assert.Empty(absent.Keys);
			Assert.Equal(default, absent.Bytes("m_RenderDataMap/Array/data#0/second/texture"));
		}
		// Fail after entering data#0 so the next read must reset dynamic path indices too.
		using (MemoryStream malformed = Data(w => { w.Write(2); w.Write(99L); w.Write(-1); }))
		{
			Assert.Throws<InvalidDataException>(() => UnityProjection.Read(malformed, template, false, state));
			Assert.True(malformed.CanRead);
		}
		input.Position = 0;
		UnityProjection reused = UnityProjection.Read(input, template, false, state);
		string[] keys = ["m_RenderDataMap/Array/data#0/first", "m_RenderDataMap/Array/data#0/second/texture",
			"m_RenderDataMap/Array/data#1/first", "m_RenderDataMap/Array/data#1/second/texture"];
		foreach (UnityProjection projection in new[] { first, reused })
		{
			Assert.Equal(keys, projection.Keys);
			for (int entry = 0; entry < 2; entry++)
			{
				Assert.Equal(11L + entry, projection.Number(keys[entry * 2]));
				string path = keys[entry * 2 + 1];
				Assert.Equal(expected[entry], projection.Bytes(path));
				Assert.Equal(0, projection.Number(path));
				Assert.Equal(string.Empty, projection.Text(path));
			}
			Assert.Equal(default, projection.Bytes("m_RenderDataMap/Array/data#2/second/texture"));
		}
		Assert.Equal(0, input.PayloadReads);
		Assert.Equal(input.Length, input.Position);
	}

	[Fact]
	public void AtlasFieldsNamedDataAreIndexedOnlyWhenTheirParentIsAnArray()
	{
		var template = Root(Field("m_RenderDataMap", AssetValueType.None,
			Field("data", AssetValueType.Int64),
			Field("Array", AssetValueType.Array, Field("size", AssetValueType.Int32),
				Field("data", AssetValueType.None, Field("first", AssetValueType.Int64)))));
		template.Type = "SpriteAtlas";
		UnityProjection.State state = new(template);
		using MemoryStream original = Data(w => { w.Write(11L); w.Write(2); w.Write(21L); w.Write(22L); });
		UnityProjection first = UnityProjection.Read(original, template, false, state, validateLayout: true);
		using MemoryStream changed = Data(w => { w.Write(31L); w.Write(1); w.Write(41L); });
		UnityProjection second = UnityProjection.Read(changed, template, false, state, validateLayout: true);
		Assert.Equal(new[] { "m_RenderDataMap/data", "m_RenderDataMap/Array/data#0/first", "m_RenderDataMap/Array/data#1/first" }, first.Keys);
		Assert.Equal(11, first.Number("m_RenderDataMap/data"));
		Assert.Equal(21, first.Number("m_RenderDataMap/Array/data#0/first"));
		Assert.Equal(22, first.Number("m_RenderDataMap/Array/data#1/first"));
		Assert.Equal(new[] { "m_RenderDataMap/data", "m_RenderDataMap/Array/data#0/first" }, second.Keys);
		Assert.Equal(31, second.Number("m_RenderDataMap/data"));
		Assert.Equal(41, second.Number("m_RenderDataMap/Array/data#0/first"));
		Assert.Equal(0, second.Number("m_RenderDataMap/Array/data#1/first"));
		Assert.Equal(0, first.Number("m_RenderDataMap/data#0"));
		Assert.Equal(0, second.Number("m_RenderDataMap/data#0"));
		Assert.Equal(original.Length, original.Position);
		Assert.Equal(changed.Length, changed.Position);
	}
	private sealed class RangePayloadGuard(byte[] bytes, System.Collections.Generic.IReadOnlyList<UnityProjection.ByteRange> payloads) : MemoryStream(bytes, false)
	{
		public int PayloadReads { get; private set; }
		private void Check(int count)
		{
			foreach (UnityProjection.ByteRange payload in payloads)
			{
				if (count > 0 && Position < payload.Offset + payload.Length && Position + count > payload.Offset)
				{
					PayloadReads++;
					throw new InvalidOperationException("Borrowed byte-range payload was read.");
				}
			}
		}
		public override int Read(byte[] buffer, int offset, int count) { Check(count); return base.Read(buffer, offset, count); }
		public override int Read(Span<byte> buffer) { Check(buffer.Length); return base.Read(buffer); }
		public override int ReadByte() { Check(1); return base.ReadByte(); }
	}

	[Fact]
	public void RepeatedScalarProjectionReusesItsReaderWithinABoundedAllocationBudget()
	{
		const int count = 4096;
		var template = Root(Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		using MemoryStream input = Data(w => w.Write(173));
		Assert.Equal(173, UnityProjection.Read(input, template, false, state, validateLayout: true).Number("m_Width"));
		long totalWidth = 0;
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < count; i++)
		{
			input.Position = 0;
			totalWidth += UnityProjection.Read(input, template, false, state, validateLayout: true).Number("m_Width");
		}
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.Equal(count * 173L, totalWidth);
		output.WriteLine($"{count} scalar projections allocated {allocated} bytes ({allocated / count} per object).");
		Assert.True(allocated <= count * 192L,
			$"{count} scalar projections allocated {allocated} bytes; per-shape reader scratch must be reused while each result keeps independent scalar storage.");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ProjectionScratchReleasesItsBorrowedInputAfterSuccessOrFailure(bool truncated)
	{
		var template = Root(Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		WeakReference source = ReadAndWatchBorrowedInput(template, state, truncated);
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
		Assert.False(source.IsAlive, "A live shape must not retain an input after projection finishes or fails.");
		using MemoryStream next = Data(w => w.Write(257));
		Assert.Equal(257, UnityProjection.Read(next, template, false, state, validateLayout: true).Number("m_Width"));
		Assert.True(next.CanRead);
		GC.KeepAlive(state);
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static WeakReference ReadAndWatchBorrowedInput(AssetTypeTemplateField template, UnityProjection.State state, bool truncated)
	{
		using MemoryStream input = new(truncated ? new byte[] { 7 } : new byte[] { 7, 0, 0, 0 }, false);
		WeakReference watched = new(input);
		if (truncated) Assert.Throws<EndOfStreamException>(() => UnityProjection.Read(input, template, false, state, validateLayout: true));
		else Assert.Equal(7, UnityProjection.Read(input, template, false, state, validateLayout: true).Number("m_Width"));
		Assert.True(input.CanRead, "Projection only borrows its input; the caller owns disposal.");
		return watched;
	}

	[Fact]
	public void ReusedReaderChangesInputAndEndianWithoutChangingRetainedValuesOrReadingPayloads()
	{
		var template = Root(Field("m_Name", AssetValueType.String), Field("m_Width", AssetValueType.Int32),
			Field("image data", AssetValueType.ByteArray, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8)),
			Field("m_Height", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		UnityProjection[] retained = new UnityProjection[3];
		string[] names = ["hé中", "x", "last"];
		for (int pass = 0; pass < retained.Length; pass++)
		{
			bool bigEndian = pass == 1;
			long payloadStart = 0;
			byte[] name = Encoding.UTF8.GetBytes(names[pass]);
			using MemoryStream encoded = Data(w =>
			{
				void Integer(int value) => w.Write(bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value) : value);
				Integer(name.Length); w.Write(name);
				while ((w.BaseStream.Position & 3) != 0) w.Write((byte)0);
				Integer(100 + pass);
				Integer(7); payloadStart = w.BaseStream.Position; w.Write(new byte[7]);
				Integer(200 + pass);
			});
			using ScalarPayloadGuard input = new(encoded.ToArray(), payloadStart, 7);
			retained[pass] = UnityProjection.Read(input, template, bigEndian, state, validateLayout: true);
			Assert.Equal(0, input.PayloadReads);
			Assert.True(input.CanRead);
			Assert.Equal(input.Length, input.Position);
			Assert.Equal(new UnityProjection.ByteRange(payloadStart, 7), retained[pass].Bytes("image data"));
		}
		for (int pass = retained.Length - 1; pass >= 0; pass--)
		{
			Assert.Equal(names[pass], retained[pass].Text("m_Name"));
			Assert.Equal(100 + pass, retained[pass].Number("m_Width"));
			Assert.Equal(200 + pass, retained[pass].Number("m_Height"));
			Assert.Equal(7, retained[pass].Bytes("image data").Length);
		}
	}
	[Fact]
	public void StaticRepeatedMetadataKeepsSchemaKeysAndLastWritesWithoutAllocatingUnusedIndexedPaths()
	{
		const int count = 2048, payloadLength = 1024 * 1024;
		long payloadStart = 0;
		using MemoryStream encoded = Data(w =>
		{
			w.Write(count);
			for (int i = 0; i < count; i++) w.Write((long)i);
			w.Write(payloadLength); payloadStart = w.BaseStream.Position; w.Write(new byte[payloadLength]); w.Write(17L);
		});
		var template = Root(Field("m_RenderDataMap", AssetValueType.None,
			Field("Array", AssetValueType.Array, Field("size", AssetValueType.Int32), Field("data", AssetValueType.Int64))),
			Field("ignoredPayload", AssetValueType.ByteArray, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8)),
			Field("m_Width", AssetValueType.Int64));
		UnityProjection.State state = new(template);
		byte[] bytes = encoded.ToArray();
		using ScalarPayloadGuard input = new(bytes, payloadStart, payloadLength);
		UnityProjection warm = UnityProjection.Read(input, template, false, state, validateLayout: true);
		Assert.Equal(count - 1, warm.Number("m_RenderDataMap/Array/data"));
		input.Position = 0;
		long before = GC.GetAllocatedBytesForCurrentThread();
		UnityProjection first = UnityProjection.Read(input, template, false, state, validateLayout: true);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4 + (count - 1) * 8), 7777L);
		input.Position = 0;
		UnityProjection second = UnityProjection.Read(input, template, false, state, validateLayout: true);
		Assert.Equal(new[] { "m_RenderDataMap/Array/data", "m_Width" }, first.Keys);
		Assert.Equal(first.Keys, second.Keys);
		Assert.Equal(count - 1, first.Number("m_RenderDataMap/Array/data"));
		Assert.Equal(7777, second.Number("m_RenderDataMap/Array/data"));
		Assert.Equal(17, first.Number("m_Width"));
		Assert.Equal(0, first.Number("m_RenderDataMap/Array/data#0"));
		Assert.Equal(0, input.PayloadReads);
		Assert.Equal(input.Length, input.Position);
		output.WriteLine($"{count} static repeated captures allocated {allocated} bytes.");
		Assert.True(allocated <= count * 48L,
			$"{count} static repeated captures allocated {allocated} bytes; repeated schema values must retain last writes without building unused per-element indexed path names.");
	}
	[Theory]
	[InlineData(32, 256)]
	[InlineData(1024, 2400)]
	public void RetainedCapturedNamesAvoidTemporaryUtf8ArraysWithinABoundedAllocationBudget(int length, int perObjectBudget)
	{
		const int count = 4096;
		string expected = new('n', length);
		using MemoryStream encoded = Data(w => { w.Write(length); w.Write(Encoding.UTF8.GetBytes(expected)); });
		var template = Root(Field("m_Name", AssetValueType.String));
		UnityProjection.State state = new(template);
		Assert.Equal(expected, UnityProjection.Read(encoded, template, false, state).Text("m_Name"));
		UnityProjection[] retained = new UnityProjection[count];
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < count; i++)
		{
			encoded.Position = 0;
			retained[i] = UnityProjection.Read(encoded, template, false, state);
		}
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.All(retained, projection => Assert.Equal(expected, projection.Text("m_Name")));
		output.WriteLine($"{count} captured names of {length} UTF8 bytes allocated {allocated} bytes ({allocated / count} per object).");
		Assert.True(allocated <= count * (long)perObjectBudget,
			$"{count} captured names of {length} UTF8 bytes allocated {allocated} bytes; decoding metadata names must not allocate a temporary byte array for every retained string.");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(256)]
	[InlineData(257)]
	[InlineData(1_048_576)]
	public void CapturedTextBoundariesKeepAlignmentPayloadGuardsAndIndependentValues(int length)
	{
		string expected = new('x', length);
		long payloadStart = 0;
		using MemoryStream encoded = Data(w =>
		{
			w.Write(length); w.Write(Encoding.UTF8.GetBytes(expected));
			while ((w.BaseStream.Position & 3) != 0) w.Write((byte)0);
			w.Write(1024); payloadStart = w.BaseStream.Position; w.Write(new byte[1024]); w.Write(713);
		});
		var template = Root(Field("m_Name", AssetValueType.String),
			Field("ignoredPayload", AssetValueType.ByteArray, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8)),
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		using ScalarPayloadGuard input = new(encoded.ToArray(), payloadStart, 1024);
		UnityProjection first = UnityProjection.Read(input, template, false, state, validateLayout: true);
		using MemoryStream empty = Data(w => { w.Write(0); w.Write(0); w.Write(911); });
		UnityProjection next = UnityProjection.Read(empty, template, false, state, validateLayout: true);
		Assert.Equal(expected, first.Text("m_Name"));
		Assert.Equal(string.Empty, next.Text("m_Name"));
		Assert.Equal(713, first.Number("m_Width"));
		Assert.Equal(911, next.Number("m_Width"));
		Assert.Equal(0, input.PayloadReads);
		Assert.Equal(input.Length, input.Position);
	}

	[Theory]
	[InlineData(5, 3)]
	[InlineData(257, 255)]
	public void CapturedTextKeepsMatureEarlyEofPrefixAndMalformedUtf8Replacement(int declaredLength, int available)
	{
		byte[] payload = Enumerable.Repeat((byte)'a', available).ToArray();
		payload[^1] = 0xE2;
		using MemoryStream encoded = Data(w => { w.Write(declaredLength); w.Write(payload); });
		using PartialTextStream control = new(encoded.ToArray(), 4L + declaredLength);
		using AssetsFileReader mature = new(control);
		Assert.Equal(declaredLength, mature.ReadInt32());
		string expected = mature.ReadStringLength(declaredLength);
		Assert.EndsWith("\uFFFD", expected);
		using PartialTextStream input = new(encoded.ToArray(), 4L + declaredLength);
		UnityProjection result = UnityProjection.Read(input, Root(Field("m_Name", AssetValueType.String)), false);
		Assert.Equal(expected, result.Text("m_Name"));
		Assert.Equal(4 + available, input.Position);
		Assert.True(input.CanRead);
	}

	[Fact]
	public void CapturedTextCombinesShortReadsWithoutChangingMalformedUtf8Replacement()
	{
		byte[] payload = [0x68, 0xC3, 0xA9, 0xE4, 0xB8, 0xAD, 0xE2, 0x28, 0xA1];
		using MemoryStream encoded = Data(w => { w.Write(payload.Length); w.Write(payload); });
		using PartialTextStream input = new(encoded.ToArray(), encoded.Length);
		Assert.Equal("hé中\uFFFD(\uFFFD", UnityProjection.Read(input, Root(Field("m_Name", AssetValueType.String)), false).Text("m_Name"));
		Assert.Equal(encoded.Length, input.Position);
	}

	[Fact]
	public void FailedOversizedCapturedTextReturnsItsPooledBufferAndReusableStateRecovers()
	{
		var template = Root(Field("m_Name", AssetValueType.String));
		UnityProjection.State state = new(template);
		using MemoryStream encoded = Data(w => { w.Write(1024); w.Write(new byte[1024]); });
		using PartialTextStream input = new(encoded.ToArray(), encoded.Length) { Failure = new IOException("UTF8 read sentinel") };
		using PoolObserver observer = new();
		Assert.Same(input.Failure, Assert.Throws<IOException>(() => UnityProjection.Read(input, template, false, state)));
		Assert.True(input.CanRead);
		Assert.NotEmpty(observer.Rented);
		Assert.All(observer.Rented, id => Assert.Contains(id, observer.Returned));
		using MemoryStream next = Data(w => { w.Write(4); w.Write(Encoding.UTF8.GetBytes("next")); });
		Assert.Equal("next", UnityProjection.Read(next, template, false, state).Text("m_Name"));
		observer.Dispose();
		int observed = observer.Rented.Count;
		byte[] control = System.Buffers.ArrayPool<byte>.Shared.Rent(1024);
		System.Buffers.ArrayPool<byte>.Shared.Return(control);
		Assert.Equal(observed, observer.Rented.Count);
	}

	private sealed class PartialTextStream(byte[] bytes, long declaredLength) : MemoryStream(bytes, false)
	{
		public IOException? Failure { get; init; }
		public override long Length => declaredLength;
		public override int Read(Span<byte> buffer)
		{
			if (Position >= 4 && Failure is { } error) throw error;
			if (buffer.IsEmpty) return 0;
			int next = base.ReadByte();
			if (next < 0) return 0;
			buffer[0] = (byte)next;
			return 1;
		}
		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
	}

	private sealed class PoolObserver : System.Diagnostics.Tracing.EventListener
	{
		private readonly int thread = Environment.CurrentManagedThreadId;
		public System.Collections.Generic.List<int> Rented { get; } = new(8);
		public System.Collections.Generic.List<int> Returned { get; } = new(8);
		protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
		{
			if (source.Name == "System.Buffers.ArrayPoolEventSource") EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose);
		}
		protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs data)
		{
			if (Environment.CurrentManagedThreadId != thread || data.Payload is not { Count: >= 2 } payload || payload[0] is not int id || payload[1] is not int length || length != 1024) return;
			if (data.EventName == "BufferRented" && Rented.Count < 8) Rented.Add(id);
			if (data.EventName == "BufferReturned" && Returned.Count < 8) Returned.Add(id);
		}
	}
	[Fact]
	public void RetainedStaticMetadataUsesCompactIndependentValues()
	{
		var template = Root(Field("m_Name", AssetValueType.String), Field("m_Width", AssetValueType.Int64),
			Field("m_Height", AssetValueType.Int64), Field("m_Type", AssetValueType.Int64),
			Field("m_RenderDataKey", AssetValueType.None, Enumerable.Range(0, 4).Select(i => Field($"data[{i}]", AssetValueType.Int64)).ToArray()),
			Field("m_SpriteAtlas", AssetValueType.None, Field("m_FileID", AssetValueType.Int64), Field("m_PathID", AssetValueType.Int64)));
		using MemoryStream encoded = Data(w => { w.Write(4); w.Write(Encoding.UTF8.GetBytes("hero")); for (long i = 0; i < 9; i++) w.Write(i); });
		byte[] bytes = encoded.ToArray();
		using MemoryStream input = new(bytes, false);
		UnityProjection.State state = new(template);
		Assert.Equal("hero", UnityProjection.Read(input, template, false, state).Text("m_Name"));
		UnityProjection[] retained = new UnityProjection[4096];
		long allocated = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < retained.Length; i++)
		{
			System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), i);
			input.Position = 0;
			retained[i] = UnityProjection.Read(input, template, false, state);
		}
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		for (int i = retained.Length - 1; i >= 0; i--)
		{
			Assert.Equal(i, retained[i].Number("m_Width"));
			Assert.Equal("hero", retained[i].Text("m_Name"));
			Assert.Equal(8, retained[i].Number("m_SpriteAtlas/m_PathID"));
			Assert.Equal(10, retained[i].Keys.Count());
		}
		output.WriteLine($"4096 retained static projections allocated {allocated} bytes.");
		Assert.True(allocated < 3L * 1024 * 1024, $"4096 retained static projections allocated {allocated} bytes; metadata must share its schema and avoid per-field boxing.");
		GC.KeepAlive(retained);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DuplicateStaticPathsWithDifferentKindsKeepTheLastValue(bool stringLast)
	{
		var number = Field("m_Name", AssetValueType.Int64);
		var text = Field("m_Name", AssetValueType.String);
		var template = stringLast ? Root(number, text) : Root(text, number);
		using MemoryStream input = Data(w => { if (stringLast) w.Write(19L); w.Write(0); if (!stringLast) w.Write(19L); });
		UnityProjection projection = UnityProjection.Read(input, template, false);
		Assert.Equal(stringLast ? 0 : 19, projection.Number("m_Name"));
		Assert.Equal(string.Empty, projection.Text("m_Name"));
		Assert.Equal(default, projection.Bytes("m_Name"));
		Assert.Equal(new[] { "m_Name" }, projection.Keys);
	}

	[Fact]
	public void StaticKeysOnlyIncludeVisitedPathsAndKeepFirstInsertionOrder()
	{
		var array = Field("m_RenderDataKey", AssetValueType.Array, Field("size", AssetValueType.Int32), Field("data", AssetValueType.Int64));
		var template = Root(array, Field("m_Width", AssetValueType.Int64), Field("m_Width", AssetValueType.Int64), Field("m_Name", AssetValueType.String));
		UnityProjection.State state = new(template);
		using MemoryStream empty = Data(w => { w.Write(0); w.Write(1L); w.Write(2L); w.Write(0); });
		UnityProjection first = UnityProjection.Read(empty, template, false, state);
		Assert.Equal(new[] { "m_Width", "m_Name" }, first.Keys);
		Assert.Equal(2, first.Number("m_Width"));
		using MemoryStream populated = Data(w => { w.Write(2); w.Write(7L); w.Write(8L); w.Write(3L); w.Write(4L); w.Write(0); });
		UnityProjection second = UnityProjection.Read(populated, template, false, state);
		Assert.Equal(new[] { "m_RenderDataKey/data", "m_Width", "m_Name" }, second.Keys);
		Assert.Equal(8, second.Number("m_RenderDataKey/data"));
		Assert.Equal(new[] { "m_Width", "m_Name" }, first.Keys);
	}

	[Fact]
	public void RepeatedByteRangesKeepTheLastBorrowedRangeAndTypedDefaults()
	{
		var array = Field("m_RenderDataKey", AssetValueType.Array, Field("size", AssetValueType.Int32),
			Field("data", AssetValueType.ByteArray, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8)));
		using MemoryStream input = Data(w => { w.Write(2); w.Write(0); w.Write(3); w.Write(new byte[] { 1, 2, 3 }); w.Write(7L); });
		UnityProjection projection = UnityProjection.Read(input, Root(array, Field("m_Width", AssetValueType.Int64)), false);
		Assert.Equal(new UnityProjection.ByteRange(12, 3), projection.Bytes("m_RenderDataKey/data"));
		Assert.Equal(string.Empty, projection.Text("m_RenderDataKey/data"));
		Assert.Equal(0, projection.Number("m_RenderDataKey/data"));
		Assert.Equal(string.Empty, projection.Text("missing"));
		Assert.Equal(0, projection.Number("missing"));
		Assert.Equal(default, projection.Bytes("missing"));
		Assert.Equal(7, projection.Number("m_Width"));
		Assert.Equal(new[] { "m_RenderDataKey/data", "m_Width" }, projection.Keys);
	}
	[Fact]
	public void BorrowedSubrangeChecksBoundsAndEveryReadFormWithoutOwningTheSource()
	{
		using MemoryStream input = new([9, 1, 2, 3, 4, 9], false);
		UnityProjection.State state = new(Root(Field("m_Width", AssetValueType.Int32)));
		Stream bounded = state.Reader.BaseStream;
		var attach = bounded.GetType().GetMethod("AttachRange")!;
		foreach ((long offset, long count) in new[] { (-1L, 1L), (0L, -1L), (7L, 0L), (5L, 2L) })
			Assert.IsType<ArgumentOutOfRangeException>(Assert.Throws<System.Reflection.TargetInvocationException>(() => attach.Invoke(bounded, [input, offset, count])).InnerException);
		attach.Invoke(bounded, [input, 1L, 4L]);
		Assert.Equal(4, bounded.Length); Assert.Equal(0, bounded.Position);
		byte[] first = new byte[2]; Assert.Equal(2, bounded.Read(first, 0, 2)); Assert.Equal(new byte[] { 1, 2 }, first);
		Assert.Equal(3, bounded.ReadByte()); Assert.Equal(3, bounded.Position);
		Assert.Equal(3, bounded.Seek(-1, SeekOrigin.End)); Assert.Equal(4, bounded.ReadByte());
		Assert.Throws<EndOfStreamException>(() => bounded.ReadByte());
		bounded.Position = 0; Assert.Equal(4, bounded.Read(new byte[4].AsSpan()));
		bounded.GetType().GetMethod("Attach")!.Invoke(bounded, [input]); input.Position = 1;
		Assert.Equal(1, bounded.Read(new byte[1], 0, 1)); Assert.Equal(2, bounded.ReadByte());
		using MemoryStream shrunk = new(); shrunk.Write([9, 1, 2, 3, 4, 9]);
		attach.Invoke(bounded, [shrunk, 1L, 4L]); shrunk.SetLength(0);
		Assert.Equal(-1, bounded.ReadByte()); Assert.Equal(0, bounded.Position);

		Assert.Equal(3, input.Position); bounded.Dispose(); Assert.True(input.CanRead);
	}

	[Fact]
	public void BorrowedDynamicAtlasScratchReplacesOldDictionaryCapturesOnEveryRead()
	{
		var template = Root(Field("m_RenderDataMap", AssetValueType.None, Field("Array", AssetValueType.Array,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.None,
				Field("first", AssetValueType.Int64), Field("second", AssetValueType.None, Field("texture", AssetValueType.None,
					Field("m_FileID", AssetValueType.Int32), Field("m_PathID", AssetValueType.Int64)))))));
		template.Type = "SpriteAtlas";
		UnityProjection.State state = new(template);
		using MemoryStream first = Data(w => { w.Write(1); w.Write(7L); w.Write(0); w.Write(9L); });
		UnityProjection scratch = UnityProjection.ReadBorrowed(first, 0, first.Length, template, false, state);
		Assert.Contains(scratch.Keys, key => key.Contains("data#0"));
		using MemoryStream empty = Data(w => w.Write(0));
		Assert.Same(scratch, UnityProjection.ReadBorrowed(empty, 0, empty.Length, template, false, state)); Assert.Empty(scratch.Keys);
	}

}
