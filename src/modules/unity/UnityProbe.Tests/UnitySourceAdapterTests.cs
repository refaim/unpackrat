using System;
using System.Collections.Generic;
using System.Linq;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Exercises the AssetRipper adapter (<see cref="UnitySource"/>) with tiny generated assets built
/// through <see cref="AssetCreator"/> instead of hand-written mocks. This is the only place that
/// touches AssetRipper types; the export policy itself is tested against the project-owned
/// interfaces.
/// </summary>
public sealed class UnitySourceAdapterTests
{
	private static ProcessedAssetCollection NewCollection() => AssetCreator.CreateCollection(UnityVersion.V_2022);

	private static ITexture2D NewTexture(int width, int height, int imageCount, TextureFormat format, byte[] data)
	{
		ITexture2D texture = AssetCreator.CreateTexture2D(NewCollection());
		texture.Width_C28 = width;
		texture.Height_C28 = height;
		texture.ImageCount_C28 = imageCount;
		texture.Format_C28E = format;
		texture.ImageData_C28 = data;
		return texture;
	}

	private static ITextAsset NewTextAsset(byte[] payload)
	{
		ITextAsset text = AssetCreator.CreateTextAsset(NewCollection());
		text.Script_C49 = new Utf8String(payload);
		return text;
	}

	private static IProbeAsset WrapOne(IUnityObjectBase asset) => UnitySource.Wrap(new[] { asset }).Single();

