using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class BudgetsTests
{
	[Fact]
	public void ConstantsMatchTheDocumentedLimits()
	{
		Assert.Equal(256L * 1024 * 1024, Budgets.MaxTextureBytes);
		Assert.Equal(16, Budgets.TextureBytesPerPixel);
		Assert.Equal(16 * 1024 * 1024, Budgets.MaxTextBytes);
	}

	[Fact]
	public void SmallTextureIsAccepted()
	{
		Assert.True(Budgets.TryGetTextureBytes(1, 1, 1, out long bytes));
		Assert.Equal(Budgets.TextureBytesPerPixel, bytes);
	}

	[Fact]
	public void FourThousandNinetySixSquareRgbaFloatIsExactlyTheCap()
	{
		// 4096 x 4096 x 16 bytes = 256 MiB, the largest real texture we still accept.
		Assert.True(Budgets.TryGetTextureBytes(4096, 4096, 1, out long bytes));
		Assert.Equal(Budgets.MaxTextureBytes, bytes);
	}

	[Fact]
	public void TextureWidthPastTheCapIsRejected()
	{
		// 4097 x 4096 pixels already exceed the decoded byte budget.
		Assert.False(Budgets.TryGetTextureBytes(4097, 4096, 1, out long bytes));
		Assert.Equal(0, bytes);
	}

	[Fact]
	public void TextureHeightPastTheCapIsRejected()
	{
		Assert.False(Budgets.TryGetTextureBytes(4096, 4097, 1, out long bytes));
		Assert.Equal(0, bytes);
	}

	[Fact]
	public void TooManyLayersIsRejected()
	{
		// The planar size fits on its own, but multiplying by the layer count exceeds the budget.
		Assert.False(Budgets.TryGetTextureBytes(4096, 4096, 2, out long bytes));
		Assert.Equal(0, bytes);
	}

	[Theory]
	[InlineData(0, 1, 1)]
	[InlineData(1, 0, 1)]
	[InlineData(1, 1, 0)]
	[InlineData(-5, 1, 1)]
	[InlineData(1, -5, 1)]
	[InlineData(1, 1, -5)]
	public void NonPositiveDimensionsAreRejected(int width, int height, int depth)
	{
		Assert.False(Budgets.TryGetTextureBytes(width, height, depth, out long bytes));
		Assert.Equal(0, bytes);
	}

	[Fact]
	public void HostileDimensionsAreRejectedWithoutOverflow()
	{
		Assert.False(Budgets.TryGetTextureBytes(int.MaxValue, int.MaxValue, int.MaxValue, out long bytes));
		Assert.Equal(0, bytes);
	}

	[Fact]
	public void HugeLayerCountIsRejectedEvenWithTinyPlanarSize()
	{
		Assert.False(Budgets.TryGetTextureBytes(1, 1, int.MaxValue, out long bytes));
		Assert.Equal(0, bytes);
	}

	[Fact]
	public void MeshConstantsMatchTheDocumentedLimits()
	{
		Assert.Equal(4096, Budgets.MaxMeshSubMeshes);
		Assert.Equal(8_000_000L, Budgets.MaxMeshVertices);
		Assert.Equal(256L * 1024 * 1024, Budgets.MaxMeshIndexBytes);
		Assert.Equal(512L * 1024 * 1024, Budgets.MaxMeshSourceBytes);
		Assert.Equal(2_147_483_648L, Budgets.MaxMeshDecodedBytes);
		Assert.Equal(80L, Budgets.MeshDecodedBytesPerItem);
	}

	/// <summary>
	/// Decision table for the mesh budget: every dimension at its cap is accepted, and each
	/// dimension one past its cap (with all others in range) is refused on its own.
	/// </summary>
	[Theory]
	[InlineData(4096, 8_000_000, 2_147_483_648, 256L * 1024 * 1024, 512L * 1024 * 1024, true)]
	[InlineData(4097, 8_000_000, 2_147_483_648, 256L * 1024 * 1024, 512L * 1024 * 1024, false)]
	[InlineData(4096, 8_000_001, 2_147_483_648, 256L * 1024 * 1024, 512L * 1024 * 1024, false)]
	[InlineData(4096, 8_000_000, 2_147_483_649, 256L * 1024 * 1024, 512L * 1024 * 1024, false)]
	[InlineData(4096, 8_000_000, 2_147_483_648, 256L * 1024 * 1024 + 1, 512L * 1024 * 1024, false)]
	[InlineData(4096, 8_000_000, 2_147_483_648, 256L * 1024 * 1024, 512L * 1024 * 1024 + 1, false)]
	public void MeshBudgetIsDecidedFromEveryDimension(long subMeshes, long vertices, long decodedBytes, long indexBytes, long sourceBytes, bool expected)
	{
		Assert.Equal(expected, Budgets.IsWithinMeshBudget(subMeshes, vertices, decodedBytes, indexBytes, sourceBytes));
	}
}
