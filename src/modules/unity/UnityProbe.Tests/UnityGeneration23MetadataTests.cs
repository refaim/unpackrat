using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.Streams.Smart;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>Input-controlled metadata must fail before a count allocates or a string leaves its span.</summary>
public sealed class UnityGeneration23MetadataTests
{
	public enum CountField { Types, Objects, ScriptTypes, Externals, RefTypes, TypeDependencies }
	public enum BadCount { Negative, AboveMaximum, ExceedsMetadata }
	public enum StringField { Signature, UserInformation, ExternalVirtualPath, ExternalPath, RefClass, RefNamespace, RefAssembly }
	private const int StringByteLimit = 1024 * 1024;
	private static readonly byte[] Pixels = [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];

	public static TheoryData<CountField, BadCount, bool> InvalidCounts
	{
		get
		{
			TheoryData<CountField, BadCount, bool> cases = new();
			foreach (CountField metadataField in Enum.GetValues<CountField>())
				foreach (BadCount corruption in Enum.GetValues<BadCount>())
				{
					cases.Add(metadataField, corruption, false);
					cases.Add(metadataField, corruption, true);
				}
			return cases;
		}
	}

	[Theory]
	[MemberData(nameof(InvalidCounts))]
	public void InvalidArrayCountsAreInvalidDataBeforeAllocation(CountField field, BadCount corruption, bool compact)
	{
		(byte[] bytes, int offset, int stride) = CountFixture(field);
		int maximum = field == CountField.Objects ? 1_000_000 : 65536;
		int metadataEnd = 48 + BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
		int count = corruption switch
		{
			BadCount.Negative => -1,
			BadCount.AboveMaximum => maximum + 1,
			_ => ((metadataEnd - offset - 4) / stride) + 1,
		};
		BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), count);
		AssertInvalidMetadata(bytes, compact);
	}

	public static TheoryData<CountField, bool> TruncatedCounts
	{
		get
		{
			TheoryData<CountField, bool> cases = new();
			foreach (CountField metadataField in Enum.GetValues<CountField>())
			{
				cases.Add(metadataField, false);
				cases.Add(metadataField, true);
			}
			return cases;
		}
	}

	[Theory]
	[MemberData(nameof(TruncatedCounts))]
	public void CountWordsCannotBorrowTheirLastByteFromOutsideMetadata(CountField field, bool compact)
	{
		(byte[] bytes, int offset, _) = CountFixture(field);
		BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), offset + 3 - 48);
		AssertInvalidMetadata(bytes, compact);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void TypeCountAtTheLimitAndEmptyAuxiliaryArraysRemainReadable(bool compact)
	{
		byte[] bytes = Rewrite(file =>
		{
			TypeTreeType type = Assert.Single(file.Metadata.TypeTreeTypes);
			file.Metadata.TypeTreeTypes = Enumerable.Repeat(type, 65536).ToList();
		});
		using SerializedFile parsed = Read(bytes, compact);
		Assert.Equal(65536, parsed.Types.Length);
		Assert.Equal(1, parsed.ObjectCount);
		Assert.Empty(parsed.ScriptTypes.ToArray());
		Assert.Empty(parsed.Dependencies.ToArray());
		Assert.Empty(parsed.RefTypes.ToArray());
	}

	public enum ResourceCap { TypeCount, TypeTreeNodes, TypeTreeStrings }

	[Theory]
	[InlineData(ResourceCap.TypeCount, false)]
	[InlineData(ResourceCap.TypeCount, true)]
	[InlineData(ResourceCap.TypeTreeNodes, false)]
	[InlineData(ResourceCap.TypeTreeNodes, true)]
	[InlineData(ResourceCap.TypeTreeStrings, false)]
	[InlineData(ResourceCap.TypeTreeStrings, true)]
	public void CoherentMatureWriterMetadataRejectsOnlyTheExceededResourcePolicy(ResourceCap resource, bool compact)
	{
		byte[] bytes = Rewrite(file =>
		{
			TypeTreeType type = Assert.Single(file.Metadata.TypeTreeTypes);
			if (resource == ResourceCap.TypeCount)
			{
				file.Metadata.TypeTreeTypes = Enumerable.Repeat(type, 65537).ToList();
			}
			else if (resource == ResourceCap.TypeTreeNodes)
			{
				type.TypeBlob.Nodes = Enumerable.Repeat(type.TypeBlob.Nodes[0], 65537).ToList();
			}
			else
			{
				type.TypeBlob.Nodes = [];
				type.TypeBlob.StringBufferBytes = new byte[StringByteLimit + 1];
				Array.Fill(type.TypeBlob.StringBufferBytes, (byte)'x');
				type.TypeBlob.StringBufferBytes[^1] = 0;
			}
		}, typed: resource != ResourceCap.TypeCount);
		// The mature writer recalculates the frame formula, metadata size, physical file size,
		// and data offset together, so removing the policy cap cannot hide behind a range error.
		using (MemoryStream input = new(bytes, writable: false))
		using (AssetsFileReader reader = new(input))
		{
			AssetsFile independent = new();
			independent.Read(reader);
			Assert.Equal(bytes.LongLength, independent.Header.FileSize);
			Assert.True(48L + independent.Header.MetadataSize <= independent.Header.DataOffset);
			Assert.True(independent.Header.DataOffset <= independent.Header.FileSize);
			Assert.True(independent.Header.MetadataSize < 64 * 1024 * 1024);
			if (resource == ResourceCap.TypeCount) Assert.Equal(65537, independent.Metadata.TypeTreeTypes.Count);
			else if (resource == ResourceCap.TypeTreeNodes) Assert.Equal(65537, independent.Metadata.TypeTreeTypes[0].TypeBlob.Nodes.Count);
			else Assert.Equal(StringByteLimit + 1, independent.Metadata.TypeTreeTypes[0].TypeBlob.StringBufferBytes.Length);
		}
		AssertInvalidMetadata(bytes, compact);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void MinimalExternalReferenceDescriptorsFitTheirFortySixByteWireLayout(bool compact)
	{
		byte[] bytes = Rewrite(file =>
		{
			file.Metadata.RefTypes = Enumerable.Range(0, 2).Select(static _ => new TypeTreeType
			{
				TypeId = 28, IsRefType = true, ScriptTypeIndex = ushort.MaxValue,
				ScriptIdHash = Hash128.NewBlankHash(), TypeHash = Hash128.NewBlankHash(),
				ExtTypeHash = Hash128.NewBlankHash(), TypeBlobIsDefinition = false,
				TypeBlob = new TypeTreeBlob { Nodes = [], StringBufferBytes = [] },
				TypeReference = new AssetTypeReference(string.Empty, string.Empty, string.Empty),
			}).ToList();
		}, typed: true);
		using SerializedFile parsed = Read(bytes, compact);
		Assert.Equal(2, parsed.RefTypes.Length);
		foreach (var reference in parsed.RefTypes)
		{
			Assert.False(reference.IsTypeTreeDefinition);
			Assert.Empty(reference.OldType.Nodes);
			Assert.Empty(reference.ClassName.ToString());
			Assert.Empty(reference.Namespace.ToString());
			Assert.Empty(reference.AsmName.ToString());
		}
	}

	public static TheoryData<StringField, bool> MetadataStrings
	{
		get
		{
			TheoryData<StringField, bool> cases = new();
			foreach (StringField metadataField in Enum.GetValues<StringField>())
			{
				cases.Add(metadataField, false);
				cases.Add(metadataField, true);
			}
			return cases;
		}
	}

	[Theory]
	[MemberData(nameof(MetadataStrings))]
	public void EveryMetadataStringRejectsContentBeyondTheWireByteLimit(StringField field, bool compact)
	{
		byte[] bytes = StringFixture(field, new string('x', StringByteLimit));
		AssertInvalidMetadata(bytes, compact);
	}

	[Theory]
	[MemberData(nameof(MetadataStrings))]
	public void EveryMetadataStringRequiresItsTerminatorInsideMetadata(StringField field, bool compact)
	{
		string text = field == StringField.Signature ? "unterminated_version_marker" : "guard_string_marker";
		byte[] bytes = StringFixture(field, text);
		int offset = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text));
		Assert.True(offset >= 48);
		int end = field == StringField.Signature ? offset + 13 : offset + Encoding.UTF8.GetByteCount(text);
		BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), end - 48);
		AssertInvalidMetadata(bytes, compact);
	}

	[Theory]
	[MemberData(nameof(MetadataStrings))]
	public void BoundedMetadataStringsKeepTheirUtf8Values(StringField field, bool compact)
	{
		string text = field == StringField.Signature ? "6000.5.3f1" : "данные-終";
		using SerializedFile parsed = Read(StringFixture(field, text), compact);
		string actual = field switch
		{
			StringField.Signature => parsed.Version.ToString(),
			StringField.UserInformation => parsed.UserInformation.ToString(),
			StringField.ExternalVirtualPath => parsed.Dependencies[0].AssetPath.ToString(),
			StringField.ExternalPath => parsed.Dependencies[0].PathNameOrigin,
			StringField.RefClass => parsed.RefTypes[0].ClassName.ToString(),
			StringField.RefNamespace => parsed.RefTypes[0].Namespace.ToString(),
			_ => parsed.RefTypes[0].AsmName.ToString(),
		};
		Assert.Equal(text, actual);
		Assert.Equal(1, parsed.ObjectCount);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void StringWithTerminatorAtTheWireLimitRemainsReadable(bool compact)
	{
		string text = new('x', StringByteLimit - 1);
		using SerializedFile parsed = Read(StringFixture(StringField.UserInformation, text), compact);
		Assert.Equal(text, parsed.UserInformation.ToString());
	}

	private static (byte[] Bytes, int Offset, int Stride) CountFixture(CountField field)
	{
		byte[] bytes = field == CountField.TypeDependencies
			? UnityFixtureBuilder.SerializedGeneration23TypedTexture("metadata_texture", Pixels)
			: Rewrite(static _ => { });
		if (field == CountField.TypeDependencies)
		{
			int magic = bytes.AsSpan().IndexOf("mhtt"u8);
			Assert.True(magic > 0);
			return (bytes, magic + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(magic - 4, 4)), 4);
		}
		int types = Array.IndexOf(bytes, (byte)0, 48) + 6;
		Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(types, 4)));
		int objects = types + 4 + 23;
		Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(objects, 4)));
		int scripts = ((objects + 4 + 3) & ~3) + 24;
		int externals = scripts + 4;
		int references = externals + 4;
		Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(scripts, 4)));
		Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(externals, 4)));
		Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(references, 4)));
		return field switch
		{
			CountField.Types => (bytes, types, 23),
			CountField.Objects => (bytes, objects, 24),
			CountField.ScriptTypes => (bytes, scripts, 12),
			CountField.Externals => (bytes, externals, 22),
			_ => (bytes, references, 23),
		};
	}

	private static byte[] StringFixture(StringField field, string text)
	{
		bool referenced = field is StringField.RefClass or StringField.RefNamespace or StringField.RefAssembly;
		return Rewrite(file =>
		{
			switch (field)
			{
				case StringField.Signature: file.Metadata.UnityVersion = text; break;
				case StringField.UserInformation: file.Metadata.UserInformation = text; break;
				case StringField.ExternalVirtualPath:
				case StringField.ExternalPath:
					file.Metadata.Externals = [new AssetsFileExternal
					{
						VirtualAssetPathName = field == StringField.ExternalVirtualPath ? text : "virtual",
						PathName = field == StringField.ExternalPath ? text : "external.assets",
					}];
					break;
				default:
					file.Metadata.RefTypes = [new TypeTreeType
					{
						TypeId = 114, IsRefType = true, ScriptTypeIndex = ushort.MaxValue,
						ScriptIdHash = Hash128.NewBlankHash(), TypeHash = Hash128.NewBlankHash(),
						ExtTypeHash = Hash128.NewBlankHash(), TypeBlobIsDefinition = true,
						TypeBlob = new TypeTreeBlob { Nodes = [], StringBufferBytes = [] },
						TypeReference = new AssetTypeReference(
							field == StringField.RefClass ? text : "Fixture",
							field == StringField.RefNamespace ? text : "Fixtures",
							field == StringField.RefAssembly ? text : "TestAssembly"),
					}];
					break;
			}
		}, referenced);
	}

	private static byte[] Rewrite(Action<AssetsFile> change, bool typed = false)
	{
		byte[] bytes = typed ? UnityFixtureBuilder.SerializedGeneration23TypedTexture("metadata_texture", Pixels)
			: UnityFixtureBuilder.SerializedGenerationTexture(22, "metadata_texture", Pixels);
		using MemoryStream input = new(bytes, writable: false);
		using AssetsFileReader reader = new(input);
		AssetsFile file = new();
		file.Read(reader);
		file.Header.Version = 23;
		change(file);
		using MemoryStream output = new();
		using (AssetsFileWriter writer = new(output)) file.Write(writer, 0);
		return output.ToArray();
	}

	private static SerializedFile Read(byte[] bytes, bool compact)
	{
		using SmartStream input = SmartStream.CreateMemory(bytes);
		return (SerializedFile)SchemeReader.ReadFile(input, "metadata.assets", "metadata.assets",
			compact ? FileReadMode.CompactMetadataOnly : FileReadMode.MetadataOnly);
	}

	private static void AssertInvalidMetadata(byte[] bytes, bool compact)
	{
		Assert.Throws<InvalidDataException>(() =>
		{
			using SerializedFile parsed = Read(bytes, compact);
		});
	}
}