	[Fact]
	public void WrapsASinglePixelTextureAndRendersPng()
	{
		// 1x1 RGBA32 with four bytes is enough for the real TextureConverter decoder.
		ITexture2D texture = NewTexture(1, 1, 1, TextureFormat.RGBA32, [0, 128, 255, 255]);

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		Assert.Equal(texture.PathID, wrapped.PathId);
		Assert.False(string.IsNullOrEmpty(wrapped.Version));

		byte[]? png = wrapped.RenderPng();
		Assert.NotNull(png);
		Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png!.Take(4));
	}

	[Fact]
	public void TextureAdapterReportsItsOriginalName()
	{
		// The paintable label is metadata only; it must be surfaced as the asset's own name.
		ITexture2D texture = NewTexture(1, 1, 1, TextureFormat.RGBA32, [0, 128, 255, 255]);
		texture.Name = new Utf8String("hero");

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		Assert.Equal("hero", wrapped.Name);
	}

	[Fact]
	public void TextureWithoutImageDataRendersNothing()
	{
		ITexture2D texture = NewTexture(1, 1, 1, TextureFormat.RGBA32, []);

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		Assert.Null(wrapped.RenderPng());
	}

	[Fact]
	public void OversizedTextureIsRejectedBeforeDecoding()
	{
		// Dimensions far beyond the decoded-size budget, but only four bytes of payload, so the
		// bound must reject the texture without the converter ever materialising the bitmap.
		ITexture2D texture = NewTexture(100_000, 100_000, 1, TextureFormat.RGBA32, [0, 0, 0, 0]);

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		Assert.Null(wrapped.RenderPng());
	}

	[Fact]
	public void WrapsTextAssetAndReturnsRawBytes()
	{
		ITextAsset text = NewTextAsset([1, 2, 3]);

		IProbeText wrapped = Assert.IsAssignableFrom<IProbeText>(WrapOne(text));
		Assert.Equal(text.PathID, wrapped.PathId);
		Assert.Equal(new byte[] { 1, 2, 3 }, wrapped.ReadData());
	}

	[Fact]
	public void EmptyTextAssetReturnsEmptyPayload()
	{
		ITextAsset text = AssetCreator.CreateTextAsset(NewCollection());

		IProbeText wrapped = Assert.IsAssignableFrom<IProbeText>(WrapOne(text));
		Assert.Empty(wrapped.ReadData()!);
	}

	[Fact]
	public void OversizedTextAssetIsRejectedBeforeCopying()
	{
		// 16 MiB + 1 is a safe allocation for the test yet one byte past the copy budget, so the
		// adapter must refuse to materialise the payload.
		ITextAsset text = NewTextAsset(new byte[(16 * 1024 * 1024) + 1]);

		IProbeText wrapped = Assert.IsAssignableFrom<IProbeText>(WrapOne(text));
		Assert.Null(wrapped.ReadData());
	}

	[Fact]
	public void NonTextureTextAssetBecomesAnOtherMarker()
	{
		IGameObject other = AssetCreator.CreateGameObject(NewCollection());

		List<IProbeAsset> wrapped = UnitySource.Wrap(new IUnityObjectBase[] { other }).ToList();
		IProbeAsset asset = Assert.Single(wrapped);
		Assert.False(asset is IProbeTexture);
		Assert.False(asset is IProbeText);
	}

	[Fact]
	public void RgbaFloatTextureFitsTheDecodedByteBudget()
	{
		// RGBAFloat is four 32-bit floats per pixel: 16 bytes of payload for a 1x1 image, the
		// widest decoded pixel the budget must cover. The real TextureConverter materialises the
		// bitmap, so the decoded byte bound must be at least that bitmap's size.
		ITexture2D texture = NewTexture(1, 1, 1, TextureFormat.RGBAFloat, new byte[16]);

		Assert.True(TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap));
		Assert.True(Budgets.TryGetTextureBytes(1, 1, 1, out long bytes));
		Assert.True(bytes >= bitmap.ByteSize);
	}

	[Fact]
	public void OversizedStreamedTextureSourceIsRejectedWithoutReading()
	{
		// The 1x1 decoded size is tiny, but the streamed source payload is one byte past the
		// source cap. The adapter must refuse it from the declared stream size alone, before the
		// converter resolves and copies the resource, so the resource stream stays untouched.
		byte[] payload = new byte[(128 * 1024 * 1024) + 1];
		ITexture2D texture = NewTexture(1, 1, 1, TextureFormat.RGBA32, []);
		texture.StreamData_C28!.Size = (uint)payload.Length;
		texture.StreamData_C28!.Path = new Utf8String("fixture.resource");

		using ResourceFile resource = new(payload, "", "fixture.resource", writable: false);
		texture.Collection.Bundle.AddResource(resource);

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		Assert.Null(wrapped.RenderPng());
		Assert.Equal(0, resource.Stream.Position);
	}

	[Fact]
	public void SmallStreamedTextureSourceExportsPngAndConsumesTheResource()
	{
		// A 4-byte source within the cap must still export through the very same streamed path, so
		// the guard only rejects oversized payloads.
		byte[] payload = [0, 128, 255, 255];
		ITexture2D texture = NewTexture(1, 1, 1, TextureFormat.RGBA32, []);
		texture.StreamData_C28!.Size = (uint)payload.Length;
		texture.StreamData_C28!.Path = new Utf8String("fixture.resource");

		using ResourceFile resource = new(payload, "", "fixture.resource", writable: false);
		texture.Collection.Bundle.AddResource(resource);

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		byte[]? png = wrapped.RenderPng();
		Assert.NotNull(png);
		Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png!.Take(4));
		Assert.Equal(payload.Length, resource.Stream.Position);
	}

	[Fact]
	public void OversizedInlineTextureSourceIsRejected()
	{
		// 128 MiB + 1 of inline data is one byte past the source cap, so it is refused from the
		// declared length even though the 1x1 decoded size is tiny.
		ITexture2D texture = NewTexture(1, 1, 1, TextureFormat.RGBA32, new byte[(128 * 1024 * 1024) + 1]);

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		Assert.Null(wrapped.RenderPng());
	}

	[Fact]
	public void HugeUnsignedStreamedSizeIsRejectedWithoutAllocation()
	{
		// uint.MaxValue must be refused from the unsigned streamed size alone, before any resource
		// is resolved or the converter allocates. No resource is registered, so any attempt to read
		// it would fail rather than return null.
		ITexture2D texture = NewTexture(1, 1, 1, TextureFormat.RGBA32, []);
		texture.StreamData_C28!.Size = uint.MaxValue;
		texture.StreamData_C28!.Path = new Utf8String("fixture.resource");

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		Assert.Null(wrapped.RenderPng());
	}

	[Fact]
	public void LegacyTextureWithoutStreamedDataStillRenders()
	{
		// Version 3.5 has no streamed data field at all; the optional streamed check must not throw
		// and a small inline payload must still export.
		ITexture2D texture = AssetCreator.CreateTexture2D(AssetCreator.CreateCollection(UnityVersion.Parse("3.5.0f1")));
		texture.Width_C28 = 1;
		texture.Height_C28 = 1;
		texture.ImageCount_C28 = 1;
		texture.Format_C28E = TextureFormat.RGBA32;
		texture.ImageData_C28 = [0, 128, 255, 255];
		Assert.Null(texture.StreamData_C28);

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		byte[]? png = wrapped.RenderPng();
		Assert.NotNull(png);
		Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png!.Take(4));
	}

	[Fact]
	public void ValidInlineDataIgnoresUnusedOversizedStreamedMetadata()
	{
		// The converter prefers non-empty inline data, so an unused oversized streamed size on the
		// same texture must not reject an otherwise valid inline payload.
		ITexture2D texture = NewTexture(1, 1, 1, TextureFormat.RGBA32, [0, 128, 255, 255]);
		texture.StreamData_C28!.Size = uint.MaxValue;
		texture.StreamData_C28!.Path = new Utf8String("unused.resource");

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		byte[]? png = wrapped.RenderPng();
		Assert.NotNull(png);
		Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png!.Take(4));
	}

	[Fact]
	public void LegacyEmptyTextureWithNullStreamedDataRendersNothing()
	{
		// A legacy texture with no inline data and no streamed field reaches the guard's optional
		// streamed check with a null value; it must fall through to the converter and return null
		// rather than throwing.
		ITexture2D texture = AssetCreator.CreateTexture2D(AssetCreator.CreateCollection(UnityVersion.Parse("3.5.0f1")));
		texture.Width_C28 = 1;
		texture.Height_C28 = 1;
		texture.ImageCount_C28 = 1;
		texture.Format_C28E = TextureFormat.RGBA32;
		texture.ImageData_C28 = [];
		Assert.Null(texture.StreamData_C28);

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		Assert.Null(wrapped.RenderPng());
	}

	[Fact]
	public void LegacyEmptyTextureWithNullStreamedDataReportsUnknownSourceSize()
	{
		// The same legacy texture (empty inline data, no streamed field at all) must also survive
		// catalogue construction. Reading its declared source length from metadata alone must treat
		// the absent descriptor as the unknown sentinel zero rather than dereferencing it, and the
		// catalogue must build successfully without rendering or exporting anything.
		ITexture2D texture = AssetCreator.CreateTexture2D(AssetCreator.CreateCollection(UnityVersion.Parse("3.5.0f1")));
		texture.Width_C28 = 1;
		texture.Height_C28 = 1;
		texture.ImageCount_C28 = 1;
		texture.Format_C28E = TextureFormat.RGBA32;
		texture.ImageData_C28 = [];
		Assert.Null(texture.StreamData_C28);

		IProbeAsset wrapped = WrapOne(texture);
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("legacy", wrapped)));

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(0L, entry.SourceSize);
	}

	[Fact]
	public void EmptyRuntimePlaceholderIsNotAnExportableTexture()
	{
		// A runtime font atlas cleared to nothing is a placeholder: zero dimensions, no inline
		// bytes and no declared streamed resource. Its metadata alone proves there is nothing to
		// render, so it must never be surfaced as an extractable PNG.
		ITexture2D texture = NewTexture(0, 0, 1, TextureFormat.RGBA32, []);
		texture.StreamData_C28!.Size = 0;
		texture.StreamData_C28!.Path = Utf8String.Empty;

		IProbeAsset wrapped = WrapOne(texture);
		Assert.False(wrapped is IProbeTexture);
		Assert.False(wrapped is IProbeText);
	}

	[Fact]
	public void EmptyRuntimePlaceholderIsAbsentFromTheCatalogue()
	{
		ITexture2D texture = NewTexture(0, 0, 1, TextureFormat.RGBA32, []);
		texture.StreamData_C28!.Size = 0;
		texture.StreamData_C28!.Path = Utf8String.Empty;

		IProbeAsset wrapped = WrapOne(texture);
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("empty", wrapped)));

		Assert.Empty(session.Entries);
	}

	[Fact]
	public void EmptyPlaceholderWithInlinePayloadStaysAnExportableTexture()
	{
		// Zero dimensions but a real inline payload is corrupt or unsupported, not provably empty,
		// so it must stay visible as a texture whose export still fails rather than be hidden.
		ITexture2D texture = NewTexture(0, 0, 1, TextureFormat.RGBA32, [0, 128, 255, 255]);

		Assert.True(WrapOne(texture) is IProbeTexture);
	}

	[Fact]
	public void EmptyPlaceholderWithDeclaredStreamSizeStaysAnExportableTexture()
	{
		ITexture2D texture = NewTexture(0, 0, 1, TextureFormat.RGBA32, []);
		texture.StreamData_C28!.Size = 4;
		texture.StreamData_C28!.Path = Utf8String.Empty;

		Assert.True(WrapOne(texture) is IProbeTexture);
	}

	[Fact]
	public void EmptyPlaceholderWithNamedStreamDescriptorStaysAnExportableTexture()
	{
		// A named descriptor with a zero declared size still declares an external resource, so it
		// is kept even when that resource turns out to be missing (a missing reference).
		ITexture2D texture = NewTexture(0, 0, 1, TextureFormat.RGBA32, []);
		texture.StreamData_C28!.Size = 0;
		texture.StreamData_C28!.Path = new Utf8String("missing.resource");

		Assert.True(WrapOne(texture) is IProbeTexture);
	}

	[Theory]
	[InlineData(1, 0)]
	[InlineData(0, 1)]
	public void TextureWithAnyPositiveDimensionStaysAnExportableTexture(int width, int height)
	{
		ITexture2D texture = NewTexture(width, height, 1, TextureFormat.RGBA32, []);

		Assert.True(WrapOne(texture) is IProbeTexture);
	}

	[Fact]
	public void PositiveDimensionTextureWithUnknownSourceSizeStaysCatalogued()
	{
		// Classification never keys on the declared source length: an unknown SourceSize (zero)
		// with a real dimension must remain a catalogue entry.
		ITexture2D texture = NewTexture(0, 1, 1, TextureFormat.RGBA32, []);

		IProbeAsset wrapped = WrapOne(texture);
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("unknown", wrapped)));

		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(0L, entry.SourceSize);
	}

	[Fact]
	public void LegacyEmptyPlaceholderWithoutStreamedDescriptorIsNotAnExportableTexture()
	{
		// 3.5 has no streamed field at all; an absent descriptor is no declaration, so a legacy
		// zero-size placeholder is excluded exactly like a modern one.
		ITexture2D texture = AssetCreator.CreateTexture2D(AssetCreator.CreateCollection(UnityVersion.Parse("3.5.0f1")));
		texture.Width_C28 = 0;
		texture.Height_C28 = 0;
		texture.ImageCount_C28 = 1;
		texture.Format_C28E = TextureFormat.RGBA32;
		texture.ImageData_C28 = [];
		Assert.Null(texture.StreamData_C28);

		Assert.False(WrapOne(texture) is IProbeTexture);
	}

	[Fact]
	public void TextureAdapterReportsTheOriginalPathTheProcessorRecorded()
	{
		// The original-path processor records the asset's own path on the asset object; the adapter
		// must surface it as metadata, untransformed and without making it a file-system path.
		ITexture2D texture = NewTexture(1, 1, 1, TextureFormat.RGBA32, [0, 128, 255, 255]);
		texture.OriginalPath = "Assets/Resources/hero.png";

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		Assert.Equal("Assets/Resources/hero.png", wrapped.OriginalPath);
	}

	[Fact]
	public void TextureAdapterWithoutARecordedOriginalPathReportsNull()
	{
		ITexture2D texture = NewTexture(1, 1, 1, TextureFormat.RGBA32, [0, 128, 255, 255]);
		Assert.Null(texture.OriginalPath);

		IProbeTexture wrapped = Assert.IsAssignableFrom<IProbeTexture>(WrapOne(texture));
		Assert.Null(wrapped.OriginalPath);
	}

	[Fact]
	public void TextAdapterReportsTheOriginalPathTheProcessorRecorded()
	{
		ITextAsset text = NewTextAsset([1, 2, 3]);
		text.OriginalPath = "Assets/Resources/notes.txt";

		IProbeText wrapped = Assert.IsAssignableFrom<IProbeText>(WrapOne(text));
		Assert.Equal("Assets/Resources/notes.txt", wrapped.OriginalPath);
	}

	[Fact]
	public void TextAdapterWithoutARecordedOriginalPathReportsNull()
	{
		ITextAsset text = NewTextAsset([1, 2, 3]);
		Assert.Null(text.OriginalPath);

		IProbeText wrapped = Assert.IsAssignableFrom<IProbeText>(WrapOne(text));
		Assert.Null(wrapped.OriginalPath);
	}
}
