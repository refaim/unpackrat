using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_0;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_128;
using AssetRipper.SourceGenerated.Classes.ClassID_152;
using AssetRipper.SourceGenerated.Classes.ClassID_329;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Classes.ClassID_687078895;
using AssetRipper.SourceGenerated.Extensions;
using AssetsTools.NET;

namespace Unpackrat.UnityProbe;

internal static partial class UnitySource
{
	// Retained resource/atlas identities remain ordinary Unity objects for upstream PPtr and
	// original-path processing. Unsupported shapes are skipped after complete source ID validation.

	private sealed class DeferredObject(AssetInfo info, ObjectInfo data, IReadOnlyList<SerializedTypeReference> references, Shape shape, LazyAssetFactory owner) : UnityObjectBase(info), IObject, INamed, IProbeResource
	{
		private long sourceSize;
		private string extension = string.Empty;
		public Utf8String Name { get; set; } = Utf8String.Empty;
		public uint HideFlags_C0 { get; set; }
		public HideFlags HideFlags_C0E { get => (HideFlags)HideFlags_C0; set => HideFlags_C0 = (uint)value; }
		public void CopyValues(IObject source) => HideFlags_C0 = source.HideFlags_C0;
		public ObjectInfo Data { get; } = data;
		public IReadOnlyList<SerializedTypeReference> References { get; } = references;
		public Shape Shape { get; } = shape;
		public UnityProjection? Projection { get; set; }
		public Dictionary<(ulong, ulong, ulong, ulong, long), (int FileId, long PathId)>? AtlasTextures { get; set; }
		UnityEntryKind IProbeResource.Kind => Shape.Kind.GetValueOrDefault();
		long IProbeResource.PathId => PathID;
		string IProbeResource.Name => Name.String;
		string? IProbeResource.OriginalPath => OriginalPath;
		long IProbeResource.SourceSize => sourceSize;
		string IProbeResource.OutputExtension => extension;
		byte[]? IProbeResource.ExportData() => owner.Export(this);
		public void CaptureResourceMetadata(long size, string outputExtension)
		{
			sourceSize = size;
			extension = outputExtension;
		}
	}

	private sealed record Shape(UnityEntryKind? Kind, bool Atlas, AssetTypeTemplateField? Template, IAudioClip? Audio)
	{
		public UnityProjection.State? Traversal { get; } = Template is null ? null : new(Template);
	}

	internal sealed class LazyAssetFactory(AssetFactoryBase fullReader) : AssetFactoryBase
	{
		public override bool ReleaseSourceObjectDescriptors => true;
		private readonly Dictionary<(int ClassId, UnityVersion Version, bool Release), Shape> shapes = [];

		public override void ValidateObjectTable(ReadOnlySpan<ObjectInfo> objects)
		{
			HashSet<long> ids = new(objects.Length);
			foreach (ref readonly ObjectInfo descriptor in objects)
			{
				if (descriptor.FileID == 0 || !ids.Add(descriptor.FileID))
					throw new ArgumentException("Source object path IDs must be nonzero and unique.", nameof(objects));
			}
		}

		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> bytes, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
			=> fullReader.ReadAsset(info, bytes, type, references);

		public override IUnityObjectBase? ReadAsset(AssetInfo info, in ObjectInfo descriptor, IReadOnlyList<SerializedTypeReference> references)
		{
			if (info.ClassID is 142 or 147) return fullReader.ReadAsset(info, descriptor.ObjectData, descriptor.Type, references);
			Shape shape = GetShape(info, info.Collection.Version);
			if (shape.Kind is null && !shape.Atlas) return null;
			bool patch = info.Collection.Version.Type == UnityVersionType.Patch
				&& !SpecialFileNames.IsDefaultResourceOrBuiltinExtra(info.Collection.Name);
			DeferredObject identity = Project(info, descriptor, references, shape, patch);
			if (patch && identity.Projection is null && identity.Shape.Template is not null)
			{
				UnityVersion original = info.Collection.Version;
				UnityVersion next = new(original.Major, original.Minor, unchecked((ushort)(original.Build + 1u)));
				identity = Project(info, descriptor, references, GetShape(info, next), true);
			}
			return identity;
		}

