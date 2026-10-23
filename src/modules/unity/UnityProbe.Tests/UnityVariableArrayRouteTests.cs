using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityVariableArrayRouteTests
{
	private const int FieldBudget = 16_000_000;
	private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
	private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void NonEmptyStringArrayKeepsRouteAndConsumesNameOnce(bool bigEndian)
	{
		AssetTypeTemplateField template = Root(
			Field("m_Name", AssetValueType.String),
			ArrayField("tags", Field("data", AssetValueType.String)),
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State routeState = new(template);
		UnityProjection.State matureState = new(template);
		Assert.NotNull(routeState.Route);

		List<byte> body = [];
		AddString(body, bigEndian, "hero");
		AddScalar(body, bigEndian, AssetValueType.Int32, 2);
		AddString(body, bigEndian, "alpha");
		AddString(body, bigEndian, "beta");
		AddScalar(body, bigEndian, AssetValueType.Int32, 77);
		const int offset = 9;
		byte[] source = Embed(body.ToArray(), offset, 6);
		using CountingStream routeInput = new(source);
		using CountingStream matureInput = new(source);
		Outcome route = Run(template, routeState, routeInput, offset, body.Count, route: true, bigEndian: bigEndian);
		Outcome mature = Run(template, matureState, matureInput, offset, body.Count, route: false, bigEndian: bigEndian);

		Assert.Null(route.Failure);
		Assert.Null(mature.Failure);
		UnityProjection routeResult = Assert.IsType<UnityProjection>(route.Result);
		UnityProjection matureResult = Assert.IsType<UnityProjection>(mature.Result);
		Assert.Equal(77, routeResult.Number("m_Width"));
		Assert.Equal(77, matureResult.Number("m_Width"));
		Assert.Equal("hero", Encoding.UTF8.GetString(routeResult.RawName));
		Assert.Equal(Encoding.UTF8.GetString(matureResult.RawName), Encoding.UTF8.GetString(routeResult.RawName));
		Assert.Equal(mature.Logical, route.Logical);
		Assert.Equal(mature.Physical, route.Physical);
		Assert.Equal(4, CoveredBytes(routeInput.Reads, offset, 4));
		Assert.Equal(4, CoveredBytes(routeInput.Reads, offset + 8, 4));
		Assert.Equal(4, CoveredBytes(routeInput.Reads, offset + 4, 4));
		Assert.Equal(4, CoveredBytes(matureInput.Reads, offset + 4, 4));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void StructArraySkipsStringAndBytePayloadsAcrossAlignment(bool bigEndian)
	{
		AssetTypeTemplateField inner = Field("inner", AssetValueType.None,
			Field("value", AssetValueType.UInt16));
		inner.IsAligned = true;
		AssetTypeTemplateField blob = Field("blob", AssetValueType.ByteArray,
			Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		blob.IsAligned = true;
		AssetTypeTemplateField record = Field("record", AssetValueType.None,
			Field("tag", AssetValueType.UInt8),
			Field("path", AssetValueType.String),
			blob,
			inner);
		record.IsAligned = true;
		AssetTypeTemplateField entries = ArrayField("entries", record);
		entries.IsAligned = true;
		AssetTypeTemplateField template = Root(
			Field("m_Name", AssetValueType.String),
			entries,
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State routeState = new(template);
		UnityProjection.State matureState = new(template);
		Assert.NotNull(routeState.Route);

		List<byte> body = [];
		AddString(body, bigEndian, "hero");
		AddScalar(body, bigEndian, AssetValueType.Int32, 2);
		List<(int Offset, int Length)> payloads = [];
		for (int index = 0; index < 2; index++)
		{
			AddScalar(body, bigEndian, AssetValueType.UInt8, index + 1);
			string path = index == 0 ? "alpha" : "beta";
			payloads.Add((AddString(body, bigEndian, path), path.Length));
			byte[] bytes = index == 0 ? new byte[] { 1, 2, 3 } : new byte[] { 4, 5 };
			payloads.Add((AddBytes(body, bigEndian, bytes), bytes.Length));
			Align4(body);
			AddScalar(body, bigEndian, AssetValueType.UInt16, index + 10);
			Align4(body);
			Align4(body);
		}
		Align4(body);
		AddScalar(body, bigEndian, AssetValueType.Int32, 88);
		const int offset = 11;
		byte[] source = Embed(body.ToArray(), offset, 4);
		using CountingStream routeInput = new(source);
		using CountingStream matureInput = new(source);
		Outcome route = Run(template, routeState, routeInput, offset, body.Count, route: true, bigEndian: bigEndian);
		Outcome mature = Run(template, matureState, matureInput, offset, body.Count, route: false, bigEndian: bigEndian);

		Assert.Null(route.Failure);
		Assert.Null(mature.Failure);
		UnityProjection routeResult = Assert.IsType<UnityProjection>(route.Result);
		UnityProjection matureResult = Assert.IsType<UnityProjection>(mature.Result);
		Assert.Equal(88, routeResult.Number("m_Width"));
		Assert.Equal(88, matureResult.Number("m_Width"));
		Assert.Equal(mature.Logical, route.Logical);
		Assert.Equal(mature.Physical, route.Physical);
		Assert.Equal(4, CoveredBytes(routeInput.Reads, offset + 4, 4));
		foreach ((int payloadOffset, int length) in payloads)
			Assert.Equal(0, CoveredBytes(routeInput.Reads, offset + payloadOffset, length));
	}

	[Theory]
	[InlineData(-1, false, false)]
	[InlineData(-1, false, true)]
	[InlineData(16_000_001, false, false)]
	[InlineData(16_000_001, false, true)]
	[InlineData(0, true, false)]
	[InlineData(0, true, true)]
	public void MalformedNestedCountsMatchMatureFromTheCountPrefix(int nestedCount, bool truncated, bool bigEndian)
	{
		AssetTypeTemplateField inner = ArrayField("inner", Field("data", AssetValueType.String));
		AssetTypeTemplateField record = Field("record", AssetValueType.None,
			Field("tag", AssetValueType.UInt8), inner);
		AssetTypeTemplateField template = Root(
			Field("m_Name", AssetValueType.String),
			ArrayField("outer", record),
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State routeState = new(template);
		UnityProjection.State matureState = new(template);
		Assert.NotNull(routeState.Route);

		List<byte> body = [];
		AddString(body, bigEndian, "hero");
		AddScalar(body, bigEndian, AssetValueType.Int32, 1);
		AddScalar(body, bigEndian, AssetValueType.UInt8, 7);
		if (truncated) body.AddRange([1, 0]);
		else AddScalar(body, bigEndian, AssetValueType.Int32, nestedCount);
		AddScalar(body, bigEndian, AssetValueType.Int32, 9);
		const int offset = 9;
		byte[] source = Embed(body.ToArray(), offset, 4);
		using CountingStream routeInput = new(source);
		using CountingStream matureInput = new(source);
		Outcome route = Run(template, routeState, routeInput, offset, body.Count, route: true, bigEndian: bigEndian);
		Outcome mature = Run(template, matureState, matureInput, offset, body.Count, route: false, bigEndian: bigEndian);

		Assert.NotNull(route.Failure);
		Assert.NotNull(mature.Failure);
		Assert.Equal(mature.Failure.GetType(), route.Failure.GetType());
		Assert.Equal(mature.Logical, route.Logical);
		Assert.Equal(mature.Physical, route.Physical);
		Assert.Equal(4, CoveredBytes(routeInput.Reads, offset + 4, 4));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PrimitiveVariableArraySkipsBulkPayload(bool bigEndian)
	{
		AssetTypeTemplateField values = ArrayField("values", Field("data", AssetValueType.UInt16));
		values.IsAligned = true;
		AssetTypeTemplateField template = Root(
			Field("m_Name", AssetValueType.String),
			values,
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State routeState = new(template);
		UnityProjection.State matureState = new(template);
		Assert.NotNull(routeState.Route);

		List<byte> body = [];
		AddString(body, bigEndian, "hero");
		AddScalar(body, bigEndian, AssetValueType.Int32, 3);
		int payload = body.Count;
		for (int index = 0; index < 3; index++)
			AddScalar(body, bigEndian, AssetValueType.UInt16, index + 1);
		Align4(body);
		AddScalar(body, bigEndian, AssetValueType.Int32, 99);
		const int offset = 5;
		byte[] source = Embed(body.ToArray(), offset, 3);
		using CountingStream routeInput = new(source);
		using CountingStream matureInput = new(source);
		Outcome route = Run(template, routeState, routeInput, offset, body.Count, route: true, bigEndian: bigEndian);
		Outcome mature = Run(template, matureState, matureInput, offset, body.Count, route: false, bigEndian: bigEndian);

		Assert.Null(route.Failure);
		Assert.Null(mature.Failure);
		UnityProjection routeResult = Assert.IsType<UnityProjection>(route.Result);
		UnityProjection matureResult = Assert.IsType<UnityProjection>(mature.Result);
		Assert.Equal(99, routeResult.Number("m_Width"));
		Assert.Equal(99, matureResult.Number("m_Width"));
		Assert.Equal(mature.Logical, route.Logical);
		Assert.Equal(mature.Physical, route.Physical);
		Assert.Equal(0, CoveredBytes(routeInput.Reads, offset + payload, 6));
		Assert.Equal(4, CoveredBytes(routeInput.Reads, offset + 4, 4));
	}

	[Fact]
	public void PrimitiveVariableArrayUsesTheSharedBudgetAfterFastFixedSkip()
	{
		AssetTypeTemplateField template = Root(
			ArrayField("ignored", Field("data", AssetValueType.None)),
			ArrayField("values", Field("data", AssetValueType.UInt8)),
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State state = new(template);
		Assert.NotNull(state.Route);

		List<byte> body = [];
		AddScalar(body, false, AssetValueType.Int32, FieldBudget - 2);
		AddScalar(body, false, AssetValueType.Int32, 4);
		body.AddRange([1, 2, 3, 4]);
		AddScalar(body, false, AssetValueType.Int32, 9);
		const int offset = 7;
		byte[] source = Embed(body.ToArray(), offset, 3);
		using CountingStream input = new(source);
		InvalidDataException failure = Assert.Throws<InvalidDataException>(() =>
			UnityProjection.ReadBorrowed(input, offset, body.Count, template, false, state));

		Assert.Equal("Unity projection exceeded its field budget.", failure.Message);
		Assert.Equal(offset + 8, input.Position);
		Assert.Equal(0, CoveredBytes(input.Reads, offset + 8, 4));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(99)]
	public void InvalidVariableStringRangeMatchesMature(int declaredLength)
	{
		AssetTypeTemplateField template = Root(
			Field("m_Name", AssetValueType.String),
			ArrayField("tags", Field("data", AssetValueType.String)),
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State routeState = new(template);
		UnityProjection.State matureState = new(template);
		Assert.NotNull(routeState.Route);

		List<byte> body = [];
		AddString(body, false, "hero");
		AddScalar(body, false, AssetValueType.Int32, 1);
		AddScalar(body, false, AssetValueType.Int32, declaredLength);
		AddScalar(body, false, AssetValueType.Int32, 7);
		const int offset = 4;
		byte[] source = Embed(body.ToArray(), offset, 3);
		using CountingStream routeInput = new(source);
		using CountingStream matureInput = new(source);
		Outcome route = Run(template, routeState, routeInput, offset, body.Count, route: true, bigEndian: false);
		Outcome mature = Run(template, matureState, matureInput, offset, body.Count, route: false, bigEndian: false);

		Assert.NotNull(route.Failure);
		Assert.NotNull(mature.Failure);
		Assert.Equal(mature.Failure.GetType(), route.Failure.GetType());
		Assert.Equal(mature.Logical, route.Logical);
		Assert.Equal(mature.Physical, route.Physical);
		Assert.Equal(4, CoveredBytes(routeInput.Reads, offset + 4, 4));
	}

	[Fact]
	public void UnsafeVariableElementKeepsTheWholeObjectMatureFallback()
	{
		AssetTypeTemplateField element = Field("data", AssetValueType.None,
			Field("unsupported", AssetValueType.ManagedReferencesRegistry));
		AssetTypeTemplateField template = Root(
			Field("m_Name", AssetValueType.String),
			ArrayField("refs", element),
			Field("m_Width", AssetValueType.Int32));
		UnityProjection.State routeState = new(template);
		UnityProjection.State matureState = new(template);
		Assert.NotNull(routeState.Route);

		List<byte> body = [];
		AddString(body, false, "hero");
		AddScalar(body, false, AssetValueType.Int32, 1);
		AddScalar(body, false, AssetValueType.Int32, 7);
		const int offset = 3;
		byte[] source = Embed(body.ToArray(), offset, 2);
		using CountingStream routeInput = new(source);
		using CountingStream matureInput = new(source);
		Outcome route = Run(template, routeState, routeInput, offset, body.Count, route: true, bigEndian: false);
		Outcome mature = Run(template, matureState, matureInput, offset, body.Count, route: false, bigEndian: false);

		Assert.NotNull(route.Failure);
		Assert.NotNull(mature.Failure);
		Assert.Equal(mature.Failure.GetType(), route.Failure.GetType());
		Assert.Equal(mature.Failure.Message, route.Failure.Message);
		Assert.Equal(mature.Logical, route.Logical);
		Assert.Equal(mature.Physical, route.Physical);
		Assert.Equal(12, CoveredBytes(routeInput.Reads, offset, 4));
		Assert.Equal(8, CoveredBytes(matureInput.Reads, offset, 4));
		Assert.Equal(4, CoveredBytes(routeInput.Reads, offset + 8, 4));
		Assert.Equal(8, CoveredBytes(routeInput.Reads, offset + 4, 4));
		Assert.Equal(4, CoveredBytes(matureInput.Reads, offset + 4, 4));
	}

	private sealed record Outcome(Exception? Failure, long Logical, long Physical, UnityProjection? Result);

	private static Outcome Run(AssetTypeTemplateField template, UnityProjection.State state, Stream input,
		long offset, long length, bool route, bool bigEndian)
	{
		state.Reader.BigEndian = bigEndian;
		object bounded = state.Reader.BaseStream;
		Type boundedType = bounded.GetType();
		MethodInfo attach = boundedType.GetMethod("AttachRange", Instance)!;
		MethodInfo detach = boundedType.GetMethod("Detach", Instance)!;
		MethodInfo read = typeof(UnityProjection).GetMethod("Read", Static, null,
			[typeof(AssetTypeTemplateField), typeof(UnityProjection.State), typeof(bool), typeof(bool),
				typeof(bool), typeof(bool)], null)!;
		attach.Invoke(bounded, [input, offset, length]);
		try
		{
			Exception? failure = null;
			UnityProjection? result = null;
			try
			{
				result = (UnityProjection?)read.Invoke(null,
					[template, state, false, false, route, true]);
			}
			catch (TargetInvocationException error)
			{
				failure = error.InnerException;
			}
			return new Outcome(failure, state.Reader.Position, input.Position, result);
		}
		finally
		{
			detach.Invoke(bounded, null);
		}
	}

	private static long CoveredBytes(IEnumerable<ReadRange> reads, long start, int length)
	{
		long end = start + length;
		long total = 0;
		foreach (ReadRange read in reads)
		{
			long overlap = Math.Min(end, read.Offset + read.Count) - Math.Max(start, read.Offset);
			if (overlap > 0) total += overlap;
		}
		return total;
	}

	private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] fields)
		=> Field("Base", AssetValueType.None, fields);

	private static AssetTypeTemplateField ArrayField(string name, AssetTypeTemplateField element)
		=> Field(name, AssetValueType.Array, Field("size", AssetValueType.Int32), element);

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

	private static int AddString(List<byte> bytes, bool bigEndian, string value)
	{
		byte[] encoded = Encoding.UTF8.GetBytes(value);
		AddScalar(bytes, bigEndian, AssetValueType.Int32, encoded.Length);
		int start = bytes.Count;
		bytes.AddRange(encoded);
		Align4(bytes);
		return start;
	}

	private static int AddBytes(List<byte> bytes, bool bigEndian, byte[] value)
	{
		AddScalar(bytes, bigEndian, AssetValueType.Int32, value.Length);
		int start = bytes.Count;
		bytes.AddRange(value);
		return start;
	}

	private static void AddScalar(List<byte> bytes, bool bigEndian, AssetValueType kind, long value)
	{
		Span<byte> encoded = stackalloc byte[8];
		int width = kind switch
		{
			AssetValueType.Bool or AssetValueType.Int8 or AssetValueType.UInt8 => 1,
			AssetValueType.Int16 or AssetValueType.UInt16 => 2,
			AssetValueType.Int32 or AssetValueType.UInt32 => 4,
			AssetValueType.Int64 or AssetValueType.UInt64 => 8,
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

	private readonly record struct ReadRange(long Offset, int Count);

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
	}
}
