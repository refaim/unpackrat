using System;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityModuleVersionTests
{
	[Fact]
	public void ManagedAssemblyVersionMatchesTheUnityModuleVersionFile()
	{
		Assert.Equal(new Version(1, 0, 1, 0), typeof(UnitySession).Assembly.GetName().Version);
	}
}
