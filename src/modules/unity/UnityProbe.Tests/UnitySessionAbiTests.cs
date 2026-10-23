using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Drives the frozen Unity session C ABI. The repository-owned text-only fixture is the end-to-end
/// subject and every step goes through an unmanaged entry point; the remaining cases inject their own
/// session factory or graph so failure and lifetime paths stay hermetic.
/// </summary>
public sealed unsafe class UnitySessionAbiTests
{
	private const string FixtureName = "fixture";

	/// <summary>The chunk ceiling the frozen contract states, independent of the product constant.</summary>
	private const int FrozenChunkLimit = 65536;

	private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "text-only.assets");

	private static byte[] FixtureBytes => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "text-only.expected.txt"));

	// Exported entry points, reached through real unmanaged function pointers.

	private static int OpenExport(string path, out ulong session, out int count)
	{
		delegate* unmanaged[Cdecl]<ushort*, int, ulong*, int*, int> open = &NativeUnityApi.Open;
		ulong token = 7;
		int entryCount = 7;
		int result;
		fixed (char* pointer = path)
		{
			result = open((ushort*)pointer, path.Length, &token, &entryCount);
		}

		session = token;
		count = entryCount;
		return result;
	}

	private static int OpenExport(ushort* path, int pathLength, ulong* session, int* count)
	{
		delegate* unmanaged[Cdecl]<ushort*, int, ulong*, int*, int> open = &NativeUnityApi.Open;
		return open(path, pathLength, session, count);
	}

	private static int GetEntryExport(ulong session, int index, UnityEntry* entry, ushort* name, int nameCapacity)
	{
		delegate* unmanaged[Cdecl]<ulong, int, UnityEntry*, ushort*, int, int> getEntry = &NativeUnityApi.GetEntry;
		return getEntry(session, index, entry, name, nameCapacity);
	}

	private static int ExportExport(ulong session, int index, delegate* unmanaged[Cdecl]<void*, byte*, int, int> write, void* context)
	{
		delegate* unmanaged[Cdecl]<ulong, int, delegate* unmanaged[Cdecl]<void*, byte*, int, int>, void*, int> export = &NativeUnityApi.Export;
		return export(session, index, write, context);
	}

	private static int CloseExport(ulong session)
	{
		delegate* unmanaged[Cdecl]<ulong, int> close = &NativeUnityApi.Close;
		return close(session);
	}

	private static int GetSourceSizeExport(ulong session, int index, long* sizeOut)
	{
		delegate* unmanaged[Cdecl]<ulong, int, long*, int> getSourceSize = &NativeUnityApi.GetSourceSize;
		return getSourceSize(session, index, sizeOut);
	}

	private static int GetEntryPathExport(ulong session, int index, ushort* path, int pathCapacity, int* pathLength)
	{
		delegate* unmanaged[Cdecl]<ulong, int, ushort*, int, int*, int> getEntryPath = &NativeUnityApi.GetEntryPath;
		return getEntryPath(session, index, path, pathCapacity, pathLength);
	}

	private static int GetEntryExtensionExport(ulong session, int index, ushort* extension, int capacity, int* length)
	{
		delegate* unmanaged[Cdecl]<ulong, int, ushort*, int, int*, int> getEntryExtension = &NativeUnityApi.GetEntryExtension;
		return getEntryExtension(session, index, extension, capacity, length);
	}

	// The same boundary, reached directly so an injected factory can stand in for the file system.

	private static int OpenThrough(UnitySessionAbi abi, string path, out ulong session, out int count)
	{
		ulong token = 7;
		int entryCount = 7;
		int result;
		fixed (char* pointer = path)
		{
			result = abi.Open((ushort*)pointer, path.Length, &token, &entryCount);
		}

		session = token;
		count = entryCount;
		return result;
	}

	private static int ExportThrough(UnitySessionAbi abi, ulong session, int index, delegate* unmanaged[Cdecl]<void*, byte*, int, int> write, void* context)
		=> abi.Export(session, index, write, context);

	private static int GetEntryThrough(UnitySessionAbi abi, ulong session, int index)
	{
		UnityEntry entry = Descriptor();
		return abi.GetEntry(session, index, &entry, null, 0);
	}

	private static int ReentrantExportProbe(UnitySessionAbi abi, ulong session)
		=> abi.Export(session, 0, &NullSinkCallback, null);

	private static int ReentrantSourceSizeProbe(UnitySessionAbi abi, ulong session)
	{
		long size = -1;
		return abi.GetSourceSize(session, 0, &size);
	}

	private static int ReentrantEntryPathProbe(UnitySessionAbi abi, ulong session)
	{
		fixed (char* path = new char[8])
		{
			int length = -1;
			return abi.GetEntryPath(session, 0, (ushort*)path, 8, &length);
		}
	}

	private static int ReentrantExtensionProbe(UnitySessionAbi abi, ulong session)
	{
		fixed (char* extension = new char[8])
		{
			int length = -1;
			return abi.GetEntryExtension(session, 0, (ushort*)extension, 8, &length);
		}
	}

	private static UnitySessionAbi FakeAbi(int maxSessions, IUnityGraph graph)
		=> new(_ => UnitySession.FromGraph(graph), maxSessions);

	private static FakeGraph TextGraph(string name, byte[]? data, long pathId = 1)
		=> new(new FakeCollection("collected", new FakeText(pathId, data, name: name)));

	private static byte[] Canary(int length, byte value = 0xCC)
	{
		byte[] bytes = new byte[length];
		Array.Fill(bytes, value);
		return bytes;
	}

	private static void AssertUnchanged(byte[] bytes, int offset, int length, byte value = 0xCC)
	{
		for (int i = offset; i < offset + length; i++)
		{
			Assert.Equal(value, bytes[i]);
		}
	}

	private static UnityEntry Descriptor()
	{
		UnityEntry entry = default;
		entry.StructSize = UnityEntry.Size;
		return entry;
	}

	// Bridge: the exported ABI over the repository fixture.

	[Fact]
	public void TextOnlyFixtureRoundTripsThroughTheExportedEntryPoints()
	{
		Sink sink = new();
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out int count));
			Assert.NotEqual(0UL, session);
			Assert.Equal(1, count);

			UnityEntry entry = Descriptor();
			Assert.Equal(UnityAbi.Ok, GetEntryExport(session, 0, &entry, null, 0));
			Assert.Equal(32u, entry.StructSize);
			Assert.Equal(UnityAbi.KindTextRaw, entry.Kind);
			Assert.Equal(0, entry.CollectionIndex);
			Assert.Equal(1L, entry.PathId);
			Assert.Equal((uint)FixtureName.Length, entry.NameLength);
			Assert.Equal(0u, entry.Reserved);
			Assert.Equal(0u, entry.Reserved2);

			char[] name = new char[16];
			Array.Fill(name, 'Z');
			fixed (char* namePointer = name)
			{
				Assert.Equal(UnityAbi.Ok, GetEntryExport(session, 0, &entry, (ushort*)namePointer, FixtureName.Length));
			}

			Assert.Equal(FixtureName, new string(name, 0, FixtureName.Length));
			for (int i = FixtureName.Length; i < name.Length; i++)
			{
				// The label is copied without a terminator and nothing beyond it is touched.
				Assert.Equal('Z', name[i]);
			}

			Assert.Equal(UnityAbi.Ok, ExportExport(session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
			byte[] chunk = Assert.Single(sink.Chunks);
			Assert.Equal(FixtureBytes.Length, chunk.Length);
			Assert.Equal(FixtureBytes, chunk);

			Assert.Equal(UnityAbi.Ok, CloseExport(session));
			Assert.Equal(UnityAbi.InvalidHandle, CloseExport(session));
		}
		finally
		{
			handle.Free();
		}
	}

	[Fact]
	public void EntryLayoutIsExactlyThirtyTwoBytesWithTheDocumentedOffsets()
	{
		Assert.Equal(32, Marshal.SizeOf<UnityEntry>());
		Assert.Equal(0, Marshal.OffsetOf<UnityEntry>(nameof(UnityEntry.StructSize)).ToInt32());
		Assert.Equal(4, Marshal.OffsetOf<UnityEntry>(nameof(UnityEntry.Kind)).ToInt32());
		Assert.Equal(8, Marshal.OffsetOf<UnityEntry>(nameof(UnityEntry.CollectionIndex)).ToInt32());
		Assert.Equal(12, Marshal.OffsetOf<UnityEntry>(nameof(UnityEntry.Reserved)).ToInt32());
		Assert.Equal(16, Marshal.OffsetOf<UnityEntry>(nameof(UnityEntry.PathId)).ToInt32());
		Assert.Equal(24, Marshal.OffsetOf<UnityEntry>(nameof(UnityEntry.NameLength)).ToInt32());
		Assert.Equal(28, Marshal.OffsetOf<UnityEntry>(nameof(UnityEntry.Reserved2)).ToInt32());
	}

	// Open.

	[Fact]
	public void OpenRejectsInvalidPathArgumentsAndZeroesItsOutputs()
	{
		ulong session = 9;
		int count = 9;
		fixed (char* path = "0123456789")
		{
			Assert.Equal(UnityAbi.InvalidArgument, OpenExport(null, 4, &session, &count));
			Assert.Equal(0UL, session);
			Assert.Equal(0, count);

			Assert.Equal(UnityAbi.InvalidArgument, OpenExport((ushort*)path, 0, &session, &count));
			Assert.Equal(UnityAbi.InvalidArgument, OpenExport((ushort*)path, -3, &session, &count));
			Assert.Equal(UnityAbi.InvalidArgument, OpenExport((ushort*)path, ArgumentReader.MaxLength + 1, &session, &count));
			Assert.Equal(0UL, session);
			Assert.Equal(0, count);
		}
	}

	[Fact]
	public void OpenRejectsAnEmbeddedNulAndMissingOutputs()
	{
		ulong session = 9;
		int count = 9;
		string embedded = "bad\0name.assets";
		fixed (char* path = embedded)
		{
			Assert.Equal(UnityAbi.InvalidArgument, OpenExport((ushort*)path, embedded.Length, &session, &count));
			Assert.Equal(0UL, session);
			Assert.Equal(0, count);
		}

		string present = "anything.assets";
		fixed (char* path = present)
		{
			// Without a session output there is nowhere to report a handle, so nothing is opened.
			Assert.Equal(UnityAbi.InvalidArgument, OpenExport((ushort*)path, present.Length, null, &count));
			Assert.Equal(0, count);
			Assert.Equal(UnityAbi.InvalidArgument, OpenExport((ushort*)path, present.Length, &session, null));
			Assert.Equal(0UL, session);
		}
	}

	[Fact]
	public void OpenReportsMissingAndUnreadableInputAsDataFailure()
	{
		string directory = Temp.NewDirectory();
		Assert.Equal(UnityAbi.DataFailure, OpenExport(Path.Combine(directory, "absent.assets"), out ulong session, out int count));
		Assert.Equal(0UL, session);
		Assert.Equal(0, count);

		// A directory is present but not a Unity file.
		Assert.Equal(UnityAbi.DataFailure, OpenExport(directory, out session, out count));
		Assert.Equal(0UL, session);
		Assert.Equal(0, count);
	}

	[Fact]
	public void OpenMapsFactoryFailuresToDataFailure()
	{
		UnitySessionAbi loadFailure = new(_ => throw new UnityLoadException("unreadable"), 4);
		Assert.Equal(UnityAbi.DataFailure, OpenThrough(loadFailure, "input.assets", out ulong session, out int count));
		Assert.Equal(0UL, session);
		Assert.Equal(0, count);

		UnitySessionAbi unexpected = new(_ => throw new InvalidOperationException("unexpected"), 4);
		Assert.Equal(UnityAbi.DataFailure, OpenThrough(unexpected, "input.assets", out _, out _));
	}

	[Fact]
	public void OpenPropagatesFactoryOutOfMemory()
	{
		UnitySessionAbi exhausted = new(_ => throw new OutOfMemoryException(), 4);
		Assert.Throws<OutOfMemoryException>(() => OpenThrough(exhausted, "input.assets", out _, out _));
	}

	[Fact]
	public void RegistryConstructorRejectsNullFactoryAndUnsupportedCapacity()
	{
		Assert.Throws<ArgumentNullException>(() => new UnitySessionAbi(null!, 4));
		Assert.Throws<ArgumentOutOfRangeException>(() => new UnitySessionAbi(_ => throw new InvalidOperationException(), 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => new UnitySessionAbi(_ => throw new InvalidOperationException(), UnitySessionAbi.MaxRegistrySize + 1));
		Assert.NotNull(new UnitySessionAbi(_ => throw new InvalidOperationException(), UnitySessionAbi.MaxRegistrySize));
	}

	[Fact]
	public void CreateProductionReturnsAnIndependentRegistryPerCall()
	{
		UnitySessionAbi first = UnitySessionAbi.CreateProduction();
		UnitySessionAbi second = UnitySessionAbi.CreateProduction();
		Assert.NotSame(first, second);

		Assert.Equal(UnityAbi.Ok, OpenThrough(first, FixturePath, out ulong token, out int count));
		Assert.Equal(1, count);
		Assert.Equal(UnityAbi.Ok, first.Close(token));

		// Registries share no state, so the second one never issued that session.
		UnityEntry entry = Descriptor();
		Assert.Equal(UnityAbi.InvalidHandle, second.GetEntry(token, 0, &entry, null, 0));
	}

	// get_entry.

	[Fact]
	public void GetEntryRejectsForgedStaleAndZeroHandles()
	{
		UnityEntry probe = Descriptor();
		Assert.Equal(UnityAbi.InvalidHandle, GetEntryExport(0, 0, &probe, null, 0));
		Assert.Equal(UnityAbi.InvalidHandle, GetEntryExport(1, 0, &probe, null, 0));
		Assert.Equal(UnityAbi.InvalidHandle, GetEntryExport(ulong.MaxValue, 0, &probe, null, 0));

		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));
		Assert.Equal(UnityAbi.Ok, CloseExport(session));

		byte[] raw = Canary(48);
		fixed (byte* rawPointer = raw)
		{
			UnityEntry* entry = (UnityEntry*)(rawPointer + 8);
			entry->StructSize = UnityEntry.Size;
			Assert.Equal(UnityAbi.InvalidHandle, GetEntryExport(session, 0, entry, null, 0));
			Assert.Equal(0u, entry->NameLength);
			AssertUnchanged(raw, 0, 8);
			AssertUnchanged(raw, 40, 8);
		}
	}

	[Fact]
	public void GetEntryRejectsANullOrWrongSizedDescriptorWithoutWriting()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));
		Assert.Equal(UnityAbi.InvalidArgument, GetEntryExport(session, 0, null, null, 0));

		byte[] raw = Canary(48);
		fixed (byte* rawPointer = raw)
		{
			UnityEntry* entry = (UnityEntry*)(rawPointer + 8);
			entry->StructSize = 31;
			Assert.Equal(UnityAbi.InvalidArgument, GetEntryExport(session, 0, entry, null, 0));
			Assert.Equal(31u, entry->StructSize);

			entry->StructSize = 33;
			Assert.Equal(UnityAbi.InvalidArgument, GetEntryExport(session, 0, entry, null, 0));
			Assert.Equal(33u, entry->StructSize);
		}

		// A rejected size leaves everything but the caller-written size field untouched.
		AssertUnchanged(raw, 0, 8);
		AssertUnchanged(raw, 12, 36);
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	[Fact]
	public void GetEntryInitializesTheDescriptorBeforeAValidatedFailure()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		byte[] raw = Canary(48);
		fixed (byte* rawPointer = raw)
		{
			UnityEntry* entry = (UnityEntry*)(rawPointer + 8);
			entry->StructSize = UnityEntry.Size;
			Assert.Equal(UnityAbi.InvalidArgument, GetEntryExport(session, 0, entry, null, 5));
			Assert.Equal(UnityEntry.Size, entry->StructSize);
			Assert.Equal(0, entry->Kind);
			Assert.Equal(0, entry->CollectionIndex);
			Assert.Equal(0L, entry->PathId);
			Assert.Equal(0u, entry->NameLength);
			Assert.Equal(0u, entry->Reserved);
			Assert.Equal(0u, entry->Reserved2);
		}

		AssertUnchanged(raw, 0, 8);
		AssertUnchanged(raw, 40, 8);
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	[Fact]
	public void GetEntryReportsOutOfRangeIndexWithEmptyMetadata()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out int count));

		foreach (int index in new[] { -1, count })
		{
			UnityEntry entry = Descriptor();
			Assert.Equal(UnityAbi.OutOfRange, GetEntryExport(session, index, &entry, null, 0));
			Assert.Equal(UnityEntry.Size, entry.StructSize);
			Assert.Equal(0, entry.Kind);
			Assert.Equal(0, entry.CollectionIndex);
			Assert.Equal(0L, entry.PathId);
			Assert.Equal(0u, entry.NameLength);
		}

		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	[Fact]
	public void GetEntryRejectsInvalidNameArguments()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		char[] name = new char[8];
		Array.Fill(name, 'Z');
		fixed (char* namePointer = name)
		{
			UnityEntry entry = Descriptor();
			Assert.Equal(UnityAbi.InvalidArgument, GetEntryExport(session, 0, &entry, (ushort*)namePointer, -1));
			Assert.Equal(UnityEntry.Size, entry.StructSize);
			Assert.Equal(UnityAbi.InvalidArgument, GetEntryExport(session, 0, &entry, null, 3));

			// A given buffer is never written once the arguments are rejected.
			for (int i = 0; i < name.Length; i++)
			{
				Assert.Equal('Z', name[i]);
			}
		}

		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	[Fact]
	public void GetEntryReportsBufferTooSmallWithoutAPartialName()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		char[] name = new char[8];
		Array.Fill(name, 'Z');
		fixed (char* namePointer = name)
		{
			UnityEntry entry = Descriptor();
			Assert.Equal(UnityAbi.BufferTooSmall, GetEntryExport(session, 0, &entry, (ushort*)namePointer, FixtureName.Length - 1));
			Assert.Equal(UnityEntry.Size, entry.StructSize);
			Assert.Equal(UnityAbi.KindTextRaw, entry.Kind);
			Assert.Equal((uint)FixtureName.Length, entry.NameLength);
		}

		for (int i = 0; i < name.Length; i++)
		{
			Assert.Equal('Z', name[i]);
		}

		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	// get_source_size.

	[Fact]
	public void SourceSizeReportsTheDeclaredLengthOfTheFixtureTextAsset()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		long size = -1;
		Assert.Equal(UnityAbi.Ok, GetSourceSizeExport(session, 0, &size));
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
		Assert.Equal(FixtureBytes.Length, size);
	}

	[Fact]
	public void GetSourceSizeRejectsANullOutputAndForgedStaleAndZeroHandles()
	{
		Assert.Equal(UnityAbi.InvalidArgument, GetSourceSizeExport(0, 0, null));

		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		List<(int Result, long Size)> forgedHandles = new();
		foreach (ulong handle in new[] { 0UL, 1UL, ulong.MaxValue })
		{
			long size = -1;
			forgedHandles.Add((GetSourceSizeExport(handle, 0, &size), size));
		}

		Assert.Equal(UnityAbi.Ok, CloseExport(session));

		long stale = -1;
		int staleResult = GetSourceSizeExport(session, 0, &stale);

		Assert.Equal(
			new[]
			{
				(UnityAbi.InvalidHandle, 0L),
				(UnityAbi.InvalidHandle, 0L),
				(UnityAbi.InvalidHandle, 0L),
			},
			forgedHandles);
		Assert.Equal(UnityAbi.InvalidHandle, staleResult);
		Assert.Equal(0L, stale);
	}

	[Fact]
	public void GetSourceSizeReportsOutOfRangeIndexAndZeroesItsOutput()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out int count));

		List<(int Result, long Size)> attempts = new();
		foreach (int index in new[] { -1, count })
		{
			long size = -1;
			attempts.Add((GetSourceSizeExport(session, index, &size), size));
		}

		Assert.Equal(UnityAbi.Ok, CloseExport(session));

		Assert.Equal(new[] { (UnityAbi.OutOfRange, 0L), (UnityAbi.OutOfRange, 0L) }, attempts);
	}

	[Fact]
	public void GetSourceSizeReportsBusyDuringAnActiveExport()
	{
		UnitySessionAbi production = NativeUnityApi.Production;
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		List<int> attempts = new();
		Sink sink = new() { Reenter = _ => attempts.Add(ReentrantSourceSizeProbe(production, session)) };
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportExport(session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Equal(new[] { UnityAbi.Busy }, attempts);
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	[Fact]
	public void SourceSizeKeepsKnownProviderLengthsAndNormalizesNegativeOnes()
	{
		FakeGraph graph = new(
			new FakeCollection("first", new FakeTexture(10, [1], name: "hero", sourceSize: 24)),
			new FakeCollection("second", new FakeText(20, [2], name: "notes", sourceSize: -5)));
		UnitySessionAbi abi = FakeAbi(4, graph);

		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out int count));

		long first = -1;
		int firstResult = abi.GetSourceSize(session, 0, &first);

		long second = -1;
		int secondResult = abi.GetSourceSize(session, 1, &second);

		Assert.Equal(UnityAbi.Ok, abi.Close(session));

		Assert.Equal(2, count);
		Assert.Equal(UnityAbi.Ok, firstResult);
		Assert.Equal(24L, first);
		Assert.Equal(UnityAbi.Ok, secondResult);
		Assert.Equal(0L, second);
	}

	// get_entry_path.

	[Fact]
	public void EntryPathReportsTheUtf16UnitsOfTheRecordedOriginalPath()
	{
		// A non-ASCII unit proves the copy is unit-for-unit, not a byte or code-page transform.
		const string Path = "Assets/Resources/h\u00e9ro.txt";
		UnitySessionAbi abi = FakeAbi(4, new FakeGraph(
			new FakeCollection("collected", new FakeText(5, [1], name: "notes", originalPath: Path))));
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));

		// A null buffer with zero capacity is the size query: only the required count is written.
		int required = -1;
		Assert.Equal(UnityAbi.Ok, abi.GetEntryPath(session, 0, null, 0, &required));
		Assert.Equal(Path.Length, required);

		// A buffer of exactly the required size receives every unit and no terminator.
		char[] buffer = new char[Path.Length];
		fixed (char* pointer = buffer)
		{
			Assert.Equal(UnityAbi.Ok, abi.GetEntryPath(session, 0, (ushort*)pointer, Path.Length, &required));
		}

		Assert.Equal(Path, new string(buffer));
		Assert.Equal(UnityAbi.Ok, abi.Close(session));
	}

	[Fact]
	public void GetEntryPathReportsBufferTooSmallWithTheRequiredCountAndNoPartialWrite()
	{
		const string Path = "Assets/Resources/notes.txt";
		UnitySessionAbi abi = FakeAbi(4, new FakeGraph(
			new FakeCollection("collected", new FakeText(5, [1], name: "notes", originalPath: Path))));
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));

		char[] buffer = new char[Path.Length];
		Array.Fill(buffer, 'Z');
		int required = -1;
		fixed (char* pointer = buffer)
		{
			Assert.Equal(UnityAbi.BufferTooSmall, abi.GetEntryPath(session, 0, (ushort*)pointer, Path.Length - 1, &required));
		}

		Assert.Equal(Path.Length, required);
		foreach (char unit in buffer)
		{
			Assert.Equal('Z', unit);
		}

		Assert.Equal(UnityAbi.Ok, abi.Close(session));
	}

	[Fact]
	public void EntryPathOfAnEntryWithoutARecordedPathIsEmptyThroughARealBuffer()
	{
		UnitySessionAbi abi = FakeAbi(4, new FakeGraph(
			new FakeCollection("collected", new FakeText(5, [1], name: "notes"))));
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));

		char[] buffer = ['Z', 'Z'];
		int length = -1;
		fixed (char* pointer = buffer)
		{
			Assert.Equal(UnityAbi.Ok, abi.GetEntryPath(session, 0, (ushort*)pointer, 0, &length));
		}

		Assert.Equal(0, length);
		Assert.Equal(new[] { 'Z', 'Z' }, buffer);
		Assert.Equal(UnityAbi.Ok, abi.Close(session));
	}

	[Fact]
	public void GetEntryPathRejectsInvalidArgumentsAndZeroesTheLength()
	{
		// A null length output is the one argument that cannot be zeroed.
		Assert.Equal(UnityAbi.InvalidArgument, GetEntryPathExport(0, 0, null, 0, null));

		// A null buffer with a non-zero capacity promises memory that does not exist.
		int length = -1;
		Assert.Equal(UnityAbi.InvalidArgument, GetEntryPathExport(0, 0, null, 4, &length));
		Assert.Equal(0, length);

		char[] buffer = new char[4];
		fixed (char* pointer = buffer)
		{
			length = -1;
			Assert.Equal(UnityAbi.InvalidArgument, GetEntryPathExport(0, 0, (ushort*)pointer, -1, &length));
		}

		Assert.Equal(0, length);
	}

	[Fact]
	public void GetEntryPathRejectsForgedStaleAndZeroHandles()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		List<(int Result, int Length)> forged = new();
		foreach (ulong handle in new[] { 0UL, 1UL, ulong.MaxValue })
		{
			int length = -1;
			forged.Add((GetEntryPathExport(handle, 0, null, 0, &length), length));
		}

		Assert.Equal(UnityAbi.Ok, CloseExport(session));

		int stale = -1;
		int staleResult = GetEntryPathExport(session, 0, null, 0, &stale);

		Assert.Equal(
			new[] { (UnityAbi.InvalidHandle, 0), (UnityAbi.InvalidHandle, 0), (UnityAbi.InvalidHandle, 0) },
			forged);
		Assert.Equal(UnityAbi.InvalidHandle, staleResult);
		Assert.Equal(0, stale);
	}

	[Fact]
	public void GetEntryPathReportsOutOfRangeIndexAndZeroesItsLength()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out int count));

		List<(int Result, int Length)> attempts = new();
		foreach (int index in new[] { -1, count })
		{
			int length = -1;
			attempts.Add((GetEntryPathExport(session, index, null, 0, &length), length));
		}

		Assert.Equal(UnityAbi.Ok, CloseExport(session));

		Assert.Equal(new[] { (UnityAbi.OutOfRange, 0), (UnityAbi.OutOfRange, 0) }, attempts);
	}

	[Fact]
	public void GetEntryPathReportsBusyDuringAnActiveExport()
	{
		UnitySessionAbi production = NativeUnityApi.Production;
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		List<int> attempts = new();
		Sink sink = new() { Reenter = _ => attempts.Add(ReentrantEntryPathProbe(production, session)) };
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportExport(session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Equal(new[] { UnityAbi.Busy }, attempts);
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	[Fact]
	public void EntryPathReportsTheResourceManagerRecordedPathThroughTheExport()
	{
		// A real ResourceManager-container fixture yields a container-metadata path, so the exported
		// getter must return those exact UTF-16 units for a session opened through the ABI.
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "resources.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedResourceManagerTextAsset("hero.txt", "fixture", "payload"));

		Assert.Equal(UnityAbi.Ok, OpenExport(selected, out ulong session, out int count));
		Assert.Equal(1, count);

		int required = -1;
		Assert.Equal(UnityAbi.Ok, GetEntryPathExport(session, 0, null, 0, &required));

		string expected = Path.Join("Assets/Resources", "hero.txt");
		Assert.Equal(expected.Length, required);

		char[] buffer = new char[required];
		fixed (char* pointer = buffer)
		{
			Assert.Equal(UnityAbi.Ok, GetEntryPathExport(session, 0, (ushort*)pointer, required, &required));
		}

		Assert.Equal(UnityAbi.Ok, CloseExport(session));
		Assert.Equal(expected, new string(buffer));
	}

	[Fact]
	public void HermeticCatalogueReportsKindsSignedPathIdsAndUtf16LabelsAcrossCollections()
	{
		const string Label = "h\u00e9llo \ud834\udd1e"; // includes a surrogate pair: 8 UTF-16 units.
		byte[] png = [1, 2, 3, 4, 5];
		FakeGraph graph = new(
			new FakeCollection("first", new FakeText(long.MinValue, [9], name: Label), new FakeTexture(7, png, name: "icon")),
			new FakeCollection("second", new FakeText(long.MinValue, [8], name: "dup")));
		UnitySessionAbi abi = FakeAbi(4, graph);

		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out int count));
		Assert.Equal(3, count);

		UnityEntry first = Descriptor();
		Assert.Equal(UnityAbi.Ok, abi.GetEntry(session, 0, &first, null, 0));
		Assert.Equal(UnityAbi.KindTextRaw, first.Kind);
		Assert.Equal(0, first.CollectionIndex);
		Assert.Equal(long.MinValue, first.PathId);
		Assert.Equal((uint)Label.Length, first.NameLength);

		UnityEntry icon = Descriptor();
		Assert.Equal(UnityAbi.Ok, abi.GetEntry(session, 1, &icon, null, 0));
		Assert.Equal(UnityAbi.KindTexturePng, icon.Kind);
		Assert.Equal(0, icon.CollectionIndex);
		Assert.Equal(7L, icon.PathId);
		Assert.Equal(4u, icon.NameLength);

		UnityEntry duplicate = Descriptor();
		Assert.Equal(UnityAbi.Ok, abi.GetEntry(session, 2, &duplicate, null, 0));
		Assert.Equal(UnityAbi.KindTextRaw, duplicate.Kind);
		Assert.Equal(1, duplicate.CollectionIndex);
		Assert.Equal(long.MinValue, duplicate.PathId);

		char[] name = new char[Label.Length + 2];
		Array.Fill(name, 'Z');
		fixed (char* namePointer = name)
		{
			Assert.Equal(UnityAbi.Ok, abi.GetEntry(session, 0, &first, (ushort*)namePointer, name.Length));
		}

		Assert.Equal(Label, new string(name, 0, Label.Length));
		Assert.Equal('Z', name[Label.Length]);
		Assert.Equal('Z', name[Label.Length + 1]);

		Sink sink = new();
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportThrough(abi, session, 1, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Equal(png, Assert.Single(sink.Chunks));
		Assert.Equal(UnityAbi.Ok, abi.Close(session));
	}

	[Fact]
	public void GetEntryAllowsAnEmptyLabelAsASizeQueryAndThroughARealBuffer()
	{
		UnitySessionAbi abi = FakeAbi(2, TextGraph(string.Empty, [1]));
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out int count));
		Assert.Equal(1, count);

		UnityEntry entry = Descriptor();
		Assert.Equal(UnityAbi.Ok, abi.GetEntry(session, 0, &entry, null, 0));
		Assert.Equal(0u, entry.NameLength);

		char[] name = ['Z'];
		fixed (char* namePointer = name)
		{
			Assert.Equal(UnityAbi.Ok, abi.GetEntry(session, 0, &entry, (ushort*)namePointer, 0));
			Assert.Equal('Z', name[0]);
			Assert.Equal(UnityAbi.Ok, abi.GetEntry(session, 0, &entry, (ushort*)namePointer, 1));
			Assert.Equal('Z', name[0]);
		}

		Assert.Equal(UnityAbi.Ok, abi.Close(session));
	}

	// export.

	[Fact]
	public void ExportSplitsThePayloadIntoChunksOfAtMostSixtyFiveKilobytes()
	{
		byte[] payload = new byte[FrozenChunkLimit + 10];
		for (int i = 0; i < payload.Length; i++)
		{
			payload[i] = (byte)i;
		}

		UnitySessionAbi abi = FakeAbi(2, TextGraph("big", payload));
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));

		Sink sink = new();
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportThrough(abi, session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Equal(2, sink.Chunks.Count);
		Assert.Equal(FrozenChunkLimit, sink.Chunks[0].Length);
		Assert.Equal(10, sink.Chunks[1].Length);
		byte[] concatenated = [.. sink.Chunks[0], .. sink.Chunks[1]];
		Assert.Equal(payload, concatenated);
	}

	[Fact]
	public void ExportOfAnEmptyPayloadMakesNoCallback()
	{
		UnitySessionAbi abi = FakeAbi(2, TextGraph("empty", []));
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));

		Sink sink = new();
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportThrough(abi, session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Empty(sink.Chunks);
		Assert.Equal(UnityAbi.Ok, abi.Close(session));
	}

	[Fact]
	public void ExportCancellationStopsFurtherCallbacksAndLeavesTheSessionUsable()
	{
		byte[] payload = new byte[(2 * FrozenChunkLimit) + 1];
		UnitySessionAbi abi = FakeAbi(2, TextGraph("big", payload));
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));

		Sink sink = new() { Responder = call => call == 0 ? UnityAbi.CancelRequest : UnityAbi.Ok };
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Cancelled, ExportThrough(abi, session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Single(sink.Chunks);

		// The abandoned export released the session: listing and a fresh export still work.
		UnityEntry entry = Descriptor();
		Assert.Equal(UnityAbi.Ok, abi.GetEntry(session, 0, &entry, null, 0));
		Sink second = new();
		GCHandle secondHandle = GCHandle.Alloc(second);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportThrough(abi, session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(secondHandle)));
		}
		finally
		{
			secondHandle.Free();
		}

		byte[] concatenated = [.. second.Chunks[0], .. second.Chunks[1], .. second.Chunks[2]];
		Assert.Equal(payload, concatenated);
		Assert.Equal(UnityAbi.Ok, abi.Close(session));
	}

	[Fact]
	public void ExportStopsAtAWriteFailureWithoutAnyLaterCallback()
	{
		byte[] payload = new byte[(3 * FrozenChunkLimit) + 10];
		for (int i = 0; i < payload.Length; i++)
		{
			payload[i] = (byte)i;
		}

		UnitySessionAbi abi = FakeAbi(2, TextGraph("big", payload));
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));

		Sink sink = new() { Responder = call => call == 1 ? UnityAbi.WriteFailure : UnityAbi.Ok };
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.WriteFailure, ExportThrough(abi, session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		// The failing chunk was delivered, nothing after it was, and the accepted prefix is exact.
		Assert.Equal(2, sink.Chunks.Count);
		Assert.Equal(FrozenChunkLimit, sink.Chunks[0].Length);
		Assert.Equal(FrozenChunkLimit, sink.Chunks[1].Length);
		byte[] accepted = [.. sink.Chunks[0], .. sink.Chunks[1]];
		Assert.Equal(payload[..(2 * FrozenChunkLimit)], accepted);

		// The abandoned export released the session: a later export still delivers every chunk.
		Sink retry = new();
		GCHandle retryHandle = GCHandle.Alloc(retry);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportThrough(abi, session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(retryHandle)));
		}
		finally
		{
			retryHandle.Free();
		}

		Assert.Equal(4, retry.Chunks.Count);
		byte[] complete = [.. retry.Chunks[0], .. retry.Chunks[1], .. retry.Chunks[2], .. retry.Chunks[3]];
		Assert.Equal(payload, complete);
		Assert.Equal(UnityAbi.Ok, abi.Close(session));
	}

	[Fact]
	public void ExportPropagatesOutOfMemoryFromMaterialisation()
	{
		FakeGraph graph = new(new FakeCollection("collected", new OomText(1, "exhausted")));
		UnitySessionAbi abi = FakeAbi(2, graph);
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));

		// Out-of-memory inside the session stays fatal; the callback is never reached.
		Assert.Throws<OutOfMemoryException>(() => ExportThrough(abi, session, 0, &NullSinkCallback, null));

		// The fatal export still released the session.
		Assert.Equal(UnityAbi.Ok, abi.Close(session));
	}

	[Fact]
	public void ExportRejectsANullCallbackBeforeTouchingTheHandle()
	{
		Assert.Equal(UnityAbi.InvalidArgument, ExportExport(0, 0, null, null));
	}

	[Fact]
	public void ExportRejectsForgedHandlesAndOutOfRangeIndexesWithoutCallingBack()
	{
		Sink sink = new();
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.InvalidHandle, ExportExport(0, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
			Assert.Equal(UnityAbi.InvalidHandle, ExportExport(ulong.MaxValue, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));

			Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out int count));
			Assert.Equal(UnityAbi.OutOfRange, ExportExport(session, -1, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
			Assert.Equal(UnityAbi.OutOfRange, ExportExport(session, count, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
			Assert.Equal(UnityAbi.Ok, CloseExport(session));
			Assert.Equal(UnityAbi.InvalidHandle, ExportExport(session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Empty(sink.Chunks);
	}

	[Fact]
	public void ExportReportsAnUnexportableEntryAsDataFailure()
	{
		UnitySessionAbi abi = FakeAbi(2, TextGraph("unsupported", null));
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));

		Sink sink = new();
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.DataFailure, ExportThrough(abi, session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Empty(sink.Chunks);
		Assert.Equal(UnityAbi.Ok, abi.Close(session));
	}

	[Fact]
	public void ExportMapsAFailureInsideTheSessionToDataFailure()
	{
		FakeGraph graph = new(new FakeCollection("collected", new FaultyText(1, "broken")));
		UnitySessionAbi abi = FakeAbi(2, graph);
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));

		Sink sink = new();
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.DataFailure, ExportThrough(abi, session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		// The throwing payload is reported as a data failure and the session stays usable.
		Assert.Empty(sink.Chunks);
		Assert.Equal(UnityAbi.Ok, abi.Close(session));
	}

	// One operation at a time.

	[Fact]
	public void AReentrantCallbackObservesBusyForEveryOperation()
	{
		UnitySessionAbi production = NativeUnityApi.Production;
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		List<int> reentrant = new();
		Sink sink = new()
		{
			Reenter = _ =>
			{
				reentrant.Add(GetEntryThrough(production, session, 0));
				reentrant.Add(ReentrantExportProbe(production, session));
				reentrant.Add(production.Close(session));
			},
		};

		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportExport(session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		// The active export is never disposed under the callback, and every attempt reports busy.
		Assert.Equal(new[] { UnityAbi.Busy, UnityAbi.Busy, UnityAbi.Busy }, reentrant);
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	[Fact]
	public void ClosingAnActivelyExportingSessionReportsBusy()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		List<int> attempts = new();
		Sink sink = new() { Reenter = _ => attempts.Add(CloseExport(session)) };
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportExport(session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Equal(new[] { UnityAbi.Busy }, attempts);
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	[Fact]
	public void AConcurrentOperationOnTheSameSessionReportsBusy()
	{
		UnitySessionAbi production = NativeUnityApi.Production;
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		using ManualResetEventSlim insideCallback = new(false);
		using ManualResetEventSlim otherAttempted = new(false);
		int otherResult = UnityAbi.Ok;
		bool otherCompleted = false;
		Thread other = new(() =>
		{
			insideCallback.Wait(TimeSpan.FromSeconds(10));
			otherResult = GetEntryThrough(production, session, 0);
			otherCompleted = true;
			otherAttempted.Set();
		});
		other.Start();

		Sink sink = new()
		{
			Reenter = _ =>
			{
				insideCallback.Set();
				otherAttempted.Wait(TimeSpan.FromSeconds(10));
			},
		};

		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportExport(session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.True(otherCompleted);
		Assert.True(other.Join(TimeSpan.FromSeconds(10)));
		Assert.Equal(UnityAbi.Busy, otherResult);
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	// Registry ownership.

	[Fact]
	public void AnExhaustedRegistryRejectsTheNewSessionAndKeepsTheOwner()
	{
		List<FakeGraph> created = new();
		UnitySessionAbi abi = new(_ =>
		{
			FakeGraph graph = TextGraph("owner", [1, 2, 3]);
			created.Add(graph);
			return UnitySession.FromGraph(graph);
		}, 1);

		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out int count));
		Assert.Equal(1, count);
		Assert.Single(created);

		Assert.Equal(UnityAbi.Busy, OpenThrough(abi, "graph", out ulong rejected, out int rejectedCount));
		Assert.Equal(0UL, rejected);
		Assert.Equal(0, rejectedCount);

		// The registry never took ownership of the rejected session, so opening disposed it.
		Assert.Equal(2, created.Count);
		Assert.Equal(1, created[1].DisposeCalls);
		Assert.Equal(0, created[0].DisposeCalls);

		Sink sink = new();
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportThrough(abi, session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(sink.Chunks));
		Assert.Equal(UnityAbi.Ok, abi.Close(session));
		Assert.Equal(1, created[0].DisposeCalls);
	}

	[Fact]
	public void IndependentSessionsGetDistinctNeverReusedTokensAndCloseReleasesResources()
	{
		List<FakeGraph> created = new();
		UnitySessionAbi abi = new(_ =>
		{
			FakeGraph graph = TextGraph("owner", [1]);
			created.Add(graph);
			return UnitySession.FromGraph(graph);
		}, 2);

		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong first, out _));
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong second, out _));
		Assert.NotEqual(first, second);

		Sink sink = new();
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportThrough(abi, second, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Single(sink.Chunks);
		Assert.Equal(UnityAbi.Ok, abi.Close(first));
		Assert.Equal(1, created[0].DisposeCalls);
		Assert.Equal(0, created[1].DisposeCalls);

		UnityEntry entry = Descriptor();
		Assert.Equal(UnityAbi.InvalidHandle, abi.GetEntry(first, 0, &entry, null, 0));
		Assert.Equal(UnityAbi.InvalidHandle, abi.Close(first));

		// The surviving session is untouched, and a freed slot never reissues an old token.
		Assert.Equal(UnityAbi.Ok, abi.GetEntry(second, 0, &entry, null, 0));
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong third, out _));
		Assert.NotEqual(first, third);
		Assert.NotEqual(second, third);
		Assert.Equal(UnityAbi.Ok, abi.Close(second));
		Assert.Equal(UnityAbi.Ok, abi.Close(third));
		Assert.Equal(1, created[1].DisposeCalls);
		Assert.Equal(1, created[2].DisposeCalls);
	}

	[Fact]
	public void CloseRejectsZeroAndForgedHandles()
	{
		Assert.Equal(UnityAbi.InvalidHandle, CloseExport(0));
		Assert.Equal(UnityAbi.InvalidHandle, CloseExport(1));
		Assert.Equal(UnityAbi.InvalidHandle, CloseExport(ulong.MaxValue));
	}

	[Fact]
	public void CloseReleasesTheRealSessionFileHandles()
	{
		// A private copy keeps the exclusive open below independent of the other test classes that
		// hold the shared fixture open concurrently.
		string path = Path.Combine(Temp.NewDirectory(), "text-only.assets");
		File.Copy(FixturePath, path);

		Assert.Equal(UnityAbi.Ok, OpenExport(path, out ulong session, out _));
		Assert.Equal(UnityAbi.Ok, CloseExport(session));

		using FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
		Assert.True(exclusive.Length > 0);
	}

	[Fact]
	public void TheLastSupportedRegistrySlotIssuesUsableCloseableHandles()
	{
		List<FakeGraph> created = new();
		UnitySessionAbi abi = new(_ =>
		{
			FakeGraph graph = TextGraph("owner", [1]);
			created.Add(graph);
			return UnitySession.FromGraph(graph);
		}, UnitySessionAbi.MaxRegistrySize);

		ulong[] tokens = new ulong[UnitySessionAbi.MaxRegistrySize];
		for (int i = 0; i < tokens.Length; i++)
		{
			Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out tokens[i], out int count));
			Assert.Equal(1, count);
			Assert.NotEqual(0UL, tokens[i]);
		}

		Assert.Equal(tokens.Length, tokens.Distinct().Count());

		// The highest supported slot is the case the token encoding must still resolve.
		UnityEntry entry = Descriptor();
		Assert.Equal(UnityAbi.Ok, abi.GetEntry(tokens[^1], 0, &entry, null, 0));
		Assert.Equal((uint)"owner".Length, entry.NameLength);

		Sink sink = new();
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportThrough(abi, tokens[^1], 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Equal(new byte[] { 1 }, Assert.Single(sink.Chunks));

		Assert.Equal(UnityAbi.Busy, OpenThrough(abi, "graph", out ulong rejected, out int rejectedCount));
		Assert.Equal(0UL, rejected);
		Assert.Equal(0, rejectedCount);
		Assert.Equal(tokens.Length + 1, created.Count);
		Assert.Equal(1, created[^1].DisposeCalls);

		foreach (ulong token in tokens)
		{
			Assert.Equal(UnityAbi.Ok, abi.Close(token));
		}

		Assert.All(created, graph => Assert.Equal(1, graph.DisposeCalls));
	}

	[Fact]
	public void ARegistryThatWouldSpillTheSlotFieldIsRejected()
	{
		// Slot 255 cannot fit in the low byte of a token, so one capacity is out of range.
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new UnitySessionAbi(_ => throw new InvalidOperationException(), 256));
	}

	[Fact]
	public void RegistryConstructorRejectsAnOutOfRangeInitialSequence()
	{
		// The deterministic counter seam is validated, so an out-of-range start is refused and the
		// ceiling itself is a valid starting point.
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new UnitySessionAbi(_ => throw new InvalidOperationException(), 1, -1));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new UnitySessionAbi(_ => throw new InvalidOperationException(), 1, UnitySessionAbi.MaxSequence + 1));
		Assert.NotNull(new UnitySessionAbi(_ => throw new InvalidOperationException(), 1, UnitySessionAbi.MaxSequence));
	}

	[Fact]
	public void ARegistryAtTheSequenceCeilingRefusesTheNextSessionAsDataFailure()
	{
		List<FakeGraph> created = new();
		UnitySessionAbi abi = new(_ =>
		{
			FakeGraph graph = TextGraph("owner", [1]);
			created.Add(graph);
			return UnitySession.FromGraph(graph);
		}, 2, UnitySessionAbi.MaxSequence - 1);

		// The last available sequence value is still issued and yields a usable, closeable handle.
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong last, out _));
		Assert.NotEqual(0UL, last);
		UnityEntry entry = Descriptor();
		Assert.Equal(UnityAbi.Ok, abi.GetEntry(last, 0, &entry, null, 0));
		Assert.Equal((uint)"owner".Length, entry.NameLength);
		Assert.Equal(UnityAbi.Ok, abi.Close(last));
		Assert.Equal(1, created[0].DisposeCalls);

		// The sequence must never wrap, so the next open is refused as a data failure with no handle,
		// and the session it created is disposed rather than leaked or registered in a free slot.
		Assert.Equal(UnityAbi.DataFailure, OpenThrough(abi, "graph", out ulong refused, out int refusedCount));
		Assert.Equal(0UL, refused);
		Assert.Equal(0, refusedCount);
		Assert.Equal(2, created.Count);
		Assert.Equal(1, created[1].DisposeCalls);
	}

	[Fact]
	public void ARejectedOpenReportsAFailingCleanupAsDataFailure()
	{
		List<FakeGraph> kept = new();
		List<FailingDisposeGraph> failing = new();
		UnitySessionAbi abi = new(_ =>
		{
			if (kept.Count == 0)
			{
				FakeGraph graph = TextGraph("kept", [1]);
				kept.Add(graph);
				return UnitySession.FromGraph(graph);
			}

			FailingDisposeGraph broken = new(new IOException("cleanup failed"));
			failing.Add(broken);
			return UnitySession.FromGraph(broken);
		}, 1);

		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out int count));
		Assert.Equal(1, count);

		// The registry is full, and disposing the rejected session fails as well.
		Assert.Equal(UnityAbi.DataFailure, OpenThrough(abi, "graph", out ulong rejected, out int rejectedCount));
		Assert.Equal(0UL, rejected);
		Assert.Equal(0, rejectedCount);
		Assert.Equal(1, failing[0].DisposeCalls);
		Assert.Equal(0, kept[0].DisposeCalls);

		// The registered session is untouched and still reachable.
		UnityEntry entry = Descriptor();
		Assert.Equal(UnityAbi.Ok, abi.GetEntry(session, 0, &entry, null, 0));
		Assert.Equal((uint)"kept".Length, entry.NameLength);
		Assert.Equal(UnityAbi.Ok, abi.Close(session));
		Assert.Equal(1, kept[0].DisposeCalls);
	}

	[Fact]
	public void AFailingCloseIsDataFailureAndInvalidatesTheTokenFirst()
	{
		List<FailingDisposeGraph> created = new();
		UnitySessionAbi abi = new(_ =>
		{
			FailingDisposeGraph graph = new(new IOException("cleanup failed"));
			created.Add(graph);
			return UnitySession.FromGraph(graph);
		}, 1);

		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));
		Assert.Equal(UnityAbi.DataFailure, abi.Close(session));
		Assert.Equal(1, created[0].DisposeCalls);

		// The handle was dropped before the failing disposal, so it can never be used again.
		UnityEntry entry = Descriptor();
		Assert.Equal(UnityAbi.InvalidHandle, abi.GetEntry(session, 0, &entry, null, 0));
		Assert.Equal(UnityAbi.InvalidHandle, abi.Close(session));

		// Nothing was left registered or busy either: the slot serves a new session.
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong replacement, out _));
		Assert.NotEqual(session, replacement);
		Assert.Equal(UnityAbi.DataFailure, abi.Close(replacement));
	}

	[Fact]
	public void ACleanupThatExhaustsMemoryStaysFatal()
	{
		UnitySessionAbi abi = new(_ => UnitySession.FromGraph(new FailingDisposeGraph(new OutOfMemoryException())), 1);
		Assert.Equal(UnityAbi.Ok, OpenThrough(abi, "graph", out ulong session, out _));

		// Cleanup of a rejected open runs out of memory and stays fatal.
		Assert.Throws<OutOfMemoryException>(() => OpenThrough(abi, "graph", out _, out _));

		// Closing the registered session is fatal too, and its handle is dropped all the same.
		Assert.Throws<OutOfMemoryException>(() => abi.Close(session));
		UnityEntry entry = Descriptor();
		Assert.Equal(UnityAbi.InvalidHandle, abi.GetEntry(session, 0, &entry, null, 0));
	}

	[Fact]
	public void RealFontKindAndExtensionRoundTripThroughTheExportedEntryPoints()
	{
		string directory = Temp.NewDirectory();
		string selected = Path.Combine(directory, "font.assets");
		File.WriteAllBytes(selected, UnityFixtureBuilder.SerializedFont("legacy", [0x00, 0x01, 0x00, 0x00]));

		Assert.Equal(UnityAbi.Ok, OpenExport(selected, out ulong session, out int count));
		Assert.Equal(1, count);

		UnityEntry entry = Descriptor();
		Assert.Equal(UnityAbi.Ok, GetEntryExport(session, 0, &entry, null, 0));
		Assert.Equal(UnityAbi.KindFont, entry.Kind);

		int required = -1;
		Assert.Equal(UnityAbi.Ok, GetEntryExtensionExport(session, 0, null, 0, &required));
		Assert.Equal(3, required);

		char[] buffer = new char[required];
		fixed (char* pointer = buffer)
		{
			Assert.Equal(UnityAbi.Ok, GetEntryExtensionExport(session, 0, (ushort*)pointer, required, &required));
		}

		Assert.Equal("ttf", new string(buffer));
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	[Fact]
	public void EntryExtensionReportsTheUtf16UnitsOfTheCatalogueExtension()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		// A null buffer with zero capacity is the size query: only the required count is written.
		int required = -1;
		Assert.Equal(UnityAbi.Ok, GetEntryExtensionExport(session, 0, null, 0, &required));
		Assert.Equal("bin".Length, required);

		char[] buffer = new char[required];
		fixed (char* pointer = buffer)
		{
			Assert.Equal(UnityAbi.Ok, GetEntryExtensionExport(session, 0, (ushort*)pointer, required, &required));
		}

		Assert.Equal("bin", new string(buffer));
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	[Fact]
	public void GetEntryExtensionReportsBufferTooSmallWithTheRequiredCountAndNoPartialWrite()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		char[] buffer = ['Z', 'Z'];
		int required = -1;
		fixed (char* pointer = buffer)
		{
			Assert.Equal(UnityAbi.BufferTooSmall, GetEntryExtensionExport(session, 0, (ushort*)pointer, 2, &required));
		}

		Assert.Equal("bin".Length, required);
		Assert.Equal(new[] { 'Z', 'Z' }, buffer);
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	[Fact]
	public void GetEntryExtensionRejectsInvalidArgumentsAndZeroesTheLength()
	{
		// A null length output is the one argument that cannot be zeroed.
		Assert.Equal(UnityAbi.InvalidArgument, GetEntryExtensionExport(0, 0, null, 0, null));

		// A null buffer with a non-zero capacity promises memory that does not exist.
		int length = -1;
		Assert.Equal(UnityAbi.InvalidArgument, GetEntryExtensionExport(0, 0, null, 4, &length));
		Assert.Equal(0, length);

		char[] buffer = new char[4];
		fixed (char* pointer = buffer)
		{
			length = -1;
			Assert.Equal(UnityAbi.InvalidArgument, GetEntryExtensionExport(0, 0, (ushort*)pointer, -1, &length));
		}

		Assert.Equal(0, length);
	}

	[Fact]
	public void GetEntryExtensionRejectsForgedStaleAndZeroHandles()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		List<(int Result, int Length)> forged = new();
		foreach (ulong handle in new[] { 0UL, 1UL, ulong.MaxValue })
		{
			int length = -1;
			forged.Add((GetEntryExtensionExport(handle, 0, null, 0, &length), length));
		}

		Assert.Equal(UnityAbi.Ok, CloseExport(session));

		int stale = -1;
		int staleResult = GetEntryExtensionExport(session, 0, null, 0, &stale);

		Assert.Equal(
			new[] { (UnityAbi.InvalidHandle, 0), (UnityAbi.InvalidHandle, 0), (UnityAbi.InvalidHandle, 0) },
			forged);
		Assert.Equal(UnityAbi.InvalidHandle, staleResult);
		Assert.Equal(0, stale);
	}

	[Fact]
	public void GetEntryExtensionReportsOutOfRangeIndexAndZeroesItsLength()
	{
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out int count));

		List<(int Result, int Length)> attempts = new();
		foreach (int index in new[] { -1, count })
		{
			int length = -1;
			attempts.Add((GetEntryExtensionExport(session, index, null, 0, &length), length));
		}

		Assert.Equal(UnityAbi.Ok, CloseExport(session));

		Assert.Equal(new[] { (UnityAbi.OutOfRange, 0), (UnityAbi.OutOfRange, 0) }, attempts);
	}

	[Fact]
	public void GetEntryExtensionReportsBusyDuringAnActiveExport()
	{
		UnitySessionAbi production = NativeUnityApi.Production;
		Assert.Equal(UnityAbi.Ok, OpenExport(FixturePath, out ulong session, out _));

		List<int> attempts = new();
		Sink sink = new() { Reenter = _ => attempts.Add(ReentrantExtensionProbe(production, session)) };
		GCHandle handle = GCHandle.Alloc(sink);
		try
		{
			Assert.Equal(UnityAbi.Ok, ExportExport(session, 0, &SinkCallback, (void*)GCHandle.ToIntPtr(handle)));
		}
		finally
		{
			handle.Free();
		}

		Assert.Equal(new[] { UnityAbi.Busy }, attempts);
		Assert.Equal(UnityAbi.Ok, CloseExport(session));
	}

	/// <summary>Records the chunks a callback received and answers with a test-supplied code.</summary>
	private sealed class Sink
	{
		public List<byte[]> Chunks { get; } = new();

		public Func<int, int>? Responder { get; set; }

		public Action<int>? Reenter { get; set; }
	}

	/// <summary>A text asset whose payload is corrupt and throws when the export reads it.</summary>
	private sealed class FaultyText(long pathId, string name) : IProbeText
	{
		public long PathId { get; } = pathId;

		public string Version { get; } = "1.2.3";

		public string Name { get; } = name;

		public string? OriginalPath => null;

		public long SourceSize => 0;

		public byte[]? ReadData() => throw new InvalidDataException("corrupt payload");
	}

	/// <summary>A text asset whose payload read exhausts memory.</summary>
	private sealed class OomText(long pathId, string name) : IProbeText
	{
		public long PathId { get; } = pathId;

		public string Version { get; } = "1.2.3";

		public string Name { get; } = name;

		public string? OriginalPath => null;

		public long SourceSize => 0;

		public byte[]? ReadData() => throw new OutOfMemoryException();
	}

	/// <summary>An empty graph whose disposal always fails, so cleanup failures can be observed.</summary>
	private sealed class FailingDisposeGraph(Exception failure) : IUnityGraph
	{
		public IReadOnlyList<IUnityGraphCollection> Collections { get; } = Array.Empty<IUnityGraphCollection>();

		public int DisposeCalls { get; private set; }

		public void Dispose()
		{
			DisposeCalls++;
			throw failure;
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static int SinkCallback(void* context, byte* bytes, int length)
	{
		Sink sink = (Sink)GCHandle.FromIntPtr((nint)context).Target!;
		int call = sink.Chunks.Count;
		sink.Chunks.Add(new Span<byte>(bytes, length).ToArray());
		sink.Reenter?.Invoke(call);
		return sink.Responder?.Invoke(call) ?? 0;
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static int NullSinkCallback(void* context, byte* bytes, int length) => UnityAbi.Ok;
}
