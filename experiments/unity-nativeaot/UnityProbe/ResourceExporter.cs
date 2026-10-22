using System;
using System.Collections.Generic;
using System.IO;

namespace Unpackrat.UnityProbe;

/// <summary>
/// Bounded, deterministic export policy: at most one texture and one text asset are written
/// beneath the requested directory, together with a manifest.
/// </summary>
/// <remarks>
/// Operates purely on <see cref="IProbeAsset"/> so the policy is independent of AssetRipper.
/// The number of examined assets is bounded by <see cref="DefaultScanLimit"/> so a hostile or
/// pathological archive cannot make the scan unbounded.
/// </remarks>
internal static class ResourceExporter
{
	public const int DefaultScanLimit = 200_000;
	private const string UnknownVersion = "unknown";

	/// <summary>
	/// Exports at most one texture and one text asset found in <paramref name="assets"/> into
	/// <paramref name="outputDirectory"/>. Returns the number of successfully exported resources.
	/// </summary>
	public static int ExportAssets(IEnumerable<IProbeAsset> assets, string outputDirectory, int scanLimit = DefaultScanLimit)
	{
		ArgumentNullException.ThrowIfNull(assets);
		ArgumentNullException.ThrowIfNull(outputDirectory);
		Directory.CreateDirectory(outputDirectory);

		bool textureDone = false;
		bool textDone = false;
		string version = UnknownVersion;
		int textures = 0;
		int texts = 0;
		List<ManifestResource> resources = new();

		int scanned = 0;
		foreach (IProbeAsset asset in assets)
		{
			if (++scanned > scanLimit || (textureDone && textDone))
			{
				break;
			}

			if (!textureDone && asset is IProbeTexture texture)
			{
				version = ResolveVersion(texture.Version, version);
				if (TryExportTexture(texture, outputDirectory, resources))
				{
					textureDone = true;
					textures++;
				}
			}
			else if (!textDone && asset is IProbeText text)
			{
				version = ResolveVersion(text.Version, version);
				if (TryExportText(text, outputDirectory, resources))
				{
					textDone = true;
					texts++;
				}
			}
		}

		ManifestWriter.Write(outputDirectory, version, textures, texts, resources);
		return resources.Count;
	}

	private static string ResolveVersion(string assetVersion, string current)
	{
		return current == UnknownVersion ? assetVersion : current;
	}

	private static bool TryExportTexture(IProbeTexture texture, string outputDirectory, List<ManifestResource> resources)
	{
		byte[]? png = texture.RenderPng();
		if (png is null)
		{
			return false;
		}

		string fileName = $"texture_{texture.PathId}.png";
		File.WriteAllBytes(Path.Combine(outputDirectory, fileName), png);
		resources.Add(new ManifestResource(ManifestResource.TextureKind, texture.PathId, fileName, png.Length));
		return true;
	}

	private static bool TryExportText(IProbeText text, string outputDirectory, List<ManifestResource> resources)
	{
		byte[]? data = text.ReadData();
		if (data is null)
		{
			return false;
		}

		string fileName = $"text_{text.PathId}.bin";
		File.WriteAllBytes(Path.Combine(outputDirectory, fileName), data);
		resources.Add(new ManifestResource(ManifestResource.TextKind, text.PathId, fileName, data.Length));
		return true;
	}
}
