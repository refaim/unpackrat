using AssetRipper.Assets;
using AssetRipper.Assets.Metadata;
using AssetRipper.Primitives;
using AssemblyDefinitionAsset = AssetRipper.SourceGenerated.Classes.ClassID_1152215463.AssemblyDefinitionAsset;
using AssemblyDefinitionReferenceAsset = AssetRipper.SourceGenerated.Classes.ClassID_662584278.AssemblyDefinitionReferenceAsset;
using AudioClip = AssetRipper.SourceGenerated.Classes.ClassID_83.AudioClip;
using AssetBundle = AssetRipper.SourceGenerated.Classes.ClassID_142.AssetBundle;
using Cubemap = AssetRipper.SourceGenerated.Classes.ClassID_89.Cubemap;
using Font = AssetRipper.SourceGenerated.Classes.ClassID_128.Font;
using Mesh = AssetRipper.SourceGenerated.Classes.ClassID_43.Mesh;
using MonoScript = AssetRipper.SourceGenerated.Classes.ClassID_115.MonoScript;
using MovieTexture = AssetRipper.SourceGenerated.Classes.ClassID_152.MovieTexture;
using PackageManifest = AssetRipper.SourceGenerated.Classes.ClassID_1896753125.PackageManifest;
using ResourceManager = AssetRipper.SourceGenerated.Classes.ClassID_147.ResourceManager;
using RuleSetFileAsset = AssetRipper.SourceGenerated.Classes.ClassID_954905827.RuleSetFileAsset;
using Shader = AssetRipper.SourceGenerated.Classes.ClassID_48.Shader;
using ShaderInclude = AssetRipper.SourceGenerated.Classes.ClassID_109.ShaderInclude;
using Sprite = AssetRipper.SourceGenerated.Classes.ClassID_213.Sprite;
using SpriteAtlas = AssetRipper.SourceGenerated.Classes.ClassID_687078895.SpriteAtlas;
using TextAsset = AssetRipper.SourceGenerated.Classes.ClassID_49.TextAsset;
using Texture2D = AssetRipper.SourceGenerated.Classes.ClassID_28.Texture2D;
using VideoClip = AssetRipper.SourceGenerated.Classes.ClassID_329.VideoClip;

namespace Unpackrat.UnityProbe;

/// <summary>
/// Creates definitions only for the resource classes the probe exposes. The normal reader keeps
/// its own type-tree fallback for every other class, so an unsupported class returns null here.
/// </summary>
internal static class UnityResourceDefinitionFactory
{
	public static IUnityObjectBase? Create(AssetInfo info, UnityVersion version)
	{
		return info.ClassID switch
		{
			28 => Texture2D.Create(info, version),
			49 => TextAsset.Create(info, version),
			128 => Font.Create(info, version),
			329 => VideoClip.Create(info, version),
			152 => MovieTexture.Create(info, version),
			83 => AudioClip.Create(info, version),
			43 => Mesh.Create(info, version),
			213 => Sprite.Create(info, version),
			687078895 => SpriteAtlas.Create(info, version),
			89 => Cubemap.Create(info, version),
			115 => MonoScript.Create(info, version),
			954905827 => RuleSetFileAsset.Create(info),
			662584278 => AssemblyDefinitionReferenceAsset.Create(info),
			1896753125 => PackageManifest.Create(info),
			1152215463 => AssemblyDefinitionAsset.Create(info, version),
			48 => Shader.Create(info, version),
			109 => ShaderInclude.Create(info, version),
			142 => AssetBundle.Create(info, version),
			147 => ResourceManager.Create(info, version),
			_ => null,
		};
	}
}
