using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityStructuralCoverageTests
{
    private const int FieldBudget = 16_000_000;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

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

    private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] children)
        => Field("Base", AssetValueType.None, children);

    private static AssetTypeTemplateField ArrayField(string name, AssetTypeTemplateField element)
        => Field(name, AssetValueType.Array, Field("size", AssetValueType.Int32), element);

    private static MemoryStream Data(Action<BinaryWriter> write)
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) write(writer);
        stream.Position = 0;
        return stream;
    }

    private static UnityProjection Read(AssetTypeTemplateField template, Action<BinaryWriter> write)
    {
        using MemoryStream stream = Data(write);
        return UnityProjection.Read(stream, template, false);
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

    private static bool Overlaps((long Offset, int Count) read, long start, int length)
        => read.Count > 0 && read.Offset < start + length && start < read.Offset + read.Count;

    private sealed class CountingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes, writable: false);
        public List<(long Offset, int Count)> Reads { get; } = [];
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
            if (value >= 0) Reads.Add((inner.Position - 1, 1));
            return value;
        }

        private void Record(int count)
        {
            if (count > 0) Reads.Add((inner.Position - count, count));
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void NonDictionaryProjectionStoresCapturedFloatAndDoubleBitsInSlotStorage()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Width", AssetValueType.Float),
            Field("m_Height", AssetValueType.Double),
            Field("m_Type", AssetValueType.UInt32));
        UnityProjection projection = Read(template, writer =>
        {
            writer.Write(0x7FC00123u);
            writer.Write(0x7FF8000000000001UL);
            writer.Write(9u);
        });

        Assert.True(projection.TryFloat32("m_Width", out uint single));
        Assert.Equal(0x7FC00123u, single);
        Assert.True(projection.TryBits("m_Height", out long bits));
        Assert.Equal(unchecked((long)0x7FF8000000000001UL), bits);
        Assert.False(projection.TryFloat32("m_Height", out _));
        Assert.Equal(9u, (uint)projection.Number("m_Type"));
    }

    [Fact]
    public void CapturedScalarKindsKeepTheirOwnSignednessAndBitWidth()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Width", AssetValueType.None,
                Field("flag", AssetValueType.Bool),
                Field("i8", AssetValueType.Int8),
                Field("u8", AssetValueType.UInt8),
                Field("i16", AssetValueType.Int16),
                Field("u16", AssetValueType.UInt16),
                Field("i32", AssetValueType.Int32),
                Field("u32", AssetValueType.UInt32),
                Field("i64", AssetValueType.Int64),
                Field("u64", AssetValueType.UInt64)));
        UnityProjection projection = Read(template, writer =>
        {
            writer.Write(true);
            writer.Write((sbyte)-2);
            writer.Write((byte)250);
            writer.Write((short)-3);
            writer.Write((ushort)65000);
            writer.Write(-4);
            writer.Write(4_000_000_000u);
            writer.Write(-5L);
            writer.Write(18_000_000_000_000_000_000UL);
        });

        Assert.Equal(1, projection.Number("m_Width/flag"));
        Assert.Equal(-2, projection.Number("m_Width/i8"));
        Assert.Equal(250, projection.Number("m_Width/u8"));
        Assert.Equal(-3, projection.Number("m_Width/i16"));
        Assert.Equal(65_000, projection.Number("m_Width/u16"));
        Assert.Equal(-4, projection.Number("m_Width/i32"));
        Assert.Equal(4_000_000_000L, projection.Number("m_Width/u32"));
        Assert.Equal(-5L, projection.Number("m_Width/i64"));
        Assert.Equal(unchecked((long)18_000_000_000_000_000_000UL), projection.Number("m_Width/u64"));
        Assert.Equal(unchecked((ulong)-4), projection.Unsigned("m_Width/i32"));
    }

    [Fact]
    public void DynamicPathsNumberOnlyAfterArrayDataNamesAndResetSiblingIndices()
    {
        AssetTypeTemplateField template = Root(
            Field("m_RD", AssetValueType.None,
                Field("textureRect", AssetValueType.None,
                    Field("x", AssetValueType.Float), Field("y", AssetValueType.Float))),
            Field("other", AssetValueType.None,
                Field("Array", AssetValueType.Array, Field("size", AssetValueType.Int32),
                    Field("data", AssetValueType.None,
                        Field("value", AssetValueType.Int32),
                        Field("nested", AssetValueType.None, Field("leaf", AssetValueType.Int32))))));
        template.Type = "SpriteAtlas";
        UnityProjection projection = Read(template, writer =>
        {
            writer.Write(1.5f);
            writer.Write(2.5f);
            writer.Write(2);
            writer.Write(10);
            writer.Write(11);
            writer.Write(20);
            writer.Write(21);
        });

        Assert.True(projection.TryBits("m_RD/textureRect/x", out long x));
        Assert.Equal(0x3FC00000L, x);
        Assert.False(projection.Keys.Any(key => key.Contains("other/Array/data#", StringComparison.Ordinal)));
        Assert.Contains("m_RD/textureRect/x", projection.Keys);
    }

    [Fact]
    public void TextScratchSwitchesToPooledBufferAndDecodesOnTruncatedInput()
    {
        AssetTypeTemplateField template = Root(Field("m_Name", AssetValueType.String));
        string prefix = new('a', 300);
        byte[] full = Encoding.UTF8.GetBytes(prefix);
        byte[] truncated = full[..127];
        UnityProjection complete = Read(template, writer =>
        {
            writer.Write(full.Length);
            writer.Write(full);
        });
        Assert.Equal(prefix, complete.Text("m_Name"));

        byte[] partialBody = new byte[4 + truncated.Length];
        BinaryPrimitives.WriteInt32LittleEndian(partialBody, full.Length);
        truncated.CopyTo(partialBody, 4);
        using SparseBodyStream shortInput = new(partialBody, 1024, long.MaxValue);
        UnityProjection partial = UnityProjection.ReadBorrowed(shortInput, 0, 1024, template, false,
            new UnityProjection.State(template));
        Assert.Equal(new string('a', truncated.Length), partial.Text("m_Name"));
    }

    [Fact]
    public void RawNameCaptureStopsAtShortBodyAndRetainsOnlyReadBytes()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Name", AssetValueType.String),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template);
        byte[] body = [6, 0, 0, 0, (byte)'a', (byte)'b', (byte)'c'];
        const long advertisedLength = 64;
        using SparseBodyStream input = new(body, advertisedLength, zeroStart: 12);
        UnityProjection projection = UnityProjection.ReadBorrowed(input, 0, advertisedLength, template, false, state,
            captureRawName: true);

        Assert.Equal(new byte[] { (byte)'a', (byte)'b', (byte)'c' }, projection.RawName.ToArray());
        Assert.True(string.IsNullOrEmpty(projection.Text("m_Name")));
    }

    [Fact]
    public void LayoutValidationAcceptsZeroPaddingAndTextureExtensionButRefusesUnknownTail()
    {
        AssetTypeTemplateField template = Root(Field("m_Width", AssetValueType.Int32));
        UnityProjection.State paddedState = new(template);
        using MemoryStream padded = new(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0 }, false);
        Assert.Equal(7, UnityProjection.Read(padded, template, false, paddedState, validateLayout: true).Number("m_Width"));

        UnityProjection.State textureState = new(template);
        using MemoryStream textureTail = new(Embed(new byte[] { 7, 0, 0, 0 }, 0, 24), false);
        Assert.Equal(7, UnityProjection.Read(textureTail, template, false, textureState, validateLayout: true, textureTail: true).Number("m_Width"));

        UnityProjection.State nonzeroState = new(template);
        using MemoryStream nonzero = new(new byte[] { 7, 0, 0, 0, 1 }, false);
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => UnityProjection.Read(nonzero,
            template, false, nonzeroState, validateLayout: true));
        Assert.Equal("Unity layout does not consume the object.", failure.Message);

        UnityProjection.State longState = new(template);
        using MemoryStream longTail = new(new byte[4101], false);
        Assert.Throws<InvalidDataException>(() => UnityProjection.Read(longTail, template, false, longState,
            validateLayout: true));
    }

    [Fact]
    public void TextureTailOfTheWrongLengthIsStillRejected()
    {
        AssetTypeTemplateField template = Root(Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template);
        using MemoryStream input = new(Embed(new byte[] { 7, 0, 0, 0 }, 0, 23), false);
        Assert.Throws<InvalidDataException>(() => UnityProjection.Read(input, template, false, state,
            validateLayout: true, textureTail: true));
    }

    [Fact]
    public void BorrowedProjectionStreamRejectsOutOfRangeAttachmentAndSeek()
    {
        Type streamType = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "ProjectionStream");
        using Stream stream = (Stream)Activator.CreateInstance(streamType, Instance, null,
            new object[] { Stream.Null }, null)!;
        MethodInfo attach = streamType.GetMethod("AttachRange", Instance)!;
        MethodInfo detach = streamType.GetMethod("Detach", Instance)!;
        MethodInfo sync = streamType.GetMethod("SyncBorrowedSource", Instance)!;
        using MemoryStream source = new(new byte[16], writable: true);

        Assert.Throws<TargetInvocationException>(() => attach.Invoke(stream, new object[] { source, -1L, 4L }));
        Assert.Throws<TargetInvocationException>(() => attach.Invoke(stream, new object[] { source, 0L, -1L }));
        Assert.Throws<TargetInvocationException>(() => attach.Invoke(stream, new object[] { source, 17L, 0L }));
        Assert.Throws<TargetInvocationException>(() => attach.Invoke(stream, new object[] { source, 8L, 9L }));

        attach.Invoke(stream, new object[] { source, 4L, 4L });
        Assert.Equal(4L, stream.Length);
        source.Position = 0;
        stream.Position = 2;
        Assert.Equal(2L, stream.Position);
        Assert.Equal(0L, source.Position);
        sync.Invoke(stream, null);
        Assert.Equal(6, source.Position);
        Assert.Equal(0, stream.ReadByte());
        Assert.Equal(3L, stream.Position);
        Assert.Equal(7L, source.Position);
        Assert.Equal(0, stream.ReadByte());
        Assert.Equal(4L, stream.Position);
        Assert.Equal(8L, source.Position);
        Assert.Throws<EndOfStreamException>(() => stream.ReadByte());
        Assert.Throws<EndOfStreamException>(() => stream.Position = -1);
        Assert.Throws<EndOfStreamException>(() => stream.Position = 5);
        Assert.Throws<EndOfStreamException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<EndOfStreamException>(() => stream.Seek(5, SeekOrigin.Begin));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)99));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        detach.Invoke(stream, null);
        Assert.Equal(0L, stream.Position);
    }

    [Fact]
    public void ArraySkipBudgetFailureReportsTheExactElementAndFieldCursor()
    {
        AssetTypeTemplateField template = Root(
            ArrayField("ignored", Field("data", AssetValueType.None,
                Field("value", AssetValueType.Int32),
                Field("tail", AssetValueType.UInt16))),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State routeState = new(template);
        UnityProjection.State matureState = new(template);
        Assert.NotNull(routeState.Route);
        int count = FieldBudget / 2 + 1;
        List<byte> prefix = [];
        AddScalar(prefix, false, AssetValueType.Int32, count);
        const int offset = 6;
        long declaredLength = 4L + (long)count * 6L;
        using SparseBodyStream routeInput = new(Embed(prefix.ToArray(), offset, 0), offset + declaredLength, offset + 4);
        using SparseBodyStream matureInput = new(Embed(prefix.ToArray(), offset, 0), offset + declaredLength, offset + 4);
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => UnityProjection.ReadBorrowed(
            routeInput, offset, declaredLength, template, false, routeState));

        Assert.Equal("Unity projection exceeded its field budget.", failure.Message);
        Assert.Throws<InvalidDataException>(() => UnityProjection.ReadBorrowed(
            matureInput, offset, declaredLength, template, false, matureState, validateLayout: true));
        long routeCursor = RouteFailureCursor(routeState, routeInput, offset, declaredLength,
            typeof(InvalidDataException));
        long matureCursor = MatureFailureCursor(template, matureState, matureInput, offset, declaredLength,
            typeof(InvalidDataException));
        Assert.Equal(matureCursor, routeCursor);
    }

    [Fact]
    public void ArraySkipTruncationReportsTheFirstIncompleteField()
    {
        AssetTypeTemplateField template = Root(
            ArrayField("ignored", Field("data", AssetValueType.None,
                Field("value", AssetValueType.Int32),
                Field("tail", AssetValueType.UInt16))),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State routeState = new(template);
        UnityProjection.State matureState = new(template);
        Assert.NotNull(routeState.Route);
        List<byte> body = [];
        AddScalar(body, false, AssetValueType.Int32, 2);
        body.AddRange(new byte[] { 1, 0, 0, 0, 2, 0, 3, 0, 0, 0, 4, 0 });
        body.RemoveAt(body.Count - 1);
        int offset = 3;
        using CountingStream routeInput = new(Embed(body.ToArray(), offset, 2));
        using CountingStream matureInput = new(Embed(body.ToArray(), offset, 2));
        Assert.Throws<EndOfStreamException>(() => UnityProjection.ReadBorrowed(
            routeInput, offset, body.Count, template, false, routeState));
        Assert.Throws<EndOfStreamException>(() => UnityProjection.ReadBorrowed(
            matureInput, offset, body.Count, template, false, matureState, validateLayout: true));
        long routeCursor = RouteFailureCursor(routeState, routeInput, offset, body.Count,
            typeof(EndOfStreamException));
        long matureCursor = MatureFailureCursor(template, matureState, matureInput, offset, body.Count,
            typeof(EndOfStreamException));
        Assert.Equal(matureCursor, routeCursor);
    }

    [Fact]
    public void ArraySkipTruncationInsideFirstElementReturnsItsFieldBoundary()
    {
        AssetTypeTemplateField template = Root(
            ArrayField("ignored", Field("data", AssetValueType.None,
                Field("value", AssetValueType.UInt32),
                Field("tail", AssetValueType.UInt64))),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State routeState = new(template);
        UnityProjection.State matureState = new(template);
        Assert.NotNull(routeState.Route);
        List<byte> body = [];
        AddScalar(body, false, AssetValueType.Int32, 1);
        body.AddRange(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 });
        int offset = 5;
        using CountingStream routeInput = new(Embed(body.ToArray(), offset, 2));
        using CountingStream matureInput = new(Embed(body.ToArray(), offset, 2));
        Assert.Throws<EndOfStreamException>(() => UnityProjection.ReadBorrowed(
            routeInput, offset, body.Count, template, false, routeState));
        Assert.Throws<EndOfStreamException>(() => UnityProjection.ReadBorrowed(
            matureInput, offset, body.Count, template, false, matureState, validateLayout: true));
        long routeCursor = RouteFailureCursor(routeState, routeInput, offset, body.Count,
            typeof(EndOfStreamException));
        long matureCursor = MatureFailureCursor(template, matureState, matureInput, offset, body.Count,
            typeof(EndOfStreamException));
        Assert.Equal(matureCursor, routeCursor);
    }

    [Fact]
    public void VariableFixedElementsAndCapturedFieldsKeepTheMatureRouteFallback()
    {
        AssetTypeTemplateField aligned = ArrayField("aligned", Field("data", AssetValueType.None,
            Field("value", AssetValueType.Int32)));
        aligned.IsAligned = true;
        AssetTypeTemplateField captured = ArrayField("captured", Field("data", AssetValueType.Int32));
        AssetTypeTemplateField emptyElement = ArrayField("empty", Field("data", AssetValueType.None));
        AssetTypeTemplateField template = Root(aligned, captured, emptyElement, Field("m_Width", AssetValueType.Int32));
        AssetTypeTemplateField shape = Field("m_RenderDataKey", AssetValueType.None,
            Field("data", AssetValueType.Int32));
        Assert.NotNull(new UnityProjection.State(Root(ArrayField("x", shape),
            Field("m_Width", AssetValueType.Int32))).Route);
        Assert.NotNull(new UnityProjection.State(Root(ArrayField("x", Field("data", AssetValueType.String)),
            Field("m_Width", AssetValueType.Int32))).Route);
        // A childless array fails State's schema walk before Route compilation; exercise the
        // route builder's defensive branch with a valid terminal-captured state.
        AssetTypeTemplateField width = Field("m_Width", AssetValueType.Int32);
        UnityProjection.State malformedState = new(Root(width));
        Assert.NotNull(UnityProjection.SchemaRoute.TryCompile(
            Root(ArrayField("single", Field("data", AssetValueType.Int32)), width), malformedState));
        Assert.Null(UnityProjection.SchemaRoute.TryCompile(
            Root(Field("single", AssetValueType.Array), width), malformedState));
        Assert.Null(new UnityProjection.State(Root(ArrayField("m_RenderDataKey",
            Field("data", AssetValueType.Int32)), Field("m_Width", AssetValueType.Int32))).Route);
        Assert.NotNull(new UnityProjection.State(template).Route);
        Assert.NotNull(new UnityProjection.State(Root(aligned, Field("m_Width", AssetValueType.Int32))).Route);
        Assert.NotNull(new UnityProjection.State(Root(captured, Field("m_Width", AssetValueType.Int32))).Route);
    }

    [Fact]
    public void SkipReplayUsesFieldBoundariesWhenTheMergedRunExceedsTheBudget()
    {
        AssetTypeTemplateField template = Root(
            ArrayField("ignored", Field("data", AssetValueType.None)),
            Field("first", AssetValueType.Int32),
            Field("second", AssetValueType.Int32),
            Field("third", AssetValueType.Int32),
            Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template);
        Assert.NotNull(state.Route);
        List<byte> body = [];
        AddScalar(body, false, AssetValueType.Int32, FieldBudget - 3);
        AddScalar(body, false, AssetValueType.Int32, 1);
        AddScalar(body, false, AssetValueType.Int32, 2);
        AddScalar(body, false, AssetValueType.Int32, 3);
        AddScalar(body, false, AssetValueType.Int32, 9);
        int offset = 3;
        using CountingStream input = new(Embed(body.ToArray(), offset, 2));
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => UnityProjection.ReadBorrowed(
            input, offset, body.Count, template, false, state));
        Assert.Equal("Unity projection exceeded its field budget.", failure.Message);
        Assert.Equal(offset + 4, input.Position);
    }

    [Fact]
    public void BytesStepAndLastTextStepPreserveTheMatureCursor()
    {
        AssetTypeTemplateField bytesTemplate = Root(Field("m_Script", AssetValueType.String));
        UnityProjection.State bytesState = new(bytesTemplate);
        Assert.NotNull(bytesState.Route);
        byte[] bytesBody = [2, 0, 0, 0, 7, 8];
        using (CountingStream input = new(Embed(bytesBody, 2, 1)))
        {
            UnityProjection projection = UnityProjection.ReadBorrowed(input, 2, bytesBody.Length,
                bytesTemplate, false, bytesState);
            Assert.Equal(new UnityProjection.ByteRange(4, 2), projection.Bytes("m_Script"));
            Assert.Equal(6, input.Position);
        }

        AssetTypeTemplateField textTemplate = Root(Field("m_Name", AssetValueType.String));
        UnityProjection.State textState = new(textTemplate);
        Assert.NotNull(textState.Route);
        byte[] textBody = [3, 0, 0, 0, (byte)'a', (byte)'b', (byte)'c'];
        using (CountingStream input = new(Embed(textBody, 1, 0)))
        {
            UnityProjection projection = UnityProjection.ReadBorrowed(input, 1, textBody.Length,
                textTemplate, false, textState);
            Assert.Equal("abc", projection.Text("m_Name"));
            Assert.Equal(1 + textBody.Length, input.Position);
        }
    }

    [Fact]
    public void TextLengthIsCappedWhileScriptBytesAcceptTheFullValidRange()
    {
        AssetTypeTemplateField text = Root(Field("m_Name", AssetValueType.String));
        Assert.Throws<InvalidDataException>(() => Read(text, writer => writer.Write(-1)));
        Assert.Throws<InvalidDataException>(() => Read(text, writer => writer.Write(4)));
        Assert.Throws<InvalidDataException>(() => Read(text, writer =>
        {
            writer.Write(2_000_000);
            writer.Write(new byte[2_000_000]);
        }));

        AssetTypeTemplateField script = Root(Field("m_Script", AssetValueType.String));
        Assert.Throws<InvalidDataException>(() => Read(script, writer => writer.Write(-1)));
        UnityProjection bytes = Read(script, writer =>
        {
            writer.Write(2_000_000);
            writer.Write(new byte[2_000_000]);
        });
        Assert.Equal(new UnityProjection.ByteRange(4, 2_000_000), bytes.Bytes("m_Script"));
    }

    [Fact]
    public void TemplateRejectsSchemaDepthBeyondTheStackBound()
    {
        TypeTreeNodeStruct node = new("int", "leaf", 1, default, []);
        for (int depth = 0; depth < 130; depth++)
            node = new TypeTreeNodeStruct("Test", $"n{depth}", 1, default, [node]);
        Assert.Throws<InvalidDataException>(() => UnityProjection.Template(node));
    }

    [Fact]
    public void ProjectionStateRejectsSchemaDeeperThanItsNameBuffers()
    {
        AssetTypeTemplateField node = Field("leaf", AssetValueType.Int32);
        for (int depth = 0; depth < 130; depth++)
            node = Field($"n{depth}", AssetValueType.None, node);
        Assert.Throws<InvalidDataException>(() => new UnityProjection.State(Root(node)));
    }

    [Fact]
    public void SpriteGeometryPredicatesRefuseAbsentFactsBeforeUsingTheirBits()
    {
        Type factsReader = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "SpriteFactsReader");
        MethodInfo finite = factsReader.GetMethod("IsFinite", Static)!;
        MethodInfo positive = factsReader.GetMethod("Positive", Static)!;
        MethodInfo unit = factsReader.GetMethod("IsUnitDownscale", Static)!;
        MethodInfo known = factsReader.GetMethod("IsKnownRectangleSettings", Static)!;
        MethodInfo identity = factsReader.GetMethod("IsIdentityRotation", Static)!;
        MethodInfo escape = factsReader.GetMethod("IsExportableRectangle", Static)!;

        Assert.True((bool)finite.Invoke(null, [BitConverter.SingleToUInt32Bits(1f)])!);
        Assert.False((bool)finite.Invoke(null, [BitConverter.SingleToUInt32Bits(float.NaN)])!);
        Assert.True((bool)positive.Invoke(null, [BitConverter.SingleToUInt32Bits(1f)])!);
        Assert.False((bool)positive.Invoke(null, [BitConverter.SingleToUInt32Bits(0f)])!);
        Assert.True((bool)unit.Invoke(null, [0u])!);
        Assert.True((bool)unit.Invoke(null, [BitConverter.SingleToUInt32Bits(1f)])!);
        Assert.False((bool)unit.Invoke(null, [BitConverter.SingleToUInt32Bits(2f)])!);
        Assert.True((bool)known.Invoke(null, [2u])!);
        Assert.False((bool)known.Invoke(null, [0u])!);
        Assert.True((bool)identity.Invoke(null, [2u])!);
        Assert.False((bool)identity.Invoke(null, [16u])!);

        Type sourceType = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "SpriteSourceFacts");
        Type canvasType = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "SpriteCanvasFacts");
        object absentSource = Activator.CreateInstance(sourceType)!;
        object absentCanvas = Activator.CreateInstance(canvasType)!;
        uint one = BitConverter.SingleToUInt32Bits(1f);
        object source = Activator.CreateInstance(sourceType, true, one, one, one, one, one, one, one, 2u)!;
        object canvas = Activator.CreateInstance(canvasType, true, one, one)!;

        Assert.False((bool)escape.Invoke(null, [absentSource, canvas, 1, 1])!);
        Assert.False((bool)escape.Invoke(null, [source, absentCanvas, 1, 1])!);
    }

    [Fact]
    public void SpriteFactsComparableRequiresEveryFinitePositiveComponentAndExportablePlacement()
    {
        Type sourceType = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "SpriteSourceFacts");
        Type canvasType = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "SpriteCanvasFacts");
        Type factsType = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "SpriteFacts");
        uint one = BitConverter.SingleToUInt32Bits(1f);
        uint zero = 0u;
        AssetCollection collection = AssetCreator.CreateCollection(UnityVersion.Parse("2021.3.0f1"));

        object Build(object source, object canvas)
        {
            object facts = Activator.CreateInstance(factsType)!;
            factsType.GetField("Source")!.SetValue(facts, source);
            factsType.GetField("Canvas")!.SetValue(facts, canvas);
            factsType.GetField("SourceCollection")!.SetValue(facts, collection);
            factsType.GetField("SourceWidth")!.SetValue(facts, 1);
            factsType.GetField("SourceHeight")!.SetValue(facts, 1);
            factsType.GetField("Resolved")!.SetValue(facts, true);
            return facts;
        }

        object presentSource = Activator.CreateInstance(sourceType, true, zero, zero, one, one, zero, zero, zero, 2u)!;
        object presentCanvas = Activator.CreateInstance(canvasType, true, one, one)!;
        object present = Build(presentSource, presentCanvas);
        Assert.True((bool)factsType.GetProperty("Comparable")!.GetValue(present)!);
        Assert.False((bool)factsType.GetProperty("Comparable")!.GetValue(Build(absent(sourceType), presentCanvas))!);
        Assert.False((bool)factsType.GetProperty("Comparable")!.GetValue(Build(presentSource, absent(canvasType)))!);
        object unresolved = Build(presentSource, presentCanvas);
        factsType.GetField("Resolved")!.SetValue(unresolved, false);
        Assert.False(Assert.IsType<bool>(factsType.GetProperty("Comparable")!.GetValue(unresolved)));

        object missingCollection = Build(presentSource, presentCanvas);
        factsType.GetField("SourceCollection")!.SetValue(missingCollection, null);
        Assert.False(Assert.IsType<bool>(factsType.GetProperty("Comparable")!.GetValue(missingCollection)));
    }

    private static object absent(Type type) => Activator.CreateInstance(type)!;

    [Fact]
    public void SourceFullTextureRequiresFinitePositiveTextureAndCanvasDimensions()
    {
        Type sourceType = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "SpriteSourceFacts");
        Type canvasType = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "SpriteCanvasFacts");
        uint one = BitConverter.SingleToUInt32Bits(1f);
        uint zero = 0u;
        object zeroSource = Activator.CreateInstance(sourceType, true, zero, zero, one, one, zero, zero, zero, 2u)!;
        object zeroCanvas = Activator.CreateInstance(canvasType, true, one, one)!;
        Type factsType = typeof(UnitySession).Assembly.GetTypes().Single(type => type.Name == "SpriteFacts");
        AssetCollection collection = AssetCreator.CreateCollection(UnityVersion.Parse("2021.3.0f1"));
        object facts = Activator.CreateInstance(factsType)!;
        factsType.GetField("Source")!.SetValue(facts, zeroSource);
        factsType.GetField("Canvas")!.SetValue(facts, zeroCanvas);
        factsType.GetField("SourceCollection")!.SetValue(facts, collection);
        factsType.GetField("SourceWidth")!.SetValue(facts, 1);
        factsType.GetField("SourceHeight")!.SetValue(facts, 1);
        factsType.GetField("Resolved")!.SetValue(facts, true);
        Assert.True(Assert.IsType<bool>(factsType.GetProperty("Comparable")!.GetValue(facts)));
    }

    [Fact]
    public void CompactSkinningChannelDimensionsAreRefusedBeforeDecode()
    {
        byte[] raw = new byte[4] { 10, 20, 30, 255 };
        byte[] assets = UnityFixtureBuilder.SerializedSprite("skin", raw, 1, 1,
            0, 0, 1, 1, 0, 0, 1, 1, 0, 0, settingsRaw: 64u,
            tightMesh: new UnityFixtureBuilder.TightMeshOptions([0f, 0f, 1f, 0f, 0f, 1f])
            {
                ChannelDimension = 5,
            });
        string selected = Path.Combine(Temp.NewDirectory(), "skin.assets");
        File.WriteAllBytes(selected, assets);
        using UnitySession session = UnitySession.Open(selected);
        UnityCatalogEntry texture = session.Entries.Single(entry => entry.Kind == UnityEntryKind.Texture);
        Assert.Equal(raw.Length, texture.SourceSize);
        Assert.Equal("png", texture.OutputExtension);
        Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
    }

    [Fact]
    public void CompactUnknownChannelFormatAndOversizedStreamIndexAreRefused()
    {
        byte[] raw = new byte[4] { 10, 20, 30, 255 };
        foreach (UnityFixtureBuilder.TightMeshOptions options in new[]
        {
            new UnityFixtureBuilder.TightMeshOptions([0f, 0f, 1f, 0f, 0f, 1f]) { ChannelFormat = 255 },
            new UnityFixtureBuilder.TightMeshOptions([0f, 0f, 1f, 0f, 0f, 1f]) { ChannelStream = 200 },
        })
        {
            string selected = Path.Combine(Temp.NewDirectory(), "bad-channel.assets");
            File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedSprite("bad", raw, 1, 1,
                0, 0, 1, 1, 0, 0, 1, 1, 0, 0, settingsRaw: 64u, tightMesh: options));
            using UnitySession session = UnitySession.Open(selected);
            UnityCatalogEntry texture = session.Entries.Single(entry => entry.Kind == UnityEntryKind.Texture);
            Assert.Equal(raw.Length, texture.SourceSize);
            Assert.Equal("png", texture.OutputExtension);
            Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
        }
    }

    [Fact]
    public void CompactUnreferencedSpriteKeepsTextureWhenItsOwnSourceIsNotExportable()
    {
        byte[] raw = new byte[4] { 10, 20, 30, 255 };
        string selected = Path.Combine(Temp.NewDirectory(), "unlinked.assets");
        File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedSprite("hero", raw, 1, 1,
            0, 0, 1, 1, 0, 0, 1, 1, 0, 0, linksTexture: false));
        using UnitySession session = UnitySession.Open(selected);
        Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
        Assert.DoesNotContain(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
    }

    [Fact]
    public void StateSlotsDistinguishTextNumberAndBytesForTheSamePath()
    {
        AssetTypeTemplateField template = Root(
            Field("m_Name", AssetValueType.String),
            Field("m_Width", AssetValueType.Int32),
            Field("image data", AssetValueType.ByteArray));
        UnityProjection projection = Read(template, writer =>
        {
            writer.Write(2);
            writer.Write(new byte[] { (byte)'o', (byte)'k' });
            Align4InWriter(writer);
            writer.Write(4);
            writer.Write(3);
            writer.Write(new byte[] { 1, 2, 3 });
        });
        Assert.Equal("ok", projection.Text("m_Name"));
        Assert.Equal(4, projection.Number("m_Width"));
        Assert.Equal(new UnityProjection.ByteRange(16, 3), projection.Bytes("image data"));
        Assert.Equal(0, projection.Number("m_Name"));
        Assert.Equal(string.Empty, projection.Text("m_Width"));
    }

    private static void Align4InWriter(BinaryWriter writer)
    {
        long position = writer.BaseStream.Position;
        while ((position & 3) != 0)
        {
            writer.Write((byte)0);
            position++;
        }
    }

    private static long RouteFailureCursor(UnityProjection.State state, Stream input, long offset, long length, Type expected)
    {
        object bounded = state.Reader.BaseStream;
        Type boundedType = bounded.GetType();
        MethodInfo attach = boundedType.GetMethod("AttachRange", Instance)!;
        MethodInfo detach = boundedType.GetMethod("Detach", Instance)!;
        MethodInfo read = state.Route!.GetType().GetMethod("Read", Instance)!;
        attach.Invoke(bounded, [input, offset, length]);
        try
        {
            TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() =>
            {
                read.Invoke(state.Route, [null!, state.Reader, bounded, false]);
            });
            Assert.IsType(expected, failure.InnerException);
            return state.Reader.Position;
        }
        finally
        {
            detach.Invoke(bounded, null);
        }
    }

    private static long MatureFailureCursor(AssetTypeTemplateField template, UnityProjection.State state,
        Stream input, long offset, long length, Type expected)
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
            TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() =>
            {
                read.Invoke(null, [template, state, true, false, false, false]);
            });
            Assert.IsType(expected, failure.InnerException);
            return state.Reader.Position;
        }
        finally
        {
            detach.Invoke(bounded, null);
        }
    }

    private sealed class SparseBodyStream(byte[] physical, long length, long zeroStart) : Stream
    {
        private long position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => position;
            set
            {
                if (value < 0 || value > length) throw new EndOfStreamException();
                position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (position >= length) return 0;
            int requested = (int)Math.Min(buffer.Length, length - position);
            if (position >= zeroStart)
            {
                buffer[..requested].Clear();
                position += requested;
                return requested;
            }
            if (position >= physical.Length) return 0;
            int available = (int)Math.Min(requested, physical.Length - position);
            physical.AsSpan((int)position, available).CopyTo(buffer);
            position += available;
            return available;
        }

        public override int ReadByte()
        {
            if (position >= length) return -1;
            byte value;
            if (position >= zeroStart) value = 0;
            else if (position < physical.Length) value = physical[(int)position];
            else return -1;
            position++;
            return value;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0 || target > length) throw new EndOfStreamException();
            position = target;
            return target;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}