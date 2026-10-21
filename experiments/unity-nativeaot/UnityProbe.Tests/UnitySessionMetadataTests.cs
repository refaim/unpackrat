using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Feature tests for the additive combined metadata read: one serialised operation that reports the
/// entry descriptor, the declared source length and the three UTF-16 field lengths, and copies each
/// caller-owned field only when its buffer is large enough.
/// </summary>
public sealed unsafe class UnitySessionMetadataTests
{
	// The descriptor length assertions derive from this fixture literal so the oracle tracks the
	// single source of truth instead of a hand-counted number.
	private const string OriginalPathLiteral = "Assets/leaf.png";

	private static UnitySessionAbi NewAbi(string name = "leaf", string? original = OriginalPathLiteral,
		long sourceSize = 11, long pathId = 42)
	{
		FakeText text = new(pathId, [1, 2, 3], name: name, sourceSize: sourceSize, originalPath: original);
		FakeGraph graph = new(new FakeCollection("collected", text));
		return new UnitySessionAbi(_ => UnitySession.FromGraph(graph), 4);
	}

	private static (UnitySessionAbi Abi, ulong Token) Open(string name = "leaf",
		string? original = OriginalPathLiteral, long sourceSize = 11, long pathId = 42)
	{
		UnitySessionAbi abi = NewAbi(name, original, sourceSize, pathId);
		const string path = "selected.assets";
		ulong token = 0;
		int count = 0;
		int result;
		fixed (char* pointer = path)
		{
			result = abi.Open((ushort*)pointer, path.Length, &token, &count);
		}

		Assert.Equal(UnityAbi.Ok, result);
		Assert.Equal(1, count);
		return (abi, token);
	}

	private static UnityMetadata Descriptor()
	{
		UnityMetadata metadata = default;
		metadata.StructSize = UnityMetadata.Size;
		return metadata;
	}

	private static byte[] Canary(int length)
	{
		byte[] bytes = new byte[length];
		Array.Fill(bytes, (byte)0xCC);
		return bytes;
	}

	private static void AssertUnchanged(byte[] bytes)
	{
		foreach (byte value in bytes)
		{
			Assert.Equal((byte)0xCC, value);
		}
	}

	[Fact]
	public void UnityMetadataHasTheFrozenFortyEightByteLayout()
	{
		Assert.Equal(48, Marshal.SizeOf<UnityMetadata>());
		Assert.Equal(48u, UnityMetadata.Size);
		Assert.Equal(0, (int)Marshal.OffsetOf<UnityMetadata>(nameof(UnityMetadata.StructSize)));
		Assert.Equal(4, (int)Marshal.OffsetOf<UnityMetadata>(nameof(UnityMetadata.Kind)));
		Assert.Equal(8, (int)Marshal.OffsetOf<UnityMetadata>(nameof(UnityMetadata.CollectionIndex)));
		Assert.Equal(16, (int)Marshal.OffsetOf<UnityMetadata>(nameof(UnityMetadata.PathId)));
		Assert.Equal(24, (int)Marshal.OffsetOf<UnityMetadata>(nameof(UnityMetadata.SourceSize)));
		Assert.Equal(32, (int)Marshal.OffsetOf<UnityMetadata>(nameof(UnityMetadata.NameLength)));
		Assert.Equal(36, (int)Marshal.OffsetOf<UnityMetadata>(nameof(UnityMetadata.OriginalLength)));
		Assert.Equal(40, (int)Marshal.OffsetOf<UnityMetadata>(nameof(UnityMetadata.ExtensionLength)));
	}

	[Fact]
	public void GetMetadataReportsEveryFieldAndCopiesEachBufferInOneCall()
	{
		(UnitySessionAbi abi, ulong token) = Open();
		UnityMetadata metadata = Descriptor();
		char[] name = new char[16];
		char[] original = new char[32];
		char[] extension = new char[8];
		fixed (char* namePointer = name, originalPointer = original, extensionPointer = extension)
		{
			int result = abi.GetMetadata(token, 0, &metadata, (ushort*)namePointer, name.Length,
				(ushort*)originalPointer, original.Length, (ushort*)extensionPointer, extension.Length);
			Assert.Equal(UnityAbi.Ok, result);
			Assert.Equal((int)UnityEntryKind.Text + 1, metadata.Kind);
			Assert.Equal(42, metadata.PathId);
			Assert.Equal(11, metadata.SourceSize);
			Assert.Equal(4, metadata.NameLength);
			Assert.Equal(OriginalPathLiteral.Length, metadata.OriginalLength);
			Assert.Equal(3, metadata.ExtensionLength);
			Assert.Equal("leaf", new string(name, 0, metadata.NameLength));
			Assert.Equal(OriginalPathLiteral, new string(original, 0, metadata.OriginalLength));
			Assert.Equal("bin", new string(extension, 0, metadata.ExtensionLength));
		}
	}

	[Fact]
	public void GetMetadataNullBuffersAreSizeQueriesThatWriteEveryLength()
	{
		(UnitySessionAbi abi, ulong token) = Open();
		UnityMetadata metadata = Descriptor();
		Assert.Equal(UnityAbi.Ok, abi.GetMetadata(token, 0, &metadata, null, 0, null, 0, null, 0));
		Assert.Equal(4, metadata.NameLength);
		Assert.Equal(OriginalPathLiteral.Length, metadata.OriginalLength);
		Assert.Equal(3, metadata.ExtensionLength);
		Assert.Equal(11, metadata.SourceSize);
	}

