using System;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityModuleVersionTests
{
	[Fact]
	public void ManagedAssemblyVersionMatchesTheUnityModuleVersionFile()
	{
		Assert.Equal(new Version(2, 0, 0, 0), typeof(UnitySession).Assembly.GetName().Version);
	}
}
