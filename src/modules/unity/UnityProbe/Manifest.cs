namespace Unpackrat.UnityProbe;

/// <summary>
/// One successfully exported resource. The on-disk name is synthetic (kind + path id),
/// never a name taken from the untrusted archive.
/// </summary>
internal sealed record ManifestResource(string Kind, long PathId, string File, int Bytes)
{
	public const string TextureKind = "texture";
	public const string TextKind = "text";
}

/// <summary>
/// Fixed identifiers of the deterministic export manifest written next to the resources.
/// </summary>
internal static class Manifest
{
	public const string Schema = "unpackrat-unity-probe/1";
	public const string FileName = "manifest.json";
}
