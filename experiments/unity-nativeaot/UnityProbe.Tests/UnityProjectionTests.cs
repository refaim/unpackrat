using System;
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
	public void IntegerCatalogueMetadataCannotSilentlyAcceptFloatingPointSchemaFields(AssetValueType type)
	{
		using MemoryStream stream = Data(w => w.Write(1.5d));
		Assert.Throws<InvalidDataException>(() => UnityProjection.Read(stream, Root(Field("m_Width", type)), false));
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
}
