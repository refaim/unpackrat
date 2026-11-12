using System.Collections.Generic;
using System.IO;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityGeneration23EqualityTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DifferentExtendedHashesKeepTypeDefinitionsDistinct(bool reference)
	{
		SerializedTypeBase first = Type(reference, 1, true);
		SerializedTypeBase second = Type(reference, 2, true);
		Assert.False(first.Equals(second));
		Assert.False(second.Equals(first));
		Assert.Equal(2, new HashSet<SerializedTypeBase> { first, second }.Count);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ExternalReferenceAndInlineDefinitionKeepTypesDistinct(bool reference)
	{
		SerializedTypeBase first = Type(reference, 1, true);
		SerializedTypeBase second = Type(reference, 1, false);
		Assert.False(first.Equals(second));
		Assert.False(second.Equals(first));
		Assert.Equal(2, new HashSet<SerializedTypeBase> { first, second }.Count);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void IdenticalExtendedMetadataKeepsEqualityAndHashCodeConsistent(bool reference, bool definition)
	{
		SerializedTypeBase first = Type(reference, 1, definition);
		SerializedTypeBase second = Type(reference, 1, definition);
		Assert.True(first.Equals(second));
		Assert.True(second.Equals(first));
		Assert.Equal(first.GetHashCode(), second.GetHashCode());
		Assert.Single(new HashSet<SerializedTypeBase> { first, second });
	}

	[Fact]
	public void SerializedAndReferenceTypesKeepEqualityAndHashCodesAfterGeneration23RoundTrip()
	{
		byte[] hash = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
		byte[] bytes = UnityFixtureBuilder.SerializedGeneration23TypedTexture("equality_texture",
			[1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255], extendedTypeHash: hash);
		using MemoryStream input = new(bytes, writable: false);
		using AssetsTools.NET.AssetsFileReader reader = new(input);
		AssetsTools.NET.AssetsFile file = new();
		file.Read(reader);
		file.Metadata.RefTypes = [new AssetsTools.NET.TypeTreeType
		{
			TypeId = 114, IsRefType = true, ScriptTypeIndex = ushort.MaxValue,
			ScriptIdHash = AssetsTools.NET.Hash128.NewBlankHash(), TypeHash = AssetsTools.NET.Hash128.NewBlankHash(),
			ExtTypeHash = new AssetsTools.NET.Hash128(hash), TypeBlobIsDefinition = true,
			TypeBlob = new AssetsTools.NET.TypeTreeBlob { Nodes = [], StringBufferBytes = [] },
			TypeReference = new AssetsTools.NET.AssetTypeReference("Fixture", "Fixtures", "TestAssembly"),
		}];
		using MemoryStream encoded = new();
		using (AssetsTools.NET.AssetsFileWriter writer = new(encoded)) file.Write(writer, 0);
		using SerializedFile original = Read(encoded.ToArray());
		using MemoryStream rewritten = new();
		original.Write(rewritten);
		using SerializedFile actual = Read(rewritten.ToArray());
		Assert.Equal(original.Types[0], actual.Types[0]);
		Assert.Equal(original.Types[0].GetHashCode(), actual.Types[0].GetHashCode());
		Assert.Equal(original.RefTypes[0], actual.RefTypes[0]);
		Assert.Equal(original.RefTypes[0].GetHashCode(), actual.RefTypes[0].GetHashCode());
	}

	private static SerializedFile Read(byte[] bytes)
	{
		using SmartStream stream = SmartStream.CreateMemory(bytes);
		return (SerializedFile)SchemeReader.ReadFile(stream, "equality.assets", "equality.assets");
	}

	private static SerializedTypeBase Type(bool reference, uint hash, bool definition)
	{
		SerializedTypeBase result = reference ? new SerializedTypeReference() : new SerializedType();
		result.TypeID = 28;
		result.ScriptTypeIndex = -1;
		result.ExtendedTypeHash = new SerializedTypeBase.Hash128(hash, 2, 3, 4);
		result.IsTypeTreeDefinition = definition;
		return result;
	}
}
