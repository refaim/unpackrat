using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_128;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityResourceReaderTests
{
	private static readonly byte[] Pixels = Enumerable.Repeat(new byte[] { 10, 20, 30, 255 }, 4).SelectMany(p => p).ToArray();

	private sealed class CaptureFactory : AssetFactoryBase
	{
		public AssetInfo? Info { get; private set; }
		public byte[] Data { get; private set; } = [];
		public SerializedType? Type { get; private set; }
		public IReadOnlyList<SerializedTypeReference> References { get; private set; } = [];

		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{
			Info = info;
			Data = data.ToArray();
			Type = type;
			References = references.ToArray();
			return null;
		}
	}

	private static void WithCapturedAsset(byte[] bytes, Action<AssetInfo, byte[], SerializedType?, IReadOnlyList<SerializedTypeReference>> action)
	{
		using SmartStream input = SmartStream.CreateMemory(bytes);
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "resource.assets", "resource.assets");
		using GameBundle root = new();
		CaptureFactory capture = new();
		root.AddCollectionFromSerializedFile(file, capture);
		Assert.NotNull(capture.Info);
		action(capture.Info.Value, capture.Data, capture.Type, capture.References);
	}

	[Fact]
	public void ResourceReaderPreservesPre35UnreadableBytes()
	{
		var collection = AssetCreator.CreateCollection(UnityVersion.Parse("3.4.0f1"));
		AssetInfo info = new(collection, 7, 49);
		byte[] data = [1, 2, 3, 4, 5];

		IUnityObjectBase result = GameAssetFactory.ReadResourceAsset(info, new ReadOnlyArraySegment<byte>(data), AssetFactory.CreateSerialized);

		UnreadableObject unreadable = Assert.IsType<UnreadableObject>(result);
		Assert.Equal(49, unreadable.AssetInfo.ClassID);
		Assert.Equal(7, unreadable.AssetInfo.PathID);
		Assert.Equal(data, PreservedBytes(unreadable));
	}

	[Fact]
	public void ResourceReaderRejectsNullConstructionCallback()
	{
		var collection = AssetCreator.CreateCollection(UnityVersion.Parse("2021.3.0f1"));
		AssetInfo info = new(collection, 7, 49);
		byte[] data = [1, 2, 3];

		Assert.Throws<ArgumentNullException>(() => GameAssetFactory.ReadResourceAsset(info, new ReadOnlyArraySegment<byte>(data), null!));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public void ResourceReaderMatchesMatureReaderForNormalAssets(int kind)
	{
		byte[] bytes = kind switch
		{
			0 => UnityFixtureBuilder.SerializedTextAsset("resource", "payload"),
			1 => UnityFixtureBuilder.SerializedFont("resource", [1, 2, 3, 4]),
			2 => UnityFixtureBuilder.SerializedInlineTexture("resource", Pixels),
			_ => throw new InvalidOperationException(),
		};

		WithCapturedAsset(bytes, (info, data, type, references) =>
		{
			GameAssetFactory mature = new(new BaseManager(static _ => { }));
			IUnityObjectBase? expected = mature.ReadAsset(info, new ReadOnlyArraySegment<byte>(data), type, references);
			IUnityObjectBase actual = GameAssetFactory.ReadResourceAsset(info, new ReadOnlyArraySegment<byte>(data), AssetFactory.CreateSerialized);

			Assert.NotNull(expected);
			Assert.Equal(expected.GetType(), actual.GetType());
			Assert.Equal(Describe(expected), Describe(actual));
		});
	}

	[Fact]
	public void DefaultOpenGraphExportsTextThroughTheResourceFactory()
	{
		string path = Path.Combine(Temp.NewDirectory(), "resource.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedTextAsset("notes", "payload"));

		using UnitySession session = UnitySession.Open(path);
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal("notes", entry.Name);
		using MemoryStream output = new();
		Assert.True(session.Export(0, output));
		Assert.Equal("payload", System.Text.Encoding.UTF8.GetString(output.ToArray()));
	}

	private static string Describe(IUnityObjectBase asset) => asset switch
	{
		ITextAsset text => $"text:{text.Name}:{Convert.ToHexString(text.Script_C49.Data)}",
		IFont font => $"font:{font.Name}:{Convert.ToHexString(font.FontData)}",
		ITexture2D texture => $"texture:{texture.Name}:{texture.Width_C28}x{texture.Height_C28}:{Convert.ToHexString(texture.ImageData_C28)}",
		_ => asset.GetType().FullName ?? asset.GetType().Name,
	};

	private static byte[] PreservedBytes(UnreadableObject unreadable)
	{
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		foreach (PropertyInfo property in typeof(UnreadableObject).GetProperties(flags))
		{
			if (property.GetIndexParameters().Length == 0 && AsBytes(property.GetValue(unreadable)) is { } bytes)
			{
				return bytes;
			}
		}
		foreach (FieldInfo field in typeof(UnreadableObject).GetFields(flags))
		{
			if (AsBytes(field.GetValue(unreadable)) is { } bytes)
			{
				return bytes;
			}
		}

		throw new InvalidOperationException("UnreadableObject does not expose its preserved bytes.");
	}

	private static byte[]? AsBytes(object? value) => value switch
	{
		byte[] bytes => bytes,
		ReadOnlyArraySegment<byte> segment => segment.ToArray(),
		_ => null,
	};
}
