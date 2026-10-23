namespace Unpackrat.UnityProbe;

/// <summary>
/// Project-owned, format-neutral view of one entry in the AssetRipper asset graph.
/// </summary>
/// <remarks>
/// Production logic depends only on these interfaces, never on AssetRipper types, so the
/// export policy stays testable with tiny fakes and the third-party surface stays confined
/// to <see cref="UnitySource"/>.
/// </remarks>
internal interface IProbeAsset
{
}

/// <summary>A texture that can be rendered to PNG bytes, or <see langword="null"/> when unsupported.</summary>
internal interface IProbeTexture : IProbeAsset
{
	/// <summary>Path id assigned by the asset collection. Used only to build a synthetic file name.</summary>
	long PathId { get; }

	/// <summary>Unity version reported by the owning collection.</summary>
	string Version { get; }

	/// <summary>Original asset label (m_Name). Metadata only, never used as an output path.</summary>
	string Name { get; }

	/// <summary>
	/// Original asset path recorded by AssetRipper's own original-path processor, or
	/// <see langword="null"/> when the asset has none. Metadata only: the export policy derives a
	/// safe virtual path from it and never passes it to the file system unsanitised.
	/// </summary>
	string? OriginalPath { get; }

	/// <summary>
	/// Declared source payload length of the texture, before any conversion: the inline image
	/// data length when present, otherwise the streamed resource size. It is resolved from
	/// metadata alone and never renders the texture. Zero means the length is unknown.
	/// </summary>
	long SourceSize { get; }

	byte[]? RenderPng();
}

/// <summary>
/// A text asset whose raw bytes can be read, or <see langword="null"/> when the payload is
/// missing or outside the export budget.
/// </summary>
internal interface IProbeText : IProbeAsset
{
	/// <summary>Path id assigned by the asset collection. Used only to build a synthetic file name.</summary>
	long PathId { get; }

	/// <summary>Unity version reported by the owning collection.</summary>
	string Version { get; }

	/// <summary>Original asset label (m_Name). Metadata only, never used as an output path.</summary>
	string Name { get; }

	/// <summary>
	/// Original asset path recorded by AssetRipper's own original-path processor, or
	/// <see langword="null"/> when the asset has none. Metadata only: the export policy derives a
	/// safe virtual path from it and never passes it to the file system unsanitised.
	/// </summary>
	string? OriginalPath { get; }

	/// <summary>
	/// Declared source payload length of the text asset, read from the script data span length
	/// without copying the bytes. Zero means the length is unknown.
	/// </summary>
	long SourceSize { get; }

	byte[]? ReadData();
}

/// <summary>
/// A catalogued resource that carries its own immutable metadata and materialises its raw source
/// bytes only when the caller names it in an export. It is the third-party-free contract the
/// additional resource kinds (Font, VideoClip, MovieTexture) are adapted to, so listing an entry
/// never reads or converts its payload and the output extension is fixed before any export.
/// </summary>
internal interface IProbeResource : IProbeAsset
{
	/// <summary>The catalogued kind of the resource.</summary>
	UnityEntryKind Kind { get; }

	/// <summary>Path id assigned by the asset collection. Used only to build a synthetic file name.</summary>
	long PathId { get; }

	/// <summary>Original asset label (m_Name). Metadata only, never used as an output path.</summary>
	string Name { get; }

	/// <summary>
	/// Original asset path recorded by AssetRipper's own original-path processor, or
	/// <see langword="null"/> when the asset has none. Metadata only.
	/// </summary>
	string? OriginalPath { get; }

	/// <summary>
	/// Declared source payload length resolved from metadata alone, or zero when it is unknown.
	/// Querying it never reads the payload body.
	/// </summary>
	long SourceSize { get; }

	/// <summary>
	/// The immutable output extension for this entry: a dotless lower-case ASCII a-z0-9 string of
	/// one to sixteen units, fixed before any export.
	/// </summary>
	string OutputExtension { get; }

	/// <summary>
	/// Materialises the raw source bytes, or <see langword="null"/> when the payload is missing,
	/// unresolvable or outside the export budget. Never called while the catalogue is built.
	/// </summary>
	byte[]? ExportData();
}
