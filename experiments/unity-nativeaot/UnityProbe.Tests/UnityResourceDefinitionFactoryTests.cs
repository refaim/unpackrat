using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AssetRipper.Assets;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityResourceDefinitionFactoryTests
{
	private static readonly string[] Versions = ["3.5.0f1", "5.6.7f1", "2022.3.10f1"];
	private static readonly Type[] ResourceContracts =
	[
		typeof(AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D),
		typeof(AssetRipper.SourceGenerated.Classes.ClassID_49.ITextAsset),
		typeof(AssetRipper.SourceGenerated.Classes.ClassID_128.IFont),
		typeof(AssetRipper.SourceGenerated.Classes.ClassID_329.IVideoClip),
		typeof(AssetRipper.SourceGenerated.Classes.ClassID_152.IMovieTexture),
		typeof(AssetRipper.SourceGenerated.Classes.ClassID_83.IAudioClip),
		typeof(AssetRipper.SourceGenerated.Classes.ClassID_43.IMesh),
		typeof(AssetRipper.SourceGenerated.Classes.ClassID_213.ISprite),
		typeof(AssetRipper.SourceGenerated.Classes.ClassID_687078895.ISpriteAtlas),
	];

	public static IEnumerable<object[]> SupportedClassIds()
	{
		int[] classIds = [28, 49, 128, 329, 152, 83, 43, 213, 687078895, 89, 115, 954905827, 662584278, 1896753125, 1152215463, 48, 109, 142, 147];
		foreach (int classId in classIds)
		{
			foreach (string version in Versions)
			{
				yield return [classId, version];
			}
		}
	}

	[Theory]
	[MemberData(nameof(SupportedClassIds))]
	public void MatchesMatureFactoryForSupportedClass(int classId, string versionText)
	{
		UnityVersion version = UnityVersion.Parse(versionText);
		AssetInfo info = new(AssetCreator.CreateCollection(version), 1, classId);

		(Type? Type, Exception? Error) expected = Observe(() => AssetFactory.CreateSerialized(info, version));
		(Type? Type, Exception? Error) actual = Observe(() => UnityResourceDefinitionFactory.Create(info, version));

		Assert.Equal(expected.Type, actual.Type);
		Assert.Equal(expected.Error?.GetType(), actual.Error?.GetType());
		Assert.NotNull(expected.Type);
	}

	[Theory]
	[InlineData(999999)]
	[InlineData(123456)]
	public void UnsupportedClassReturnsNull(int classId)
	{
		UnityVersion version = UnityVersion.Parse("2022.3.10f1");
		AssetInfo info = new(AssetCreator.CreateCollection(version), 1, classId);

		Assert.Null(UnityResourceDefinitionFactory.Create(info, version));
	}

	[Fact]
	public void ResourceDerivedClassIdsInSourceGeneratedMatchSupportedSet()
	{
		int[] expected = [28, 49, 128, 329, 152, 83, 43, 213, 687078895, 89, 115, 954905827, 662584278, 1896753125, 1152215463, 48, 109];

		int[] actual = typeof(AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D)
			.Assembly
			.GetTypes()
			.Where(type => type.IsClass && !type.IsAbstract)
			.Where(type => ResourceContracts.Any(contract => contract.IsAssignableFrom(type)))
			.Select(ClassIdFromNamespace)
			.Distinct()
			.OrderBy(classId => classId)
			.ToArray();

		Assert.Equal(expected.OrderBy(classId => classId).ToArray(), actual);
	}

	private static (Type? Type, Exception? Error) Observe(Func<IUnityObjectBase?> create)
	{
		try
		{
			return (create()?.GetType(), null);
		}
		catch (Exception error)
		{
			return (null, error);
		}
	}

	private static int ClassIdFromNamespace(Type type)
	{
		const string prefix = "AssetRipper.SourceGenerated.Classes.ClassID_";
		string? namespaceName = type.Namespace;
		if (namespaceName is null || !namespaceName.StartsWith(prefix, StringComparison.Ordinal))
		{
			throw new InvalidOperationException($"Type '{type.FullName}' is not in a ClassID namespace.");
		}

		string idText = namespaceName[prefix.Length..];
		if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out int classId))
		{
			throw new InvalidOperationException($"Type '{type.FullName}' has an invalid ClassID namespace '{namespaceName}'.");
		}

		return classId;
	}
}
