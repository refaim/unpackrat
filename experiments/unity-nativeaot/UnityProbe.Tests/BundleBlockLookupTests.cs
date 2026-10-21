using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.Streams.Smart;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class BundleBlockLookupTests
{
    private static byte[][] Blocks(int count, int size = 128) => Enumerable.Range(0, count)
        .Select(index => Enumerable.Range(0, size).Select(offset => (byte)(index * 17 + offset)).ToArray()).ToArray();

    // Reuse the existing real UnityFS fixture writer, not a model of the reader.
    private static (byte[] Bytes, long DataOffset, long SecondBlock) Bundle(byte[][] blocks,
        (string Name, long Offset, long Size)[] entries, CompressionType compression)
        => ((byte[], long, long))typeof(UnityLazyBundleIoTests).GetMethod("Bundle", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [blocks, entries, compression])!;

    private sealed class Opened : IDisposable
    {
        public SmartStream Input { get; }
        public FileContainer Container { get; }
        public Opened(byte[] bytes, FileReadMode mode = FileReadMode.MetadataOnly, Stream? physical = null)
        {
            Input = physical is null ? SmartStream.CreateMemory(bytes) : SmartStream.CreateOwned(physical);
            Container = (FileContainer)SchemeReader.ReadFile(Input, "lookup.bundle", "lookup.bundle", mode);
            Assert.Empty(Container.FailedFiles);
        }
        public void Dispose()
        {
            foreach (var resource in Container.ResourceFiles) resource.Dispose();
            Container.Dispose(); Input.Dispose();
        }
    }

    private static byte[] ReadAt(Stream stream, long position, int count)
    {
        stream.Position = position; byte[] bytes = new byte[count]; stream.ReadExactly(bytes); return bytes;
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Lz4)]
    [InlineData(CompressionType.Lz4HC)]
    [InlineData(CompressionType.Lzma)]
    public void SameBlockRewindsAndCrossBlockSeeksReturnExactBytes(CompressionType compression)
    {
        byte[][] blocks = Blocks(17); byte[] all = blocks.SelectMany(b => b).ToArray();
        var fixture = Bundle(blocks, [("all.resS", 0, all.Length)], compression);
        using Opened opened = new(fixture.Bytes); Stream stream = opened.Container.ResourceFiles.Single().Stream;
        foreach (int offset in new[] { 9 * 128 + 13, 9 * 128 + 14, 9 * 128 + 12, 127, 128, 130, 2, 16 * 128 + 120 })
            Assert.Equal(all.Skip(offset).Take(8).ToArray(), ReadAt(stream, offset, 8));
        Assert.Equal(all.Length, stream.Position); Assert.Equal(-1, stream.ReadByte());
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Lz4)]
    public void EmptyBlocksExactBoundariesAndEmptyBundleAlwaysMakeProgress(CompressionType compression)
    {
        byte[][] blocks = [[], [1, 2], [], [], [3, 4], []];
        var fixture = Bundle(blocks, [("all.resS", 0, 4), ("empty.resS", 4, 0)], compression);
        using Opened opened = new(fixture.Bytes); Stream stream = opened.Container.ResourceFiles[0].Stream;
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ReadAt(stream, 0, 4));
        Assert.Equal(new byte[] { 3, 4 }, ReadAt(stream, 2, 2));
        Assert.Equal(new byte[] { 1, 2 }, ReadAt(stream, 0, 2));
        Assert.Equal(-1, opened.Container.ResourceFiles[1].Stream.ReadByte());
        Assert.Equal(0, stream.Read(Array.Empty<byte>(), 0, 0));
        var empty = Bundle([[], []], [("empty.resS", 0, 0)], compression);
        using Opened noData = new(empty.Bytes);
        Assert.Equal(-1, noData.Container.ResourceFiles.Single().Stream.ReadByte());
    }

    [Fact]
    public void InterleavedRangesKeepIndependentPositionsAndRevalidateTheBlockHint()
    {
        byte[][] blocks = Blocks(12); byte[] all = blocks.SelectMany(b => b).ToArray();
        var fixture = Bundle(blocks, [("left.resS", 0, all.Length), ("right.resS", 7 * 128, 3 * 128)], CompressionType.Lz4);
        using Opened opened = new(fixture.Bytes);
        using SmartStream left = opened.Container.ResourceFiles[0].Stream.CreateReference();
        using SmartStream right = opened.Container.ResourceFiles[1].Stream.CreateReference();
        Assert.Equal(all.Take(5).ToArray(), ReadAt(left, 0, 5));
        Assert.Equal(all.Skip(7 * 128 + 3).Take(8).ToArray(), ReadAt(right, 3, 8));
        Assert.Equal(5, left.Position); Assert.Equal(11, right.Position);
        Assert.Equal(all.Skip(5).Take(8).ToArray(), ReadAt(left, left.Position, 8));
        Assert.Equal(all.Skip(7 * 128 + 11).Take(8).ToArray(), ReadAt(right, right.Position, 8));
        Assert.Equal(13, left.Position); Assert.Equal(19, right.Position);
    }

    private sealed class FailingStream(byte[] bytes, long second) : MemoryStream(bytes, false)
    {
        public bool Fail { get; set; }
        public int Closes { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            if (Fail && Position >= second) throw new IOException("Synthetic block failure");
            return base.Read(buffer);
        }
        protected override void Dispose(bool disposing) { Closes++; base.Dispose(disposing); }
    }

    [Fact]
    public void FailedMissRetainsPositionAndCanRetryBothOldAndFailedBlocks()
    {
        byte[][] blocks = Blocks(2); var fixture = Bundle(blocks, [("all.resS", 0, 256)], CompressionType.Lz4);
        FailingStream physical = new(fixture.Bytes, fixture.SecondBlock);
        using (Opened opened = new(fixture.Bytes, physical: physical))
        {
            Stream stream = opened.Container.ResourceFiles.Single().Stream;
            Assert.Equal(blocks[0], ReadAt(stream, 0, 128));
            physical.Fail = true;
            Assert.Throws<IOException>(() => stream.ReadByte()); Assert.Equal(128, stream.Position);
            physical.Fail = false;
            Assert.Equal(blocks[0], ReadAt(stream, 0, 128));
            Assert.Equal(blocks[1], ReadAt(stream, 128, 128)); Assert.Equal(256, stream.Position);
        }
        Assert.Equal(1, physical.Closes);
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Lz4)]
    public void LegacyEagerReaderKeepsItsOffsetLookupAndExactSubrange(CompressionType compression)
    {
        byte[][] blocks = Blocks(12); byte[] all = blocks.SelectMany(b => b).ToArray();
        var fixture = Bundle(blocks, [("tail.resS", 7 * 128 + 5, 256)], compression);
        using Opened opened = new(fixture.Bytes, FileReadMode.Eager);
        Assert.Equal(all.Skip(7 * 128 + 5).Take(256).ToArray(), opened.Container.ResourceFiles.Single().ToByteArray());
    }

