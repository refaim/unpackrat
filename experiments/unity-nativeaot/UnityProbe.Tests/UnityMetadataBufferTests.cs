using System;
using System.IO;
using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.Import.AssetCreation;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;
using AssetRipper.IO.Files.SerializedFiles.Parser.TypeTrees;
using System.Text;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityMetadataBufferTests(ITestOutputHelper output)
{
    private sealed class CountedStream(byte[] bytes, long metadataStart, long payloadStart, int maxRead = int.MaxValue) : Stream
    {
        private readonly MemoryStream source = new(bytes, false);
        public int MetadataCalls, PayloadCalls, DisposeCalls, MetadataStarts;
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void Flush() => source.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public bool AllowPayload;
        public Action? BeforeMetadataRead;
        public long FaultAt = long.MaxValue;
        public Exception? Failure;
        public int Faults;
        private bool FailNow()
        {
            if (Position < FaultAt) return false;
            Faults++; if (Failure is not null) throw Failure; return true;
        }
        private void Observe(int count)
        {
            count = checked((int)Math.Min(count, Math.Max(0, Length - Position)));
            if (count <= 0) return;
            if (Position == metadataStart)
            {
                MetadataStarts++; Action? callback = BeforeMetadataRead; BeforeMetadataRead = null; callback?.Invoke();
            }
            if (Position >= metadataStart && Position < payloadStart) MetadataCalls++;
            if (Position >= payloadStart || count > payloadStart - Position)
            {
                PayloadCalls++;
                if (!AllowPayload) throw new InvalidOperationException("Object payload must remain unread during metadata parsing.");
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (FailNow()) return 0;
            int count = Math.Min(buffer.Length, maxRead);
            if (Position < FaultAt) count = checked((int)Math.Min(count, FaultAt - Position));
            Observe(count); return source.Read(buffer[..count]);
        }
        public override int ReadByte() { if (FailNow()) return -1; Observe(1); return source.ReadByte(); }
        protected override void Dispose(bool disposing) { DisposeCalls++; source.Dispose(); base.Dispose(disposing); }
    }
    private static SerializedFileHeader Header(byte[] bytes) { using MemoryStream stream = new(bytes); SerializedFileHeader header = new(); header.Read(stream); return header; }
    private static SerializedFile Read(SmartStream input, FileReadMode mode)
        => (SerializedFile)SchemeReader.ReadFile(input, "metadata.assets", "metadata.assets", mode);

    [Fact]
    public void CompactFormat22Reads4096SdkDescriptorsWithBlockFillsWithoutTouchingPayloads()
    {
        byte[] bytes = UnityFixtureBuilder.SerializedRepeatedTextAssets(4096); SerializedFileHeader header = Header(bytes);
        using CountedStream physical = new(bytes, 48, header.DataOffset); using SmartStream input = SmartStream.CreateOwned(physical);
        using SerializedFile file = Read(input, FileReadMode.CompactMetadataOnly);
        int calls = physical.MetadataCalls; output.WriteLine($"4096 SDK rows, metadata {header.MetadataSize} B: {calls} underlying metadata reads, {physical.PayloadCalls} payload reads.");
        Assert.Equal(4096, file.ObjectCount); Assert.Equal(0, physical.PayloadCalls);
        using SmartStream expectedInput = SmartStream.CreateMemory(bytes); using SerializedFile expected = Read(expectedInput, FileReadMode.MetadataOnly);
        for (int i = 0; i < file.ObjectCount; i++) AssertDescriptor(expected.GetObjectInfo(i), file.GetObjectInfo(i), expected, file);
        Assert.True(calls <= (header.MetadataSize + 4095) / 4096 + 8, $"Metadata used {calls} source reads; compact format22 metadata must use bounded4KiB fills rather than per-scalar reads.");
        physical.AllowPayload = true;
        foreach (int i in new[] { 0, 2048, 4095, 0 }) Assert.Equal(expected.GetObjectInfo(i).ObjectData, file.GetObjectInfo(i).ObjectData);
        Assert.Equal(0, physical.DisposeCalls);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void PublicMetadataReadPreservesReaderLengthValidationWhenBorrowedOwnerHasDifferentLength(bool longerOwner)
    {
        byte[] bytes = UnityFixtureBuilder.SerializedRepeatedTextAssets(1); SerializedFileHeader header = Header(bytes);
        if (longerOwner)
        {
            using SmartStream originalInput = SmartStream.CreateMemory(bytes); using SerializedFile original = Read(originalInput, FileReadMode.MetadataOnly);
            byte[] row = new byte[24]; BinaryPrimitives.WriteInt64LittleEndian(row, 1); BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(16), checked((int)original.GetObjectInfo(0).DataSize));
            int start = bytes.AsSpan().IndexOf(row); Assert.True(start > 0); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(start + 8), bytes.Length + 16 - header.DataOffset);
        }
        using SmartStream input = SmartStream.CreateMemory(bytes); header.Read(input);
        using SmartStream owner = SmartStream.CreateMemory(new byte[longerOwner ? bytes.Length + 128 : checked((int)header.DataOffset)]);
        SerializedFileMetadata metadata = new();
        if (longerOwner) Assert.Throws<InvalidDataException>(() => metadata.Read(input, header, owner));
        else { metadata.Read(input, header, owner); Assert.Single(metadata.Object); Assert.Equal(header.DataOffset, metadata.Object[0].DataOffset); Assert.True(metadata.Object[0].DataSize > 0); }
    }
    private static byte[] Fixture(int count, bool big = false, int generation = 22, bool extras = false)
    {
        UnityVersion version = UnityVersion.Parse("2021.3.0f1"); var collection = AssetCreator.CreateCollection(version);
        typeof(AssetCollection).GetProperty("EndianType")!.SetValue(collection, big ? EndianType.BigEndian : EndianType.LittleEndian);
        var text = AssetCreator.CreateTextAsset(collection); text.Name = new Utf8String("buffer-name"); text.Script_C49 = new Utf8String("buffer-payload");
        using MemoryStream body = new(); using (AssetWriter writer = new(body, collection)) text.WriteRelease(writer);
        SerializedFileBuilder builder = new() { Generation = (FormatVersion)generation, Version = version, Platform = BuildTarget.StandaloneWin64Player, EndianType = collection.EndianType, HasTypeTree = extras };
        SerializedType type = new() { TypeID = 49, ScriptTypeIndex = -1 }; builder.Types.Add(type);
        if (extras)
        {
            AddTree(type); SerializedTypeReference reference = new() { TypeID = 49, ScriptTypeIndex = -1, ClassName = new("RefMarker"), Namespace = new("NamespaceMarker"), AsmName = new("AssemblyMarker") }; AddTree(reference); builder.RefTypes.Add(reference);
            builder.ScriptTypes.Add(new() { LocalSerializedFileIndex = 0, LocalIdentifierInFile = 1234567890123 });
            builder.Dependencies.Add(new() { AssetPath = new("asset-marker"), PathName = "dep-marker.assets", PathNameOrigin = "dep-marker.assets" });
            builder.UserInformation = new("userinfo-marker");
        }
        for (int i = 0; i < count; i++) builder.Objects.Add(new() { FileID = i + 1, TypeID = 49, ClassID = 49, SerializedTypeIndex = 0, Type = type, ObjectData = body.ToArray() });
        using SerializedFile file = builder.Build(); using MemoryStream result = new(); file.Write(result); return result.ToArray();
    }
    private static void AddTree(SerializedTypeBase type)
    {
        type.OldType.Nodes.Add(new TypeTreeNode("int", "Base", 0, false) { TypeStrOffset = 0, NameStrOffset = 4 }); type.OldType.StringBuffer = "int\0Base\0"u8.ToArray();
    }
    [Theory][InlineData(false, int.MaxValue)][InlineData(true, int.MaxValue)][InlineData(false, 7)][InlineData(true, 7)]
    public void BufferedMetadataKeepsEndianShortReadCatalogueAndRepeatedLazyExports(bool big, int chunk)
    {
        byte[] bytes = Fixture(16, big); SerializedFileHeader header = Header(bytes); CountedStream? observed = null;
        FileBase ReadCounted(string ignored)
        {
            observed = new(bytes, 48, header.DataOffset, chunk); using SmartStream input = SmartStream.CreateOwned(observed); SerializedFile result = Read(input, FileReadMode.CompactMetadataOnly); Assert.Equal(0, observed.PayloadCalls); observed.AllowPayload = true; return result;
        }
        string path = Path.Combine(Temp.NewDirectory(), "metadata.assets"); File.WriteAllBytes(path, bytes);
        using UnitySession expected = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null));
        using UnitySession actual = UnitySession.FromGraph(UnitySource.OpenGraph(path, ReadCounted, compact: true));
        Assert.Equal(expected.Entries, actual.Entries); Assert.Equal(16, actual.Entries.Count); Assert.True(observed!.PayloadCalls > 0); Assert.Equal(0, observed.DisposeCalls);
        observed.AllowPayload = true;
        foreach (int i in new[] { 0, 8, 15, 0 })
        {
            using MemoryStream a = new(), b = new(); Assert.True(expected.Export(i, a)); Assert.True(actual.Export(i, b)); Assert.Equal(a.ToArray(), b.ToArray()); Assert.Equal("buffer-payload", Encoding.UTF8.GetString(b.ToArray()));
        }
        actual.Dispose(); Assert.Equal(1, observed.DisposeCalls);
    }
    private static void ReadMetadata(SerializedFileMetadata value, SmartStream stream, SerializedFileHeader header, SmartStream? owner, bool compact)
    {
        try { typeof(SerializedFileMetadata).GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(SmartStream), typeof(SerializedFileHeader), typeof(SmartStream), typeof(bool)], null)!.Invoke(value, [stream, header, owner, compact]); }
        catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); }
    }
    private static void AssertMetadata(SerializedFileMetadata expected, SerializedFileMetadata actual)
    {
        Assert.Equal(expected.UnityVersion, actual.UnityVersion); Assert.Equal(expected.TargetPlatform, actual.TargetPlatform); Assert.Equal(expected.EnableTypeTree, actual.EnableTypeTree); Assert.Equal(expected.LongFileID, actual.LongFileID); Assert.Equal(expected.SwapEndianess, actual.SwapEndianess); Assert.Equal(expected.UserInformation, actual.UserInformation);
        Assert.Equal(expected.Types, actual.Types); Assert.Equal(expected.ScriptTypes, actual.ScriptTypes); Assert.Equal(expected.Externals, actual.Externals); Assert.Equal(expected.RefTypes, actual.RefTypes); Assert.Equal(expected.Object.Length, actual.Object.Length);
        for (int i = 0; i < expected.Object.Length; i++)
        {
            ObjectInfo a = expected.Object[i], b = actual.Object[i]; Assert.Equal(a.FileID, b.FileID); Assert.Equal(a.TypeID, b.TypeID); Assert.Equal(a.SerializedTypeIndex, b.SerializedTypeIndex); Assert.Equal(a.ClassID, b.ClassID); Assert.Equal(a.IsDestroyed, b.IsDestroyed); Assert.Equal(a.ScriptTypeIndex, b.ScriptTypeIndex); Assert.Equal(a.Stripped, b.Stripped); Assert.Equal(a.DataOffset, b.DataOffset); Assert.Equal(a.DataSize, b.DataSize); Assert.Equal(a.HasBorrowedData, b.HasBorrowedData);
            int slot = Array.FindIndex(expected.Types, type => ReferenceEquals(type, a.Type)); Assert.Same(slot < 0 ? null : actual.Types[slot], b.Type);
        }
    }
    public static IEnumerable<object[]> UnderdeclaredWindows()
    {
        foreach (bool big in new[] { false, true }) foreach (string location in new[] { "signature", "types", "objects", "deps", "refs", "user" }) yield return [big, location];
    }
    [Theory][MemberData(nameof(UnderdeclaredWindows))]
    public void UnderdeclaredWindowRetriesOnlyTheMatureParserAndReplacesAllPartialMetadata(bool big, string location)
    {
        byte[] bytes = Fixture(3, big, extras: true); SerializedFileHeader header = Header(bytes);
        long size = location switch
        {
            "signature" => 1, "types" => 20, "objects" => ObjectOffset(bytes, big) - 48 + 9,
            "deps" => bytes.AsSpan().IndexOf("dep-marker.assets"u8) - 48 + 3,
            "refs" => bytes.AsSpan().IndexOf("RefMarker"u8) - 48 + 3,
            _ => bytes.AsSpan().IndexOf("userinfo-marker"u8) - 48 + 3
        };
        Assert.True(size > 0 && size < header.MetadataSize); header.MetadataSize = size;
        using CountedStream legacyPhysical = new(bytes, 48, header.DataOffset), compactPhysical = new(bytes, 48, header.DataOffset);
        using SmartStream legacy = SmartStream.CreateOwned(legacyPhysical), compact = SmartStream.CreateOwned(compactPhysical);
        legacy.Position = compact.Position = 48; SerializedFileMetadata expected = new(), actual = new(); expected.Read(legacy, header, legacy); ReadMetadata(actual, compact, header, compact, true);
        AssertMetadata(expected, actual); Assert.Equal(legacy.Position, compact.Position); Assert.Equal(0, legacyPhysical.PayloadCalls); Assert.Equal(0, compactPhysical.PayloadCalls); Assert.Equal(0, compactPhysical.DisposeCalls);
        // Public header recognition requires at least13bytes; every eligible real-file case also keeps catalogue/exports.
        if (size >= 13)
        {
            using MemoryStream changed = new(bytes, true); header.Write(changed); string path = Path.Combine(Temp.NewDirectory(), "underdeclared.assets"); File.WriteAllBytes(path, bytes);
            using UnitySession a = UnitySession.FromGraph(UnitySource.OpenGraph(path, readFile: null)), b = UnitySession.Open(path); Assert.Equal(a.Entries, b.Entries); Assert.Equal(3, b.Entries.Count);
            using MemoryStream x = new(), y = new(); Assert.True(a.Export(2, x)); Assert.True(b.Export(2, y)); Assert.Equal(x.ToArray(), y.ToArray());
        }
    }
    private static int ObjectOffset(byte[] bytes, bool big)
    {
        using SmartStream input = SmartStream.CreateMemory(bytes); using SerializedFile file = Read(input, FileReadMode.MetadataOnly);
        byte[] row = new byte[24]; long length = file.GetObjectInfo(0).DataSize;
        if (big) { BinaryPrimitives.WriteInt64BigEndian(row, 1); BinaryPrimitives.WriteInt32BigEndian(row.AsSpan(16), checked((int)length)); }
        else { BinaryPrimitives.WriteInt64LittleEndian(row, 1); BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(16), checked((int)length)); }
        SerializedFileHeader header = Header(bytes); int found = bytes.AsSpan(48, checked((int)(header.DataOffset - 48))).IndexOf(row); Assert.True(found >= 0); return found + 48;
    }
    [Theory][InlineData(false, 0)][InlineData(false, 1)][InlineData(false, 2)][InlineData(true, 0)][InlineData(true, 1)]
    public void GenuinePrematureEofOrSourceFailureDoesNotRetryAndKeepsLegacyLogicalCursor(bool big, int failure)
    {
        byte[] bytes = Fixture(4, big); SerializedFileHeader header = Header(bytes);
        Exception? sentinel = failure == 1 ? new IOException("read sentinel") : failure == 2 ? new OutOfMemoryException("allocation sentinel") : null;
        using CountedStream a = new(bytes, 48, header.DataOffset) { FaultAt = 61, Failure = sentinel }, b = new(bytes, 48, header.DataOffset) { FaultAt = 61, Failure = sentinel };
        using SmartStream x = SmartStream.CreateOwned(a), y = SmartStream.CreateOwned(b); x.Position = y.Position = 48;
        Exception? expected = Record.Exception(() => new SerializedFileMetadata().Read(x, header, x));
        Exception? actual = Record.Exception(() => ReadMetadata(new(), y, header, y, true));
        Assert.NotNull(expected); Assert.NotNull(actual); Assert.Equal(expected.GetType(), actual.GetType()); Assert.Equal(expected.Message, actual.Message);
        if (sentinel is not null) { Assert.Same(sentinel, expected); Assert.Same(sentinel, actual); }
        Assert.Equal(x.Position, y.Position); Assert.Equal(61, y.Position); Assert.True(a.Faults >= 1); Assert.True(b.Faults >= 1); Assert.Equal(1, b.MetadataStarts); Assert.Equal(0, b.PayloadCalls); Assert.Equal(0, b.DisposeCalls);
    }
    [Theory]
    [InlineData("owner-null")][InlineData("alignment")][InlineData("size-zero")][InlineData("size-negative")]
    [InlineData("start-beyond")][InlineData("size-beyond-file")][InlineData("start-after-data")][InlineData("size-after-data")]
    public void IneligibleMetadataWindowsKeepOriginalUnbufferedAcceptanceErrorsAndLogicalPosition(string scenario)
    {
        byte[] bytes = Fixture(2); SerializedFileHeader header = Header(bytes); long start = 48, payload = header.DataOffset;
        if (scenario == "alignment") { bytes = bytes[..48].Concat(new byte[1]).Concat(bytes[48..]).ToArray(); start = 49; header.DataOffset++; payload++; }
        if (scenario == "size-zero") header.MetadataSize = 0;
        if (scenario == "size-negative") header.MetadataSize = -1;
        if (scenario == "start-beyond") start = bytes.Length + 4;
        if (scenario == "size-beyond-file") header.MetadataSize = bytes.Length;
        if (scenario == "start-after-data") header.DataOffset = 47;
        if (scenario == "size-after-data") header.MetadataSize = header.DataOffset - start + 1;
        using CountedStream a = new(bytes, start, payload) { AllowPayload = scenario == "owner-null" }, b = new(bytes, start, payload) { AllowPayload = scenario == "owner-null" };
        using SmartStream x = SmartStream.CreateOwned(a), y = SmartStream.CreateOwned(b); x.Position = y.Position = start;
        SerializedFileMetadata expected = new(), actual = new();
        Exception? oldError = Record.Exception(() => expected.Read(x, header, scenario == "owner-null" ? null : x));
        Exception? newError = Record.Exception(() => ReadMetadata(actual, y, header, scenario == "owner-null" ? null : y, true));
        Assert.Equal(oldError?.GetType(), newError?.GetType()); Assert.Equal(oldError?.Message, newError?.Message); Assert.Equal(x.Position, y.Position);
        if (oldError is null) AssertMetadata(expected, actual);
        Assert.Equal(a.PayloadCalls, b.PayloadCalls); Assert.Equal(0, b.DisposeCalls);
        if (scenario == "owner-null") Assert.True(b.PayloadCalls > 0); else Assert.Equal(0, b.PayloadCalls);
    }
    [Theory][InlineData(FileReadMode.Eager, 22)][InlineData(FileReadMode.MetadataOnly, 22)][InlineData(FileReadMode.CompactMetadataOnly, 21)]
    public void DefaultModesAndOlderFormatsKeepTheirUnbufferedReaderAndPayloadPolicy(FileReadMode mode, int generation)
    {
        byte[] bytes = Fixture(256, generation: generation); SerializedFileHeader header = Header(bytes); long start = generation == 22 ? 48 : 20;
        using CountedStream physical = new(bytes, start, header.DataOffset) { AllowPayload = mode == FileReadMode.Eager }; using SmartStream input = SmartStream.CreateOwned(physical); using SerializedFile file = Read(input, mode);
        Assert.Equal(256, file.ObjectCount); Assert.True(physical.MetadataCalls > 256); Assert.Equal(mode == FileReadMode.Eager, physical.PayloadCalls > 0);
        physical.AllowPayload = true; Assert.Contains("buffer-payload", Encoding.UTF8.GetString(file.GetObjectInfo(0).ObjectData));
    }
    [Theory][InlineData("offset")][InlineData("length")][InlineData("type")][InlineData("count")]
    public void MalformedTableErrorsAreNotRetriedAndPreserveLegacyCursorAndPayloadGuard(string field)
    {
        byte[] bytes = Fixture(2); SerializedFileHeader header = Header(bytes); int row = ObjectOffset(bytes, false);
        if (field == "offset") BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(row + 8), -1);
        if (field == "length") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(row + 16), int.MaxValue);
        if (field == "type") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(row + 20), int.MaxValue);
        if (field == "count") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48 + Encoding.UTF8.GetByteCount("2021.3.0f1") + 1 + 4 + 1), -1);
        using CountedStream a = new(bytes, 48, header.DataOffset), b = new(bytes, 48, header.DataOffset);
        using SmartStream x = SmartStream.CreateOwned(a), y = SmartStream.CreateOwned(b); x.Position = y.Position = 48;
        Exception? expected = Record.Exception(() => new SerializedFileMetadata().Read(x, header, x));
        Exception? actual = Record.Exception(() => ReadMetadata(new(), y, header, y, true));
        Assert.NotNull(expected); Assert.NotNull(actual); Assert.Equal(expected.GetType(), actual.GetType()); Assert.Equal(expected.Message, actual.Message); Assert.Equal(x.Position, y.Position); Assert.Equal(1, b.MetadataStarts); Assert.Equal(0, b.PayloadCalls); Assert.Equal(0, b.DisposeCalls);
    }
    [Fact]
    public void NestedSlicesKeepIndependentLogicalPositionsAndPayloadLeaseAfterTemporaryBufferDisposal()
    {
        byte[] bytes = Fixture(8); SerializedFileHeader header = Header(bytes); byte[] padded = new byte[32 + bytes.Length + 32]; bytes.CopyTo(padded, 32);
        CountedStream physical = new(padded, 32 + 48, 32 + header.DataOffset); SmartStream root = SmartStream.CreateOwned(physical); root.Position = 7;
        SmartStream parent = root.CreateReadOnlySlice(24, bytes.Length + 16); parent.Position = 3; SmartStream input = parent.CreateReadOnlySlice(8, bytes.Length);
        SerializedFile file = Read(input, FileReadMode.CompactMetadataOnly); Assert.Equal(8, file.ObjectCount); Assert.Equal(48 + header.MetadataSize, input.Position); Assert.Equal(7, root.Position); Assert.Equal(3, parent.Position); Assert.Equal(0, physical.PayloadCalls);
        physical.AllowPayload = true; SmartStream body = file.GetObjectInfo(7).OpenDataStream();
        file.Dispose(); input.Dispose(); parent.Dispose(); root.Dispose(); Assert.Equal(0, physical.DisposeCalls);
        Assert.Contains("buffer-payload", Encoding.UTF8.GetString(body.ToArray())); Assert.Equal(0, body.Seek(0, SeekOrigin.Begin)); Assert.True(body.ReadByte() >= 0);
        body.Dispose(); Assert.Equal(1, physical.DisposeCalls);
    }
    [Fact]
    public void InterleavedMetadataParsingKeepsEachSourceTableTypesAndPayloadOwnerIndependent()
    {
        byte[] aBytes = Fixture(128), bBytes = Fixture(256); SerializedFileHeader ah = Header(aBytes), bh = Header(bBytes);
        using CountedStream a = new(aBytes, 48, ah.DataOffset), b = new(bBytes, 48, bh.DataOffset); using SmartStream x = SmartStream.CreateOwned(a), y = SmartStream.CreateOwned(b);
        SerializedFile? other = null; a.BeforeMetadataRead = () => other = Read(y, FileReadMode.CompactMetadataOnly);
        using SerializedFile first = Read(x, FileReadMode.CompactMetadataOnly); using SerializedFile second = other!;
        Assert.NotNull(second); Assert.Equal(128, first.ObjectCount); Assert.Equal(256, second.ObjectCount); Assert.Equal(0, a.PayloadCalls); Assert.Equal(0, b.PayloadCalls);
        Assert.NotSame(first.Types[0], second.Types[0]); Assert.NotSame(first.GetObjectInfo(0).BorrowedSource, second.GetObjectInfo(0).BorrowedSource);
        Assert.Equal(48 + ah.MetadataSize, x.Position); Assert.Equal(48 + bh.MetadataSize, y.Position);
        a.AllowPayload = b.AllowPayload = true;
        foreach (var file in new[] { first, second }) Assert.Contains("buffer-payload", Encoding.UTF8.GetString(file.GetObjectInfo(file.ObjectCount - 1).ObjectData));
        first.Dispose(); x.Dispose(); Assert.Equal(1, a.DisposeCalls); Assert.Equal(0, b.DisposeCalls);
        Assert.Contains("buffer-payload", Encoding.UTF8.GetString(second.GetObjectInfo(0).ObjectData));
    }
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count); public override int Read(Span<byte> buffer) => inner.Read(buffer); public override int ReadByte() => inner.ReadByte();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { inner.Dispose(); base.Dispose(disposing); }
    }
    [Fact]
    public void PublicDefaultMetadataApiStillReadsAnEmptyTableFromAForwardOnlyMetadataStream()
    {
        byte[] bytes = Fixture(0); SerializedFileHeader header = Header(bytes);
        using SmartStream input = SmartStream.CreateOwned(new ForwardOnlyStream(new MemoryStream(bytes, 48, bytes.Length - 48, false)));
        SerializedFileMetadata metadata = new(); metadata.Read(input, header);
        Assert.Equal(UnityVersion.Parse("2021.3.0f1"), metadata.UnityVersion); Assert.Single(metadata.Types); Assert.Empty(metadata.Object); Assert.Empty(metadata.Externals); Assert.Empty(metadata.ScriptTypes); Assert.Empty(metadata.RefTypes);
    }
    private static void AssertDescriptor(ObjectInfo expected, ObjectInfo actual, SerializedFile expectedFile, SerializedFile actualFile)
    {
        Assert.Equal(expected.FileID, actual.FileID); Assert.Equal(expected.TypeID, actual.TypeID); Assert.Equal(expected.SerializedTypeIndex, actual.SerializedTypeIndex);
        Assert.Equal(expected.ClassID, actual.ClassID); Assert.Equal(expected.IsDestroyed, actual.IsDestroyed); Assert.Equal(expected.ScriptTypeIndex, actual.ScriptTypeIndex); Assert.Equal(expected.Stripped, actual.Stripped);
        Assert.Equal(expected.DataOffset, actual.DataOffset); Assert.Equal(expected.DataSize, actual.DataSize); Assert.Equal(expected.HasObjectData, actual.HasObjectData); Assert.Equal(expected.HasBorrowedData, actual.HasBorrowedData);
        int slot = Array.FindIndex(expectedFile.Types.ToArray(), type => ReferenceEquals(type, expected.Type)); Assert.Same(slot < 0 ? null : actualFile.Types[slot], actual.Type);
    }
}
