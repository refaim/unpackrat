using System;
using AssetRipper.Assets;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnitySourceTests
{
	[Fact]
	public void WrappingAnEmptySequenceYieldsNothing()
	{
		Assert.Empty(UnitySource.Wrap(Array.Empty<IUnityObjectBase>()));
	}
}
