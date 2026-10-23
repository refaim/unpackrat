using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed unsafe class NativeProbeTests
{
	[Fact]
	public void NullInputIsInvalidArgument()
	{
		char[] output = Temp.NewDirectory().ToCharArray();
		fixed (char* outPointer = output)
		{
			delegate* unmanaged[Cdecl]<ushort*, int, ushort*, int, int> probe = &NativeProbe.Run;
			Assert.Equal(NativeProbe.InvalidArgument, probe(null, 4, (ushort*)outPointer, output.Length));
		}
	}

	[Fact]
	public void NullOutputIsInvalidArgument()
	{
		char[] input = "x".ToCharArray();
		fixed (char* inPointer = input)
		{
			delegate* unmanaged[Cdecl]<ushort*, int, ushort*, int, int> probe = &NativeProbe.Run;
			Assert.Equal(NativeProbe.InvalidArgument, probe((ushort*)inPointer, input.Length, null, 4));
		}
	}

	[Fact]
	public void MissingInputPathFailsWithoutThrowing()
	{
		string dir = Temp.NewDirectory();
		char[] input = Path.Combine(dir, "does-not-exist.assets").ToCharArray();
		char[] output = dir.ToCharArray();
		fixed (char* inPointer = input)
		fixed (char* outPointer = output)
		{
			delegate* unmanaged[Cdecl]<ushort*, int, ushort*, int, int> probe = &NativeProbe.Run;
			Assert.True(probe((ushort*)inPointer, input.Length, (ushort*)outPointer, output.Length) <= 0);
		}
	}
}