		private DeferredObject Project(AssetInfo info, in ObjectInfo descriptor, IReadOnlyList<SerializedTypeReference> references, Shape shape, bool validateLayout)
		{
			DeferredObject identity = new(info, descriptor, references, shape, this);
			if (shape.Template is not null)
			{
				try
				{
					using Stream stream = descriptor.OpenDataStream();
					identity.Projection = UnityProjection.Read(stream, shape.Template, info.Collection.EndianType == EndianType.BigEndian, shape.Traversal, validateLayout, shape.Kind == UnityEntryKind.Texture);
					identity.Name = new Utf8String(identity.Projection.Text("m_Name"));
				}
				catch (Exception error) when (error is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
				{
					// An unreadable projected object stays an identity, as an upstream unreadable object
					// does. Opening never falls back to reading its complete resource body.
					identity.Projection = null;
				}
			}
			return identity;
		}

		private Shape GetShape(AssetInfo info, UnityVersion version)
		{
			bool release = info.Collection.Flags.IsRelease();
			var key = (info.ClassID, version, release);
			if (shapes.TryGetValue(key, out Shape? shape)) return shape;
			IUnityObjectBase? definition = version.LessThan(3, 5) ? null : AssetFactory.CreateSerialized(info, version);
			shape = BuildShape(info, definition, version);
			shapes.Add(key, shape);
			return shape;
		}

		private static Shape BuildShape(AssetInfo info, IUnityObjectBase? definition, UnityVersion version)
		{
			UnityEntryKind? kind = definition switch
			{
				ITexture2D => UnityEntryKind.Texture, ITextAsset => UnityEntryKind.Text,
				IFont => UnityEntryKind.Font, IVideoClip => UnityEntryKind.VideoClip,
				IMovieTexture => UnityEntryKind.MovieTexture, IAudioClip => UnityEntryKind.AudioClip,
				IMesh => UnityEntryKind.Mesh, ISprite => UnityEntryKind.Sprite, _ => null,
			};
			bool atlas = definition is ISpriteAtlas;
			AssetTypeTemplateField? template = null;
			if ((kind is not null || atlas) && TypeTreeNodeStruct.TryMakeFromTpk((ClassIDType)info.ClassID, version, out var releaseRoot, out var editorRoot))
				template = UnityProjection.Template(info.Collection.Flags.IsRelease() ? releaseRoot : editorRoot);
			return new(kind, atlas, template, definition as IAudioClip);
		}

		public IReadOnlyList<IProbeAsset> Describe(AssetCollection collection)
			=> collection.Select(asset => asset is DeferredObject deferred ? Describe(deferred) : Wrap([asset]).Single()).ToArray();

		public void ReleaseProjectedMetadata(IEnumerable<IUnityObjectBase> assets)
		{
			foreach (IUnityObjectBase asset in assets)
			{
				if (asset is DeferredObject identity)
				{
					identity.Projection = null;
					identity.AtlasTextures = null;
				}
			}
		}

		private IProbeAsset Describe(DeferredObject identity)
		{
			if (identity.Projection is not { } fields || identity.Shape.Kind is not { } kind) return OtherAsset.Instance;
			long size;
			string extension;
			switch (kind)
			{
				case UnityEntryKind.Texture:
					if (EmptyTexture(identity)) return OtherAsset.Instance;
					size = TextureSize(fields); extension = "png"; break;
				case UnityEntryKind.Text:
					size = Range(fields, "m_Script").Length; extension = "bin"; break;
				case UnityEntryKind.Font:
					UnityProjection.ByteRange font = Range(fields, "m_FontData");
					if (font.Length == 0) return OtherAsset.Instance;
					size = font.Length;
					using (Stream stream = identity.Data.OpenDataStream())
					{
						byte[]? prefix = ReadPrefix(stream, (ulong)font.Offset, (ulong)font.Length, 4);
						uint tag = prefix is { Length: 4 } ? BinaryPrimitives.ReadUInt32LittleEndian(prefix) : 0;
						extension = tag == TtcTag ? "ttc" : tag == 0x4F54544F ? "otf" : "ttf";
					}
					break;
				case UnityEntryKind.VideoClip:
					if (fields.Text("m_ExternalResources/m_Source").Length == 0 || fields.Unsigned("m_ExternalResources/m_Size") == 0) return OtherAsset.Instance;
					size = DeclaredSourceSize.Narrow(fields.Unsigned("m_ExternalResources/m_Size"));
					string suffix = Path.GetExtension(fields.Text("m_OriginalPath"));
					extension = OutputExtensions.Video(suffix.Length == 0 ? null : suffix[1..]); break;
				case UnityEntryKind.MovieTexture:
					size = Range(fields, "m_MovieData").Length;
					if (size == 0) return OtherAsset.Instance;
					extension = "ogv"; break;
				case UnityEntryKind.AudioClip:
					UnityProjection.ByteRange audio = Range(fields, "m_AudioData");
					string source = fields.Text("m_Resource/m_Source");
					if (audio.Length == 0 && source.Length == 0) return OtherAsset.Instance;
					ulong declared = audio.Length > 0 ? (ulong)audio.Length : fields.Unsigned("m_Resource/m_Size");
					size = DeclaredSourceSize.Narrow(declared);
					IAudioClip audioDefinition = identity.Shape.Audio!;
					if (audioDefinition.Has_Type())
					{
						audioDefinition.Type = (int)fields.Number("m_Type");
						extension = OutputExtensions.Normalise(audioDefinition.GetSoundType().ToRawExtension());
					}
					else
					{
						var file = identity.Collection.Bundle.ResolveResource(source);
						byte[]? prefix = file is null ? null : ReadPrefix(file.Stream, fields.Unsigned("m_Resource/m_Offset"), declared, 1084);
						extension = AudioPayload.DescribeModern(prefix, size, declared).Extension;
					}
					break;
				case UnityEntryKind.Mesh:
					if (fields.Number("m_VertexData/m_VertexCount") == 0 && fields.Number("m_CompressedMesh/m_Vertices/m_NumItems") == 0) return OtherAsset.Instance;
					size = (long)Range(fields, "m_VertexData/m_DataSize").Length + Range(fields, "m_IndexBuffer").Length;
					extension = "glb"; break;
				default: // Sprite is the remaining member of the closed, generated resource classification.
					DeferredObject? texture = SpriteTexture(identity, out bool fromAtlas);
					if (texture is null || EmptyTexture(texture)) return OtherAsset.Instance;
					size = fromAtlas ? 0 : TextureSize(texture.Projection!); extension = "png"; break;

			}
			identity.CaptureResourceMetadata(size, extension);
			return identity;
		}

		private static UnityProjection.ByteRange Range(UnityProjection fields, string root)
		{
			UnityProjection.ByteRange range = fields.Bytes(root);
			return range.Length != 0 ? range : fields.Bytes(root + "/Array");
		}
		private static long TextureSize(UnityProjection fields)
		{
			int inline = Range(fields, "image data").Length;
			return inline != 0 ? inline : fields.Text("m_StreamData/path").Length != 0 ? DeclaredSourceSize.Narrow(fields.Unsigned("m_StreamData/size")) : 0;
		}
		private static bool EmptyTexture(DeferredObject texture)
			=> texture.Shape.Kind != UnityEntryKind.Texture || texture.Projection is not { } fields
				|| fields.Number("m_Width") == 0 && fields.Number("m_Height") == 0 && Range(fields, "image data").Length == 0
				&& fields.Unsigned("m_StreamData/size") == 0 && fields.Text("m_StreamData/path").Length == 0;

		private static DeferredObject? Reference(DeferredObject owner, string path)
			=> owner.Collection.TryGetAsset((int)owner.Projection!.Number(path + "/m_FileID"), owner.Projection.Number(path + "/m_PathID")) as DeferredObject;

		private static (ulong, ulong, ulong, ulong, long) Key(UnityProjection fields, string prefix)
			=> new(fields.Unsigned(prefix + "/first/data[0]"), fields.Unsigned(prefix + "/first/data[1]"), fields.Unsigned(prefix + "/first/data[2]"), fields.Unsigned(prefix + "/first/data[3]"), fields.Number(prefix + "/second"));
		private static Dictionary<(ulong, ulong, ulong, ulong, long), (int FileId, long PathId)> AtlasIndex(UnityProjection fields)
		{
			Dictionary<(ulong, ulong, ulong, ulong, long), (int FileId, long PathId)> result = [];
			foreach (string path in fields.Keys)
			{
				const string tail = "/first/second";
				if (!path.EndsWith(tail, StringComparison.Ordinal)) continue;
				string entry = path[..^tail.Length];
				string pointer = entry + "/second/texture";
				// The pinned AccessDictionary returns the first matching entry for duplicate keys.
				result.TryAdd(Key(fields, entry + "/first"), ((int)fields.Number(pointer + "/m_FileID"), fields.Number(pointer + "/m_PathID")));
			}
			return result;
		}
		private static DeferredObject? SpriteTexture(DeferredObject sprite, out bool fromAtlas)
		{
			UnityProjection fields = sprite.Projection!;
			fromAtlas = fields.Number("m_SpriteAtlas/m_PathID") != 0;
			if (!fromAtlas) return Reference(sprite, "m_RD/texture");
			DeferredObject? atlas = Reference(sprite, "m_SpriteAtlas");
			if (atlas?.Projection is not { } atlasFields || !atlas.Shape.Atlas) return null;
			atlas.AtlasTextures ??= AtlasIndex(atlasFields);
			return atlas.AtlasTextures.TryGetValue(Key(fields, "m_RenderDataKey"), out var pointer)
				? atlas.Collection.TryGetAsset(pointer.FileId, pointer.PathId) as DeferredObject : null;
		}

		internal byte[]? Export(IProbeResource exportTarget)
		{
			DeferredObject identity = (DeferredObject)exportTarget;
			List<DeferredObject> replaced = [];
			try
			{
				IUnityObjectBase? full = Materialize(identity, replaced);
				if (full is ISprite sprite)
				{
					if (sprite.SpriteAtlas is { } pointer && !pointer.IsNull())
					{
						if (sprite.Collection.TryGetAsset(pointer.FileID, pointer.PathID) is DeferredObject atlasIdentity)
							Materialize(atlasIdentity, replaced);
						if (sprite.SpriteAtlasP is { } atlas && atlas.RenderDataMap.TryGetValue(sprite.RenderDataKey, out var data)
							&& atlas.Collection.TryGetAsset(data.Texture.FileID, data.Texture.PathID) is DeferredObject texture)
							Materialize(texture, replaced);
					}
					else if (sprite.Collection.TryGetAsset(sprite.RD.Texture.FileID, sprite.RD.Texture.PathID) is DeferredObject texture)
						Materialize(texture, replaced);
				}
				IProbeAsset asset = full is null ? OtherAsset.Instance : Wrap([full]).Single();
				return asset switch { IProbeTexture texture => texture.RenderPng(), IProbeText text => text.ReadData(), IProbeResource resource => resource.ExportData(), _ => null };
			}
			finally
			{
				for (int i = replaced.Count - 1; i >= 0; i--) replaced[i].Collection.ReplaceAsset(replaced[i]);
			}
		}
		private IUnityObjectBase? Materialize(DeferredObject identity, List<DeferredObject> replaced)
		{
			IUnityObjectBase? full = fullReader.ReadAsset(identity.AssetInfo, identity.Data.ObjectData, identity.Data.Type, identity.References);
			if (full is null) return null;
			full.OriginalPath = identity.OriginalPath;
			full.OverridePath = identity.OverridePath;
			full.AssetBundleName = identity.AssetBundleName;
			full.MainAsset = identity.MainAsset;
			// Reserve the restoration record before changing the collection: an allocation failure
			// must never leave a materialised object behind.
			replaced.Add(identity);
			identity.Collection.ReplaceAsset(full);
			return full;
		}

	}
}
