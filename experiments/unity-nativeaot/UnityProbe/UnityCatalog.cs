using System;
using System.Collections.Generic;

namespace Unpackrat.UnityProbe;

/// <summary>
/// The kind of catalogued resource a session can export.
/// </summary>
internal enum UnityEntryKind
{
	/// <summary>A Texture2D rendered to PNG on demand.</summary>
	Texture,

	/// <summary>A TextAsset whose raw bytes are copied on demand.</summary>
	Text,

	/// <summary>A Font whose embedded source font bytes are copied on demand.</summary>
	Font,

	/// <summary>A VideoClip whose raw media bytes are copied on demand.</summary>
	VideoClip,

	/// <summary>A legacy MovieTexture whose raw ogv bytes are copied on demand.</summary>
	MovieTexture,

	/// <summary>An AudioClip whose sound bytes are copied or decoded on demand.</summary>
	AudioClip = 5,

	/// <summary>A Mesh exported as binary glTF (GLB) on demand.</summary>
	Mesh = 6,

	/// <summary>A Sprite exported as a standalone PNG cropped from its texture on demand.</summary>
	Sprite = 7,
}

/// <summary>
/// One exportable entry in a session catalogue.
/// </summary>
/// <remarks>
/// <see cref="Name"/> is the asset's own label and is metadata only; it must never be used as an
/// output file-system path. <see cref="CollectionIndex"/> is the session's own stable identity for
/// the owning internal collection, so identical <see cref="PathId"/> values in different
/// collections stay distinct. <see cref="SourceSize"/> is the declared source payload length
/// captured at catalogue time without conversion, or zero when it is unknown.
/// <see cref="OriginalPath"/> is the original asset path recorded by AssetRipper's own
/// original-path processor, or the empty string when the asset has none; it is metadata only and a
/// caller must sanitise it before turning it into a file-system path.
/// <see cref="OutputExtension"/> is the immutable, dotless lower-case output extension fixed at
/// catalogue time and never changed afterwards.
/// </remarks>
internal sealed record UnityCatalogEntry(int Index, UnityEntryKind Kind, int CollectionIndex, long PathId, string Name, long SourceSize, string OriginalPath, string OutputExtension);

/// <summary>
/// The one narrowing rule for a declared, unsigned source payload length. A length is immutable
/// catalogue metadata, so it becomes the signed value handed to callers, or the unknown sentinel
/// zero when it cannot be represented as a non-negative <see cref="long"/>.
/// </summary>
internal static class DeclaredSourceSize
{
	internal static long Narrow(ulong declared) => declared > long.MaxValue ? 0 : (long)declared;
}

/// <summary>
/// Project-owned view of a loaded asset graph whose collections are exactly the *selected* set.
/// Dependency collections loaded only for reference resolution are deliberately absent.
/// </summary>
internal interface IUnityGraph : IDisposable
{
	IReadOnlyList<IUnityGraphCollection> Collections { get; }
}

/// <summary>Project-owned view of one selected internal collection.</summary>
internal interface IUnityGraphCollection
{
	/// <summary>Stable identity of the collection within the loaded graph.</summary>
	string Identity { get; }

	IReadOnlyList<IProbeAsset> Assets { get; }
}

/// <summary>
/// Thrown when a selected input is missing, unreadable, not a supported Unity file or bundle, or
/// has become corrupt while its assets were being read.
/// </summary>
internal sealed class UnityLoadException(string message, Exception? innerException = null)
	: Exception(message, innerException);
