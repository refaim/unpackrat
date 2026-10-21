using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.Import.AssetCreation;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Streams.Smart;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_128;
using AssetRipper.SourceGenerated.Classes.ClassID_152;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_147;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Classes.ClassID_329;
using AssetRipper.SourceGenerated.Classes.ClassID_687078895;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.NativeEnums.Fmod;
using AssetRipper.SourceGenerated.Subclasses.ChannelInfo;
using AssetRipper.SourceGenerated.Subclasses.GUID;
using AssetRipper.SourceGenerated.Subclasses.SpriteAtlasData;
using AssetRipper.SourceGenerated.Subclasses.SpriteRenderData;
using AssetRipper.SourceGenerated.Subclasses.SpriteVertex;
using AssetRipper.SourceGenerated.Subclasses.StreamInfo;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using K4os.Compression.LZ4;
using AudioCompressionFormat = AssetRipper.SourceGenerated.Enums.AudioCompressionFormat;
using MeshTopology = AssetRipper.SourceGenerated.Enums.MeshTopology;
using TextureFormat = AssetRipper.SourceGenerated.Enums.TextureFormat;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// Test-only generator of tiny legal Unity fixtures. It writes the serialized-file and UnityFS
/// container formats through upstream writer types so the production reader can be exercised end to
/// end; production code never contains a format writer.
/// </summary>
internal static class UnityFixtureBuilder
{
	private const int TextAssetClassId = 49;
	private const int Texture2DClassId = 28;
	private const int FontClassId = 128;
	private const int AudioClipClassId = 83;
	private const int MovieTextureClassId = 152;
	private const int ResourceManagerClassId = 147;
	private const int VideoClipClassId = 329;
	private const int MeshClassId = 43;
	private const int SpriteClassId = 213;
	private const int SpriteAtlasClassId = 687078895;
	private const int UnityFsVersion = (int)BundleVersion.BF_2022_2;

	/// <summary>
	/// The triangle (0,0,0)-(1,0,0)-(0,1,0) with the index buffer 0,1,2, shared by the standalone
	/// mesh fixture and the mixed audio+mesh fixture so both describe the same real geometry.
	/// </summary>
	private static readonly MeshData TriangleMeshData = MeshData.Empty with
	{
		Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
		ProcessedIndexBuffer = [0u, 1u, 2u],
		SubMeshes =
		[
			new SubMeshData(0u, 0, 0, 3, 1, 3, MeshTopology.Triangles,
				new Bounds(new Vector3(0.5f, 0.5f, 0.0f), new Vector3(0.5f, 0.5f, 0.0f))),
		],
	};

	/// <summary>A serialized asset file holding one named TextAsset and optional declared dependencies.</summary>
    public static byte[] SerializedDefaultNamedClass(int classId, string name, UnityVersion? version = null, string? text = null)
	{
        UnityVersion assetVersion = version ?? UnityVersion.V_2022;
        ProcessedAssetCollection collection = AssetCreator.CreateCollection(assetVersion);
        var asset = AssetRipper.SourceGenerated.AssetFactory.CreateSerialized(new AssetRipper.Assets.Metadata.AssetInfo(collection, 1, classId), assetVersion)!;
		((AssetRipper.Assets.INamed)asset).Name = new Utf8String(name);
        if (text is not null)
        {
            ((ITextAsset)asset).Script_C49 = new Utf8String(text);
        }
		if (asset is ITexture2D texture)
		{
			texture.Width_C28 = 1; texture.Height_C28 = 1; texture.ImageCount_C28 = 1;
			texture.Format_C28E = TextureFormat.RGBA32; texture.ImageData_C28 = [10, 20, 30, 255];
		}
        SerializedFileBuilder builder = NewBuilder(assetVersion, classId);
		builder.Objects.Add(new ObjectInfo { FileID = 1, TypeID = classId, ClassID = unchecked((short)classId),
			SerializedTypeIndex = 0, Type = builder.Types[0], ObjectData = WriteAssetBytes(collection, asset) });
		return Serialize(builder.Build());
	}

    public static byte[] SerializedVersionedTexture(string name, UnityVersion version, bool mipmapped)
    {
        ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);
        ITexture2D texture = Texture2D.Create(
            new AssetRipper.Assets.Metadata.AssetInfo(collection, 1, Texture2DClassId),
            version);
        ((AssetRipper.Assets.INamed)texture).Name = new Utf8String(name);
        texture.Width_C28 = 8;
        texture.Height_C28 = 4;
        texture.ImageCount_C28 = 1;
        texture.Format_C28E = TextureFormat.RGBA32;

        int mipCount = mipmapped ? 4 : 1;
        int imageLength = 0;
        int mipWidth = 8;
        int mipHeight = 4;
        for (int level = 0; level < mipCount; level++)
        {
            imageLength = checked(imageLength + (mipWidth * mipHeight * 4));
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        byte[] imageData = new byte[imageLength];
        Array.Fill(imageData, (byte)0xA5);
        texture.ImageData_C28 = imageData;

        if (texture.Has_CompleteImageSize_C28_UInt32())
        {
            texture.CompleteImageSize_C28_UInt32 = (uint)imageData.Length;
        }
        else if (texture.Has_CompleteImageSize_C28_Int32())
        {
            texture.CompleteImageSize_C28_Int32 = imageData.Length;
        }

        if (texture.Has_MipCount_C28())
        {
            texture.MipCount_C28 = mipCount;
        }
        else if (texture.Has_MipMap_C28())
        {
            texture.MipMap_C28 = mipmapped;
        }
        else
        {
            throw new NotSupportedException(
                $"Texture2D version {version} exposes neither mip count nor mip map state.");
        }

        if (texture.TextureSettings_C28.Has_WrapU())
        {
            texture.TextureSettings_C28.WrapU = 2;
        }
        else if (texture.TextureSettings_C28.Has_WrapMode())
        {
            texture.TextureSettings_C28.WrapMode = 2;
        }
        else
        {
            throw new NotSupportedException(
                $"Texture2D version {version} exposes neither WrapU nor WrapMode.");
        }

        SerializedFileBuilder builder = NewBuilder(version, Texture2DClassId);
        builder.Objects.Add(new ObjectInfo
        {
            FileID = 1,
            TypeID = Texture2DClassId,
            ClassID = unchecked((short)Texture2DClassId),
            SerializedTypeIndex = 0,
            Type = builder.Types[0],
            ObjectData = WriteAssetBytes(collection, texture),
        });
        return Serialize(builder.Build());
    }

	public static byte[] SerializedRepeatedSprites(int count, bool malformedLast = false)
	{
		byte[] pixel = [10, 20, 30, 255];
		byte[] bytes = SerializedSprite("hero", pixel, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0);
		using SmartStream input = SmartStream.CreateMemory(bytes);
		using SerializedFile source = (SerializedFile)SchemeReader.ReadFile(input, "sprite.assets", "sprite.assets");
		SerializedFileBuilder builder = NewBuilder(source.Version, Texture2DClassId, SpriteClassId);
		builder.Objects.Add(new ObjectInfo { FileID = 1, TypeID = Texture2DClassId, ClassID = Texture2DClassId, SerializedTypeIndex = 0,
			Type = builder.Types[0], ObjectData = source.Objects[0].ObjectData });
		byte[] sprite = source.Objects[1].ObjectData;
		for (int i = 0; i < count; i++)
			builder.Objects.Add(new ObjectInfo { FileID = i + 2, TypeID = SpriteClassId, ClassID = SpriteClassId, SerializedTypeIndex = 1,
				Type = builder.Types[1], ObjectData = malformedLast && i == count - 1 ? new byte[] { 1 } : sprite });
		return Serialize(builder.Build());
	}

