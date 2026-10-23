namespace Unpackrat.UnityProbe;

/// <summary>
/// Static allocation bounds applied to untrusted asset metadata before any size-driven
/// allocation happens. A hostile archive can declare absurd texture dimensions or text
/// payloads; every one of them must be refused from metadata alone, never by attempting the
/// allocation and catching the failure.
/// </summary>
internal static class Budgets
{
	/// <summary>
	/// Largest decoded texture we will materialise, in bytes. 256 MiB is exactly the decoded
	/// size of a 4096x4096 texture at <see cref="TextureBytesPerPixel"/>, so real assets up to
	/// that size still export while larger or hostile dimensions are rejected first.
	/// </summary>
	public const long MaxTextureBytes = 256L * 1024 * 1024;

	/// <summary>
	/// Bytes per decoded pixel assumed for the texture bound. RGBAFloat/ARGBFloat, the widest
	/// common decoded pixel, is four 32-bit floats, sixteen bytes; the bound stays a safe
	/// over-estimate for narrower formats.
	/// </summary>
	public const int TextureBytesPerPixel = 16;

	/// <summary>
	/// Largest decoded pixel layer count allowed, derived from <see cref="MaxTextureBytes"/> so
	/// the decoded size check is a single comparison against that constant.
	/// </summary>
	private const long MaxTexturePixels = MaxTextureBytes / TextureBytesPerPixel;

	/// <summary>
	/// Largest raw text payload copied out of a text asset. Comfortably above real config and
	/// script text while keeping the copy bounded.
	/// </summary>
	public const int MaxTextBytes = 16 * 1024 * 1024;

	/// <summary>
	/// Largest raw texture source payload accepted before decoding, whether it arrives inline in
	/// <c>ImageData</c> or as a streamed resource. Comfortably above real compressed texture data
	/// while keeping the copy bounded; a hostile asset can declare an enormous streamed size that
	/// must be refused from its declared length alone, before the adapter copies it.
	/// </summary>
	public const long MaxTextureSourceBytes = 128L * 1024 * 1024;

	/// <summary>
	/// Largest raw VideoClip media payload copied out on request. Checked against the asset's own
	/// declared external-resource size before the content is resolved or allocated, so a hostile
	/// clip that declares an enormous streamed length is refused from metadata alone.
	/// </summary>
	public const long MaxVideoBytes = 512L * 1024 * 1024;

	/// <summary>
	/// Largest number of submeshes accepted for one mesh. Real meshes carry a handful; the bound
	/// only stops a hostile mesh whose submesh list would otherwise drive unbounded work.
	/// </summary>
	public const int MaxMeshSubMeshes = 4096;

	/// <summary>
	/// Largest declared vertex count accepted for one mesh, whether reported by the inline vertex
	/// data or by the compressed vertex vector. Eight million vertices at the widest decoded stride
	/// stays inside <see cref="MaxMeshSourceBytes"/> while still covering real meshes.
	/// </summary>
	public const long MaxMeshVertices = 8_000_000;

	/// <summary>
	/// Largest declared index buffer accepted for one mesh, in bytes. Checked from the inline index
	/// buffer length or the compressed triangle vector before any index array is materialised.
	/// </summary>
	public const long MaxMeshIndexBytes = 256L * 1024 * 1024;

	/// <summary>
	/// Largest declared inline source payload accepted for one mesh, in bytes: the mesh's own
	/// vertex-channel bytes plus index buffer. Refused from metadata before the GLB builder sizes
	/// any buffer.
	/// </summary>
	public const long MaxMeshSourceBytes = 512L * 1024 * 1024;

	/// <summary>
	/// Upper bound on the bytes one declared packed-item materialises, used to turn the summed
	/// declared channel counts into a safe decoded-byte estimate. The dominant path is the skin
	/// channel, which rents (and then retains a copy of) a <c>BoneWeight4[]</c> of the declared item
	/// count, so 80 bytes per declared item stays above every channel the pinned decoder decodes.
	/// </summary>
	public const long MeshDecodedBytesPerItem = 80;

	/// <summary>
	/// Largest aggregate decoded channel payload accepted for one mesh, in bytes, estimated from the
	/// declared counts of every packed channel before the GLB builder decompresses any of them.
	/// </summary>
	public const long MaxMeshDecodedBytes = 2_147_483_648;

	/// <summary>
	/// Largest raw AudioClip payload accepted before it is copied or fed to the decoder. Checked
	/// against the declared inline length or the declared streamed resource size before any body is
	/// read, so a hostile clip cannot drive an unbounded copy.
	/// </summary>
	public const long MaxAudioSourceBytes = 512L * 1024 * 1024;

	/// <summary>
	/// Largest decoded audio payload materialised from a single supported sample. A decoded PCM
	/// buffer is bounded by its own public sample metadata before decoding, and the rebuilt output
	/// is checked against this same ceiling.
	/// </summary>
	public const long MaxAudioDecodedBytes = 512L * 1024 * 1024;

	/// <summary>
	/// Worst-case bytes per decoded sample used to bound a WAV rebuild from metadata alone; a
	/// 32-bit PCM sample is the widest supported format.
	/// </summary>
	public const int AudioDecodedBytesPerSample = 4;

	/// <summary>
	/// Reports whether a mesh's declared submesh count, vertex count, decoded channel bytes, index
	/// bytes and source bytes are all within their explicit caps. Every value comes from metadata
	/// alone, so a hostile mesh is refused before anything is decompressed or allocated.
	/// </summary>
	public static bool IsWithinMeshBudget(long subMeshCount, long vertexCount, long decodedBytes, long indexBytes, long sourceBytes)
	{
		return subMeshCount <= MaxMeshSubMeshes
			&& vertexCount <= MaxMeshVertices
			&& decodedBytes <= MaxMeshDecodedBytes
			&& indexBytes <= MaxMeshIndexBytes
			&& sourceBytes <= MaxMeshSourceBytes;
	}

	/// <summary>
	/// Computes the worst-case decoded size (<paramref name="width"/> x <paramref name="height"/>
	/// x <paramref name="depth"/> x <see cref="TextureBytesPerPixel"/>) with wide arithmetic and
	/// reports whether it is non-empty and within <see cref="MaxTextureBytes"/>.
	/// </summary>
	/// <remarks>
	/// The checks are ordered so no intermediate product can overflow: the width/height product
	/// is compared against the cap before depth is folded in, and both products are compared
	/// before the per-pixel factor is applied. A <see langword="false"/> result means the texture
	/// must be refused without decoding.
	/// </remarks>
	public static bool TryGetTextureBytes(int width, int height, int depth, out long bytes)
	{
		bytes = 0;
		if (width <= 0 || height <= 0 || depth <= 0)
		{
			return false;
		}

		long planar = (long)width * height;
		if (planar > MaxTexturePixels)
		{
			return false;
		}

		long layered = planar * depth;
		if (layered > MaxTexturePixels)
		{
			return false;
		}

		bytes = layered * TextureBytesPerPixel;
		return true;
	}
}
