using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.Processing.Configuration;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public class UnityResourceInitializerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadAndExport_PreservesBaselineOutput(bool texture)
    {
        byte[] inputBytes = texture
            ? UnityFixtureBuilder.SerializedInlineTexture("resource", [10, 20, 30, 255])
            : UnityFixtureBuilder.SerializedTextAsset("resource", "payload");
        string workDirectory = Temp.NewDirectory();
        string inputPath = Path.Combine(workDirectory, "fixture.assets");
        string controlDirectory = Path.Combine(workDirectory, "control");
        string candidateDirectory = Path.Combine(workDirectory, "candidate");
        File.WriteAllBytes(inputPath, inputBytes);
        Directory.CreateDirectory(controlDirectory);
        Directory.CreateDirectory(candidateDirectory);

        int expectedCount;
        using (GameStructure structure = GameStructure.Load(
            [inputPath],
            LocalFileSystem.Instance,
            new CoreConfiguration
            {
                ImportSettings = new ImportSettings
                {
                    ScriptContentLevel = ScriptContentLevel.Level0,
                },
            }))
        {
            expectedCount = ResourceExporter.ExportAssets(
                UnitySource.Wrap(structure.FileCollection.FetchAssets()),
                controlDirectory);
        }

        int actualCount = UnitySource.LoadAndExport([inputPath], candidateDirectory);
        Assert.True(expectedCount > 0);
        Assert.Equal(expectedCount, actualCount);

        string[] expectedFiles = Directory.GetFiles(controlDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(controlDirectory, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string[] actualFiles = Directory.GetFiles(candidateDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(candidateDirectory, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal<string>(expectedFiles, actualFiles);
        Assert.Contains(Manifest.FileName, actualFiles);

        foreach (string relativePath in expectedFiles)
        {
            byte[] expectedBytes = File.ReadAllBytes(Path.Combine(controlDirectory, relativePath));
            byte[] actualBytes = File.ReadAllBytes(Path.Combine(candidateDirectory, relativePath));
            Assert.Equal<byte>(expectedBytes, actualBytes);
        }

        using FileStream exclusiveInput = new(
            inputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal((long)inputBytes.Length, exclusiveInput.Length);
    }

    [Theory]
    [InlineData(48)]
    [InlineData(109)]
    public void LoadResourceBundle_AppliesExplicitTargetVersionToHistoricalTextResource(int classId)
    {
        const string name = "historical-text";
        const string payload = "historical shader payload";
        UnityVersion sourceVersion = UnityVersion.Parse("3.5.0f1");
        UnityVersion targetVersion = UnityVersion.Parse("5.0.0f1");
        string inputPath = Path.Combine(Temp.NewDirectory(), "fixture.assets");
        File.WriteAllBytes(
            inputPath,
            UnityFixtureBuilder.SerializedDefaultNamedClass(classId, name, sourceVersion, payload));

        Type sourceType;
        using (GameBundle sourceBundle = GameStructure.LoadResourceBundle(
            [inputPath],
            LocalFileSystem.Instance,
            new CoreConfiguration
            {
                ImportSettings = new ImportSettings
                {
                    ScriptContentLevel = ScriptContentLevel.Level0,
                    TargetVersion = sourceVersion,
                },
            },
            new UnityResourceAssetFactory(),
            UnityResourceDefinitionFactory.Create))
        {
            IUnityObjectBase sourceAsset = FindNamedAsset(sourceBundle, classId, name);
            Assert.IsAssignableFrom<ITextAsset>(sourceAsset);
            sourceType = sourceAsset.GetType();
        }

        List<(UnityVersion Version, IUnityObjectBase? Asset)> constructions = [];
        IUnityObjectBase? Construct(AssetInfo info, UnityVersion version)
        {
            IUnityObjectBase? created = UnityResourceDefinitionFactory.Create(info, version);
            constructions.Add((version, created));
            return created;
        }

        using GameBundle targetBundle = GameStructure.LoadResourceBundle(
            [inputPath],
            LocalFileSystem.Instance,
            new CoreConfiguration
            {
                ImportSettings = new ImportSettings
                {
                    ScriptContentLevel = ScriptContentLevel.Level0,
                    TargetVersion = targetVersion,
                },
            },
            new UnityResourceAssetFactory(),
            Construct);

        IUnityObjectBase targetAsset = FindNamedAsset(targetBundle, classId, name);
        ITextAsset targetText = Assert.IsAssignableFrom<ITextAsset>(targetAsset);
        (UnityVersion Version, IUnityObjectBase? Asset) targetConstruction = constructions
            .Where(call => call.Asset is ITextAsset text
                && text.ClassID == classId
                && text is INamed named
                && named.Name.String == name)
            .Single();

        Assert.Equal(targetVersion, targetConstruction.Version);
        Assert.Same(targetConstruction.Asset, targetAsset);
        Assert.NotEqual(sourceType, targetAsset.GetType());
        Assert.Equal(name, ((INamed)targetText).Name.String);
        Assert.Equal(
            new Utf8String(payload).Data.ToArray(),
            targetText.Script_C49.Data.ToArray());

        AssetCollection collection = targetBundle.FetchAssetCollections()
            .Single(assetCollection => assetCollection.Any(asset =>
                asset.ClassID == classId
                && asset is INamed named
                && named.Name.String == name));
        Assert.Equal(targetVersion, collection.Version);
    }

    private static IUnityObjectBase FindNamedAsset(GameBundle bundle, int classId, string name)
    {
        return bundle.FetchAssets().Single(asset =>
            asset.ClassID == classId
            && asset is INamed named
            && named.Name.String == name);
    }
}
