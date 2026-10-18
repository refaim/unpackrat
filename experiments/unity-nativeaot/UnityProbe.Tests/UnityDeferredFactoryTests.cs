using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_0;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityDeferredFactoryTests
{
	private sealed class Reader : AssetFactoryBase
	{
		public int Calls;
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> refs)
		{ Calls++; return new UnknownObject(info, data.ToArray()); }
	}
	[Theory]
	[InlineData(114)]
	[InlineData(1)]
	[InlineData(123456789)]
	public void IgnoredClassesRetainOrdinaryObjectIdentityWithoutReadingTheirBodies(int classId)
	{
		var collection = AssetCreator.CreateCollection(UnityVersion.V_2022);
		AssetInfo info = new(collection, 7, classId);
		Reader inner = new(); UnitySource.LazyAssetFactory factory = new(inner);
		ObjectInfo descriptor = new() { ObjectData = [0xFF] };
		IUnityObjectBase identity = factory.ReadAsset(info, in descriptor, [])!;
		Assert.Equal(info, identity.AssetInfo); Assert.Equal(0, inner.Calls);
		IObject ordinary = Assert.IsAssignableFrom<IObject>(identity);
		ordinary.HideFlags_C0E = HideFlags.HideInHierarchy;
		Assert.Equal((uint)HideFlags.HideInHierarchy, ordinary.HideFlags_C0);
		IObject source = Assert.IsAssignableFrom<IObject>(factory.ReadAsset(info with { PathID = 8 }, in descriptor, []));
		source.HideFlags_C0 = 5; ordinary.CopyValues(source);
		Assert.Equal((HideFlags)5, ordinary.HideFlags_C0E);
		Assert.Equal(string.Empty, Assert.IsAssignableFrom<INamed>(identity).Name.String);
	}

	[Fact]
	public void UnsupportedPre35VersionsKeepTheirExistingNonResourceMeaning()
	{
		AssetInfo info = new(AssetCreator.CreateCollection(UnityVersion.Parse("3.4.0f1")), 7, 49);
		Reader inner = new(); UnitySource.LazyAssetFactory factory = new(inner);
		ObjectInfo descriptor = new() { ObjectData = [0xFF] };
		Assert.IsAssignableFrom<IUnityObjectBase>(factory.ReadAsset(info, in descriptor, []));
		Assert.Equal(0, inner.Calls);
	}

	[Fact]
	public void TheLegacyByteArrayOverloadStillDelegatesUnchanged()
	{
		AssetInfo info = new(AssetCreator.CreateCollection(UnityVersion.V_2022), 7, 49);
		Reader inner = new(); UnitySource.LazyAssetFactory factory = new(inner);
		UnknownObject result = Assert.IsType<UnknownObject>(factory.ReadAsset(info, new ReadOnlyArraySegment<byte>([1, 2]), null, []));
		Assert.Equal(new byte[] { 1, 2 }, result.RawData); Assert.Equal(1, inner.Calls);
	}

	[Fact]
	public void MalformedProjectedMetadataDoesNotFallBackToFullDeserialization()
	{
		AssetInfo info = new(AssetCreator.CreateCollection(UnityVersion.V_2022), 7, 49);
		Reader inner = new(); UnitySource.LazyAssetFactory factory = new(inner);
		foreach (byte[] bytes in new[] { new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, new byte[] { 1 } })
		{
			ObjectInfo descriptor = new() { ObjectData = bytes };
			Assert.IsAssignableFrom<IUnityObjectBase>(factory.ReadAsset(info, in descriptor, []));
			Assert.Equal(0, inner.Calls);
		}
	}

	[Fact]
	public void AlreadyMaterialisedCollectionsKeepTheirNormalAdapterViews()
	{
		var collection = AssetCreator.CreateCollection(UnityVersion.V_2022);
		var text = AssetCreator.CreateTextAsset(collection); text.Name = new Utf8String("text");
		Reader inner = new(); UnitySource.LazyAssetFactory factory = new(inner);
		IProbeText view = Assert.IsAssignableFrom<IProbeText>(Assert.Single(factory.Describe(collection)));
		Assert.Equal("text", view.Name); Assert.False(string.IsNullOrEmpty(view.Version));
	}
	[Theory]
	[InlineData(142)]
	[InlineData(147)]
	public void OriginalPathMetadataTypesUseTheMatureReaderDuringOpen(int classId)
	{
		AssetInfo info = new(AssetCreator.CreateCollection(UnityVersion.V_2022), 7, classId);
		Reader inner = new(); UnitySource.LazyAssetFactory factory = new(inner);
		ObjectInfo descriptor = new() { ObjectData = [1, 2, 3] };
		UnknownObject result = Assert.IsType<UnknownObject>(factory.ReadAsset(info, in descriptor, []));
		Assert.Equal(new byte[] { 1, 2, 3 }, result.RawData); Assert.Equal(1, inner.Calls);
	}

	[Fact]
	public void AResourceInterfaceWithoutACanonicalSchemaDoesNotInventAProjection()
	{
		// Model a future generated resource subclass whose schema is absent from the pinned TPK.
		var collection = AssetCreator.CreateCollection(UnityVersion.V_2022);
		AssetInfo future = new(collection, 7, 123456789);
		var definition = AssetCreator.CreateTextAsset(collection);
		var method = typeof(UnitySource.LazyAssetFactory).GetMethod("BuildShape", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
		object shape = method.Invoke(null, [future, definition, collection.Version])!;
		Assert.Equal(UnityEntryKind.Text, shape.GetType().GetProperty("Kind")!.GetValue(shape));
		Assert.Null(shape.GetType().GetProperty("Template")!.GetValue(shape));
	}	[Theory]
	[InlineData(49)]
	[InlineData(114)]
	public void PatchVersionUnreadableOrIgnoredObjectsNeverFallBackToFullResourceReads(int classId)
	{
		AssetInfo info = new(AssetCreator.CreateCollection(UnityVersion.Parse("5.4.1p1")), 7, classId);
		Reader inner = new(); UnitySource.LazyAssetFactory factory = new(inner);
		ObjectInfo descriptor = new() { ObjectData = [0xFF, 0xFF, 0xFF, 0xFF] };
		Assert.IsAssignableFrom<IUnityObjectBase>(factory.ReadAsset(info, in descriptor, []));
		Assert.Equal(0, inner.Calls);
	}
}
