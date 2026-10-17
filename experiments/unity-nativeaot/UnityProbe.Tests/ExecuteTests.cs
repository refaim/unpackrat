using System;
using System.IO;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class ExecuteTests
{
	[Fact]
	public void ReturnsExporterResult()
	{
		Assert.Equal(7, NativeProbe.Execute("in", "out", (_, _) => 7));
	}

	[Fact]
	public void MapsUnexpectedExceptionsToDataFailure()
	{
		Assert.Equal(NativeProbe.DataFailure,
			NativeProbe.Execute("in", "out", (_, _) => throw new InvalidOperationException("boom")));
	}

	[Fact]
	public void RethrowsOutOfMemory()
	{
		Assert.Throws<OutOfMemoryException>(
			() => NativeProbe.Execute("in", "out", (_, _) => throw new OutOfMemoryException()));
	}

	[Fact]
	public void NullExporterUsesTheRealPipeline()
	{
		string dir = Temp.NewDirectory();
		string missing = Path.Combine(dir, "absent.assets");
		Assert.True(NativeProbe.Execute(missing, dir, export: null) <= 0);
	}
}
