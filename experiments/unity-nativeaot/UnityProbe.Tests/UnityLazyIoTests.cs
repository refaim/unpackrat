using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Buffers.Binary;
using System.Collections.Generic;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>Real parser reads must stay inside metadata until an object range is requested.</summary>
public sealed class UnityLazyIoTests
{
	[Fact]
	public void FailedOwningWrapperAllocationDisposesTheAlreadyOwnedBundleSource()
	{
		GuardedStream physical = new([1, 2, 3], long.MaxValue);
		using SmartStream source = SmartStream.CreateOwned(physical);
		Type streamType = typeof(SmartStream).Assembly.GetType("AssetRipper.IO.Files.BundleFiles.FileStream.BundleDataStream", true)!;
		Stream bundle = (Stream)Activator.CreateInstance(streamType, [source, new BlocksInfo()])!;
		Assert.Equal(2, source.RefCount);
		OutOfMemoryException failure = new("injected owning wrapper allocation");
		Func<Stream, SmartStream> allocate = stream => { Assert.Same(bundle, stream); throw failure; };
		MethodInfo? method = typeof(SmartStream).GetMethod("CreateOwned", BindingFlags.Static | BindingFlags.NonPublic,
			null, [typeof(Stream), allocate.GetType()], null);
		Assert.NotNull(method);
		TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [bundle, allocate]));
		Assert.Same(failure, thrown.InnerException);
		Assert.Equal(1, source.RefCount);
		Assert.Throws<ObjectDisposedException>(() => bundle.Length);
		source.Dispose();
		Assert.True(physical.Closed);
		Assert.Equal(1, physical.DisposeCalls);
	}

	[Fact]
	public void OwningBundleWrapperTransfersTheSourceUntilItsLastReferenceIsClosed()
	{
		GuardedStream physical = new([1, 2, 3], long.MaxValue);
		SmartStream source = SmartStream.CreateOwned(physical);
		Type streamType = typeof(SmartStream).Assembly.GetType("AssetRipper.IO.Files.BundleFiles.FileStream.BundleDataStream", true)!;
		Stream bundle = (Stream)Activator.CreateInstance(streamType, [source, new BlocksInfo()])!;
		SmartStream owner = SmartStream.CreateOwned(bundle);
		using SmartStream second = owner.CreateReference();
		source.Dispose();
		owner.Dispose();
		Assert.False(physical.Closed);
		Assert.Equal(0, second.Length);
		second.Dispose();
		Assert.True(physical.Closed);
		Assert.Equal(1, physical.DisposeCalls);
		Assert.Throws<ObjectDisposedException>(() => bundle.Length);
		Assert.Throws<ArgumentNullException>(() => SmartStream.CreateOwned(null!));
	}
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FailedSliceWrapperAllocationRollsBackTheAcquiredReference(bool failOuterWrapper)
	{
		GuardedStream physical = new([1, 2, 3], long.MaxValue);
		using SmartStream source = SmartStream.CreateOwned(physical);
		OutOfMemoryException failure = new("injected slice allocation");
		Func<SmartStream, object, long, long, Stream> makeRange = (owned, gate, offset, length) =>
		{
			Assert.Equal(2, source.RefCount);
			if (!failOuterWrapper) throw failure;
			Type type = typeof(SmartStream).Assembly.GetType("AssetRipper.IO.Files.Streams.OwnedReadOnlyRange", true)!;
			return (Stream)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null, [owned, gate, offset, length], null)!;
		};
		Func<Stream, SmartStream> makeWrapper = _ => throw failure;
		MethodInfo? method = typeof(SmartStream).GetMethod("CreateReadOnlySlice", BindingFlags.Instance | BindingFlags.NonPublic,
			null, [typeof(long), typeof(long), makeRange.GetType(), makeWrapper.GetType()], null);
		Assert.NotNull(method);
		TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(() => method.Invoke(source, [0L, 1L, makeRange, makeWrapper]));
		Assert.Same(failure, thrown.InnerException);
		Assert.Equal(1, source.RefCount);
		source.Dispose();
		Assert.True(physical.Closed);
	}

	[Fact]
	public void FailedBundleReaderAllocationRollsBackTheAcquiredSourceSlice()
	{
		GuardedStream physical = new([1, 2, 3], long.MaxValue);
		using SmartStream source = SmartStream.CreateOwned(physical);
		Type streamType = typeof(SmartStream).Assembly.GetType("AssetRipper.IO.Files.BundleFiles.FileStream.BundleDataStream", true)!;
		Type readerType = typeof(SmartStream).Assembly.GetType("AssetRipper.IO.Files.BundleFiles.FileStream.BundleFileBlockReader", true)!;
		Type delegateType = typeof(Func<,,>).MakeGenericType(typeof(SmartStream), typeof(BlocksInfo), readerType);
		OutOfMemoryException failure = new("injected block reader allocation");
		ReaderAllocationFault fault = new(source, failure);
		Delegate allocate = typeof(ReaderAllocationFault).GetMethod(nameof(ReaderAllocationFault.Allocate))!.MakeGenericMethod(readerType).CreateDelegate(delegateType, fault);
		ConstructorInfo? constructor = streamType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
			null, [typeof(SmartStream), typeof(BlocksInfo), delegateType], null);
		Assert.NotNull(constructor);
		TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(() => constructor.Invoke([source, new BlocksInfo(), allocate]));
		Assert.Same(failure, thrown.InnerException);
		Assert.Equal(2, fault.ReferencesDuringAllocation);
		Assert.Equal(1, source.RefCount);
		source.Dispose();
		Assert.True(physical.Closed);
	}

	private sealed class ReaderAllocationFault(SmartStream root, Exception failure)
	{
		public int ReferencesDuringAllocation { get; private set; }
		public T Allocate<T>(SmartStream source, BlocksInfo blocks)
		{
			ReferencesDuringAllocation = root.RefCount;
			throw failure;
		}
	}
	[Fact]
	public void CollectionCallsDescriptorOverrideBeforeEvaluatingObjectDataAndDefaultStillDelegates()
	{
		var fixture = Fixture();
		using GuardedStream physical = new(fixture.Bytes, fixture.Offset);
		using SmartStream input = SmartStream.CreateOwned(physical);
		using SerializedFile file = (SerializedFile)ReadMetadataOnly(input);
		DescriptorFactory deferred = new();
		using SerializedBundle lazyBundle = new();
		lazyBundle.AddCollectionFromSerializedFile(file, deferred);
		Assert.Equal(1, deferred.Descriptors);
		Assert.Equal(0, deferred.Bodies);
		Assert.Equal(0, physical.PayloadReads);
		physical.AllowPayload = true;
		EagerFactory legacy = new();
		using SerializedBundle eagerBundle = new();
		eagerBundle.AddCollectionFromSerializedFile(file, legacy);
		Assert.Equal(1, legacy.Bodies);
		Assert.Equal(fixture.Object, legacy.Data);
		Assert.False(file.Objects[0].HasObjectData);
	}

	private class EagerFactory : AssetFactoryBase
	{
		public int Bodies { get; private set; }
		public byte[] Data { get; private set; } = [];
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> bytes, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{
			Bodies++;
			Data = bytes.ToArray();
			return null;
		}
	}
	private sealed class DescriptorFactory : EagerFactory
	{
		public int Descriptors { get; private set; }
		public override IUnityObjectBase? ReadAsset(AssetInfo info, in ObjectInfo descriptor, IReadOnlyList<SerializedTypeReference> references)
		{
			Assert.False(descriptor.HasObjectData);
			Assert.True(descriptor.DataSize > 0);
			Descriptors++;
			return null;
		}
	}
	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void DescriptorReleaseRequiresBothMetadataOnlyAndFactoryOptIn(bool metadataOnly, bool release)
	{
		var fixture = Fixture();
		GuardedStream physical = new(fixture.Bytes, fixture.Offset) { AllowPayload = !metadataOnly };
		using SmartStream input = SmartStream.CreateOwned(physical);
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "selected.assets", "selected.assets",
			metadataOnly ? FileReadMode.MetadataOnly : FileReadMode.Eager);
		int initialPayloadReads = physical.PayloadReads;
		OwnedDescriptorFactory factory = new(release);
		using SerializedBundle bundle = new();
		bundle.AddCollectionFromSerializedFile(file, factory);
		Assert.Equal(1, factory.Validations);
		Assert.Equal(1, factory.Descriptors);
		Assert.Equal(metadataOnly && release ? 0 : 1, file.Objects.Length);
		Assert.Equal(initialPayloadReads, physical.PayloadReads);
		physical.AllowPayload = true;
		using (SmartStream first = factory.Copy.OpenDataStream())
		using (SmartStream second = factory.Copy.OpenDataStream())
		{
			Assert.Equal(fixture.Object[0], first.ReadByte());
			Assert.Equal(0, second.Position);
			Assert.Equal(fixture.Object, second.ToArray());
		}
		Assert.False(physical.Closed);
		file.Dispose();
		input.Dispose();
		Assert.True(physical.Closed);
		Assert.Equal(1, physical.DisposeCalls);
	}
	private sealed class OwnedDescriptorFactory(bool release) : EagerFactory
	{
		public override bool ReleaseSourceObjectDescriptors => release;
		public int Validations { get; private set; }
		public int Descriptors { get; private set; }
		public ObjectInfo Copy { get; private set; }
		public override void ValidateObjectTable(ReadOnlySpan<ObjectInfo> objects)
		{
			Assert.Equal(0, Descriptors);
			Assert.Equal(1, objects.Length);
			Validations++;
		}
		public override IUnityObjectBase? ReadAsset(AssetInfo info, in ObjectInfo descriptor, IReadOnlyList<SerializedTypeReference> references)
		{
			Assert.Equal(1, Validations);
			Copy = descriptor;
			Descriptors++;
			return null;
		}
	}
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FailedObjectTableValidationKeepsItsTableAndNeverReadsAnObjectBody(bool allocationFailure)
	{
		var fixture = Fixture();
		GuardedStream physical = new(fixture.Bytes, fixture.Offset);
		using SmartStream input = SmartStream.CreateOwned(physical);
		using SerializedFile file = (SerializedFile)ReadMetadataOnly(input);
		object original = typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
		Exception failure = allocationFailure ? new OutOfMemoryException("table sentinel") : new ArgumentException("table sentinel");
		TableFailureFactory factory = new(failure);
		using SerializedBundle bundle = new();
		Assert.Same(failure, Assert.ThrowsAny<Exception>(() => bundle.AddCollectionFromSerializedFile(file, factory)));
		Assert.Single(bundle.Collections);
		Assert.Equal(1, factory.Validations);
		Assert.Equal(0, factory.Bodies);
		Assert.Equal(0, physical.PayloadReads);
		Assert.Same(original, typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file));
		Assert.Equal(1, file.Objects.Length);
	}
	private sealed class TableFailureFactory(Exception failure) : EagerFactory
	{
		public override bool ReleaseSourceObjectDescriptors => true;
		public int Validations { get; private set; }
		public override void ValidateObjectTable(ReadOnlySpan<ObjectInfo> objects) { Validations++; throw failure; }
	}
	[Fact]
	public void DefaultObjectTableValidationKeepsLegacyNullFactoryBehavior()
	{
		using SmartStream input = SmartStream.CreateMemory(UnityFixtureBuilder.SerializedRepeatedTextAssets(2));
		using SerializedFile file = (SerializedFile)ReadMetadataOnly(input);
		ObjectInfo[] table = (ObjectInfo[])typeof(SerializedFile).GetField("m_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
		table[0].FileID = 0; table[1].FileID = 0;
		EagerFactory factory = new();
		using SerializedBundle bundle = new();
		var collection = bundle.AddCollectionFromSerializedFile(file, factory);
		Assert.Empty(collection);
		Assert.Equal(2, factory.Bodies);
		Assert.Equal(2, file.Objects.Length);
	}
	[Fact]
	public void ReadOnlySlicesEnforceBoundsRestoreSourceAndReportClosedState()
	{
		using GuardedStream physical = new([1, 2, 3, 4, 5, 6], 3);
		using SmartStream source = SmartStream.CreateOwned(physical);
		source.Position = 1;
		foreach (var invalid in new[] { (-1L, 1L), (0L, -1L), (7L, 0L), (5L, 2L), (long.MaxValue, long.MaxValue) })
			Assert.Throws<ArgumentOutOfRangeException>(() => source.CreateReadOnlySlice(invalid.Item1, invalid.Item2));
		using SmartStream slice = source.CreateReadOnlySlice(2, 3);
		Stream actual = (Stream)typeof(SmartStream).GetProperty("Stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(slice)!;
		Assert.True(actual.CanRead);
		Assert.True(actual.CanSeek);
		Assert.False(actual.CanWrite);
		Assert.Throws<InvalidOperationException>(() => slice.Read(new byte[3], 0, 3));
		Assert.Equal(1, source.Position);
		Assert.Equal(0, slice.Position);
		physical.AllowPayload = true;
		Assert.Equal(new byte[] { 3, 4, 5 }, slice.ToArray());
		Assert.Equal(1, source.Position);
		Assert.Equal(0, slice.Seek(0, SeekOrigin.Begin));
		Assert.Equal(1, slice.Seek(1, SeekOrigin.Current));
		Assert.Equal(2, slice.Seek(-1, SeekOrigin.End));
		Assert.Throws<ArgumentException>(() => slice.Seek(0, (SeekOrigin)42));
		Assert.Throws<ArgumentOutOfRangeException>(() => slice.Read(new byte[2], -1, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => slice.Read(new byte[2], 0, 3));
		Assert.Throws<NotSupportedException>(() => slice.SetLength(0));
		Assert.Throws<NotSupportedException>(() => slice.Write([7], 0, 1));
		slice.Flush();
		slice.Dispose();
		Assert.False(actual.CanRead);
		Assert.False(actual.CanSeek);
		Assert.Throws<ObjectDisposedException>(() => actual.ReadByte());
		Assert.Throws<ObjectDisposedException>(() => actual.Length);
		Assert.Throws<ObjectDisposedException>(() => actual.Position);
		Assert.Throws<ObjectDisposedException>(() => actual.Position = 0);
		Assert.Throws<ObjectDisposedException>(() => actual.Seek(0, SeekOrigin.Begin));
		Assert.Throws<ObjectDisposedException>(() => actual.Flush());
		actual.Dispose();
	}

	[Fact]
	public void LazyByteArrayAccessIsExplicitNonCachingAndCanBeReplacedByBuilderData()
	{
		var fixture = Fixture();
		using GuardedStream physical = new(fixture.Bytes, fixture.Offset) { AllowPayload = true };
		using SmartStream input = SmartStream.CreateOwned(physical);
		using SerializedFile file = (SerializedFile)ReadMetadataOnly(input);
		ObjectInfo descriptor = file.Objects[0];
		Assert.Equal(fixture.Object, descriptor.ObjectData);
		int reads = physical.PayloadReads;
		Assert.Equal(fixture.Object, descriptor.ObjectData);
		Assert.True(physical.PayloadReads > reads);
		Assert.False(descriptor.HasObjectData);
		descriptor.ObjectData = [9, 8];
		Assert.True(descriptor.HasObjectData);
		Assert.Equal(2, descriptor.DataSize);
		using SmartStream replaced = descriptor.OpenDataStream();
		Assert.Equal(new byte[] { 9, 8 }, replaced.ToArray());
		descriptor.ObjectData = null;
		Assert.False(descriptor.HasObjectData);
		Assert.Equal(0, descriptor.DataSize);
		Assert.Empty(descriptor.ObjectData);
		using SmartStream empty = descriptor.OpenDataStream();
		Assert.Equal(-1, empty.ReadByte());
	}

	[Fact]
	public void ReadingANewSerializedFileReleasesThePreviousFileOwnedSource()
	{
		var fixture = Fixture();
		GuardedStream first = new(fixture.Bytes, fixture.Offset);
		using SmartStream input = SmartStream.CreateOwned(first);
		using SerializedFile file = (SerializedFile)ReadMetadataOnly(input);
		input.Dispose();
		Assert.False(first.Closed);
		using SmartStream second = SmartStream.CreateMemory(fixture.Bytes);
		file.Read(second);
		Assert.True(first.Closed);
		Assert.False(file.Objects[0].HasObjectData);
	}

	[Fact]
	public void FailedRereadRollsBackTheNewLeaseAndKeepsTheOldFileUsable()
	{
		var fixture = Fixture();
		GuardedStream physical = new(fixture.Bytes, fixture.Offset) { AllowPayload = true };
		using SmartStream original = SmartStream.CreateOwned(physical);
		using SerializedFile file = (SerializedFile)ReadMetadataOnly(original);
		original.Dispose();
		byte[] invalid = fixture.Bytes.ToArray();
		BinaryPrimitives.WriteInt64BigEndian(invalid.AsSpan(32), -1);
		using SmartStream other = SmartStream.CreateMemory(invalid);
		Assert.Throws<InvalidDataException>(() => file.Read(other));
		Assert.Equal(1, other.RefCount);
		Assert.False(physical.Closed);
		using SmartStream body = file.Objects[0].OpenDataStream();
		Assert.Equal(fixture.Object, body.ToArray());
		body.Dispose();
		file.Dispose();
		Assert.True(physical.Closed);
	}

	[Fact]
	public void DirectLegacySchemeAndUnknownResourceRemainUsable()
	{
		var fixture = Fixture();
		using SmartStream input = SmartStream.CreateMemory(fixture.Bytes);
		using SerializedFile file = SerializedFileScheme.Default.Read(input, "direct.assets", "direct.assets");
		Assert.True(file.Objects[0].HasObjectData);
		using SmartStream unknown = SmartStream.CreateMemory(new byte[80]);
		using ResourceFile resource = Assert.IsType<ResourceFile>(SchemeReader.ReadFile(unknown, "unknown", "unknown", FileReadMode.MetadataOnly));
		Assert.Equal(new byte[80], resource.ToByteArray());
	}

	[Theory]
	[InlineData(-1L, 0L, 0)]
	[InlineData(long.MaxValue, 0L, 0)]
	[InlineData(0L, -1L, 0)]
	[InlineData(0L, long.MaxValue, 0)]
	[InlineData(0L, 0L, -1)]
	[InlineData(0L, 0L, int.MaxValue)]
	public void InvalidObjectBoundsAreRejectedBeforeAnyPayloadRead(long dataOffset, long byteStart, int size)
	{
		var fixture = Fixture();
		byte[] bytes = fixture.Bytes.ToArray();
		byte[] pattern = new byte[24];
		BinaryPrimitives.WriteInt64LittleEndian(pattern, 1);
		BinaryPrimitives.WriteInt32LittleEndian(pattern.AsSpan(16), fixture.Object.Length);
		int table = bytes.AsSpan(0, (int)fixture.Offset).IndexOf(pattern);
		Assert.True(table >= 0);
		BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(32), dataOffset);
		BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(table + 8), byteStart);
		BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(table + 16), size);
		using GuardedStream physical = new(bytes, fixture.Offset);
		using SmartStream input = SmartStream.CreateOwned(physical);
		Assert.Throws<InvalidDataException>(() => ReadMetadataOnly(input));
		Assert.Equal(0, physical.PayloadReads);
		Assert.Equal(1, input.RefCount);
	}
	private static FileBase ReadMetadataOnly(SmartStream stream) =>
		SchemeReader.ReadFile(stream, "guarded.assets", "guarded.assets", FileReadMode.MetadataOnly);

	private static SmartStream Wrap(Stream stream) => (SmartStream)Activator.CreateInstance(
		typeof(SmartStream), BindingFlags.NonPublic | BindingFlags.Instance, null, [stream], null)!;

	private static SmartStream OpenObject(ObjectInfo entry) => entry.OpenDataStream();

	private static (byte[] Bytes, long Offset, byte[] Object) Fixture()
	{
		byte[] bytes = UnityFixtureBuilder.SerializedTextAsset("fixture", "payload");
		using MemoryStream headerStream = new(bytes, false);
		SerializedFileHeader header = new();
		header.Read(headerStream);
		using SmartStream eagerStream = SmartStream.CreateMemory(bytes, 0, bytes.Length, false);
		using SerializedFile eager = (SerializedFile)SchemeReader.ReadFile(eagerStream, "eager.assets", "eager.assets");
		return (bytes, header.DataOffset, eager.Objects[0].ObjectData);
	}

	[Fact]
	public void MetadataOnlyReadsTheObjectTableWithoutTouchingTheObjectBody()
	{
		var fixture = Fixture();
		using GuardedStream guarded = new(fixture.Bytes, fixture.Offset);
		using SmartStream input = Wrap(guarded);
		using SerializedFile file = (SerializedFile)ReadMetadataOnly(input);
		Assert.Equal(1, file.Objects.Length);
		ObjectInfo entry = file.Objects[0];
		Assert.False(entry.HasObjectData);
		Assert.Equal(fixture.Offset, entry.DataOffset);
		Assert.Equal(fixture.Object.LongLength, entry.DataSize);
		Assert.Equal(0, guarded.PayloadReads);
		guarded.AllowPayload = true;
		using SmartStream body = OpenObject(entry);
		Assert.Equal(fixture.Object, body.ToArray());
		Assert.True(guarded.PayloadReads > 0);
		Assert.False(entry.HasObjectData);
	}

	[Fact]
	public void ObjectStreamsHaveIndependentBoundedCursorsAndKeepTheirSourceAlive()
	{
		var fixture = Fixture();
		GuardedStream guarded = new(fixture.Bytes, fixture.Offset);
		SmartStream input = Wrap(guarded);
		SerializedFile file = (SerializedFile)ReadMetadataOnly(input);
		guarded.AllowPayload = true;
		using SmartStream first = OpenObject(file.Objects[0]);
		using SmartStream second = OpenObject(file.Objects[0]);
		file.Dispose();
		input.Dispose();
		Assert.False(guarded.Closed);
		Assert.Equal(fixture.Object[0], first.ReadByte());
		Assert.Equal(0, second.Position);
		Assert.Equal(fixture.Object[0], second.ReadByte());
		Assert.Equal(1, first.Position);
		Assert.Equal(first.Length, first.Seek(0, SeekOrigin.End));
		Assert.Equal(-1, first.ReadByte());
		Assert.Equal(0, first.Read(new byte[3], 0, 3));
		Assert.ThrowsAny<ArgumentException>(() => first.Position = -1);
		Assert.ThrowsAny<ArgumentException>(() => first.Position = first.Length + 1);
		Assert.ThrowsAny<ArgumentException>(() => first.Seek(long.MaxValue, SeekOrigin.Current));
		first.Dispose();
		Assert.False(guarded.Closed);
		second.Dispose();
		Assert.True(guarded.Closed);
	}

	[Fact]
	public void DefaultEagerReadStillLoadsTheSameBodyAndBuilderDataRemainsLoaded()
	{
		var fixture = Fixture();
		using GuardedStream guarded = new(fixture.Bytes, fixture.Offset) { AllowPayload = true };
		using SmartStream input = Wrap(guarded);
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "eager.assets", "eager.assets");
		Assert.True(guarded.PayloadReads > 0);
		Assert.Equal(fixture.Object, file.Objects[0].ObjectData);
		Assert.True(file.Objects[0].HasObjectData);
		ObjectInfo builder = new() { ObjectData = [] };
		Assert.True(builder.HasObjectData);
		using SmartStream empty = OpenObject(builder);
		Assert.Equal(0, empty.Length);
	}

	private sealed class GuardedStream(byte[] bytes, long payloadStart) : Stream
	{
		private readonly MemoryStream source = new(bytes, false);
		public bool AllowPayload { get; set; }
		public int PayloadReads { get; private set; }
		public bool Closed { get; private set; }
		public int DisposeCalls { get; private set; }
		public override bool CanRead => !Closed;
		public override bool CanSeek => !Closed;
		public override bool CanWrite => false;
		public override long Length => source.Length;
		public override long Position { get => source.Position; set => source.Position = value; }
		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
		public override int Read(Span<byte> buffer)
		{
			if (Position + Math.Min(buffer.Length, Length - Position) > payloadStart)
			{
				PayloadReads++;
				if (!AllowPayload) throw new InvalidOperationException("Metadata reader touched an object payload");
			}
			return source.Read(buffer);
		}
		public override int ReadByte()
		{
			Span<byte> one = stackalloc byte[1];
			return Read(one) == 0 ? -1 : one[0];
		}
		public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
		public override void Flush() { }
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		protected override void Dispose(bool disposing) { DisposeCalls++; Closed = true; source.Dispose(); base.Dispose(disposing); }
	}
}
