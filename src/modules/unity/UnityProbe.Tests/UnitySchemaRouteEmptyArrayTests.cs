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

public sealed class UnitySchemaRouteEmptyArrayTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void EmptyStringAndStructArraysUseTheGuardedRoute()
    {
        AssetTypeTemplateField secondary = ArrayField("secondaryTextures", Field("data", AssetValueType.None,
            Field("path", AssetValueType.String),
            Field("texture", AssetValueType.None,
                Field("m_FileID", AssetValueType.Int32),
                Field("m_PathID", AssetValueType.Int64))));
        AssetTypeTemplateField template = Root(
            Field("m_Name", AssetValueType.String),
            ArrayField("m_AtlasTags", Field("data", AssetValueType.String)),
            secondary,
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template);
        Assert.NotNull(state.Route);

        List<byte> body = [];
        AddString(body, false, "sprite");
        AddScalar(body, false, AssetValueType.Int32, 0);
        AddScalar(body, false, AssetValueType.Int32, 0);
        AddScalar(body, false, AssetValueType.Int32, 513);
        const int offset = 7;
        using CountingStream input = new(Embed(body.ToArray(), offset, 5));
        UnityProjection result = UnityProjection.ReadBorrowed(input, offset, body.Count, template, false, state);

        Assert.Equal("sprite", result.Text("m_Name"));
        Assert.Equal(513, result.Number("m_Width"));
        Assert.All(input.Reads, read => Assert.InRange(read.Offset, offset, offset + body.Count - read.Count));
    }

    [Fact]
    public void AlignedEmptyVariableArrayStartingAfterAByteAlignsTheNextField()
    {
        AssetTypeTemplateField tags = ArrayField("m_AtlasTags", Field("data", AssetValueType.String));
        tags.IsAligned = true;
        AssetTypeTemplateField template = Root(
            Field("m_Type", AssetValueType.UInt8),
            tags,
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template);
        Assert.NotNull(state.Route);

        List<byte> body = [];
        AddScalar(body, false, AssetValueType.UInt8, 7);
        AddScalar(body, false, AssetValueType.Int32, 0);
        body.AddRange(new byte[3]);
        AddScalar(body, false, AssetValueType.Int32, 44);
        const int offset = 5;
        using CountingStream input = new(Embed(body.ToArray(), offset, 2));
        UnityProjection result = UnityProjection.ReadBorrowed(input, offset, body.Count, template, false, state);

        Assert.Equal(7, result.Number("m_Type"));
        Assert.Equal(44, result.Number("m_Width"));
    }

    [Fact]
    public void UnalignedEmptyVariableArrayKeepsTheNextFieldAtTheCountEnd()
    {
        AssetTypeTemplateField tags = ArrayField("m_AtlasTags", Field("data", AssetValueType.String));
        AssetTypeTemplateField template = Root(
            Field("m_Type", AssetValueType.UInt8),
            tags,
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State routeState = new(template);
        UnityProjection.State matureState = new(template);
        Assert.NotNull(routeState.Route);

        List<byte> body = [];
        AddScalar(body, false, AssetValueType.UInt8, 7);
        AddScalar(body, false, AssetValueType.Int32, 0);
        Assert.Equal(5, body.Count);
        AddScalar(body, false, AssetValueType.Int32, 44);
        const int offset = 0;
        using CountingStream routeInput = new(Embed(body.ToArray(), offset, 2));
        using CountingStream matureInput = new(Embed(body.ToArray(), offset, 2));
        ReadOutcome route = Capture(template, routeState, routeInput, offset, body.Count, false, true, false);
        ReadOutcome mature = Capture(template, matureState, matureInput, offset, body.Count, false, false, false);

        Assert.Null(route.Failure);
        Assert.Null(mature.Failure);
        UnityProjection routeResult = Assert.IsType<UnityProjection>(route.Result);
        UnityProjection matureResult = Assert.IsType<UnityProjection>(mature.Result);
        Assert.Equal(7, routeResult.Number("m_Type"));
        Assert.Equal(7, matureResult.Number("m_Type"));
        Assert.Equal(44, routeResult.Number("m_Width"));
        Assert.Equal(44, matureResult.Number("m_Width"));
        Assert.Equal(mature.Logical, route.Logical);
        Assert.Equal(mature.Physical, route.Physical);
    }

    [Fact]
    public void MultipleEmptyGuardsKeepTheFollowingFixedArraySkip()
    {
        AssetTypeTemplateField fixedArray = ArrayField("m_SubMeshes", Field("data", AssetValueType.None,
            Field("firstByte", AssetValueType.UInt32),
            Field("indexCount", AssetValueType.UInt16)));
        fixedArray.IsAligned = true;
        AssetTypeTemplateField template = Root(
            ArrayField("m_AtlasTags", Field("data", AssetValueType.String)),
            ArrayField("secondaryTextures", Field("data", AssetValueType.None,
                Field("name", AssetValueType.String),
                Field("texture", AssetValueType.None, Field("m_PathID", AssetValueType.Int64)))),
            fixedArray,
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template);
        Assert.NotNull(state.Route);

        List<byte> body = [];
        AddScalar(body, false, AssetValueType.Int32, 0);
        AddScalar(body, false, AssetValueType.Int32, 0);
        AddScalar(body, false, AssetValueType.Int32, 1);
        int dataStart = body.Count;
        AddScalar(body, false, AssetValueType.UInt32, 0x01020304);
        AddScalar(body, false, AssetValueType.UInt16, 0x0506);
        const int dataLength = 6;
        Align4(body);
        AddScalar(body, false, AssetValueType.Int32, 777);
        const int offset = 3;
        using CountingStream input = new(Embed(body.ToArray(), offset, 4));
        UnityProjection result = UnityProjection.ReadBorrowed(input, offset, body.Count, template, false, state);

        Assert.Equal(777, result.Number("m_Width"));
        Assert.DoesNotContain(input.Reads, read => Overlaps(read, offset + dataStart, dataLength));
    }

    [Fact]
    public void NonEmptyVariableArrayUsesTheGuardedRouteWithoutRestartingTheObject()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Name", AssetValueType.String),
            ArrayField("m_AtlasTags", Field("data", AssetValueType.String)),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State routeState = new(template);
        UnityProjection.State matureState = new(template);
        Assert.NotNull(routeState.Route);

        List<byte> body = [];
        AddString(body, false, "hero");
        AddScalar(body, false, AssetValueType.Int32, 2);
        AddString(body, false, "alpha");
        AddString(body, false, "beta");
        AddScalar(body, false, AssetValueType.Int32, 77);
        const int offset = 9;
        using CountingStream routeInput = new(Embed(body.ToArray(), offset, 6));
        using CountingStream matureInput = new(Embed(body.ToArray(), offset, 6));
        ReadOutcome route = Capture(template, routeState, routeInput, offset, body.Count, false, true, true);
        ReadOutcome mature = Capture(template, matureState, matureInput, offset, body.Count, false, false, true);

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
        Assert.Equal(8, CoveredBytes(matureInput.Reads, offset, 4));
        Assert.Equal(4, CoveredBytes(routeInput.Reads, offset + 8, 4));
    }

    [Fact]
    public void TheSameStateAlternatesEmptyAndNonEmptyVariableArrays()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Name", AssetValueType.String),
            ArrayField("m_AtlasTags", Field("data", AssetValueType.String)),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template);
        Assert.NotNull(state.Route);

        List<byte> emptyBody = [];
        AddString(emptyBody, false, "zero");
        AddScalar(emptyBody, false, AssetValueType.Int32, 0);
        AddScalar(emptyBody, false, AssetValueType.Int32, 11);
        using (CountingStream input = new(Embed(emptyBody.ToArray(), 5, 3)))
        {
            UnityProjection empty = UnityProjection.ReadBorrowed(input, 5, emptyBody.Count, template, false, state);
            Assert.Equal("zero", empty.Text("m_Name"));
            Assert.Equal(11, empty.Number("m_Width"));
        }

        List<byte> fullBody = [];
        AddString(fullBody, false, "full");
        AddScalar(fullBody, false, AssetValueType.Int32, 1);
        AddString(fullBody, false, "entry");
        AddScalar(fullBody, false, AssetValueType.Int32, 22);
        using (CountingStream input = new(Embed(fullBody.ToArray(), 7, 3)))
        {
            UnityProjection full = UnityProjection.ReadBorrowed(input, 7, fullBody.Count, template, false, state);
            Assert.Equal("full", full.Text("m_Name"));
            Assert.Equal(22, full.Number("m_Width"));
        }

        List<byte> finalBody = [];
        AddString(finalBody, false, "again");
        AddScalar(finalBody, false, AssetValueType.Int32, 0);
        AddScalar(finalBody, false, AssetValueType.Int32, 33);
        using (CountingStream input = new(Embed(finalBody.ToArray(), 2, 3)))
        {
            UnityProjection final = UnityProjection.ReadBorrowed(input, 2, finalBody.Count, template, false, state);
            Assert.Equal("again", final.Text("m_Name"));
            Assert.Equal(33, final.Number("m_Width"));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16_000_001)]
    public void MalformedVariableArrayCountsMatchTheMatureFailure(int count)
    {
        AssetTypeTemplateField template = Root(
            ArrayField("m_AtlasTags", Field("data", AssetValueType.String)),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State routeState = new(template);
        UnityProjection.State matureState = new(template);
        Assert.NotNull(routeState.Route);

        List<byte> body = [];
        AddScalar(body, false, AssetValueType.Int32, count);
        AddScalar(body, false, AssetValueType.Int32, 9);
        const int offset = 3;
        using CountingStream routeInput = new(Embed(body.ToArray(), offset, 2));
        using CountingStream matureInput = new(Embed(body.ToArray(), offset, 2));
        ReadOutcome route = Capture(template, routeState, routeInput, offset, body.Count, false, true, false);
        ReadOutcome mature = Capture(template, matureState, matureInput, offset, body.Count, false, false, false);

        Assert.NotNull(route.Failure);
        Assert.NotNull(mature.Failure);
        Assert.Equal(mature.Failure.GetType(), route.Failure.GetType());
        Assert.Equal(mature.Logical, route.Logical);
        Assert.Equal(mature.Physical, route.Physical);
    }

    [Fact]
    public void TruncatedVariableArrayCountMatchesTheMatureCursor()
    {
        AssetTypeTemplateField template = Root(
            ArrayField("m_AtlasTags", Field("data", AssetValueType.String)),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State routeState = new(template);
        UnityProjection.State matureState = new(template);
        Assert.NotNull(routeState.Route);

        byte[] body = [1, 0];
        const int offset = 5;
        using CountingStream routeInput = new(Embed(body, offset, 3));
        using CountingStream matureInput = new(Embed(body, offset, 3));
        ReadOutcome route = Capture(template, routeState, routeInput, offset, body.Length, false, true, false);
        ReadOutcome mature = Capture(template, matureState, matureInput, offset, body.Length, false, false, false);

        Assert.NotNull(route.Failure);
        Assert.NotNull(mature.Failure);
        Assert.Equal(mature.Failure.GetType(), route.Failure.GetType());
        Assert.Equal(mature.Logical, route.Logical);
        Assert.Equal(mature.Physical, route.Physical);
    }

    private sealed record ReadOutcome(Exception? Failure, long Logical, long Physical, UnityProjection? Result);

    private static ReadOutcome Capture(AssetTypeTemplateField template, UnityProjection.State state, Stream input,
        long offset, long length, bool validateLayout, bool reuse, bool captureRawName)
    {
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
                    [template, state, validateLayout, false, reuse, captureRawName]);
            }
            catch (TargetInvocationException error)
            {
                failure = error.InnerException;
            }
            return new ReadOutcome(failure, state.Reader.Position, input.Position, result);
        }
        finally
        {
            detach.Invoke(bounded, null);
        }
    }

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
    {
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        AddScalar(bytes, bigEndian, AssetValueType.Int32, encoded.Length);
        bytes.AddRange(encoded);
        Align4(bytes);
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

    private static bool Overlaps(CountingStream.ReadRange read, long start, int length)
        => read.Count > 0 && read.Offset < start + length && start < read.Offset + read.Count;

    private static long CoveredBytes(IEnumerable<CountingStream.ReadRange> reads, long start, int length)
    {
        long end = start + length;
        long total = 0;
        foreach (CountingStream.ReadRange read in reads)
        {
            long overlap = Math.Min(end, read.Offset + read.Count) - Math.Max(start, read.Offset);
            if (overlap > 0) total += overlap;
        }
        return total;
    }

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
