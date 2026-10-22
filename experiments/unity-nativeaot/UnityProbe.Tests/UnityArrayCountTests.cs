using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityArrayCountTests(ITestOutputHelper output)
{
    private static AssetTypeTemplateField Field(string name, AssetValueType kind, params AssetTypeTemplateField[] children)
        => new() { Name = name, Type = name, ValueType = kind, HasValue = kind != AssetValueType.None,
            IsArray = kind is AssetValueType.Array or AssetValueType.ByteArray, Children = children.ToList() };
    private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] fields) => Field("Base", AssetValueType.None, fields);
    private static AssetTypeTemplateField Array(string name, AssetTypeTemplateField element)
        => Field(name, AssetValueType.Array, Field("size", AssetValueType.Int32), element);
    private static void Int32(Stream stream, int value, bool bigEndian)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        else BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
    private static byte[] Counts(bool bigEndian, params int[] values)
    {
        using MemoryStream stream = new();
        foreach (int value in values) Int32(stream, value, bigEndian);
        return stream.ToArray();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EachOrdinaryArrayPrefixIsReadOnceAcross4096BoundedObjects(bool bigEndian, bool validateLayout)
    {
        const int objectCount = 4096;
        const int origin = 11;
        const int payloadLength = 32;
        var template = Root(
            Array("m_RenderDataKey", Field("data", AssetValueType.Int32)),
            Array("ignored", Array("data", Field("data", AssetValueType.UInt16))),
            Field("payload", AssetValueType.ByteArray, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8)),
            Field("m_Width", AssetValueType.Int32));
        using MemoryStream encoded = new();
        encoded.Write(new byte[origin]);
        foreach (int value in new[] { 2, 7, 11, 1, 0, payloadLength }) Int32(encoded, value, bigEndian);
        encoded.Write(new byte[payloadLength]);
        Int32(encoded, 101, bigEndian);
        long objectLength = encoded.Length - origin;
        encoded.Write(new byte[9]);
        using PrefixGuard input = new(encoded.ToArray(), [origin, origin + 12, origin + 16], origin + 24, payloadLength);
        UnityProjection.State state = new(template);
        for (int i = 0; i < objectCount; i++)
        {
            UnityProjection value = UnityProjection.ReadBorrowed(input, origin, objectLength, template, bigEndian, state, validateLayout);
            Assert.Equal(11, value.Number("m_RenderDataKey/data"));
            Assert.Equal(101, value.Number("m_Width"));
            Assert.Equal(new[] { "m_RenderDataKey/data", "m_Width" }, value.Keys);
            Assert.Equal(origin + objectLength, input.Position);
            Assert.Equal(0, input.PayloadReads);
        }
        output.WriteLine($"{objectCount} objects: ordinary-array prefix reads={input.PrefixReads}, expected={objectCount * 3}; payload reads={input.PayloadReads}.");
        Assert.Equal(objectCount * 3, input.PrefixReads);
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 16_000_001)]
    [InlineData(true, -1)]
    [InlineData(true, 16_000_001)]
    public void InvalidCountsAreRejectedBeforeElementsAndTheSameStateCanRetry(bool bigEndian, int count)
    {
        var template = Root(Array("m_RenderDataKey", Field("data", AssetValueType.Int32)), Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template);
        using MemoryStream invalid = new(Counts(bigEndian, count), false);
        Assert.Throws<InvalidDataException>(() => UnityProjection.ReadBorrowed(invalid, 0, invalid.Length, template, bigEndian, state));
        Assert.Equal(4, invalid.Position);
        using MemoryStream valid = new(Counts(bigEndian, 0, 103), false);
        UnityProjection value = UnityProjection.ReadBorrowed(valid, 0, valid.Length, template, bigEndian, state, validateLayout: true);
        Assert.Equal(103, value.Number("m_Width"));
        Assert.Equal(new[] { "m_Width" }, value.Keys);
        Assert.Equal(valid.Length, valid.Position);
        Assert.True(invalid.CanRead);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public void TruncatedPrefixesCannotLeaveAnArrayCountForTheNextObject(bool bigEndian, int length)
    {
        var template = Root(Array("m_RenderDataKey", Field("data", AssetValueType.Int32)), Field("m_Width", AssetValueType.Int32));
        UnityProjection.State state = new(template);
        using MemoryStream truncated = new(new byte[length], false);
        Assert.Throws<EndOfStreamException>(() => UnityProjection.ReadBorrowed(truncated, 0, length, template, bigEndian, state));
        using MemoryStream valid = new(Counts(bigEndian, 1, 23, 107), false);
        UnityProjection value = UnityProjection.ReadBorrowed(valid, 0, valid.Length, template, bigEndian, state);
        Assert.Equal(23, value.Number("m_RenderDataKey/data"));
        Assert.Equal(107, value.Number("m_Width"));
        Assert.Equal(valid.Length, valid.Position);
    }

    private sealed class PrefixGuard(byte[] data, long[] prefixes, long payloadStart, int payloadLength) : Stream
    {
        private readonly MemoryStream inner = new(data, false);
        internal int PrefixReads { get; private set; }
        internal int PayloadReads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(Span<byte> buffer)
        {
            if (Position < payloadStart + payloadLength && Position + buffer.Length > payloadStart)
            {
                PayloadReads++;
                throw new InvalidOperationException("Projection read an ignored payload body.");
            }
            if (prefixes.Contains(Position)) PrefixReads++;
            return inner.Read(buffer);
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int ReadByte() { Span<byte> value = stackalloc byte[1]; return Read(value) == 0 ? -1 : value[0]; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