	// Modes: direct, atlas key7, missing atlas key8, malformed header, invalid direct file.
	public static (byte[] Selected, byte[] Atlas) SerializedSpriteLinkCases(params int[] modes)
	{
		byte[] pixel = [10, 20, 30, 255];
		(byte[] selected, byte[] atlas) = SerializedAtlasSprite("hero", pixel, 1, 1, pixel, 1, 1, 0, 0, 1, 1, 0, 0, 2, 7, 7);
		(byte[] missing, _) = SerializedAtlasSprite("hero", pixel, 1, 1, pixel, 1, 1, 0, 0, 1, 1, 0, 0, 2, 8, 7);
		byte[] direct = SerializedSprite("hero", pixel, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0);
		byte[] invalid = SerializedSprite("hero", pixel, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, textureFileOverride: -1);
		byte[] Body(byte[] bytes, int row)
		{
			using SmartStream input = SmartStream.CreateMemory(bytes);
			using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(input, "links.assets", "links.assets");
			return file.Objects[row].ObjectData;
		}
		byte[][] bodies = [Body(direct, 1), Body(selected, 1), Body(missing, 1), [1], Body(invalid, 1)];
		SerializedFileBuilder builder = NewBuilder(UnityVersion.Parse("2021.3.0f1"), Texture2DClassId, SpriteClassId);
		builder.Objects.Add(new ObjectInfo { FileID = 1, TypeID = Texture2DClassId, ClassID = Texture2DClassId, SerializedTypeIndex = 0,
			Type = builder.Types[0], ObjectData = Body(direct, 0) });
		for (int i = 0; i < modes.Length; i++) builder.Objects.Add(new ObjectInfo { FileID = i + 2, TypeID = SpriteClassId, ClassID = SpriteClassId,
			SerializedTypeIndex = 1, Type = builder.Types[1], ObjectData = bodies[modes[i]] });
		builder.Dependencies.Add(new FileIdentifier { Type = AssetType.Serialized, AssetPath = Utf8String.Empty,
			PathNameOrigin = AtlasDependencyName, PathName = SpecialFileNames.FixFileIdentifier(AtlasDependencyName) });
		return (Serialize(builder.Build()), atlas);
	}

	public static byte[] SerializedRepeatedTextAssets(int count)
	{
		SerializedFileBuilder builder = NewBuilder(TextAssetClassId);
		for (int i = 1; i <= count; i++)
			builder.Objects.Add(new ObjectInfo { FileID = i, TypeID = TextAssetClassId, ClassID = TextAssetClassId,
				SerializedTypeIndex = 0, Type = builder.Types[0], ObjectData = TextAssetData("notes", "payload") });
		return Serialize(builder.Build());
	}

	public static byte[] SerializedTextAsset(string name, string payload, long pathId = 1, params string[] dependencies)
	{
		SerializedFileBuilder builder = NewBuilder(TextAssetClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = pathId,
			TypeID = TextAssetClassId,
			ClassID = TextAssetClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = TextAssetData(name, payload),
		});
		foreach (string dependency in dependencies)
		{
			builder.Dependencies.Add(new FileIdentifier
			{
				Type = AssetType.Serialized,
				AssetPath = Utf8String.Empty,
				PathNameOrigin = dependency,
				PathName = SpecialFileNames.FixFileIdentifier(dependency),
			});
		}