	[Fact]
	public void GetMetadataShortBufferReportsBufferTooSmallAndLeavesThatBufferUntouched()
	{
		(UnitySessionAbi abi, ulong token) = Open();
		UnityMetadata metadata = Descriptor();
		byte[] original = Canary(64);
		fixed (byte* pointer = original)
		{
			int result = abi.GetMetadata(token, 0, &metadata, null, 0, (ushort*)pointer, 8, null, 0);
			Assert.Equal(UnityAbi.BufferTooSmall, result);
			// Every length is still written so a bounded caller can retry with exact buffers.
			Assert.Equal(OriginalPathLiteral.Length, metadata.OriginalLength);
		}

		AssertUnchanged(original);
	}

	[Fact]
	public void GetMetadataRejectsANullOrMismatchedDescriptor()
	{
		(UnitySessionAbi abi, ulong token) = Open();
		Assert.Equal(UnityAbi.InvalidArgument, abi.GetMetadata(token, 0, null, null, 0, null, 0, null, 0));
		UnityMetadata wrongSize = Descriptor();
		wrongSize.StructSize = UnityMetadata.Size - 1;
		Assert.Equal(UnityAbi.InvalidArgument, abi.GetMetadata(token, 0, &wrongSize, null, 0, null, 0, null, 0));
	}

	[Fact]
	public void GetMetadataRejectsNegativeOrMismatchedCapacities()
	{
		(UnitySessionAbi abi, ulong token) = Open();
		UnityMetadata metadata = Descriptor();
		fixed (char* buffer = new char[4])
		{
			Assert.Equal(UnityAbi.InvalidArgument,
				abi.GetMetadata(token, 0, &metadata, (ushort*)buffer, -1, null, 0, null, 0));
			Assert.Equal(UnityAbi.InvalidArgument,
				abi.GetMetadata(token, 0, &metadata, null, 1, null, 0, null, 0));
			Assert.Equal(UnityAbi.InvalidArgument,
				abi.GetMetadata(token, 0, &metadata, null, 0, (ushort*)buffer, -1, null, 0));
			Assert.Equal(UnityAbi.InvalidArgument,
				abi.GetMetadata(token, 0, &metadata, null, 0, null, 0, (ushort*)buffer, -1));
		}
	}

	[Fact]
	public void GetMetadataRejectsAnOutOfRangeIndexAndAnUnknownSession()
	{
		(UnitySessionAbi abi, ulong token) = Open();
		UnityMetadata metadata = Descriptor();
		Assert.Equal(UnityAbi.OutOfRange, abi.GetMetadata(token, -1, &metadata, null, 0, null, 0, null, 0));
		Assert.Equal(UnityAbi.OutOfRange, abi.GetMetadata(token, 1, &metadata, null, 0, null, 0, null, 0));
		Assert.Equal(UnityAbi.InvalidHandle,
			abi.GetMetadata(token + 0x100, 0, &metadata, null, 0, null, 0, null, 0));
	}

	[Fact]
	public void GetMetadataThunkForwardsThroughTheExportedEntryPoint()
	{
		UnityMetadata metadata = Descriptor();
		delegate* unmanaged[Cdecl]<ulong, int, UnityMetadata*, ushort*, int, ushort*, int, ushort*, int, int> getMetadata =
			&NativeUnityApi.GetMetadata;
		Assert.Equal(UnityAbi.InvalidHandle, getMetadata(0, 0, &metadata, null, 0, null, 0, null, 0));
	}

	[Fact]
	public void GetMetadataShortNameBufferReportsBufferTooSmallAndLeavesTheNameUntouched()
	{
		(UnitySessionAbi abi, ulong token) = Open();
		UnityMetadata metadata = Descriptor();
		byte[] name = Canary(8);
		fixed (byte* pointer = name)
		{
			int result = abi.GetMetadata(token, 0, &metadata, (ushort*)pointer, 3, null, 0, null, 0);
			Assert.Equal(UnityAbi.BufferTooSmall, result);
			// "leaf" needs four UTF-16 units, so a three-unit buffer reports the short buffer and
			// every declared length is still written for an exact retry.
			Assert.Equal(4, metadata.NameLength);
			Assert.Equal(OriginalPathLiteral.Length, metadata.OriginalLength);
			Assert.Equal(3, metadata.ExtensionLength);
		}

		AssertUnchanged(name);
	}

	[Fact]
	public void GetMetadataShortExtensionBufferReportsBufferTooSmallAndLeavesTheExtensionUntouched()
	{
		(UnitySessionAbi abi, ulong token) = Open();
		UnityMetadata metadata = Descriptor();
		byte[] extension = Canary(8);
		fixed (byte* pointer = extension)
		{
			int result = abi.GetMetadata(token, 0, &metadata, null, 0, null, 0, (ushort*)pointer, 2);
			Assert.Equal(UnityAbi.BufferTooSmall, result);
			Assert.Equal(4, metadata.NameLength);
			Assert.Equal(OriginalPathLiteral.Length, metadata.OriginalLength);
			Assert.Equal(3, metadata.ExtensionLength);
		}

		AssertUnchanged(extension);
	}

	[Fact]
	public void GetMetadataRejectsANullBufferWithPositiveCapacityForOriginalAndExtension()
	{
		(UnitySessionAbi abi, ulong token) = Open();
		UnityMetadata metadata = Descriptor();
		Assert.Equal(UnityAbi.InvalidArgument,
			abi.GetMetadata(token, 0, &metadata, null, 0, null, 1, null, 0));
		Assert.Equal(UnityAbi.InvalidArgument,
			abi.GetMetadata(token, 0, &metadata, null, 0, null, 0, null, 1));
	}
}
