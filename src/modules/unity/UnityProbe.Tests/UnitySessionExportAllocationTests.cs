using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed unsafe class UnitySessionExportAllocationTests
{
	[Fact]
	public void EmptyPayloadExportsSuccessfullyWithoutCallingTheWriter()
	{
		FakeText text = new(1, [], name: "empty");
		FakeGraph graph = new(new FakeCollection("selected", text));
		UnitySessionAbi abi = new(_ => UnitySession.FromGraph(graph), 1);
		ulong session = 0;
		int count = 0;
		fixed (char* path = "fixture")
			Assert.Equal(UnityAbi.Ok, abi.Open((ushort*)path, 7, &session, &count));
		Assert.Equal(1, count);
		int calls = 0;
		try
		{
			Assert.Equal(UnityAbi.Ok, abi.Export(session, 0, &CountUnexpectedChunk, &calls));
			Assert.Equal(UnityAbi.Ok, abi.Export(session, 0, &CountUnexpectedChunk, &calls));
			Assert.Equal(0, calls);
			Assert.Equal(2, text.ReadCalls);
		}
		finally
		{
			Assert.Equal(UnityAbi.Ok, abi.Close(session));
		}
		Assert.Equal(1, graph.DisposeCalls);
	}

	[Fact]
	public void CallbackExportDoesNotAllocateASecondPayloadSizedBuffer()
	{
		byte[] payload = new byte[1024 * 1024 + 17];
		for (int i = 0; i < payload.Length; i++) payload[i] = unchecked((byte)(i * 31));
		FakeText text = new(1, payload, name: "large");
		FakeGraph graph = new(new FakeCollection("selected", text));
		UnitySessionAbi abi = new(_ => UnitySession.FromGraph(graph), 1);
		ulong session = 0;
		int count = 0;
		fixed (char* path = "fixture")
			Assert.Equal(UnityAbi.Ok, abi.Open((ushort*)path, 7, &session, &count));
		Assert.Equal(1, count);
		try
		{
			fixed (byte* expected = payload)
			{
				Transfer transfer = new() { Expected = expected, Length = payload.Length };
				for (int i = 0; i < 4; i++)
				{
					transfer.Offset = 0;
					Assert.Equal(UnityAbi.Ok, abi.Export(session, 0, &VerifyChunk, &transfer));
					Assert.Equal(payload.Length, transfer.Offset);
				}

				transfer.Offset = 0;
				long before = GC.GetAllocatedBytesForCurrentThread();
				int status = abi.Export(session, 0, &VerifyChunk, &transfer);
				long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
				Assert.Equal(UnityAbi.Ok, status);
				Assert.Equal(payload.Length, transfer.Offset);
				Assert.True(allocated < 65536,
					$"Export allocated {allocated} bytes for an existing {payload.Length}-byte payload; callback delivery must not stage another payload-sized buffer.");
			}
			Assert.Equal(5, text.ReadCalls);
		}
		finally
		{
			Assert.Equal(UnityAbi.Ok, abi.Close(session));
		}
		Assert.Equal(1, graph.DisposeCalls);
	}

	private struct Transfer
	{
		public byte* Expected;
		public int Length;
		public int Offset;
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static int CountUnexpectedChunk(void* context, byte* bytes, int length)
	{
		(*(int*)context)++;
		return UnityAbi.Ok;
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static int VerifyChunk(void* context, byte* bytes, int length)
	{
		Transfer* transfer = (Transfer*)context;
		if (length <= 0 || length > 65536 || length > transfer->Length - transfer->Offset
			|| !new ReadOnlySpan<byte>(bytes, length).SequenceEqual(new ReadOnlySpan<byte>(transfer->Expected + transfer->Offset, length)))
			return -1;
		transfer->Offset += length;
		return UnityAbi.Ok;
	}
}
