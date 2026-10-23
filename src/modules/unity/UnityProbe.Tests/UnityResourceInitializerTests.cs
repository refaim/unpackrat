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
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using TextureFormat = AssetRipper.SourceGenerated.Enums.TextureFormat;
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

    [Theory]
    [InlineData("3.5.0f1", "2022.3.10f1", true)]
    [InlineData("3.5.0f1", "2022.3.10f1", false)]
    [InlineData("2022.3.10f1", "3.5.0f1", true)]
    [InlineData("2022.3.10f1", "3.5.0f1", false)]
    public void LoadResourceBundle_MigratesTextureVersionStateLikeFullStructure(
        string sourceVersionText,
        string targetVersionText,
        bool mipmapped)
    {
        const string name = "versioned-texture";
        const int width = 8;
        const int height = 4;
        UnityVersion sourceVersion = UnityVersion.Parse(sourceVersionText);
        UnityVersion targetVersion = UnityVersion.Parse(targetVersionText);
        bool sourceIsModern = sourceVersionText == "2022.3.10f1";
        bool targetIsModern = targetVersionText == "2022.3.10f1";
        int expectedMipCount = mipmapped ? 4 : 1;

        int expectedImageLength = 0;
        int mipWidth = width;
        int mipHeight = height;
        for (int level = 0; level < expectedMipCount; level++)
        {
            expectedImageLength = checked(expectedImageLength + (mipWidth * mipHeight * 4));
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        byte[] expectedImageData = Enumerable.Repeat((byte)0xA5, expectedImageLength).ToArray();
        string inputPath = Path.Combine(Temp.NewDirectory(), "fixture.assets");
        File.WriteAllBytes(
            inputPath,
            UnityFixtureBuilder.SerializedVersionedTexture(name, sourceVersion, mipmapped));

        CoreConfiguration Configuration(UnityVersion version)
        {
            return new CoreConfiguration
            {
                ImportSettings = new ImportSettings
                {
                    ScriptContentLevel = ScriptContentLevel.Level0,
                    TargetVersion = version,
                },
            };
        }

        void AssertTexture(ITexture2D texture, bool modern, UnityVersion expectedVersion)
        {
            Assert.Equal(name, ((INamed)texture).Name.String);
            Assert.Equal(width, texture.Width_C28);
            Assert.Equal(height, texture.Height_C28);
            Assert.Equal(1, texture.ImageCount_C28);
            Assert.Equal(TextureFormat.RGBA32, texture.Format_C28E);
            Assert.Equal(expectedVersion, texture.Collection.Version);
            Assert.Equal<byte>(expectedImageData, texture.ImageData_C28);

            if (modern)
            {
                Assert.True(texture.TextureSettings_C28.Has_WrapU());
                Assert.Equal(2, Convert.ToInt32(texture.TextureSettings_C28.WrapU));
                Assert.True(texture.Has_MipCount_C28());
                Assert.Equal(expectedMipCount, texture.MipCount_C28);
            }
            else
            {
                Assert.True(texture.TextureSettings_C28.Has_WrapMode());
                Assert.Equal(2, Convert.ToInt32(texture.TextureSettings_C28.WrapMode));
                Assert.True(texture.Has_MipMap_C28());
                Assert.Equal(mipmapped, texture.MipMap_C28);
            }

            if (texture.Has_CompleteImageSize_C28_UInt32())
            {
                Assert.Equal((uint)expectedImageData.Length, texture.CompleteImageSize_C28_UInt32);
            }
            if (texture.Has_CompleteImageSize_C28_Int32())
            {
                Assert.Equal(expectedImageData.Length, texture.CompleteImageSize_C28_Int32);
            }
        }

        Type sourceType;
        using (GameBundle sourceBundle = GameStructure.LoadResourceBundle(
            [inputPath],
            LocalFileSystem.Instance,
            Configuration(sourceVersion),
            new UnityResourceAssetFactory(),
            UnityResourceDefinitionFactory.Create))
        {
            ITexture2D sourceTexture = Assert.IsAssignableFrom<ITexture2D>(
                FindNamedAsset(sourceBundle, 28, name));
            sourceType = sourceTexture.GetType();
            AssertTexture(sourceTexture, sourceIsModern, sourceVersion);
        }

        Type fullType;
        using (GameStructure structure = GameStructure.Load(
            [inputPath],
            LocalFileSystem.Instance,
            Configuration(targetVersion)))
        {
            ITexture2D fullTexture = Assert.IsAssignableFrom<ITexture2D>(
                structure.FileCollection.FetchAssets().Single(asset =>
                    asset.ClassID == 28
                    && asset is INamed named
                    && named.Name.String == name));
            fullType = fullTexture.GetType();
            AssertTexture(fullTexture, targetIsModern, targetVersion);
        }

        using GameBundle resourceBundle = GameStructure.LoadResourceBundle(
            [inputPath],
            LocalFileSystem.Instance,
            Configuration(targetVersion),
            new UnityResourceAssetFactory(),
            UnityResourceDefinitionFactory.Create);
        ITexture2D resourceTexture = Assert.IsAssignableFrom<ITexture2D>(
            FindNamedAsset(resourceBundle, 28, name));
        AssertTexture(resourceTexture, targetIsModern, targetVersion);

        Assert.Equal(fullType, resourceTexture.GetType());
        Assert.NotEqual(sourceType, fullType);
        Assert.NotEqual(sourceType, resourceTexture.GetType());
    }

    private static IUnityObjectBase FindNamedAsset(GameBundle bundle, int classId, string name)
    {
        return bundle.FetchAssets().Single(asset =>
            asset.ClassID == classId
            && asset is INamed named
            && named.Name.String == name);
    }
}