#if BLOCK_LOOKUP_DIAGNOSTICS
    private static Type Diagnostic => typeof(SmartStream).Assembly.GetType("BundleLookupDiagnostics", throwOnError: true)!;
    private static void Reset() => Diagnostic.GetMethod("Reset")!.Invoke(null, null);
    private static long Count(string field) => (long)Diagnostic.GetField(field)!.GetValue(null)!;

    [Fact]
    public void SameIntervalReadsPerformOneLookupRatherThanOneLookupPerScalar()
    {
        byte[][] blocks = Blocks(17); var fixture = Bundle(blocks, [("all.resS", 0, 17 * 128)], CompressionType.Lz4);
        using Opened opened = new(fixture.Bytes); Stream stream = opened.Container.ResourceFiles.Single().Stream;
        Reset();
        for (int i = 0; i < 64; i++)
        {
            stream.Position = 9 * 128 + i; Assert.Equal(blocks[9][i], stream.ReadByte());
        }
        Assert.Equal(1, Count("Searches")); Assert.InRange(Count("Comparisons"), 1, 8);
    }

    [Fact]
    public void IndexedMissesUseKnownOffsetsWithoutEitherPrefixScan()
    {
        byte[][] blocks = Blocks(17); var fixture = Bundle(blocks, [("all.resS", 0, 17 * 128)], CompressionType.Lz4);
        using Opened opened = new(fixture.Bytes); Stream stream = opened.Container.ResourceFiles.Single().Stream;
        Reset();
        foreach (int index in new[] { 2, 5, 7 }) Assert.Equal(blocks[index][0], ReadAt(stream, index * 128, 1)[0]);
        Assert.Equal(3, Count("Misses")); Assert.Equal(0, Count("EntryPrefix")); Assert.Equal(0, Count("RangePrefix"));
    }

    [Fact]
    public void LegacyReaderPositiveControlStillCountsItsOwnPrefixScan()
    {
        byte[][] blocks = Blocks(17); var fixture = Bundle(blocks, [("tail.resS", 7 * 128, 16)], CompressionType.Lz4);
        Reset();
        using Opened opened = new(fixture.Bytes, FileReadMode.Eager);
        Assert.Equal(blocks[7].Take(16).ToArray(), opened.Container.ResourceFiles.Single().ToByteArray());
        Assert.Equal(7, Count("EntryPrefix")); Assert.Equal(0, Count("RangePrefix"));
    }
#endif
}