		return Serialize(builder.Build());
	}

	/// <summary>
	/// A serialized asset file holding one named VideoClip whose media lives in a sibling streamed
	/// resource of <paramref name="sizeBytes"/> bytes, together with the clip's recorded original
	/// path. The object data is produced by the upstream writer for a real clip.
	/// </summary>
	public static byte[] SerializedVideoClip(string name, string originalPath, string resourceName, uint sizeBytes, long pathId = 1, params string[] dependencies)
	{
		UnityVersion version = UnityVersion.Parse("2021.3.0f1");
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);
		IVideoClip clip = AssetCreator.CreateVideoClip(collection);
		clip.Name = new Utf8String(name);
		clip.OriginalPath_R = originalPath;
		clip.ExternalResources.Source = new Utf8String(resourceName);
		clip.ExternalResources.Offset = 0;
		clip.ExternalResources.Size = sizeBytes;

		byte[] objectData = WriteAssetBytes(collection, clip);

		SerializedFileBuilder builder = NewBuilder(VideoClipClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = pathId,
			TypeID = VideoClipClassId,
			ClassID = VideoClipClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = objectData,
		});
		foreach (string dependency in dependencies)
		{
			builder.Dependencies.Add(new FileIdentifier
			{
				Type = AssetType.Serialized,
				AssetPath = Utf8String.Empty,
				PathNameOrigin = dependency,
				PathName = SpecialFileNames.FixFileIdentifier(dependency),
			});
		}

		return Serialize(builder.Build());
	}

	/// <summary>
	/// A serialized asset file holding one named legacy MovieTexture whose raw movie bytes are
	/// inline in the object data, produced by the upstream writer for a real movie texture.
	/// </summary>
	public static byte[] SerializedMovieTexture(string name, byte[] movieData, long pathId = 1, params string[] dependencies)
	{
		// A legacy version is chosen on purpose: MovieTexture's inline movie bytes only exist up to
		// about Unity 2018.3, which is exactly the legacy shape this fixture must reproduce.
		UnityVersion version = UnityVersion.Parse("2018.3.0f1");
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);
		IMovieTexture movie = AssetCreator.CreateMovieTexture(collection);
		movie.Name = new Utf8String(name);
		movie.MovieData_C152 = movieData;

		byte[] objectData = WriteAssetBytes(collection, movie);

		SerializedFileBuilder builder = NewBuilder(version, MovieTextureClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = pathId,
			TypeID = MovieTextureClassId,
			ClassID = MovieTextureClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = objectData,
		});
		foreach (string dependency in dependencies)
		{
			builder.Dependencies.Add(new FileIdentifier
			{
				Type = AssetType.Serialized,
				AssetPath = Utf8String.Empty,
				PathNameOrigin = dependency,
				PathName = SpecialFileNames.FixFileIdentifier(dependency),
			});
		}

		return Serialize(builder.Build());
	}

	/// <summary>
	/// A serialized asset file holding one Texture2D whose pixels live in a sibling streamed
	/// resource of <paramref name="sizeBytes"/> bytes. The object data is produced by the upstream
	/// writer for a real texture, never by a hand-written layout.
	/// </summary>
	public static byte[] SerializedStreamedTexture(string name, string resourceName, uint sizeBytes, long pathId = 1, params string[] dependencies)
		=> SerializedTexture(name, [], resourceName, sizeBytes, pathId, dependencies);

	/// <summary>
	/// A serialized asset file holding one Texture2D whose pixels are inline in the object data and
	/// which declares no streamed resource at all.
	/// </summary>
	public static byte[] SerializedInlineTexture(string name, byte[] imageData, long pathId = 1, params string[] dependencies)
		=> SerializedTexture(name, imageData, null, 0, pathId, dependencies);

	/// <summary>
	/// A serialized asset file holding one Texture2D that carries both inline image data and a
	/// declared streamed resource, so the inline-over-stream precedence can be exercised.
	/// </summary>
	public static byte[] SerializedTextureWithInlineAndStream(string name, byte[] imageData, string resourceName, uint sizeBytes, long pathId = 1, params string[] dependencies)
		=> SerializedTexture(name, imageData, resourceName, sizeBytes, pathId, dependencies);

	/// <summary>
	/// Writes one Texture2D object through the upstream writer. A non-null
	/// <paramref name="resourceName"/> attaches a declared streamed resource; otherwise the texture
	/// declares no streamed resource at all.
	/// </summary>
	private static byte[] SerializedTexture(string name, byte[] imageData, string? resourceName, uint sizeBytes, long pathId, string[] dependencies)
	{
		UnityVersion version = UnityVersion.Parse("2021.3.0f1");
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);
		ITexture2D texture = AssetCreator.CreateTexture2D(collection);
		texture.Width_C28 = 1;
		texture.Height_C28 = 1;
		texture.ImageCount_C28 = 1;
		texture.Format_C28E = TextureFormat.RGBA32;
		texture.ImageData_C28 = imageData;
		if (resourceName is not null)
		{
			texture.StreamData_C28!.Path = new Utf8String(resourceName);
			texture.StreamData_C28!.Size = sizeBytes;
		}

		texture.Name = new Utf8String(name);

		byte[] objectData;
		using (MemoryStream stream = new())
		{
			using (AssetWriter writer = new(stream, collection))
			{
				texture.WriteRelease(writer);
			}
			objectData = stream.ToArray();
		}

		SerializedFileBuilder builder = NewBuilder(Texture2DClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = pathId,
			TypeID = Texture2DClassId,
			ClassID = Texture2DClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = objectData,
		});
		foreach (string dependency in dependencies)
		{
			builder.Dependencies.Add(new FileIdentifier
			{
				Type = AssetType.Serialized,
				AssetPath = Utf8String.Empty,
				PathNameOrigin = dependency,
				PathName = SpecialFileNames.FixFileIdentifier(dependency),
			});
		}

		return Serialize(builder.Build());
	}

	/// <summary>
	/// A serialized asset file holding a ResourceManager whose container maps
	/// <paramref name="resourcePath"/> to a real pointer at a TextAsset in the same file, plus the
	/// TextAsset itself. Loading it makes AssetRipper's own original-path processor record a real
	/// "Assets/Resources/..." path for that asset through the resource-manager container.
	/// </summary>
	public static byte[] SerializedResourceManagerTextAsset(string resourcePath, string name, string payload, int? monoBehaviourClassId = null, long monoBehaviourPathId = 3, params string[] dependencies)
	{
		UnityVersion version = UnityVersion.Parse("2021.3.0f1");
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);

		ITextAsset text = AssetCreator.CreateTextAsset(collection);
		text.Name = new Utf8String(name);
		text.Script_C49 = new Utf8String(System.Text.Encoding.UTF8.GetBytes(payload));

		IResourceManager manager = AssetCreator.CreateResourceManager(collection);
		var pair = manager.Container.AddNew();
		pair.Key = new Utf8String(resourcePath);
		pair.Value.SetAsset(collection, text);

		byte[] textData = WriteAssetBytes(collection, text);
		byte[] managerData = WriteAssetBytes(collection, manager);

		SerializedFileBuilder builder = NewBuilder(TextAssetClassId, ResourceManagerClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = text.PathID,
			TypeID = TextAssetClassId,
			ClassID = TextAssetClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = textData,
		});
		builder.Objects.Add(new ObjectInfo
		{
			FileID = manager.PathID,
			TypeID = ResourceManagerClassId,
			ClassID = ResourceManagerClassId,
			SerializedTypeIndex = 1,
			Type = builder.Types[1],
			ObjectData = managerData,
		});
		if (monoBehaviourClassId is { } classId)
		{
			// The format writer supplies a real MonoBehaviour layout. Negative scripted class IDs
			// are normalised to 114 by the upstream collection reader before reaching our factory.
			var script = AssetRipper.SourceGenerated.Classes.ClassID_114.MonoBehaviour.Create(
				new AssetRipper.Assets.Metadata.AssetInfo(collection, monoBehaviourPathId, 114));
			WithTypes(builder, classId);
			builder.Objects.Add(new ObjectInfo
			{
				FileID = monoBehaviourPathId,
				TypeID = classId,
				ClassID = 114,
				SerializedTypeIndex = 2,
				Type = builder.Types[2],
				ObjectData = WriteAssetBytes(collection, script),
			});
		}
		foreach (string dependency in dependencies)
		{
			builder.Dependencies.Add(new FileIdentifier
			{
				Type = AssetType.Serialized,
				AssetPath = Utf8String.Empty,
				PathNameOrigin = dependency,
				PathName = SpecialFileNames.FixFileIdentifier(dependency),
			});
		}
		return Serialize(builder.Build());
	}

	/// <summary>
	/// A serialized asset file holding one named legacy AudioClip whose raw sound bytes are inline
	/// in the object data. A legacy version is used on purpose so the clip carries its original
	/// sound type, which makes the stored payload a raw media file rather than a modern FSB stream.
	/// The object data is produced by the upstream writer for a real clip.
	/// </summary>
	public static byte[] SerializedLegacyAudioClip(string name, byte[] audioData, long pathId = 1, params string[] dependencies)
	{
		UnityVersion version = UnityVersion.Parse("4.7.0f1");
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);
		IAudioClip clip = AssetCreator.CreateAudioClip(collection);
		clip.Name = new Utf8String(name);
		clip.AudioData = audioData;
		clip.Type = (int)FmodSoundType.Wav;

		// A legacy clip is only a valid AudioClip fixture when its chosen Unity version actually
		// serialises both the original sound type and the inline bytes; assert it on the created
		// asset before the writer runs so a version mismatch fails here rather than as a silently
		// empty payload.
		if (!clip.Has_AudioData() || !clip.Has_Type())
		{
			throw new InvalidOperationException("The chosen Unity version does not serialise inline audio data and sound type.");
		}

		byte[] objectData = WriteAssetBytes(collection, clip);

		SerializedFileBuilder builder = NewBuilder(version, AudioClipClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = pathId,
			TypeID = AudioClipClassId,
			ClassID = AudioClipClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = objectData,
		});
		foreach (string dependency in dependencies)
		{
			builder.Dependencies.Add(new FileIdentifier
			{
				Type = AssetType.Serialized,
				AssetPath = Utf8String.Empty,
				PathNameOrigin = dependency,
				PathName = SpecialFileNames.FixFileIdentifier(dependency),
			});
		}

		return Serialize(builder.Build());
	}

	/// <summary>
	/// A serialized asset file holding one named modern AudioClip whose sound bank lives in a
	/// sibling streamed resource of <paramref name="sizeBytes"/> bytes. The object data is produced
	/// by the upstream writer for a real modern clip, which carries a streamed resource and a
	/// compression format rather than the removed legacy inline bytes and sound type.
	/// </summary>
	public static byte[] SerializedStreamedFsbAudioClip(
		string name,
		string resourceName,
		uint sizeBytes,
		ulong offset = 0,
		int compressionFormat = -1,
		long pathId = 1,
		params string[] dependencies)
	{
		UnityVersion version = UnityVersion.Parse("2021.3.0f1");
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);
		IAudioClip clip = AssetCreator.CreateAudioClip(collection);
		clip.Name = new Utf8String(name);
		clip.Resource!.Source = new Utf8String(resourceName);
		clip.Resource!.Offset = offset;
		clip.Resource!.Size = sizeBytes;
		if (compressionFormat >= 0)
		{
			clip.CompressionFormat = compressionFormat;
		}

		if (!clip.Has_Resource())
		{
			throw new InvalidOperationException("The chosen Unity version does not serialise a streamed audio resource.");
		}

		byte[] objectData = WriteAssetBytes(collection, clip);

		SerializedFileBuilder builder = NewBuilder(version, AudioClipClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = pathId,
			TypeID = AudioClipClassId,
			ClassID = AudioClipClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = objectData,
		});
		foreach (string dependency in dependencies)
		{
			builder.Dependencies.Add(new FileIdentifier
			{
				Type = AssetType.Serialized,
				AssetPath = Utf8String.Empty,
				PathNameOrigin = dependency,
				PathName = SpecialFileNames.FixFileIdentifier(dependency),
			});
		}

		return Serialize(builder.Build());
	}

	/// <summary>
	/// Test-only knobs for a sprite's tight mesh, so malformed shapes can be authored without a
	/// second fixture builder.
	/// </summary>
	public sealed record TightMeshOptions(float[] Points)
	{
		/// <summary>Whether the modern index buffer is written (default) or left empty.</summary>
		public bool IncludeIndexBuffer { get; init; } = true;

		/// <summary>Whether an odd-length (malformed) index buffer is written instead.</summary>
		public bool OddIndexBuffer { get; init; }

		/// <summary>Explicit index values; when null, the identity 0..n-1 list is written.</summary>
		public ushort[]? Indices { get; init; }

		/// <summary>Overrides the submesh's declared index count.</summary>
		public uint? IndexCount { get; init; }

		/// <summary>Overrides the submesh's declared first-byte offset.</summary>
		public uint? FirstByte { get; init; }

		/// <summary>Whether the submesh is written at all.</summary>
		public bool IncludeSubMesh { get; init; } = true;

		/// <summary>The submesh topology.</summary>
		public MeshTopology Topology { get; init; } = MeshTopology.Triangles;

		/// <summary>
		/// Overrides the declared vertex count independently of the written vertex data, so a
		/// metadata-only mismatch (a huge declared count over a tiny body) can be authored.
		/// </summary>
		public uint? DeclaredVertexCount { get; init; }

		/// <summary>Overrides the single channel's byte offset inside its stream.</summary>
		public byte? ChannelOffset { get; init; }

		/// <summary>Overrides the first channel's declared dimension.</summary>
		public byte? ChannelDimension { get; init; }

		/// <summary>Overrides the first channel's declared format.</summary>
		public byte? ChannelFormat { get; init; }

		/// <summary>Overrides the first channel's stream index.</summary>
		public byte? ChannelStream { get; init; }

		/// <summary>Writes this many extra channels identical to the first one.</summary>
		public int DuplicateChannelCount { get; init; }

		/// <summary>Appends one extra channel with a zero dimension, which no decoder decodes.</summary>
		public bool AddUnusedChannel { get; init; }
	}

	/// <summary>
	/// A serialized asset file holding one Texture2D and one Sprite whose render data points at
	/// that texture. The texture pixels are inline RGBA32 and the sprite's rect, texture rect,
	/// texture-rect offset, pivot and settings are written through the upstream writer, exactly
	/// like a real serialized sprite. <paramref name="settingsRaw"/> defaults to the plain
	/// rectangle setting (bit 1 set, packed bit clear).
	/// </summary>
	public static byte[] SerializedSprite(
		string name,
		byte[] textureData,
		int textureWidth,
		int textureHeight,
		float textureRectX,
		float textureRectY,
		float textureRectWidth,
		float textureRectHeight,
		float spriteRectX,
		float spriteRectY,
		float spriteRectWidth,
		float spriteRectHeight,
		float offsetX,
		float offsetY,
		uint settingsRaw = 2u,
		float pixelsToUnits = 100f,
		TightMeshOptions? tightMesh = null,
		float downscale = 0f,
		bool linksTexture = true,
		long texturePathId = 1,
		long spritePathId = 2,
		string? layoutVersion = null,
		string? headerVersion = null,
		bool externalTexture = false,
		int? textureFileOverride = null,
		params string[] dependencies)
	{
		UnityVersion version = UnityVersion.Parse(layoutVersion ?? "2021.3.0f1");
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);

		ITexture2D texture = CreateTexture(collection, name + "_texture", textureData, textureWidth, textureHeight);

		ISprite sprite = AssetCreator.CreateSprite(collection);
		sprite.Name = new Utf8String(name);
		sprite.Rect.X = spriteRectX;
		sprite.Rect.Y = spriteRectY;
		sprite.Rect.Width = spriteRectWidth;
		sprite.Rect.Height = spriteRectHeight;
		if (sprite.Has_Pivot()) { sprite.Pivot.X = 0.5f; sprite.Pivot.Y = 0.5f; }
		sprite.PixelsToUnits = pixelsToUnits;
		ISpriteRenderData renderData = sprite.RD;
		if (linksTexture)
		{
			renderData.Texture.SetAsset(collection, externalTexture
				? CreateTexture(AssetCreator.CreateCollection(version), name + "_external", textureData, textureWidth, textureHeight) : texture);
		}

		if (textureFileOverride is { } file)
		{
			Span<byte> pointerBytes = stackalloc byte[12];
			BinaryPrimitives.WriteInt32LittleEndian(pointerBytes, file);
			BinaryPrimitives.WriteInt64LittleEndian(pointerBytes[4..], renderData.Texture.PathID);
			EndianSpanReader reader = new(pointerBytes, EndianType.LittleEndian);
			renderData.Texture.ReadRelease(ref reader);
		}

		renderData.DownscaleMultiplier = downscale;
		renderData.TextureRect.X = textureRectX;
		renderData.TextureRect.Y = textureRectY;
		renderData.TextureRect.Width = textureRectWidth;
		renderData.TextureRect.Height = textureRectHeight;
		renderData.TextureRectOffset.X = offsetX;
		renderData.TextureRectOffset.Y = offsetY;
		renderData.SettingsRaw = settingsRaw;
		if (layoutVersion is not null) { renderData.UvTransform.Z = 1f; renderData.UvTransform.W = 1f; }
		if (tightMesh is not null)
		{
			SetTightMesh(renderData, tightMesh);
		}

		byte[] textureObjectData = WriteAssetBytes(collection, texture);
		byte[] spriteObjectData = WriteAssetBytes(collection, sprite);

		SerializedFileBuilder builder = NewBuilder(UnityVersion.Parse(headerVersion ?? version.ToString()), Texture2DClassId, SpriteClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = texturePathId,
			TypeID = Texture2DClassId,
			ClassID = Texture2DClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = textureObjectData,
		});
		builder.Objects.Add(new ObjectInfo
		{
			FileID = spritePathId,
			TypeID = SpriteClassId,
			ClassID = SpriteClassId,
			SerializedTypeIndex = 1,
			Type = builder.Types[1],
			ObjectData = spriteObjectData,
		});
		foreach (string dependency in dependencies)
		{
			builder.Dependencies.Add(new FileIdentifier
			{
				Type = AssetType.Serialized,
				AssetPath = Utf8String.Empty,
				PathNameOrigin = dependency,
				PathName = SpecialFileNames.FixFileIdentifier(dependency),
			});
		}

		return Serialize(builder.Build());
	}

	/// <summary>
	/// A serialized asset file holding only a Sprite whose render data names a deliberately
	/// different local texture and rect, while the sprite's render-data key selects the real
	/// region from a SpriteAtlas that lives in a *separate* dependency file. The returned pair is
	/// the selected sprite file and the dependency atlas file the caller writes beside it; the
	/// atlas's texture and the atlas itself are dependency-owned, so they must never enter the
	/// selected catalogue.
	/// </summary>
	public static (byte[] SpriteFile, byte[] AtlasFile) SerializedAtlasSprite(
		string name,
		byte[] localPixels,
		int localWidth,
		int localHeight,
		byte[] atlasPixels,
		int atlasWidth,
		int atlasHeight,
		float atlasRectX,
		float atlasRectY,
		float atlasRectWidth,
		float atlasRectHeight,
		float offsetX,
		float offsetY,
		uint settingsRaw,
		long spriteKeyValue,
		long atlasKeyValue,
		bool linkAtlasTexture = true, int leadingAtlasEntries = 0, int atlasTextureFileId = 0, bool missingTextureDependency = false, long? referencedAtlasPath = null)
	{
		UnityVersion version = UnityVersion.Parse("2021.3.0f1");
		ProcessedAssetCollection atlasCollection = AssetCreator.CreateCollection(version);
		ITexture2D atlasTexture = CreateTexture(atlasCollection, "atlas_texture", atlasPixels, atlasWidth, atlasHeight);
		ISpriteAtlas atlas = AssetCreator.CreateSpriteAtlas(atlasCollection);
		atlas.Name = new Utf8String("hero_atlas");

		for (int i = 0; i < leadingAtlasEntries; i++)
		{
			var unusedEntry = atlas.RenderDataMap.AddNew();
			SetRenderDataKey(unusedEntry.Key, -i - 1);
		}

		AccessPairBase<AssetPair<GUID, long>, ISpriteAtlasData> entry = atlas.RenderDataMap.AddNew();
		SetRenderDataKey(entry.Key, atlasKeyValue);
		if (linkAtlasTexture)
		{
			entry.Value.Texture.SetAsset(atlasCollection, atlasTexture);
			entry.Value.Texture.GetType().GetField("m_FileID_", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(entry.Value.Texture, atlasTextureFileId);
		}
		entry.Value.TextureRect.X = atlasRectX;
		entry.Value.TextureRect.Y = atlasRectY;
		entry.Value.TextureRect.Width = atlasRectWidth;
		entry.Value.TextureRect.Height = atlasRectHeight;
		entry.Value.TextureRectOffset.X = offsetX;
		entry.Value.TextureRectOffset.Y = offsetY;
		entry.Value.SettingsRaw = settingsRaw;

		byte[] atlasObjectData = WriteAssetBytes(atlasCollection, atlas);
		byte[] atlasTextureObjectData = WriteAssetBytes(atlasCollection, atlasTexture);

		ProcessedAssetCollection spriteCollection = AssetCreator.CreateCollection(version);
		ITexture2D localTexture = CreateTexture(spriteCollection, "local_texture", localPixels, localWidth, localHeight);
		ISprite sprite = AssetCreator.CreateSprite(spriteCollection);
		sprite.Name = new Utf8String(name);
		sprite.Rect.X = 0f;
		sprite.Rect.Y = 0f;
		sprite.Rect.Width = atlasRectWidth;
		sprite.Rect.Height = atlasRectHeight;
		sprite.Pivot.X = 0.5f;
		sprite.Pivot.Y = 0.5f;
		sprite.PixelsToUnits = 100f;
		ISpriteRenderData renderData = sprite.RD;
		renderData.Texture.SetAsset(spriteCollection, localTexture);
		renderData.TextureRect.X = 0f;
		renderData.TextureRect.Y = 0f;
		renderData.TextureRect.Width = localWidth;
		renderData.TextureRect.Height = localHeight;
		renderData.TextureRectOffset.X = 0f;
		renderData.TextureRectOffset.Y = 0f;
		renderData.SettingsRaw = 2u;
		SetRenderDataKey(sprite.RenderDataKey, spriteKeyValue);
		sprite.SpriteAtlas.SetAsset(spriteCollection, atlas);
		if (referencedAtlasPath is { } atlasPath) sprite.SpriteAtlas.GetType().GetField("m_PathID_", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(sprite.SpriteAtlas, atlasPath);

		byte[] localTextureObjectData = WriteAssetBytes(spriteCollection, localTexture);
		byte[] spriteObjectData = WriteAssetBytes(spriteCollection, sprite);

		SerializedFileBuilder atlasBuilder = NewBuilder(version, Texture2DClassId, SpriteAtlasClassId);
		atlasBuilder.Objects.Add(new ObjectInfo
		{
			FileID = atlasTexture.PathID,
			TypeID = Texture2DClassId,
			ClassID = Texture2DClassId,
			SerializedTypeIndex = 0,
			Type = atlasBuilder.Types[0],
			ObjectData = atlasTextureObjectData,
		});
		atlasBuilder.Objects.Add(new ObjectInfo
		{
			FileID = atlas.PathID,
			TypeID = SpriteAtlasClassId,
			SerializedTypeIndex = 1,
			Type = atlasBuilder.Types[1],
			ObjectData = atlasObjectData,
		});

		if (missingTextureDependency) atlasBuilder.Dependencies.Add(new FileIdentifier { Type = AssetType.Serialized, AssetPath = Utf8String.Empty, PathNameOrigin = "missing-texture.assets", PathName = "missing-texture.assets" });

		SerializedFileBuilder spriteBuilder = NewBuilder(version, Texture2DClassId, SpriteClassId);
		spriteBuilder.Objects.Add(new ObjectInfo
		{
			FileID = localTexture.PathID,
			TypeID = Texture2DClassId,
			ClassID = Texture2DClassId,
			SerializedTypeIndex = 0,
			Type = spriteBuilder.Types[0],
			ObjectData = localTextureObjectData,
		});
		spriteBuilder.Objects.Add(new ObjectInfo
		{
			FileID = sprite.PathID,
			TypeID = SpriteClassId,
			ClassID = SpriteClassId,
			SerializedTypeIndex = 1,
			Type = spriteBuilder.Types[1],
			ObjectData = spriteObjectData,
		});
		spriteBuilder.Dependencies.Add(new FileIdentifier
		{
			Type = AssetType.Serialized,
			AssetPath = Utf8String.Empty,
			PathNameOrigin = AtlasDependencyName,
			PathName = SpecialFileNames.FixFileIdentifier(AtlasDependencyName),
		});

		return (Serialize(spriteBuilder.Build()), Serialize(atlasBuilder.Build()));
	}

	/// <summary>The sibling file name a sprite file declares as its atlas dependency.</summary>
	public const string AtlasDependencyName = "hero_atlas.assets";

	/// <summary>
	/// A legacy (4.3) serialized asset file holding one Texture2D and one Sprite whose render data
	/// uses the older per-vertex position and index lists instead of the modern vertex-data blob.
	/// </summary>
	public static byte[] SerializedLegacySprite(
		string name,
		byte[] textureData,
		int textureWidth,
		int textureHeight,
		float textureRectX,
		float textureRectY,
		float textureRectWidth,
		float textureRectHeight,
		float spriteRectX,
		float spriteRectY,
		float spriteRectWidth,
		float spriteRectHeight,
		float offsetX,
		float offsetY,
		uint settingsRaw = 0u,
		float pixelsToUnits = 1f,
		float[]? legacyVertices = null,
		bool withIndices = true,
		bool linkTexture = true,
		float spriteOffsetX = 0f,
		float spriteOffsetY = 0f)
	{
		UnityVersion version = UnityVersion.Parse("4.3.0f1");
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);
		ITexture2D texture = CreateTexture(collection, name + "_texture", textureData, textureWidth, textureHeight);

		ISprite sprite = AssetCreator.CreateSprite(collection);
		sprite.Name = new Utf8String(name);
		sprite.Rect.X = spriteRectX;
		sprite.Rect.Y = spriteRectY;
		sprite.Rect.Width = spriteRectWidth;
		sprite.Rect.Height = spriteRectHeight;
		sprite.PixelsToUnits = pixelsToUnits;
		// The sprite's own offset from its rect centre: distinct from the render data's
		// texture-rect offset, and the value the pre-5.6 pivot is derived from.
		sprite.Offset.X = spriteOffsetX;
		sprite.Offset.Y = spriteOffsetY;
		ISpriteRenderData renderData = sprite.RD;
		if (linkTexture)
		{
			renderData.Texture.SetAsset(collection, texture);
		}

		renderData.TextureRect.X = textureRectX;
		renderData.TextureRect.Y = textureRectY;
		renderData.TextureRect.Width = textureRectWidth;
		renderData.TextureRect.Height = textureRectHeight;
		renderData.TextureRectOffset.X = offsetX;
		renderData.TextureRectOffset.Y = offsetY;
		renderData.SettingsRaw = settingsRaw;
		if (legacyVertices is not null)
		{
			for (int i = 0; i < legacyVertices.Length / 2; i++)
			{
				ISpriteVertex vertex = renderData.Vertices.AddNew();
				vertex.Pos.X = legacyVertices[i * 2];
				vertex.Pos.Y = legacyVertices[(i * 2) + 1];
				vertex.Pos.Z = 0f;
				if (withIndices)
				{
					renderData.Indices.Add((ushort)i);
				}
			}
		}

		byte[] textureObjectData = WriteAssetBytes(collection, texture);
		byte[] spriteObjectData = WriteAssetBytes(collection, sprite);

		SerializedFileBuilder builder = NewBuilder(version, Texture2DClassId, SpriteClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = texture.PathID,
			TypeID = Texture2DClassId,
			ClassID = Texture2DClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = textureObjectData,
		});
		builder.Objects.Add(new ObjectInfo
		{
			FileID = sprite.PathID,
			TypeID = SpriteClassId,
			ClassID = SpriteClassId,
			SerializedTypeIndex = 1,
			Type = builder.Types[1],
			ObjectData = spriteObjectData,
		});
		return Serialize(builder.Build());
	}

	private static ITexture2D CreateTexture(ProcessedAssetCollection collection, string name, byte[] pixels, int width, int height)
	{
		ITexture2D texture = AssetCreator.CreateTexture2D(collection);
		texture.Width_C28 = width;
		texture.Height_C28 = height;
		texture.ImageCount_C28 = 1;
		texture.Format_C28E = TextureFormat.RGBA32;
		texture.ImageData_C28 = pixels;
		texture.Name = new Utf8String(name);
		return texture;
	}

	private static void SetRenderDataKey(AssetPair<GUID, long> key, long value)
	{
		key.Key.Data_0_ = 0x11223344;
		key.Key.Data_1_ = 0x55667788;
		key.Key.Data_2_ = 0x99AABBCC;
		key.Key.Data_3_ = 0xDDEEFF00;
		key.Value = value;
	}

	/// <summary>
	/// Writes a single-vertex-stream tight mesh (x,y per point, z zero) plus its index buffer and
	/// one triangles submesh, all through the upstream vertex-data layout a real sprite uses.
	/// </summary>
	private static void SetTightMesh(ISpriteRenderData renderData, TightMeshOptions options)
	{
		float[] points = options.Points;
		int vertexCount = points.Length / 2;
		byte[] data = new byte[vertexCount * 12];
		for (int i = 0; i < vertexCount; i++)
		{
			BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 12), points[i * 2]);
			BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan((i * 12) + 4), points[(i * 2) + 1]);
			BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan((i * 12) + 8), 0f);
		}

		renderData.VertexData.VertexCount = options.DeclaredVertexCount ?? (uint)vertexCount;
		renderData.VertexData.Data = data;
		byte dimension = options.ChannelDimension ?? 3;
		byte format = options.ChannelFormat ?? 0;
		byte offset = options.ChannelOffset ?? 0;
		byte stream = options.ChannelStream ?? 0;
		AddChannel(renderData, dimension, format, offset, stream);
		for (int i = 0; i < options.DuplicateChannelCount; i++)
		{
			AddChannel(renderData, dimension, format, offset, stream);
		}

		if (options.AddUnusedChannel)
		{
			AddChannel(renderData, 0, 0, 0, 0);
		}

		ushort[] indices = options.Indices ?? Enumerable.Range(0, vertexCount).Select(value => (ushort)value).ToArray();
		if (options.IncludeIndexBuffer)
		{
			byte[] indexBuffer = new byte[(indices.Length * 2) + (options.OddIndexBuffer ? 1 : 0)];
			for (int i = 0; i < indices.Length; i++)
			{
				BinaryPrimitives.WriteUInt16LittleEndian(indexBuffer.AsSpan(i * 2), indices[i]);
			}

			renderData.IndexBuffer = indexBuffer;
		}

		if (!options.IncludeSubMesh)
		{
			return;
		}

		ISubMesh subMesh = renderData.SubMeshes.AddNew();
		subMesh.FirstByte = options.FirstByte ?? 0u;
		subMesh.IndexCount = options.IndexCount ?? (uint)indices.Length;
		subMesh.FirstVertex = 0;
		subMesh.VertexCount = (uint)vertexCount;
		subMesh.Topology = (int)options.Topology;
	}

	private static void AddChannel(ISpriteRenderData renderData, byte dimension, byte format, byte offset, byte stream)
	{
		ChannelInfo channel = renderData.VertexData.Channels.AddNew();
		channel.Dimension = dimension;
		channel.Format = format;
		channel.Offset = offset;
		channel.Stream = stream;
	}

	/// <summary>Writes one asset through the upstream writer, exactly like a real serialized file.</summary>
	private static byte[] WriteAssetBytes(ProcessedAssetCollection collection, AssetRipper.Assets.IUnityObjectBase asset)
	{
		using MemoryStream stream = new();
		using (AssetWriter writer = new(stream, collection))
		{
			asset.WriteRelease(writer);
		}

		return stream.ToArray();
	}

	/// <summary>
	/// A serialized asset file holding one named Font whose embedded source font bytes are
	/// <paramref name="fontData"/>. The object data is produced by the upstream writer for a real
	/// font, so the raw bytes are carried verbatim and can be exported back byte for byte.
	/// </summary>
	public static byte[] SerializedFont(string name, byte[] fontData, long pathId = 1, params string[] dependencies)
	{
		UnityVersion version = UnityVersion.Parse("2021.3.0f1");
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);
		IFont font = AssetCreator.CreateFont(collection);
		font.FontData = fontData;
		font.Name = new Utf8String(name);

		byte[] objectData = WriteAssetBytes(collection, font);

		SerializedFileBuilder builder = NewBuilder(FontClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = pathId,
			TypeID = FontClassId,
			ClassID = FontClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = objectData,
		});
		foreach (string dependency in dependencies)
		{
			builder.Dependencies.Add(new FileIdentifier
			{
				Type = AssetType.Serialized,
				AssetPath = Utf8String.Empty,
				PathNameOrigin = dependency,
				PathName = SpecialFileNames.FixFileIdentifier(dependency),
			});
		}

		return Serialize(builder.Build());
	}

	/// <summary>
	/// A serialized asset file holding one named Mesh whose single submesh is the triangle
	/// (0,0,0)-(1,0,0)-(0,1,0) with the index buffer 0,1,2. The compressed mesh data and the
	/// submesh are produced by the upstream fill helper and written through the upstream writer,
	/// so the reader loads a real, non-empty Unity mesh rather than an empty header.
	/// </summary>
	public static byte[] SerializedTriangleMesh(string name, long pathId = 1, params string[] dependencies)
	{
		MeshData meshData = MeshData.Empty with
		{
			Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
			ProcessedIndexBuffer = [0u, 1u, 2u],
			SubMeshes =
			[
				new SubMeshData(0u, 0, 0, 3, 1, 3, MeshTopology.Triangles,
					new Bounds(new Vector3(0.5f, 0.5f, 0.0f), new Vector3(0.5f, 0.5f, 0.0f))),
			],
		};

		return SerializedMesh(name, meshData, pathId, dependencies);
	}

	/// <summary>
	/// A serialized asset file holding one named Mesh carrying the supplied geometry, produced by
	/// the upstream mesh fill helper and written exactly like a real serialized mesh.
	/// </summary>
	public static byte[] SerializedMesh(string name, MeshData meshData, long pathId = 1, params string[] dependencies)
	{
		UnityVersion version = UnityVersion.V_2022;
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);
		IMesh mesh = collection.CreateMesh();
		mesh.Name = new Utf8String(name);
		mesh.FillWithCompressedMeshData(meshData);

		return MeshFile(collection, mesh, version, pathId, dependencies);
	}

	/// <summary>A serialized asset file holding one named Mesh with no vertex data at all.</summary>
	public static byte[] SerializedEmptyMesh(string name, long pathId = 1, params string[] dependencies)
		=> SerializedMesh(name, MeshData.Empty, pathId, dependencies);

	/// <summary>
	/// A serialized asset file holding one named Mesh that declares <paramref name="declaredVertexCount"/>
	/// vertices in its vertex-data metadata while carrying no channel bytes, so it is a real,
	/// present mesh whose geometry cannot be materialised.
	/// </summary>
	public static byte[] SerializedMeshDeclaringVertices(string name, uint declaredVertexCount, long pathId = 1, params string[] dependencies)
	{
		UnityVersion version = UnityVersion.V_2022;
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);
		IMesh mesh = collection.CreateMesh();
		mesh.Name = new Utf8String(name);
		mesh.VertexData.VertexCount = declaredVertexCount;

		return MeshFile(collection, mesh, version, pathId, dependencies);
	}

	/// <summary>
	/// A serialized asset file holding one named Mesh that declares vertices and an index buffer
	/// but no submeshes, so it carries geometry that cannot be turned into any GLB mesh.
	/// </summary>
	public static byte[] SerializedMeshWithoutSubMeshes(string name, long pathId = 1, params string[] dependencies)
	{
		MeshData meshData = MeshData.Empty with
		{
			Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
			ProcessedIndexBuffer = [0u, 1u, 2u],
		};

		return SerializedMesh(name, meshData, pathId, dependencies);
	}

	/// <summary>
	/// A single serialized asset file holding BOTH a modern streamed AudioClip (ClassID 83) and a
	/// real triangle Mesh (ClassID 43) in one V_2022 collection, so one selected file exposes both
	/// resource kinds instead of concatenating two independently serialized fixtures. The clip
	/// streams its sound bank from a sibling resource; the mesh carries the shared triangle
	/// geometry. Two object records reference the two type records by index and carry each asset's
	/// own distinct PathID.
	/// </summary>
	public static byte[] SerializedMixedAudioAndMesh(
		string audioName,
		string meshName,
		string resourceName,
		uint resourceSizeBytes,
		params string[] dependencies)
	{
		UnityVersion version = UnityVersion.V_2022;
		ProcessedAssetCollection collection = AssetCreator.CreateCollection(version);

		// The modern clip shape: a streamed bank in a sibling resource plus a compression format.
		// PCM (0) matches the public PCM16 bank; the real codec is decided by the bank's own header
		// at export time, but the format is set so the field is serialized.
		IAudioClip clip = AssetCreator.CreateAudioClip(collection);
		clip.Name = new Utf8String(audioName);
		clip.Resource!.Source = new Utf8String(resourceName);
		clip.Resource!.Offset = 0;
		clip.Resource!.Size = resourceSizeBytes;
		clip.CompressionFormat = (int)AudioCompressionFormat.PCM;

		// A streamed clip is only a valid fixture when the chosen version actually serializes the
		// resource descriptor; assert it on the created asset before the writer runs so a version
		// mismatch fails loudly here rather than as a silently empty clip.
		if (!clip.Has_Resource())
		{
			throw new InvalidOperationException("The chosen Unity version does not serialize a streamed audio resource.");
		}

		IMesh mesh = collection.CreateMesh();
		mesh.Name = new Utf8String(meshName);
		mesh.FillWithCompressedMeshData(TriangleMeshData);

		byte[] clipData = WriteAssetBytes(collection, clip);
		byte[] meshData = WriteAssetBytes(collection, mesh);

		// One file, two type records, two object records: each object points at its own type record
		// by index and carries that asset's own PathID, so both live in one serialized file.
		SerializedFileBuilder builder = NewBuilder(version, AudioClipClassId, MeshClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = clip.PathID,
			TypeID = AudioClipClassId,
			ClassID = AudioClipClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = clipData,
		});
		builder.Objects.Add(new ObjectInfo
		{
			FileID = mesh.PathID,
			TypeID = MeshClassId,
			ClassID = MeshClassId,
			SerializedTypeIndex = 1,
			Type = builder.Types[1],
			ObjectData = meshData,
		});
		foreach (string dependency in dependencies)
		{
			builder.Dependencies.Add(new FileIdentifier
			{
				Type = AssetType.Serialized,
				AssetPath = Utf8String.Empty,
				PathNameOrigin = dependency,
				PathName = SpecialFileNames.FixFileIdentifier(dependency),
			});
		}

		return Serialize(builder.Build());
	}

	/// <summary>Writes one mesh into a serialized asset file through the upstream writer.</summary>
	private static byte[] MeshFile(ProcessedAssetCollection collection, IMesh mesh, UnityVersion version, long pathId, string[] dependencies)
	{
		byte[] objectData = WriteAssetBytes(collection, mesh);

		SerializedFileBuilder builder = NewBuilder(version, MeshClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = pathId,
			TypeID = MeshClassId,
			ClassID = MeshClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = objectData,
		});
		foreach (string dependency in dependencies)
		{
			builder.Dependencies.Add(new FileIdentifier
			{
				Type = AssetType.Serialized,
				AssetPath = Utf8String.Empty,
				PathNameOrigin = dependency,
				PathName = SpecialFileNames.FixFileIdentifier(dependency),
			});
		}

		return Serialize(builder.Build());
	}

	/// <summary>A legal uncompressed UnityFS container holding the given internal entries in order.</summary>
	public static byte[] Bundle(params (string Path, byte[] Data)[] entries)
	{
		FileStreamNode[] nodes = new FileStreamNode[entries.Length];
		long offset = 0;
		for (int i = 0; i < entries.Length; i++)
		{
			nodes[i] = new FileStreamNode { Offset = offset, Size = entries[i].Data.Length, Path = entries[i].Path };
			offset += entries[i].Data.Length;
		}

		byte[] metadata = Metadata(nodes, (uint)offset, (uint)offset, (StorageBlockFlags)CompressionType.None);
		FileStreamBundleHeader header = new()
		{
			Version = BundleVersion.BF_2022_2,
			CompressedBlocksInfoSize = metadata.Length,
			UncompressedBlocksInfoSize = metadata.Length,
			Flags = BundleFlags.BlocksAndDirectoryInfoCombined,
		};
		byte[] headerBytes = SerializeHeader(header);
		int padding = (16 - (headerBytes.Length % 16)) % 16;
		header.Size = headerBytes.Length + padding + metadata.Length + offset;
		headerBytes = SerializeHeader(header);

		using MemoryStream stream = new();
		stream.Write(headerBytes);
		stream.Write(new byte[padding]);
		stream.Write(metadata);
		foreach ((string _, byte[] data) in entries)
		{
			stream.Write(data);
		}
		return stream.ToArray();
	}

	/// <summary>
	/// A legal UnityFS container whose single data block is LZ4-compressed through the upstream
	/// codec. Reading an entry then exercises the block-decompression path, including the temp-file
	/// branch the block reader uses for entries larger than its in-memory limit, instead of the
	/// direct slice path an uncompressed bundle takes.
	/// </summary>
	public static byte[] CompressedBundle(params (string Path, byte[] Data)[] entries)
	{
		FileStreamNode[] nodes = new FileStreamNode[entries.Length];
		long offset = 0;
		for (int i = 0; i < entries.Length; i++)
		{
			nodes[i] = new FileStreamNode { Offset = offset, Size = entries[i].Data.Length, Path = entries[i].Path };
			offset += entries[i].Data.Length;
		}

		int rawLength = (int)offset;
		byte[] raw = new byte[rawLength];
		int position = 0;
		foreach ((string _, byte[] data) in entries)
		{
			data.CopyTo(raw, position);
			position += data.Length;
		}

		byte[] compressed = new byte[LZ4Codec.MaximumOutputSize(rawLength)];
		int compressedLength = LZ4Codec.Encode(raw, compressed, LZ4Level.L00_FAST);
		if (compressedLength <= 0)
		{
			throw new InvalidOperationException("LZ4 compression produced no output.");
		}

		byte[] metadata = Metadata(nodes, (uint)rawLength, (uint)compressedLength, (StorageBlockFlags)CompressionType.Lz4);
		FileStreamBundleHeader header = new()
		{
			Version = BundleVersion.BF_2022_2,
			CompressedBlocksInfoSize = metadata.Length,
			UncompressedBlocksInfoSize = metadata.Length,
			Flags = BundleFlags.BlocksAndDirectoryInfoCombined,
		};
		byte[] headerBytes = SerializeHeader(header);
		int padding = (16 - (headerBytes.Length % 16)) % 16;
		header.Size = headerBytes.Length + padding + metadata.Length + compressedLength;
		headerBytes = SerializeHeader(header);

		using MemoryStream stream = new();
		stream.Write(headerBytes);
		stream.Write(new byte[padding]);
		stream.Write(metadata);
		stream.Write(compressed, 0, compressedLength);
		return stream.ToArray();
	}

	/// <summary>A file whose signature claims UnityFS but whose content is truncated.</summary>
	public static byte[] TruncatedBundle()
	{
		byte[] bytes = new byte[40];
		"UnityFS"u8.CopyTo(bytes);
		return bytes;
	}

	/// <summary>
	/// A serialized asset file whose header and metadata are well-formed but whose object data is
	/// truncated, so the corruption only surfaces inside the object reader/factory rather than at
	/// the serialized-file header.
	/// </summary>
	public static byte[] CorruptSerializedTextAsset(long pathId = 1)
	{
		SerializedFileBuilder builder = NewBuilder(TextAssetClassId);
		builder.Objects.Add(new ObjectInfo
		{
			FileID = pathId,
			TypeID = TextAssetClassId,
			ClassID = TextAssetClassId,
			SerializedTypeIndex = 0,
			Type = builder.Types[0],
			ObjectData = [1, 2, 3],
		});
		return Serialize(builder.Build());
	}

	private static SerializedFileBuilder NewBuilder(params int[] typeIds)
	{
		return NewBuilder(UnityVersion.Parse("2021.3.0f1"), typeIds);
	}

	private static SerializedFileBuilder NewBuilder(UnityVersion version, params int[] typeIds)
	{
		SerializedFileBuilder builder = new()
		{
			Generation = FormatVersion.LargeFilesSupport,
			Version = version,
			Platform = BuildTarget.StandaloneWin64Player,
			EndianType = EndianType.LittleEndian,
			HasTypeTree = false,
		};
		return WithTypes(builder, typeIds);
	}

	private static SerializedFileBuilder WithTypes(SerializedFileBuilder builder, params int[] typeIds)
	{
		foreach (int typeId in typeIds)
		{
			builder.Types.Add(new SerializedType { TypeID = typeId, ScriptTypeIndex = -1, IsStrippedType = false });
		}

		return builder;
	}

	private static byte[] TextAssetData(string name, string payload)
	{
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
		{
			WriteUnityString(writer, name);
			WriteUnityString(writer, payload);
		}
		return stream.ToArray();
	}

	private static void WriteUnityString(BinaryWriter writer, string value)
	{
		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
		writer.Write(bytes.Length);
		writer.Write(bytes);
		int padding = (4 - (bytes.Length & 3)) & 3;
		for (int i = 0; i < padding; i++)
		{
			writer.Write((byte)0);
		}
	}

	private static byte[] Metadata(FileStreamNode[] nodes, uint uncompressedSize, uint compressedSize, StorageBlockFlags flags)
	{
		BlocksInfo blocks = new(new Hash128(), new[] { NewStorageBlock(uncompressedSize, compressedSize, flags) });
		using MemoryStream stream = new();
		using (EndianWriter writer = new(stream, EndianType.BigEndian))
		{
			blocks.Write(writer);
			new AssetRipper.IO.Files.BundleFiles.DirectoryInfo<FileStreamNode> { Nodes = nodes }.Write(writer);
		}
		return stream.ToArray();
	}

	private static StorageBlock NewStorageBlock(uint uncompressedSize, uint compressedSize, StorageBlockFlags flags)
	{
		byte[] raw = new byte[10];
		BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0), uncompressedSize);
		BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(4), compressedSize);
		BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(8), (ushort)flags);
		using MemoryStream stream = new(raw);
		using EndianReader reader = new(stream, EndianType.BigEndian);
		return StorageBlock.Read(reader);
	}

	private static byte[] SerializeHeader(FileStreamBundleHeader header)
	{
		using MemoryStream stream = new();
		using (EndianWriter writer = new(stream, EndianType.BigEndian))
		{
			header.Write(writer);
		}
		return stream.ToArray();
	}

	private static byte[] Serialize(SerializedFile file)
	{
		using MemoryStream stream = new();
		file.Write(stream);
		return stream.ToArray();
	}
}
