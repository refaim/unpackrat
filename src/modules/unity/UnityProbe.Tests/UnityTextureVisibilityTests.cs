using System;
using System.Collections.Generic;
using System.IO;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityTextureVisibilityTests
{
	private sealed class BodyReadGuard : AssetFactoryBase
	{
		public int Calls { get; private set; }
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data,
			SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{
			Calls++;
			throw new InvalidOperationException("Listing a texture must not deserialize its pixel payload.");
		}
	}

	public static IEnumerable<object[]> DeclaredTextureCases()
	{
		(int Width, int Height, bool Inline, uint StreamSize, bool StreamPath, bool Visible)[] cases =
		[
			(0, 0, false, 0, false, false),
			(1, 0, false, 0, false, true),
			(0, 1, false, 0, false, true),
			(0, 0, true, 0, false, true),
			(0, 0, false, 4, false, true),
			(0, 0, false, 0, true, true),
		];
		foreach (bool compact in new[] { false, true })
			foreach (var row in cases)
				yield return [compact, row.Width, row.Height, row.Inline, row.StreamSize, row.StreamPath, row.Visible];
	}

	[Theory]
	[MemberData(nameof(DeclaredTextureCases))]
	public void OnlyAProvablyEmptyTextureIsOmittedFromTheCatalogue(bool compact, int width, int height,
		bool inline, uint streamSize, bool streamPath, bool visible)
	{
		byte[] bytes = inline ? [0x7F] : [];
		string path = Path.Combine(Temp.NewDirectory(), "declared.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedTextureMetadata(width, height, bytes,
			streamPath ? "missing.resS" : null, streamSize));
		BodyReadGuard reader = new();
		IUnityGraph graph = UnitySource.OpenGraph(path, readFile: null, fullReader: reader, compact: compact);
		using UnitySession session = UnitySession.FromGraph(graph);

		if (visible)
		{
			UnityCatalogEntry entry = Assert.Single(session.Entries);
			Assert.Equal(UnityEntryKind.Texture, entry.Kind);
			Assert.Equal("declared", entry.Name);
			Assert.Equal("png", entry.OutputExtension);
			Assert.Equal(bytes.Length, entry.SourceSize);
		}
		else
		{
			Assert.Empty(session.Entries);
		}
		Assert.Equal(0, reader.Calls);
	}
}
