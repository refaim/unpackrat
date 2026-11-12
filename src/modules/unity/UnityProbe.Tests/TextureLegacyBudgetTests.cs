using System;
using System.IO;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.Streams.Smart;
using BuildTarget = AssetRipper.IO.Files.BuildTarget;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class TextureLegacyBudgetTests
{
	[Theory]
	[InlineData(TextureFormat.DXT1Crunched, BuildTarget.NoTarget, 0, 5000, 5000, 0UL)]
	[InlineData(TextureFormat.DXT1, BuildTarget.NoTarget, 0, 5000, 5000, 0UL)]
	[InlineData(TextureFormat.DXT1Crunched, BuildTarget.NoTarget, 0, 1, 1, 134_217_729UL)]
	[InlineData(TextureFormat.RGBA32, BuildTarget.Xbox360, 0, 5000, 5000, 0UL)]
	[InlineData(TextureFormat.RGBA32, BuildTarget.Switch, 12, 5000, 5000, 0UL)]
	[InlineData(TextureFormat.RGBA32, BuildTarget.Switch, 11, 100_000, 100_000, 0UL)]
	public void SpecialConversionPathsAreNotAdmittedBeyondTheirPreviousLimits(TextureFormat format,
		BuildTarget platform, int platformBlobBytes, int width, int height, ulong sourceBytes)
	{
		var collection = AssetCreator.CreateCollection(UnityVersion.V_2022);
		collection.SetLayout(UnityVersion.V_2022, platform, collection.Flags);
		ITexture2D texture = AssetCreator.CreateTexture2D(collection);
		texture.Width_C28 = width; texture.Height_C28 = height; texture.ImageCount_C28 = 1;
		texture.Format_C28E = format;
		texture.PlatformBlob_C28 = new byte[platformBlobBytes];
		texture.StreamData_C28!.Size = sourceBytes == 0 ? 4 : checked((uint)sourceBytes);
		texture.StreamData_C28.Path = new Utf8String("guard.resource");
		using ForbiddenSource source = new();
		using SmartStream smart = SmartStream.CreateOwned(source);
		using ResourceFile resource = new(smart, "", "guard.resource");
		collection.Bundle.AddResource(resource);
		source.Armed = true;

		Assert.False(UnitySource.TryDecodeTexture(texture, out DirectBitmap bitmap));
		Assert.True(bitmap.IsEmpty);
		Assert.Equal(0, source.Attempts);
	}

	[Fact]
	public void AnAdmittedLegacyTextureActuallyReachesTheResourceSentinel()
	{
		var collection = AssetCreator.CreateCollection(UnityVersion.V_2022);
		ITexture2D texture = AssetCreator.CreateTexture2D(collection);
		texture.Width_C28 = 1; texture.Height_C28 = 1; texture.ImageCount_C28 = 1;
		texture.Format_C28E = TextureFormat.DXT1Crunched;
		texture.StreamData_C28!.Size = 4;
		texture.StreamData_C28.Path = new Utf8String("guard.resource");
		using ForbiddenSource source = new();
		using SmartStream smart = SmartStream.CreateOwned(source);
		using ResourceFile resource = new(smart, "", "guard.resource");
		collection.Bundle.AddResource(resource);
		source.Armed = true;

		Assert.Same(source.Failure, Assert.Throws<InvalidOperationException>(() => UnitySource.TryDecodeTexture(texture, out _)));
		Assert.True(source.Attempts > 0);
	}

	[Fact]
	public void ATextureWithoutAPlatformBlobDoesNotRequireSwitchUnswizzling()
	{
		UnityVersion version = UnityVersion.Parse("3.5.0f1");
		var collection = AssetCreator.CreateCollection(version);
		collection.SetLayout(version, BuildTarget.Switch, collection.Flags);
		ITexture2D texture = AssetCreator.CreateTexture2D(collection);
		texture.Width_C28 = 1; texture.Height_C28 = 1; texture.ImageCount_C28 = 1;
		texture.Format_C28E = TextureFormat.RGBA32;
		texture.ImageData_C28 = [10, 20, 30, 255];
		Assert.Null(texture.PlatformBlob_C28);
		Assert.True(UnitySource.TryDecodeTexture(texture, out DirectBitmap bitmap));
		Assert.Equal(new byte[] { 10, 20, 30, 255 }, bitmap.Bits.ToArray());
	}

	private sealed class ForbiddenSource() : MemoryStream([1, 2, 3, 4], false)
	{
		public bool Armed { get; set; }
		public int Attempts { get; private set; }
		public InvalidOperationException Failure { get; } = new("Texture decoder accessed the resource.");
		public override long Length
		{
			get
			{
				if (Armed) { Attempts++; throw Failure; }
				return base.Length;
			}
		}
	}
}
