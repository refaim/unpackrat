using System;
using System.Collections.Generic;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityAssetFactoryTests
{
	private sealed class RecordingFactory : AssetFactoryBase
	{
		public int Calls { get; private set; }
		public Action<AssetInfo, ReadOnlyArraySegment<byte>, SerializedType?, IReadOnlyList<SerializedTypeReference>>? OnRead { get; init; }
		public IUnityObjectBase? Result { get; init; }
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{
			Calls++;
			OnRead?.Invoke(info, data, type, references);
			return Result;
		}
	}

	private static AssetInfo Info(int classId) => new(AssetCreator.CreateCollection(UnityVersion.V_2022), 7, classId);

	[Fact]
	public void UnusedScriptKeepsOnlyItsIdentityAndNeverCallsTheDeserializer()
	{
		RecordingFactory inner = new() { OnRead = (_, _, _, _) => throw new InvalidOperationException("must not deserialize") };
		AssetInfo info = Info(114);
		AssetFactoryBase factory = NewFactory(inner);
		IUnityObjectBase? asset = factory.ReadAsset(info, new ReadOnlyArraySegment<byte>([1, 2, 3]), null, []);
		UnknownObject placeholder = Assert.IsType<UnknownObject>(asset);
		Assert.Equal(info, placeholder.AssetInfo);
		Assert.Empty(placeholder.RawData);
		Assert.Equal(0, inner.Calls);
		Assert.False(Assert.Single(UnitySource.Wrap([placeholder])) is IProbeText or IProbeTexture or IProbeResource);
	}

	[Theory]
	[InlineData(28)]
	[InlineData(49)]
	[InlineData(115)]
	[InlineData(89)]
	[InlineData(147)]
	[InlineData(142)]
	[InlineData(687078895)]
	[InlineData(123456789)]
	public void OtherClassesForwardAllInputsAndTheExactResult(int classId)
	{
		AssetInfo info = Info(classId);
		ReadOnlyArraySegment<byte> data = new([9, 8, 7], 1, 2);
		SerializedType type = new();
		SerializedTypeReference[] references = [new()];
		UnknownObject result = new(info, []);
		RecordingFactory inner = new()
		{
			Result = result,
			OnRead = (actualInfo, actualData, actualType, actualReferences) =>
			{
				Assert.Equal(info, actualInfo);
				Assert.True(data.Equals(actualData));
				Assert.Same(type, actualType);
				Assert.Same(references, actualReferences);
			},
		};
		Assert.Same(result, NewFactory(inner).ReadAsset(info, data, type, references));
		Assert.Equal(1, inner.Calls);
	}

	[Fact]
	public void DelegatedNullAndMemoryFailuresKeepTheirOriginalMeaning()
	{
		AssetInfo info = Info(49);
		Assert.Null(NewFactory(new RecordingFactory()).ReadAsset(info, ReadOnlyArraySegment<byte>.Empty, null, []));
		OutOfMemoryException error = new("sentinel");
		RecordingFactory inner = new() { OnRead = (_, _, _, _) => throw error };
		Assert.Same(error, Assert.Throws<OutOfMemoryException>(() => NewFactory(inner).ReadAsset(info, ReadOnlyArraySegment<byte>.Empty, null, [])));
	}

	private static AssetFactoryBase NewFactory(AssetFactoryBase inner) => new UnityAssetFactory(inner);
}
