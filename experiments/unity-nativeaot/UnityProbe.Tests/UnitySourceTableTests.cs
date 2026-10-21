using System;
using System.IO;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Collections.Generic;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using Xunit;
using Xunit.Abstractions;
namespace Unpackrat.UnityProbe.Tests;

public sealed class UnitySourceTableTests(ITestOutputHelper output)
{
    private const FileReadMode CompactMode = FileReadMode.CompactMetadataOnly;
    private static SerializedFile Read(byte[] bytes, FileReadMode mode) { using SmartStream input = SmartStream.CreateMemory(bytes); return (SerializedFile)SchemeReader.ReadFile(input, "table.assets", "table.assets", mode); }
    [Fact]
    public void OptInBorrowedSourceTableUsesLessThanOneFullObjectInfoPerParsedRowAndPreservesEveryValue()
    {
        const int count = 4096;
        byte[] bytes = UnityFixtureBuilder.SerializedRepeatedTextAssets(count);
        using (SerializedFile warm = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(1), CompactMode)) { }
        using SerializedFile expected = Read(bytes, FileReadMode.MetadataOnly);
        long start = GC.GetAllocatedBytesForCurrentThread(); using SerializedFile actual = Read(bytes, CompactMode); long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        output.WriteLine($"4096 opt-in parsed source descriptors allocated {allocated} B (source bytes excluded).");
        Assert.True(allocated <= 48 * count + 32 * 1024, $"Opt-in source table allocated {allocated} B; its warmed parser must fit48B/row plus32KiB setup, without an eager body or64B ObjectInfo[] table.");
        Assert.Equal(count, actual.Objects.Length);
        SerializedType[] expectedTypes = expected.Types.ToArray(), actualTypes = actual.Types.ToArray();
        for (int i = 0; i < count; i++) AssertDescriptor(expected.Objects[i], actual.Objects[i], expectedTypes, actualTypes);
    }
    [Fact]
    public void ModernParsedRowsUseTwentyFourBytesPlusBoundedPerTypeSnapshots()
    {
        const int count = 4096;
        byte[] bytes = UnityFixtureBuilder.SerializedRepeatedTextAssets(count);
        using (SerializedFile warm = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(2), CompactMode)) { }
        using SerializedFile expected = Read(bytes, FileReadMode.MetadataOnly);
        long start = GC.GetAllocatedBytesForCurrentThread();
        using SerializedFile actual = Read(bytes, CompactMode);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        output.WriteLine($"4096 modern parsed descriptors allocated {allocated} B (source bytes excluded).");
        Assert.Equal(count, actual.ObjectCount);
        SerializedType[] expectedTypes = expected.Types.ToArray(), actualTypes = actual.Types.ToArray();
        for (int i = 0; i < count; i++) AssertDescriptor(expected.GetObjectInfo(i), actual.GetObjectInfo(i), expectedTypes, actualTypes);
        Assert.True(allocated <= 28 * count + 24 * 1024, $"Modern source descriptors allocated {allocated} B;24B rows and bounded per-type snapshots must fit28B/row plus24KiB setup.");
        Assert.Null(typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actual));
        string path = Path.Combine(Temp.NewDirectory(), "source24.assets"); File.WriteAllBytes(path, bytes);
        using UnitySession session = UnitySession.Open(path);
        Assert.Equal(count, session.Entries.Count);
        for (int i = 0; i < count; i++) { Assert.Equal(i + 1, session.Entries[i].PathId); Assert.Equal("notes", session.Entries[i].Name); Assert.Equal(7, session.Entries[i].SourceSize); }
        foreach (int index in new[] { 0, count / 2, count - 1, 0 }) { using MemoryStream body = new(); Assert.True(session.Export(index, body)); Assert.Equal("payload", Encoding.UTF8.GetString(body.ToArray())); }
    }
    [Fact]
    public void ModernTypeSnapshotsKeepParsedScalarsAndLiveTypeSlotAfterMutationAndReplacement()
    {
        using SerializedFile file = Read(MetadataFixture(22, false, 0), CompactMode);
        SerializedType originalType = file.Types[0]; ObjectInfo expected = file.GetObjectInfo(0);
        SerializedFile.ObjectTable captured = file.CaptureObjectTable();
        originalType.TypeID = 40000; originalType.ScriptTypeIndex = -7; originalType.IsStrippedType = !originalType.IsStrippedType;
        SerializedType[] types = (SerializedType[])typeof(SerializedFile).GetField("m_types", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
        SerializedType replacement = new() { TypeID = 1, ScriptTypeIndex = 3 }; types[0] = replacement;
        AssertDescriptor(expected, file.GetObjectInfo(0), [originalType], [replacement]);
        AssertDescriptor(expected, captured[0], [originalType], [replacement]);
        AssertDescriptor(expected, file.Objects[0], [originalType], [replacement]);
        SerializedType next = new() { TypeID = 2 }; types[0] = next;
        Assert.Same(replacement, file.GetObjectInfo(0).Type); Assert.Same(next, captured[0].Type);
        file.ReleaseObjectDescriptors(); AssertDescriptor(expected, captured[0], [originalType], [next]);
        Assert.Equal(2, captured.Count);
    }
    private sealed class CallbackStream(byte[] bytes, Action<long> callback) : Stream
    {
        private readonly MemoryStream inner = new(bytes, false);
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => false;
        public override bool CanSeek => inner.CanSeek;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) { callback(Position); return inner.Read(buffer); }
        public override int ReadByte() { callback(Position); return inner.ReadByte(); }
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void CallbackTypeScalarMutationRetainsEveryEarlierAndLaterParsedValue(int field)
    {
        byte[] bytes = MetadataFixture(22, true, 0, 3);
        using SerializedFile plain = Read(bytes, FileReadMode.MetadataOnly);
        byte[] first = new byte[24]; BinaryPrimitives.WriteInt64BigEndian(first, 1); BinaryPrimitives.WriteInt32BigEndian(first.AsSpan(16), 8);
        int firstRow = bytes.AsSpan().IndexOf(first); Assert.True(firstRow > 0);
        using SmartStream expectedSource = Parse(false, out SerializedFileMetadata expected);
        using SmartStream actualSource = Parse(true, out SerializedFileMetadata actual);
        object table = typeof(SerializedFileMetadata).GetProperty("CompactObject", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actual)!;
        Assert.Null(table.GetType().GetField("modern", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table));
        for (int i = 0; i < 3; i++) AssertDescriptor(expected.Object[i], actual.Object[i], expected.Types, actual.Types);
        Assert.Equal(49, actual.Object[0].TypeID); Assert.Equal(field == 0 ? 40000 : 49, actual.Object[1].TypeID);
        Assert.Equal((short)7, actual.Object[0].ScriptTypeIndex); Assert.Equal(field == 1 ? (short)9 : (short)7, actual.Object[2].ScriptTypeIndex);
        Assert.True(actual.Object[0].Stripped); Assert.Equal(field != 2, actual.Object[2].Stripped);
        SmartStream Parse(bool compact, out SerializedFileMetadata result)
        {
            SerializedFileMetadata metadata = new(); int calls = 0;
            SmartStream input = SmartStream.CreateOwned(new CallbackStream(bytes, position =>
            {
                if (position < firstRow + 24 || calls != 0) return;
                calls++; SerializedType type = metadata.Types[0];
                if (field == 0) type.TypeID = 40000; else if (field == 1) type.ScriptTypeIndex = 9; else type.IsStrippedType = false;
            }));
            SerializedFileHeader header = new(); header.Read(input);
            typeof(SerializedFileMetadata).GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(SmartStream), typeof(SerializedFileHeader), typeof(SmartStream), typeof(bool)], null)!.Invoke(metadata, [input, header, input, compact]);
            Assert.Equal(1, calls); result = metadata; return input;
        }
    }
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void CallbackNullTypeSlotsKeepMatureUsedAndUnusedSlotBehavior(int nullSlot)
    {
        SerializedFileBuilder builder = new() { Generation = FormatVersion.LargeFilesSupport, Version = UnityVersion.Parse("2021.3.0f1"), Platform = BuildTarget.StandaloneWin64Player, EndianType = EndianType.BigEndian };
        SerializedType type = new() { TypeID = 49 }; builder.Types.Add(type); builder.Types.Add(new SerializedType { TypeID = 50 });
        for (int i = 0; i < 3; i++) builder.Objects.Add(new ObjectInfo(type) { FileID = i + 1, SerializedTypeIndex = 0, ObjectData = [1, 2, 3, 4] });
        using SerializedFile built = builder.Build(); using MemoryStream encoded = new(); built.Write(encoded); byte[] bytes = encoded.ToArray();
        byte[] row = new byte[24]; BinaryPrimitives.WriteInt64BigEndian(row, 1); BinaryPrimitives.WriteInt32BigEndian(row.AsSpan(16), 4);
        int firstRow = bytes.AsSpan().IndexOf(row); Assert.True(firstRow > 4);
        using SmartStream expectedSource = Parse(false, out SerializedFileMetadata expected);
        using SmartStream actualSource = Parse(true, out SerializedFileMetadata actual);
        for (int i = 0; i < 3; i++) AssertDescriptor(expected.Object[i], actual.Object[i], expected.Types, actual.Types);
        Assert.Equal(nullSlot == 0 ? 0 : 49, actual.Object[0].TypeID); Assert.Equal(nullSlot == 0, actual.Object[0].Type is null);
        SmartStream Parse(bool compact, out SerializedFileMetadata result)
        {
            SerializedFileMetadata metadata = new(); int calls = 0;
            SmartStream input = SmartStream.CreateOwned(new CallbackStream(bytes, position =>
            {
                if (metadata.Types.Length != 2 || calls != 0) return;
                calls++; metadata.Types[nullSlot] = null!;
            }));
            SerializedFileHeader header = new(); header.Read(input);
            typeof(SerializedFileMetadata).GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(SmartStream), typeof(SerializedFileHeader), typeof(SmartStream), typeof(bool)], null)!.Invoke(metadata, [input, header, input, compact]);
            Assert.Equal(1, calls); result = metadata; return input;
        }
    }
    [Theory]
    [InlineData(-1)] [InlineData(int.MinValue)]
    public void LateNegativeRawTypeIndexFallsBackWithoutChangingEarlierRows(int rawIndex)
    {
        byte[] bytes = MetadataFixture(22, false, 3, 3);
        byte[] first = new byte[24]; BinaryPrimitives.WriteInt64LittleEndian(first, 1); BinaryPrimitives.WriteInt32LittleEndian(first.AsSpan(16), 8);
        int firstRow = bytes.AsSpan().IndexOf(first); Assert.True(firstRow > 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(firstRow + 24 + 20), rawIndex);
        using SerializedFile expected = Read(bytes, FileReadMode.MetadataOnly), actual = Read(bytes, CompactMode);
        Assert.Equal(3, actual.ObjectCount); Assert.Equal(rawIndex, actual.GetObjectInfo(1).SerializedTypeIndex);
        for (int i = 0; i < 3; i++) AssertDescriptor(expected.GetObjectInfo(i), actual.GetObjectInfo(i), expected.Types.ToArray(), actual.Types.ToArray());
        SerializedFile.ObjectTable capture = actual.CaptureObjectTable(); actual.ReleaseObjectDescriptors();
        Assert.Equal(rawIndex, capture[1].SerializedTypeIndex); Assert.Equal(3, capture.Count);
    }
    [Fact]
    public void OriginalMetadataReadMethodSignatureRemainsAvailable()
    {
        Assert.NotNull(typeof(SerializedFileMetadata).GetMethod("Read", [typeof(SmartStream), typeof(SerializedFileHeader), typeof(SmartStream)]));
    }

    public static IEnumerable<object[]> Layouts()
    {
        foreach (int generation in new[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22 })
            foreach (bool big in new[] { false, true }) foreach (int kind in new[] { 0, 1, 2 }) yield return [generation, big, kind];
    }
    private static byte[] MetadataFixture(int generation, bool big, int kind, int count = 2)
    {
        SerializedFileBuilder builder = new() { Generation = (FormatVersion)generation, Version = UnityVersion.Parse("2021.3.0f1"), Platform = BuildTarget.StandaloneWin64Player, EndianType = big ? EndianType.BigEndian : EndianType.LittleEndian };
        SerializedType? type = kind == 2 ? null : new SerializedType { TypeID = kind == 1 ? -1 : kind == 3 ? 0 : 49, ScriptTypeIndex = generation >= 16 ? (short)7 : (short)-1, IsStrippedType = generation >= 16 }; if (type is not null) { type.OldType.Nodes.Add(new AssetRipper.IO.Files.SerializedFiles.Parser.TypeTrees.TypeTreeNode("TextAsset", "Base", 0, false) { TypeStrOffset = 0, NameStrOffset = 10 }); type.OldType.StringBuffer = Encoding.UTF8.GetBytes("TextAsset\0Base\0"); builder.Types.Add(type); }
        for (int i = 0; i < count; i++) builder.Objects.Add(new ObjectInfo { FileID = i + 1, TypeID = kind == 1 ? -1 : kind == 3 ? 0 : 49, SerializedTypeIndex = kind == 2 ? -1 : 0, ClassID = 49, ScriptTypeIndex = 5, IsDestroyed = 17, Stripped = generation >= 16, Type = type, ObjectData = [1, 2, 3, 4, 5, 6, 7, 8] });
        using SerializedFile file = builder.Build(); using MemoryStream stream = new(); file.Write(stream); return stream.ToArray();
    }
    [Theory]
    [MemberData(nameof(Layouts))]
    public void IndexedParsedValuesPreserveAllSupportedGenerationEndianAndTypeFields(int generation, bool big, int kind)
    {
        byte[] bytes = MetadataFixture(generation, big, kind);
        using SerializedFile expected = Read(bytes, FileReadMode.MetadataOnly), actual = Read(bytes, CompactMode);
        Assert.Equal(expected.Objects.Length, actual.ObjectCount); Assert.Null(typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actual));
        SerializedType[] expectedTypes = expected.Types.ToArray(), actualTypes = actual.Types.ToArray();
        for (int i = 0; i < actual.ObjectCount; i++) AssertDescriptor(expected.Objects[i], actual.GetObjectInfo(i), expectedTypes, actualTypes);
        Assert.Throws<IndexOutOfRangeException>(() => actual.GetObjectInfo(-1)); Assert.Throws<IndexOutOfRangeException>(() => actual.GetObjectInfo(actual.ObjectCount));
        Assert.Null(typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actual));
        ObjectInfo original = actual.GetObjectInfo(0), copy = original; copy.FileID = 99; Assert.Equal(1, actual.GetObjectInfo(0).FileID); Assert.True(original.HasBorrowedData);
        ObjectInfo[] expanded = actual.Objects.ToArray(); Assert.Equal(actual.ObjectCount, expanded.Length); AssertDescriptor(expected.Objects[0], actual.GetObjectInfo(0), expectedTypes, actualTypes);
        using MemoryStream rewritten = new(); actual.Write(rewritten); using SerializedFile reread = Read(rewritten.ToArray(), CompactMode); Assert.Equal(actual.ObjectCount, reread.ObjectCount);
        actual.ReleaseObjectDescriptors(); Assert.Equal(0, actual.ObjectCount); Assert.Empty(actual.Objects.ToArray()); Assert.Throws<IndexOutOfRangeException>(() => actual.GetObjectInfo(0));
    }
    private sealed class LegacyReader : AssetFactoryBase
    {
        public int Validations, Calls; public override void ValidateObjectTable(ReadOnlySpan<ObjectInfo> rows) { Validations++; Assert.Equal(2, rows.Length); }
        public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> refs) { Calls++; Assert.NotEmpty(data.ToArray()); return null; }
    }
    [Theory]
    [InlineData(FileReadMode.Eager)]
    [InlineData(FileReadMode.MetadataOnly)]
    [InlineData(CompactMode)]
    public void LegacyFactoriesKeepSpanValidationRepeatableDataAndDescriptorValues(FileReadMode mode)
    {
        using SerializedFile file = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(2), mode); LegacyReader factory = new();
        using SerializedBundle first = new(), second = new(); first.AddCollectionFromSerializedFile(file, factory); second.AddCollectionFromSerializedFile(file, factory);
        Assert.Equal(2, factory.Validations); Assert.Equal(4, factory.Calls); Assert.Equal(2, file.ObjectCount); Assert.Equal(mode == FileReadMode.Eager, file.GetObjectInfo(0).HasObjectData);
        ObjectInfo[] table = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
        table[1].ObjectData = Encoding.UTF8.GetBytes("changed"); Assert.Equal(table[1].ObjectData, file.GetObjectInfo(1).ObjectData);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EmptyCompactTableCanBeViewedMaterializedRewrittenAndReleased(int compatibility)
    {
        using SerializedFile file = Read(MetadataFixture(22, false, 0, 0), CompactMode); Assert.Equal(0, file.ObjectCount);
        if (compatibility != 0) Assert.Empty(file.Objects.ToArray());
        using MemoryStream output = new(); file.Write(output); using SerializedFile copy = Read(output.ToArray(), CompactMode); Assert.Equal(0, copy.ObjectCount);
        file.ReleaseObjectDescriptors(); Assert.Equal(0, file.ObjectCount); Assert.Empty(file.Objects.ToArray());
    }
    private sealed class WideStream(byte[] bytes) : MemoryStream(bytes, false) { public bool Wide; public override long Length => Wide ? 4L * 1024 * 1024 * 1024 : base.Length; }
    [Fact]
    public void BorrowedIndexedValuesRetainWidePrototypeCopiesAndPayloadOwnershipAfterTableRelease()
    {
        using WideStream physical = new(UnityFixtureBuilder.SerializedTextAsset("row", "payload")); using SmartStream input = SmartStream.CreateOwned(physical);
        physical.Wide = true; using SerializedFile file = new(); typeof(FileBase).GetProperty("ReadMode")!.SetValue(file, CompactMode); file.Read(input); ObjectInfo original = file.GetObjectInfo(0);
        ObjectInfo wide = original.WithDataRange(9, original.DataOffset, 3L * 1024 * 1024 * 1024); Assert.Equal(3L * 1024 * 1024 * 1024, wide.DataSize); Assert.Same(original.Type, wide.Type); Assert.Same(original.BorrowedSource, wide.BorrowedSource);
        file.ReleaseObjectDescriptors(); using SmartStream slice = wide.OpenDataStream(); Assert.Equal(wide.DataSize, slice.Length); Assert.Equal(9, wide.FileID); Assert.True(input.CanRead);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataCompatibilityArrayIsAuthoritativeAfterMaterializationOrReplacement(bool replace)
    {
        byte[] bytes = UnityFixtureBuilder.SerializedRepeatedTextAssets(2); using SmartStream input = SmartStream.CreateMemory(bytes);
        SerializedFileHeader header = new(); header.Read(input); SerializedFileMetadata metadata = new();
        typeof(SerializedFileMetadata).GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(SmartStream), typeof(SerializedFileHeader), typeof(SmartStream), typeof(bool)], null)!.Invoke(metadata, [input, header, input, true]);
        if (replace) metadata.Object = [new ObjectInfo { FileID = 79, ObjectData = [1, 2, 3] }];
        else { Assert.Equal(2, metadata.Object.Length); metadata.Object[0].FileID = 79; }
        Assert.Equal(79, metadata.Object[0].FileID);
        Assert.Null(typeof(SerializedFileMetadata).GetProperty("CompactObject", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metadata));
        using SerializedFile file = new(); typeof(SerializedFile).GetMethod("SetProperties", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(file, [header, metadata]);
        Assert.Equal(replace ? 1 : 2, file.ObjectCount); Assert.Equal(79, file.GetObjectInfo(0).FileID);
        Assert.Same(metadata.Object, typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalMetadataReadApiKeepsEagerAndBorrowedValues(bool borrowed)
    {
        byte[] bytes = UnityFixtureBuilder.SerializedTextAsset("api", "payload"); using SmartStream input = SmartStream.CreateMemory(bytes);
        SerializedFileHeader header = new(); header.Read(input); SerializedFileMetadata metadata = new(); metadata.Read(input, header, borrowed ? input : null);
        Assert.Single(metadata.Object); Assert.Equal(1, metadata.Object[0].FileID); Assert.Equal(borrowed, metadata.Object[0].HasBorrowedData);
        using SerializedFile expected = Read(bytes, FileReadMode.Eager); Assert.Equal(expected.Objects[0].ObjectData, metadata.Object[0].ObjectData);
    }
    [Theory]
    [InlineData(-40000)]
    [InlineData(-32768)]
    [InlineData(49)]
    [InlineData(32767)]
    [InlineData(40000)]
    public void PublicDescriptorTypeConstructorPreservesTypeIdentityAndClassRange(int id)
    {
        SerializedType type = new() { TypeID = id, ScriptTypeIndex = 7, IsStrippedType = true }; ObjectInfo value = new(type);
        Assert.Same(type, value.Type); Assert.Equal(id, value.TypeID); Assert.Equal(id >= short.MinValue && id <= short.MaxValue ? (short)id : (short)0, value.ClassID); Assert.Equal(7, value.ScriptTypeIndex); Assert.True(value.Stripped);
    }
    [Theory]
    [MemberData(nameof(UnityLeanIndexTests.PathIdValidationCases), MemberType = typeof(UnityLeanIndexTests))]
    public void IndexedTableValidationKeepsAllPathIdsMixedFallbackAndCatalogueOrder(long[] ids, bool valid, bool mixed)
    {
        using SerializedFile builder = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(ids.Length), FileReadMode.Eager);
        ObjectInfo[] original = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(builder)!;
        for (int i = 0; i < ids.Length; i++) original[i].FileID = ids[i]; using MemoryStream bytes = new(); builder.Write(bytes);
        using SerializedFile file = Read(bytes.ToArray(), CompactMode);
        if (mixed && ids.Length > 0) { _ = file.Objects; ObjectInfo[] rows = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!; rows[^1].ObjectData = rows[^1].ObjectData; }
        UnitySource.LazyAssetFactory factory = new(new GameAssetFactory(new BaseManager(static _ => { })), compact: true); GameBundle root = new(); var collection = new AssetRipper.Assets.Collections.ProcessedAssetCollection(root);
        collection.SetLayout(UnityVersion.Parse("2021.3.0f1"));
        if (!valid) { Assert.Equal("objects", Assert.Throws<ArgumentException>(() => factory.ValidateObjectTable(collection, file)).ParamName); Assert.Equal(ids, Enumerable.Range(0, file.ObjectCount).Select(i => file.GetObjectInfo(i).FileID)); return; }
        var actual = root.AddCollectionFromSerializedFile(file, factory); using UnitySession session = UnitySession.FromGraph(factory.BuildCompactGraph(root, file, [actual], [], []));
        Assert.Equal(ids, session.Entries.Select(row => row.PathId)); Assert.All(session.Entries, row => { Assert.Equal("notes", row.Name); Assert.Equal(7, row.SourceSize); });
        if (ids.Length > 0) { using MemoryStream output = new(); Assert.True(session.Export(ids.Length - 1, output)); Assert.Equal("payload", Encoding.UTF8.GetString(output.ToArray())); }
    }
    [Theory]
    [InlineData(8, -1L)] [InlineData(8, long.MaxValue)]
    [InlineData(16, -1L)] [InlineData(16, int.MaxValue)] [InlineData(20, int.MaxValue)]
    public void CompactSourceStoragePreservesMatureParserFailuresForMalformedDescriptorRangesAndTypes(int field, long value)
    {
        byte[] bytes = UnityFixtureBuilder.SerializedRepeatedTextAssets(1);
        using SerializedFile expected = Read(bytes, FileReadMode.MetadataOnly);
        byte[] row = new byte[24]; BinaryPrimitives.WriteInt64LittleEndian(row, 1);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(16), checked((int)expected.GetObjectInfo(0).DataSize));
        int offset = bytes.AsSpan().IndexOf(row); Assert.True(offset > 0); Assert.Equal(-1, bytes.AsSpan(offset + 1).IndexOf(row));
        if (field == 8) BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + field), value);
        else BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + field), checked((int)value));
        Exception legacy = Assert.ThrowsAny<Exception>(() => { using var file = Read(bytes, FileReadMode.MetadataOnly); });
        Exception compact = Assert.ThrowsAny<Exception>(() => { using var file = Read(bytes, CompactMode); });
        Assert.Equal(legacy.GetType(), compact.GetType()); Assert.Equal(legacy.Message, compact.Message);
    }
    [Fact]
    public void ReleasedCompactTableDoesNotKeepDescriptorsAliveAndAnOpenedSliceKeepsItsPayloadOwner()
    {
        var state = ReadReleaseAndWatchCompactTable();
        using SerializedFile file = state.File; using SmartStream body = state.Body;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(state.Table.IsAlive); Assert.Equal(0, file.ObjectCount); Assert.Empty(file.Objects.ToArray());
        Assert.NotEmpty(body.ToArray()); Assert.Equal(0, body.Seek(0, SeekOrigin.Begin)); Assert.True(body.ReadByte() >= 0);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SerializedFile File, SmartStream Body, WeakReference Table) ReadReleaseAndWatchCompactTable()
    {
        SerializedFile file = Read(UnityFixtureBuilder.SerializedTextAsset("lifetime", "payload"), CompactMode);
        object table = typeof(SerializedFile).GetField("compactObjects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
        SmartStream body = file.GetObjectInfo(0).OpenDataStream(); file.ReleaseObjectDescriptors(); file.Dispose();
        return (file, body, new WeakReference(table));
    }
    public static IEnumerable<object[]> CallbackCases()
    {
        foreach (FileReadMode mode in new[] { FileReadMode.MetadataOnly, CompactMode })
            foreach (bool indexed in new[] { false, true })
                foreach (int action in new[] { 0, 1, 2, 3 }) yield return [mode, indexed, action];
    }
    [Theory][MemberData(nameof(CallbackCases))]
    public void ImportKeepsPostValidationTableSnapshotAcrossFactoryReleaseReplacementAndReread(FileReadMode mode, bool indexed, int action)
    {
        using SerializedFile file = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(2), mode);
        SnapshotFactory factory = new(file, indexed, action);
        using SerializedBundle bundle = new(); bundle.AddCollectionFromSerializedFile(file, factory);
        Assert.Equal(new long[] { 1, action == 3 ? 99 : 2 }, factory.Ids); Assert.Equal(1, factory.Completions);
        Assert.Equal(action == 0 ? 0 : action == 3 ? 2 : 1, file.ObjectCount);
        if (action is 1 or 2) Assert.Equal(79, file.GetObjectInfo(0).FileID);
    }
    private sealed class SnapshotFactory(SerializedFile file, bool indexed, int action) : AssetFactoryBase
    {
        public readonly List<long> Ids = []; public int Completions;
        public override void ValidateObjectTable(AssetRipper.Assets.Collections.AssetCollection collection, SerializedFile input)
        {
            Assert.Same(file, input);
            if (!indexed || action == 3) base.ValidateObjectTable(collection, input);
        }
        public override IUnityObjectBase? ReadAsset(AssetInfo info, in ObjectInfo descriptor, IReadOnlyList<SerializedTypeReference> references)
        {
            Ids.Add(descriptor.FileID);
            if (Ids.Count == 1)
            {
                if (action == 0) file.ReleaseObjectDescriptors();
                else if (action == 1)
                {
                    SerializedFileMetadata metadata = new() { Object = [new ObjectInfo { FileID = 79, ObjectData = [1] }] };
                    typeof(SerializedFile).GetMethod("SetProperties", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(file, [new SerializedFileHeader { Version = file.Generation }, metadata]);
                }
                else if (action == 2)
                {
                    using SerializedFile replacement = Read(UnityFixtureBuilder.SerializedRepeatedTextAssets(1), FileReadMode.Eager);
                    ((ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(replacement)!)[0].FileID = 79;
                    using MemoryStream bytes = new(); replacement.Write(bytes); using SmartStream source = SmartStream.CreateMemory(bytes.ToArray()); file.Read(source);
                }
                else ((ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!)[1].FileID = 99;
            }
            return null;
        }
        public override void CompleteCollection(AssetRipper.Assets.Collections.AssetCollection collection) => Completions++;
        public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> references) => throw new InvalidOperationException("Descriptor callback required.");
    }
    [Fact]
    public void EmptyAndReleasedImportViewsRemainEmptyWhenTheFileLaterReadsAnotherTable()
    {
        using SerializedFile file = new(); SerializedFile.ObjectTable before = file.CaptureObjectTable();
        Assert.Equal(0, before.Count); Assert.Throws<IndexOutOfRangeException>(() => before[0]);
        typeof(FileBase).GetProperty("ReadMode")!.SetValue(file, CompactMode);
        byte[] bytes = UnityFixtureBuilder.SerializedRepeatedTextAssets(2);
        using SmartStream input = SmartStream.CreateMemory(bytes); file.Read(input);
        SerializedFile.ObjectTable populated = file.CaptureObjectTable(); Assert.Equal(2, populated.Count);
        file.ReleaseObjectDescriptors(); SerializedFile.ObjectTable released = file.CaptureObjectTable();
        Assert.Equal(0, released.Count); Assert.Throws<IndexOutOfRangeException>(() => released[0]);
        using SmartStream second = SmartStream.CreateMemory(bytes); file.Read(second);
        Assert.Equal(0, before.Count); Assert.Equal(0, released.Count); Assert.Equal(2, populated.Count);
        Assert.Equal(2, populated[1].FileID); Assert.Equal(2, file.ObjectCount);
    }
    private static void AssertDescriptor(ObjectInfo expected, ObjectInfo actual, SerializedType[] expectedTypes, SerializedType[] actualTypes)
    {
        Assert.Equal(expected.FileID, actual.FileID); Assert.Equal(expected.TypeID, actual.TypeID); Assert.Equal(expected.SerializedTypeIndex, actual.SerializedTypeIndex);
        Assert.Equal(expected.ClassID, actual.ClassID); Assert.Equal(expected.IsDestroyed, actual.IsDestroyed); Assert.Equal(expected.ScriptTypeIndex, actual.ScriptTypeIndex); Assert.Equal(expected.Stripped, actual.Stripped);
        Assert.Equal(expected.DataOffset, actual.DataOffset); Assert.Equal(expected.DataSize, actual.DataSize); Assert.Equal(expected.HasObjectData, actual.HasObjectData); Assert.Equal(expected.HasBorrowedData, actual.HasBorrowedData);
        int type = Array.FindIndex(expectedTypes, value => ReferenceEquals(value, expected.Type)); Assert.Same(type < 0 ? null : actualTypes[type], actual.Type);
    }
}
