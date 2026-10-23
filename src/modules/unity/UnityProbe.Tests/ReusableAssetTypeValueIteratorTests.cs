using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Linq;
using AssetsTools.NET;
using Xunit;
using Xunit.Abstractions;
using Iterator = Unpackrat.UnityProbe.ReusableAssetTypeValueIterator;

namespace Unpackrat.UnityProbe.Tests;

public sealed class ReusableAssetTypeValueIteratorTests(ITestOutputHelper output)
{
	private static AssetTypeTemplateField Field(string name, AssetValueType kind, params AssetTypeTemplateField[] children)
		=> new() { Name = name, Type = name, ValueType = kind, HasValue = kind != AssetValueType.None,
			IsArray = kind is AssetValueType.Array or AssetValueType.ByteArray, Children = children.ToList() };
	private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] fields) => Field("Base", AssetValueType.None, fields);
	private static AssetTypeTemplateField Array(AssetTypeTemplateField element)
		=> Field("Array", AssetValueType.Array, Field("size", AssetValueType.Int32), element);

	[Fact]
	public void WarmedNestedAndRepeatedTraversalNeedsNoPerObjectEnumeratorAllocations()
	{
		AssetTypeTemplateField deep = Field("scalar", AssetValueType.Int32);
		for (int i = 0; i < 8; i++) deep = Field("composite", AssetValueType.None, deep);
		var template = Root(deep, Array(Field("data", AssetValueType.None, Field("value", AssetValueType.Int16))), Field("tail", AssetValueType.Int32));
		using MemoryStream input = new(new byte[] { 7, 0, 0, 0, 2, 0, 0, 0, 11, 0, 13, 0, 17, 0, 0, 0 }, false);
		using AssetsFileReader reader = new(input);
		Iterator iterator = new(template, reader);
		for (int warm = 0; warm < 16; warm++)
		{
			input.Position = 0; iterator.Reset(); while (iterator.ReadNext()) { }
		}
		const int iterations = 2048;
		long steps = 0;
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < iterations; i++)
		{
			input.Position = 0; iterator.Reset();
			while (iterator.ReadNext()) steps++;
		}
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.Equal(iterations * 15L, steps);
		Assert.Equal(16, input.Position);
		Assert.False(iterator.ReadNext());
		output.WriteLine($"{iterations} complete nested/array walks allocated {allocated} bytes ({allocated / iterations} per object).");
		Assert.True(allocated <= iterations * 8L, $"Warm traversal allocated {allocated} bytes; child-list boxing and repeated-array iterator objects must not be allocated per object.");
	}
	private static byte[] Data(Action<BinaryWriter> write)
	{
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) write(writer);
		return stream.ToArray();
	}
	private static void SameValue(AssetTypeValueField expected, AssetTypeValueField actual)
	{
		Assert.Same(expected.TemplateField, actual.TemplateField);
		Assert.Equal(expected.Value?.ValueType, actual.Value?.ValueType);
		if (expected.Value?.ValueType == AssetValueType.Array) Assert.Equal(expected.AsArray.size, actual.AsArray.size);
		else if (expected.Value?.ValueType == AssetValueType.ManagedReferencesRegistry)
		{
			Assert.Equal(expected.AsManagedReferencesRegistry.version, actual.AsManagedReferencesRegistry.version);
			Assert.Equal(expected.AsManagedReferencesRegistry.references.Count, actual.AsManagedReferencesRegistry.references.Count);
		}
		else Assert.Equal(expected.Value?.AsObject, actual.Value?.AsObject);
		Assert.Equal(expected.Children.Count, actual.Children.Count);
		for (int i = 0; i < expected.Children.Count; i++) SameValue(expected.Children[i], actual.Children[i]);
	}
	private static long CompareWalk(AssetTypeTemplateField template, byte[] data, string? materialize = null, bool bigEndian = false, RefTypeManager? manager = null)
	{
		using AssetsFileReader left = new(new MemoryStream(data, false)) { BigEndian = bigEndian };
		using AssetsFileReader right = new(new MemoryStream(data, false)) { BigEndian = bigEndian };
		AssetTypeValueIterator oracle = new(template, left, manager);
		Iterator actual = new(template, right, manager);
		int steps = 0;
		while (true)
		{
			bool expected = oracle.ReadNext();
			Assert.Equal(expected, actual.ReadNext());
			Assert.Equal(left.Position, right.Position);
			Assert.Equal(oracle.ReadPosition, actual.ReadPosition);
			Assert.Same(oracle.TempField, actual.TempField);
			Assert.Equal(oracle.Depth, actual.Depth);
			Assert.Equal<AssetTypeTemplateField>(oracle.TempFieldStack, actual.TempFieldStack);
			if (!expected) break;
			Assert.True(++steps < 1000);
			if (actual.TempField.Name == materialize)
			{
				AssetTypeValueField value = actual.ReadValueField();
				SameValue(oracle.ReadValueField(), value);
				Assert.Same(value, actual.ReadValueField());
				Assert.Equal(left.Position, right.Position);
				Assert.Equal<AssetTypeTemplateField>(oracle.TempFieldStack, actual.TempFieldStack);
			}
		}
		Assert.False(actual.ReadNext());
		return right.Position;
	}

	[Theory]
	[InlineData(AssetValueType.Bool, 1)]
	[InlineData(AssetValueType.Int8, 1)]
	[InlineData(AssetValueType.UInt8, 1)]
	[InlineData(AssetValueType.Int16, 2)]
	[InlineData(AssetValueType.UInt16, 2)]
	[InlineData(AssetValueType.Int32, 4)]
	[InlineData(AssetValueType.UInt32, 4)]
	[InlineData(AssetValueType.Int64, 8)]
	[InlineData(AssetValueType.UInt64, 8)]
	[InlineData(AssetValueType.Float, 4)]
	[InlineData(AssetValueType.Double, 8)]
	public void ScalarWidthsDepthAndMaterializationMatchThePinnedIterator(AssetValueType kind, int width)
	{
		var scalar = Field("value", kind);
		var template = Root(scalar, Field("tail", AssetValueType.UInt8));
		Assert.Equal(width + 1, CompareWalk(template, new byte[32]));
		Assert.Equal(width + 1, CompareWalk(template, new byte[32], "value"));
		scalar.IsAligned = true;
		Assert.Equal(((width + 3) & ~3) + 1, CompareWalk(template, new byte[32]));
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void ByteArraysKeepSkipMaterializeAndAlignmentPositions(bool aligned, bool materialize)
	{
		var bytes = Field("value", AssetValueType.ByteArray, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8));
		bytes.IsAligned = aligned;
		Assert.Equal((aligned ? 8 : 7) + (materialize ? 0 : 4), CompareWalk(Root(bytes, Field("tail", AssetValueType.Int32)), Data(w => { w.Write(3); w.Write(new byte[12]); }), materialize ? "value" : null));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(0)]
	[InlineData(1)]
	public void StringsPreserveMatureChildAlignmentPolicyAndValueCache(int aligned)
	{
		var text = Field("value", AssetValueType.String);
		if (aligned >= 0)
		{
			var child = Array(Field("data", AssetValueType.UInt8)); child.IsAligned = aligned == 1; text.Children.Add(child);
		}
		byte[] bytes = Data(w => { w.Write(3); w.Write(Encoding.UTF8.GetBytes("abc")); w.Write(new byte[8]); });
		var template = Root(text, Field("tail", AssetValueType.Int32));
		Assert.Equal(aligned == 0 ? 11 : 12, CompareWalk(template, bytes));
		// Pinned ReadType aligns materialized strings even when iterator skip uses an unaligned child.
		Assert.Equal(12, CompareWalk(template, bytes, "value"));
	}

	[Theory]
	[InlineData(AssetValueType.Int32, false, 2)]
	[InlineData(AssetValueType.None, false, 2)]
	[InlineData(AssetValueType.Array, false, 2)]
	[InlineData(AssetValueType.Array, true, 2)]
	[InlineData(AssetValueType.ByteArray, true, 2)]
	[InlineData(AssetValueType.Int32, false, 0)]
	public void RepeatedSubtreesAndMaterializedArrayDrainingMatchTheOracle(AssetValueType kind, bool aligned, int count)
	{
		AssetTypeTemplateField element = kind switch
		{
			AssetValueType.None => Field("data", kind, Field("nested", AssetValueType.Int32)),
			AssetValueType.Array or AssetValueType.ByteArray => Field("data", kind, Field("size", AssetValueType.Int32), Field("data", AssetValueType.UInt8)),
			_ => Field("data", kind),
		};
		element.IsAligned = aligned;
		var array = Array(element); array.IsAligned = true;
		var template = Root(array, Field("tail", AssetValueType.Int32));
		byte[] bytes = Data(w => { w.Write(count); w.Write(new byte[32]); });
		Assert.Equal(8 + 4 * count, CompareWalk(template, bytes));
		Assert.Equal(8 + 4 * count, CompareWalk(template, bytes, "Array"));
	}

	[Fact]
	public void EmptyCompositesNegativeArrayCountsAndCompositeMaterializationKeepPinnedBehavior()
	{
		var empty = Field("empty", AssetValueType.None);
		var array = Array(Field("data", AssetValueType.Int32)); array.IsAligned = true;
		var group = Field("group", AssetValueType.None, array, Field("number", AssetValueType.Int32)); group.IsAligned = true;
		var template = Root(empty, group, Field("tail", AssetValueType.UInt8));
		Assert.Equal(9, CompareWalk(template, new byte[16]));
		Assert.Equal(9, CompareWalk(template, new byte[16], "group"));
		Assert.Equal(9, CompareWalk(template, new byte[16], "empty"));
		Assert.Equal(5, CompareWalk(Root(Array(Field("data", AssetValueType.Int32)), Field("tail", AssetValueType.UInt8)), Data(w => { w.Write(-1); w.Write((byte)9); })));
	}

	[Fact]
	public void ResetConstructorOffsetsStackSnapshotsAndChangedReaderPreservePublicSemantics()
	{
		var old = Root(Field("a", AssetValueType.Int32), Field("group", AssetValueType.None, Field("b", AssetValueType.UInt8)));
		using AssetsFileReader first = new(new MemoryStream(new byte[32], false));
		using AssetsFileReader second = new(new MemoryStream(new byte[] { 0, 0, 0, 7, 0, 0, 0, 11 }, false)) { BigEndian = true };
		Iterator actual = new(old, first, 3);
		AssetTypeValueIterator oracle = new(old, new AssetsFileReader(new MemoryStream(new byte[32], false)), 3);
		Assert.Equal(0, actual.ReadPosition);
		Assert.True(actual.ReadNext()); Assert.True(oracle.ReadNext());
		Assert.Equal(oracle.Depth, actual.Depth);
		var snapshot = actual.TempFieldStack; snapshot.Clear(); Assert.Single(actual.TempFieldStack);
		first.Position = 3; actual.Reset(); Assert.Same(old, actual.TempField);
		Assert.True(actual.ReadNext()); Assert.Equal(0, actual.ReadPosition);
		var next = Root(Field("number", AssetValueType.Int32));
		actual.Reset(next, second, new RefTypeManager());
		Assert.Equal(-3, actual.ReadPosition); // Upstream's constructor basePosition is intentionally fixed.
		Assert.True(actual.ReadNext()); Assert.Equal(7, actual.ReadValueField().AsInt);
		Assert.False(actual.ReadNext()); Assert.Equal(4, second.Position);
		Assert.True(first.BaseStream.CanRead);
		first.Position = 0; actual.Reset(old, first); Assert.True(actual.ReadNext());
		Assert.Equal(0, actual.ReadValueField().AsInt);
	}

	[Fact]
	public void ListMutationKeepsTheMatureVersionFailureAndResetCanRecover()
	{
		var template = Root(Field("a", AssetValueType.Int32));
		using AssetsFileReader reader = new(new MemoryStream(new byte[16], false));
		Iterator actual = new(template, reader);
		AssetTypeValueIterator oracle = new(template, new AssetsFileReader(new MemoryStream(new byte[16], false)));
		Assert.True(actual.ReadNext()); Assert.True(oracle.ReadNext());
		template.Children.Add(Field("b", AssetValueType.Int32));
		Exception? expected = Record.Exception(() => oracle.ReadNext());
		Exception? failure = Record.Exception(() => actual.ReadNext());
		Assert.IsType<InvalidOperationException>(failure); Assert.Equal(expected!.Message, failure.Message);
		reader.Position = 0; actual.Reset(); int count = 0; while (actual.ReadNext()) count++;
		Assert.Equal(2, count); Assert.Equal(8, reader.Position);
	}

	[Fact]
	public void InvalidKindsAndSkippedOutOfBoundsReadsKeepThePinnedFailures()
	{
		var invalid = Field("invalid", (AssetValueType)255);
		using AssetsFileReader reader = new(new MemoryStream(new byte[4], false));
		Iterator actual = new(Root(invalid, Field("tail", AssetValueType.UInt8)), reader);
		Assert.True(actual.ReadNext());
		Assert.Equal("Invalid value type", Assert.Throws<Exception>(() => actual.Depth).Message);
		Assert.True(actual.ReadNext()); Assert.Equal(0, reader.Position);
		var truncated = Root(Field("tooWide", AssetValueType.Int64), Field("tail", AssetValueType.UInt8));
		actual.Reset(truncated, reader); Assert.True(actual.ReadNext());
		Assert.Equal("Iterator tried to read past reader's length.", Assert.Throws<EndOfStreamException>(() => actual.ReadNext()).Message);
		reader.Position = 0; actual.Reset(Root(Array(Field("data", AssetValueType.Int32))), reader);
		Assert.True(actual.ReadNext()); Assert.False(actual.ReadNext()); // Zero-length repeated frame.
		reader.Position = 3; actual.Reset(); Assert.Throws<EndOfStreamException>(() => actual.ReadNext());
		reader.Position = 0; actual.Reset(Root(Field("ok", AssetValueType.Int32)), reader);
		Assert.True(actual.ReadNext()); Assert.Equal(0, actual.ReadValueField().AsInt); Assert.False(actual.ReadNext());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ManagedRegistrySkipPreservesThePinnedErrorWhileMaterializationDelegatesToTheLibrary(bool manager)
	{
		var registry = Field("registry", AssetValueType.ManagedReferencesRegistry, Field("version", AssetValueType.Int32), Field("references", AssetValueType.Array));
		var template = Root(registry, Field("tail", AssetValueType.Int32));
		byte[] bytes = Data(w => { w.Write(2); w.Write(0); w.Write(7); });
		RefTypeManager? references = manager ? new RefTypeManager() : null;
		using AssetsFileReader reader = new(new MemoryStream(bytes, false));
		using AssetsFileReader control = new(new MemoryStream(bytes, false));
		Iterator actual = new(template, reader, references);
		AssetTypeValueIterator oracle = new(template, control, references);
		Assert.True(actual.ReadNext()); Assert.True(oracle.ReadNext());
		Assert.Equal(oracle.Depth, actual.Depth);
		Exception? expected = Record.Exception(() => oracle.ReadNext());
		Exception? failure = Record.Exception(() => actual.ReadNext());
		Assert.NotNull(expected); Assert.NotNull(failure); Assert.Equal(expected.GetType(), failure.GetType()); Assert.Equal(expected.Message, failure.Message);
		// Pinned ReadValueField drains the parent enumerator for registry/byte-array fields with children.
		if (manager) Assert.Equal(8, CompareWalk(template, bytes, "registry", manager: references));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void IdleFramesReleaseOldTemplateGraphsAfterCompleteOrEarlyReset(bool early)
	{
		using AssetsFileReader reader = new(new MemoryStream(new byte[32], false));
		Iterator actual = new(Root(), reader);
		WeakReference old = WalkAndWatchOldSchema(actual, reader, early);
		GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
		Assert.False(old.IsAlive, "Idle reusable frames must release the previous schema's child lists and array elements.");
		Assert.True(actual.ReadNext()); Assert.Equal(0, actual.ReadValueField().AsInt);
	}
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference WalkAndWatchOldSchema(Iterator actual, AssetsFileReader reader, bool early)
	{
		var leaf = Field("oldLeaf", AssetValueType.Int32);
		AssetTypeTemplateField deep = Array(leaf);
		for (int i = 0; i < 8; i++) deep = Field("oldGroup", AssetValueType.None, deep);
		reader.Position = 0; actual.Reset(Root(deep), reader);
		if (early) for (int i = 0; i < 8; i++) Assert.True(actual.ReadNext());
		else while (actual.ReadNext()) { }
		WeakReference result = new(leaf);
		reader.Position = 0; actual.Reset(Root(Field("next", AssetValueType.Int32)), reader);
		return result;
	}
	[Theory]
	[InlineData(AssetValueType.Array)]
	[InlineData(AssetValueType.ByteArray)]
	public void ArrayKindsWithoutTheArrayFlagKeepThePinnedZeroAdvanceBehavior(AssetValueType kind)
	{
		// Keep malformed flag/type handling compatible; do not silently interpret payloads anew.
		var malformed = Field("unflagged", kind); malformed.IsArray = false;
		var template = Root(malformed, Field("tail", AssetValueType.Int32));
		byte[] bytes = Data(w => w.Write(173));
		Assert.Equal(4, CompareWalk(template, bytes, "tail"));
		using AssetsFileReader reader = new(new MemoryStream(bytes, false));
		Iterator actual = new(template, reader);
		Assert.True(actual.ReadNext()); Assert.Same(malformed, actual.TempField);
		Assert.Equal(kind == AssetValueType.Array ? 0 : 1, actual.Depth);
		Assert.True(actual.ReadNext()); Assert.Equal(0, reader.Position);
		Assert.Equal(173, actual.ReadValueField().AsInt); Assert.False(actual.ReadNext());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void BorrowedReadAndSeekFailuresMatchTheOracleAndResetRecovers(bool read)
	{
		var template = read ? Root(Array(Field("data", AssetValueType.Int32)))
			: Root(Field("scalar", AssetValueType.Int32), Field("tail", AssetValueType.UInt8));
		using FaultStream source = new(); using FaultStream control = new();
		using AssetsFileReader reader = new(source); using AssetsFileReader left = new(control);
		Iterator actual = new(template, reader); AssetTypeValueIterator oracle = new(template, left);
		if (!read) { Assert.True(actual.ReadNext()); Assert.True(oracle.ReadNext()); }
		source.FailRead = control.FailRead = read; source.FailSeek = control.FailSeek = !read;
		Exception? expected = Record.Exception(() => oracle.ReadNext());
		Exception? failure = Record.Exception(() => actual.ReadNext());
		Assert.IsType<IOException>(failure); Assert.Equal(expected!.Message, failure.Message);
		Assert.True(source.CanRead);
		source.FailRead = source.FailSeek = false; reader.Position = 0;
		actual.Reset(Root(Field("recovered", AssetValueType.Int32)), reader);
		Assert.True(actual.ReadNext()); Assert.Equal(0, actual.ReadValueField().AsInt); Assert.False(actual.ReadNext());
	}
	private sealed class FaultStream() : MemoryStream(new byte[16], false)
	{
		public bool FailRead { get; set; }
		public bool FailSeek { get; set; }
		public override long Position { get => base.Position; set { if (FailSeek) throw new IOException("Borrowed seek failed."); base.Position = value; } }
		public override int Read(Span<byte> buffer) => FailRead ? throw new IOException("Borrowed read failed.") : base.Read(buffer);
	}
}
