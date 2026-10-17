using System;
using System.Collections.Generic;

namespace Unpackrat.UnityProbe;

/// <summary>
/// The one place that decides an entry's output extension. Every value handed to callers is a
/// dotless lower-case ASCII a-z0-9 string of one to sixteen units, so a hostile asset name can
/// never smuggle a path separator, a drive letter, an upper-case or an over-long token into the
/// catalogue. The extension is fixed before extraction and never derived from an arbitrary name.
/// </summary>
internal static class OutputExtensions
{
	/// <summary>Extension of a Texture2D entry.</summary>
	internal const string TexturePng = "png";

	/// <summary>Extension of a TextAsset entry.</summary>
	internal const string TextBin = "bin";

	/// <summary>Extension of a legacy MovieTexture entry.</summary>
	internal const string MovieOgv = "ogv";

	/// <summary>Extension of a Mesh entry, whose export is binary glTF.</summary>
	internal const string MeshGlb = "glb";

	/// <summary>Extension of a Sprite entry, whose export is a standalone cropped PNG.</summary>
	internal const string SpritePng = "png";

	/// <summary>Extension of a raw FSB sound bank entry that is not decoded to a standard container.</summary>
	internal const string Fsb = "fsb";

	/// <summary>The extension used whenever a candidate cannot be normalised or is not recognised.</summary>
	internal const string FallbackBin = "bin";

	/// <summary>Largest accepted extension length, in units of the ABI alphabet.</summary>
	internal const int MaxLength = 16;

	/// <summary>
	/// The media container extensions a VideoClip may advertise. Any other value, including a
	/// perfectly alphabet-legal but non-media one such as a hostile "exe", falls back to
	/// <see cref="FallbackBin"/> rather than being surfaced as a real binary extension.
	/// </summary>
	private static readonly HashSet<string> VideoContainers = new(StringComparer.Ordinal)
	{
		"mp4", "m4v", "mov", "webm", "ogv", "ogm", "wmv", "avi", "mkv", "mpg", "mpeg", "ts", "flv", "3gp",
	};

	/// <summary>
	/// Normalises a candidate extension to the ABI alphabet and length. A <see langword="null"/>,
	/// empty, over-long or out-of-alphabet value becomes <see cref="FallbackBin"/>; a conforming
	/// value is returned unchanged.
	/// </summary>
	internal static string Normalise(string? candidate)
	{
		if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxLength)
		{
			return FallbackBin;
		}

		for (int i = 0; i < candidate.Length; i++)
		{
			char unit = candidate[i];
			if (!char.IsAsciiLetterLower(unit) && !char.IsAsciiDigit(unit))
			{
				return FallbackBin;
			}
		}

		return candidate;
	}

	/// <summary>
	/// The output extension for a VideoClip given the extension carried by its recorded original
	/// path. Only a recognised media container survives; everything else is
	/// <see cref="FallbackBin"/>.
	/// </summary>
	internal static string Video(string? fromPath)
	{
		string normalised = Normalise(fromPath);
		return VideoContainers.Contains(normalised) ? normalised : FallbackBin;
	}
}
