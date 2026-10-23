using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AssetRipper.IO.Files.Streams.Smart;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityFlatRangeTests
{
	private static Stream Range(SmartStream stream) => (Stream)typeof(SmartStream)
		.GetProperty("Stream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stream)!;

	[Fact]
	public void DescendantOwnsItsSourceWithoutRetainingDisposedIntermediateRanges()
	{
		SourceStream physical = new();
		SmartStream root = SmartStream.CreateOwned(physical);
		var opened = OpenDescendant(root);
		using SmartStream child = opened.Child;
		root.Dispose();
		Assert.False(physical.Closed);
		Assert.Equal(new byte[] { 6, 7 }, ReadRemaining(child));
		Assert.Equal(-1, child.ReadByte());
		child.Position = 0;
		Assert.Equal(5, child.ReadByte());
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
		Assert.All(opened.Parents, parent => Assert.False(parent.IsAlive,
			"A descendant must lease the canonical source, not retain discarded parent ranges."));
		child.Dispose();
		Assert.True(physical.Closed);
		Assert.Equal(1, physical.DisposeCalls);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static (SmartStream Child, WeakReference[] Parents) OpenDescendant(SmartStream root)
	{
		root.Position = 13;
		using SmartStream parent = root.CreateReadOnlySlice(2, 10);
		parent.Position = 7;
		using SmartStream middle = parent.CreateReadOnlySlice(1, 7);
		middle.Position = 2;
		SmartStream child = middle.CreateReadOnlySlice(2, 3);
		Assert.Equal(5, child.ReadByte());
		Assert.Equal(13, root.Position);
		Assert.Equal(7, parent.Position);
		Assert.Equal(2, middle.Position);
		return (child, [new(Range(parent)), new(Range(middle))]);
	}

	private static byte[] ReadRemaining(Stream stream)
	{
		using MemoryStream output = new();
		stream.CopyTo(output);
		return output.ToArray();
	}

	[Fact]
	public void NestedSliceUsesTheCanonicalSourceGateAndComposedOffset()
	{
		using SourceStream physical = new();
		using SmartStream root = SmartStream.CreateOwned(physical);
		using SmartStream parent = root.CreateReadOnlySlice(4, 8);
		Func<SmartStream, object, long, long, Stream> createRange = (owned, gate, offset, length) =>
		{
			// Descendants own a source lease; their discarded parent is no longer an owner.
			Assert.Equal(3, root.RefCount);
			Assert.Equal(1, parent.RefCount);
			Assert.Same(physical, gate);
			Assert.Equal(6, offset);
			Assert.Equal(2, length);
			return NewRange(owned, gate, offset, length);
		};
		using SmartStream child = InvokeSlice(parent, 2, 2, createRange, Wrap);
		Assert.Equal(0, physical.ReadCalls);
		Assert.Equal(new byte[] { 6, 7 }, ReadRemaining(child));
		Assert.Equal(0, parent.Position);
	}

	[Fact]
	public void CanonicalLeaseKeepsTheCapturedPartialViewInsteadOfThePhysicalOwner()
	{
		string path = Path.Combine(Temp.NewDirectory(), "partial-view.bin");
		File.WriteAllBytes(path, Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
		FileStream physical = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		using SmartStream root = SmartStream.CreateOwned(physical);
		root.Position = 29;
		using SmartStream partial = root.CreatePartial(4, 12);
		partial.Position = 9;
		using SmartStream parent = partial.CreateReadOnlySlice(2, 8);
		parent.Position = 4;
		using SmartStream child = parent.CreateReadOnlySlice(1, 3);
		Assert.Equal(7, child.ReadByte());
		Assert.Equal(29, root.Position);
		Assert.Equal(9, partial.Position);
		Assert.Equal(4, parent.Position);
		parent.Dispose();
		partial.Dispose();
		root.Dispose();
		Assert.True(physical.CanRead);
		Assert.Equal(new byte[] { 8, 9 }, ReadRemaining(child));
		child.Dispose();
		Assert.False(physical.CanRead);
	}
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FailedNestedSliceAllocationReleasesTheCanonicalSourceLease(bool failWrapper)
	{
		SourceStream physical = new();
		using SmartStream root = SmartStream.CreateOwned(physical);
		using SmartStream parent = root.CreateReadOnlySlice(2, 8);
		OutOfMemoryException failure = new("nested range allocation sentinel");
		Func<SmartStream, object, long, long, Stream> createRange = (owned, gate, offset, length) =>
		{
			Assert.Equal(3, root.RefCount);
			Assert.Equal(1, parent.RefCount);
			Assert.Same(physical, gate);
			Assert.Equal(3, offset);
			if (!failWrapper) throw failure;
			return NewRange(owned, gate, offset, length);
		};
		Func<Stream, SmartStream> createWrapper = _ => throw failure;
		TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(() => InvokeSlice(parent, 1, 2, createRange, createWrapper));
		Assert.Same(failure, thrown.InnerException);
		Assert.Equal(2, root.RefCount);
		Assert.Equal(1, parent.RefCount);
		Assert.Equal(0, physical.ReadCalls);
		parent.Dispose();
		root.Dispose();
		Assert.Equal(1, physical.DisposeCalls);
	}

	[Fact]
	public void ThreeLevelsAndSiblingsKeepIndependentCursorsAndOriginalPositions()
	{
		using SourceStream physical = new();
		using SmartStream root = SmartStream.CreateOwned(physical);
		root.Position = 14;
		using SmartStream parent = root.CreateReadOnlySlice(2, 10);
		parent.Position = 8;
		using SmartStream middle = parent.CreateReadOnlySlice(1, 7);
		middle.Position = 5;
		using SmartStream first = middle.CreateReadOnlySlice(1, 3);
		using SmartStream second = middle.CreateReadOnlySlice(2, 3);
		Assert.Equal(4, first.ReadByte());
		Assert.Equal(5, second.ReadByte());
		Assert.Equal(5, first.ReadByte());
		Assert.Equal(1, second.Position);
		Assert.Equal(14, root.Position);
		Assert.Equal(8, parent.Position);
		Assert.Equal(5, middle.Position);
		Assert.Equal(0, first.Seek(-2, SeekOrigin.Current));
		Assert.Equal(2, first.Seek(-1, SeekOrigin.End));
		Assert.Equal(6, first.ReadByte());
	}

	[Theory]
	[InlineData(-1L, 0L)]
	[InlineData(0L, -1L)]
	[InlineData(7L, 0L)]
	[InlineData(5L, 2L)]
	[InlineData(long.MaxValue, long.MaxValue)]
	public void ParentBoundsAreRejectedBeforeAcquiringACanonicalLease(long offset, long length)
	{
		using SourceStream physical = new();
		using SmartStream root = SmartStream.CreateOwned(physical);
		using SmartStream parent = root.CreateReadOnlySlice(2, 6);
		Assert.Throws<ArgumentOutOfRangeException>(() => parent.CreateReadOnlySlice(offset, length));
		Assert.Equal(2, root.RefCount);
		Assert.Equal(1, parent.RefCount);
		Assert.Equal(0, physical.ReadCalls);
	}

	[Theory]
	[InlineData(0L, 0L)]
	[InlineData(6L, 0L)]
	[InlineData(0L, 6L)]
	[InlineData(5L, 1L)]
	public void EmptyEndAndNonemptyNestedSlicesKeepTheirDeclaredBounds(long offset, long length)
	{
		using SourceStream physical = new();
		using SmartStream root = SmartStream.CreateOwned(physical);
		root.Position = 15;
		using SmartStream parent = root.CreateReadOnlySlice(2, 6);
		parent.Position = 3;
		using SmartStream child = parent.CreateReadOnlySlice(offset, length);
		Assert.Equal(length, child.Length);
		Assert.Equal(Enumerable.Range(checked((int)(2 + offset)), checked((int)length)).Select(value => (byte)value).ToArray(), ReadRemaining(child));
		Assert.Equal(-1, child.ReadByte());
		Assert.Equal(length, child.Position);
		Assert.Equal(15, root.Position);
		Assert.Equal(3, parent.Position);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FailedNestedReadRestoresSourceAndAncestorPositionsAndCanBeRetried(bool seekFailure)
	{
		using SourceStream physical = new();
		using SmartStream root = SmartStream.CreateOwned(physical);
		root.Position = 12;
		using SmartStream parent = root.CreateReadOnlySlice(2, 10);
		parent.Position = 7;
		using SmartStream middle = parent.CreateReadOnlySlice(1, 7);
		middle.Position = 4;
		using SmartStream child = middle.CreateReadOnlySlice(1, 2);
		physical.FailRead = !seekFailure;
		physical.FailPosition = seekFailure ? 4 : null;
		Assert.Throws<IOException>(() => child.ReadByte());
		Assert.Equal(12, root.Position);
		Assert.Equal(7, parent.Position);
		Assert.Equal(4, middle.Position);
		Assert.Equal(0, child.Position);
		physical.FailRead = false;
		physical.FailPosition = null;
		Assert.Equal(4, child.ReadByte());
		Assert.Equal(1, child.Position);
		Assert.Equal(12, root.Position);
	}

	[Fact]
	public void DisposedParentCannotAcquireANewSourceLease()
	{
		using SourceStream physical = new();
		using SmartStream root = SmartStream.CreateOwned(physical);
		using SmartStream parent = root.CreateReadOnlySlice(2, 6);
		Range(parent).Dispose();
		Assert.Throws<ObjectDisposedException>(() => parent.CreateReadOnlySlice(0, 0));
		Assert.Equal(1, root.RefCount);
		Assert.Equal(0, physical.ReadCalls);
	}

	[Fact]
	public void CanonicalOffsetsNearInt64LimitKeepEmptyEndSlicesAndRejectOverflowingBounds()
	{
		using SourceStream physical = new(long.MaxValue);
		using SmartStream root = SmartStream.CreateOwned(physical);
		root.Position = 17;
		using SmartStream parent = root.CreateReadOnlySlice(long.MaxValue - 10, 10);
		using SmartStream child = parent.CreateReadOnlySlice(10, 0);
		using SmartStream descendant = child.CreateReadOnlySlice(0, 0);
		Assert.Equal(0, descendant.Length);
		Assert.Equal(-1, descendant.ReadByte());
		Assert.Throws<ArgumentOutOfRangeException>(() => parent.CreateReadOnlySlice(long.MaxValue, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => child.Seek(long.MaxValue, SeekOrigin.Current));
		Assert.Equal(17, root.Position);
		Assert.Equal(0, physical.ReadCalls);
	}

	private static Stream NewRange(SmartStream owned, object gate, long offset, long length)
		=> (Stream)Activator.CreateInstance(typeof(SmartStream).Assembly.GetType("AssetRipper.IO.Files.Streams.OwnedReadOnlyRange", true)!,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [owned, gate, offset, length], null)!;
	private static SmartStream Wrap(Stream source) => (SmartStream)Activator.CreateInstance(typeof(SmartStream),
		BindingFlags.Instance | BindingFlags.NonPublic, null, [source], null)!;
	private static SmartStream InvokeSlice(SmartStream source, long offset, long length,
		Func<SmartStream, object, long, long, Stream> createRange, Func<Stream, SmartStream> createWrapper)
		=> (SmartStream)typeof(SmartStream).GetMethod("CreateReadOnlySlice", BindingFlags.Instance | BindingFlags.NonPublic,
			null, [typeof(long), typeof(long), createRange.GetType(), createWrapper.GetType()], null)!.Invoke(source, [offset, length, createRange, createWrapper])!;

	private sealed class SourceStream(long length = 16) : Stream
	{
		private long position;
		public bool Closed { get; private set; }
		public int DisposeCalls { get; private set; }
		public int ReadCalls { get; private set; }
		public bool FailRead { get; set; }
		public long? FailPosition { get; set; }
		public override bool CanRead => !Closed;
		public override bool CanSeek => !Closed;
		public override bool CanWrite => false;
		public override long Length => length;
		public override long Position
		{
			get => position;
			set { if (value < 0 || value > length) throw new ArgumentOutOfRangeException(nameof(value)); position = value; if (FailPosition == value) throw new IOException("seek sentinel"); }
		}
		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
		public override int Read(Span<byte> buffer)
		{
			ReadCalls++;
			if (FailRead) throw new IOException("read sentinel");
			int count = (int)Math.Min(buffer.Length, length - position);
			for (int i = 0; i < count; i++) buffer[i] = unchecked((byte)position++);
			return count;
		}
		public override int ReadByte() { Span<byte> one = stackalloc byte[1]; return Read(one) == 0 ? -1 : one[0]; }
		public override long Seek(long offset, SeekOrigin origin) { Position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentException() }) + offset); return Position; }
		public override void Flush() { }
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		protected override void Dispose(bool disposing) { if (disposing) { DisposeCalls++; Closed = true; } base.Dispose(disposing); }
	}
}
