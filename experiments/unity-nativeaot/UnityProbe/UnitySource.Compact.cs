using System;
using System.Buffers;
using System.Collections;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;

namespace Unpackrat.UnityProbe;

internal static partial class UnitySource
{
	internal sealed partial class LazyAssetFactory
	{
		private readonly Dictionary<AssetCollection, CompactCollection> compactCollections = [];
		private readonly ArrayBufferWriter<byte> names = new();
		private readonly HashSet<CompactCollection> touchedResolvers = [];
		private void EndCompactOperation(bool paths)
		{
			foreach (CompactCollection rows in touchedResolvers) rows.EndOperation(paths);
			touchedResolvers.Clear();
		}
		public override void ValidateObjectTable(AssetCollection collection, ReadOnlySpan<ObjectInfo> objects)
			=> PrepareCompactTable(collection, new SourceTable(objects));
		public override void ValidateObjectTable(AssetCollection collection, AssetRipper.IO.Files.SerializedFiles.SerializedFile file)
			=> PrepareCompactTable(collection, new SourceTable(default, file));
		private void PrepareCompactTable(AssetCollection collection, SourceTable objects)
		{
			ValidateObjectTable(objects);
			if (!compactTable) return;
			int resources = 0, sprites = 0;
			for (int i = 0; i < objects.Count; i++)
			{
				ObjectInfo descriptor = objects[i];
				// This is the pinned serialized collection's own TypeID-to-ClassID rule.
				int classID = descriptor.TypeID < 0 ? 114 : descriptor.TypeID;
				if (classID is 142 or 147) continue;
				Shape shape = GetShape(new(collection, descriptor.FileID, classID), collection.Version);
				if (shape.Kind is not { } kind) continue;
				resources++; if (kind == UnityEntryKind.Sprite) sprites++;
			}
			if (resources > 0) compactCollections.Add(collection, new(this, collection, resources, sprites));
		}
		public override void CompleteCollection(AssetCollection collection)
		{
			if (compactCollections.TryGetValue(collection, out CompactCollection? rows))
			{
				rows.Complete(); collection.AssetResolver = rows;
			}
		}
		public IUnityGraph BuildCompactGraph(GameBundle root, FileBase selected,
			IReadOnlyList<SerializedAssetCollection> selectedCollections, IReadOnlyList<FileBase> owned,
			IReadOnlyList<SmartStream> roots)
			=> new CompactGraph(this, root, selected, selectedCollections, owned, roots);
		private void ResolveCompactCollections(IReadOnlyList<SerializedAssetCollection> selected)
		{
			foreach (CompactCollection rows in compactCollections.Values) rows.ResolveSpriteReferences();
			HashSet<SpriteKey> seen = [];
			HashSet<AssetCollection> selectedSet = new(selected, ReferenceEqualityComparer.Instance);
			foreach (SerializedAssetCollection collection in selected)
			{
				if (compactCollections.TryGetValue(collection, out CompactCollection? rows))
					rows.SuppressStructuralDuplicates(seen, selectedSet);
			}
			foreach (CompactCollection rows in compactCollections.Values) rows.ReleaseSpriteScratch();
		}

		private struct PayloadRow(long path, long offset, uint length, int prototype)
		{
			public long Path = path;
			public long Offset = offset;
			public uint Length = length;
			public int Prototype = prototype;
		}
		private struct Header(int nameOffset, int nameLength, long size, string extension, bool visible)
		{
			private const int VisibilityBit = int.MinValue;
			private const int ExportableBit = 1 << 30;
			private const int NameLengthMask = ExportableBit - 1;
			public int NameOffset = nameOffset;
			// Projection bounds raw names to 1,048,576 bytes, leaving the two high bits
			// free: the sign bit carries visibility and bit 30 carries exportability.
			private int flags = nameLength | (visible ? VisibilityBit | ExportableBit : 0);
			public readonly int NameLength => flags & NameLengthMask;
			public long Size = size;
			public string Extension = extension;
			public readonly bool Exportable => (flags & ExportableBit) != 0;
			public readonly bool Visible => (flags & VisibilityBit) != 0;
			public void Reveal() { flags |= VisibilityBit | ExportableBit; }
			public void Hide() => flags &= ~VisibilityBit;
		}
		private sealed record Prototype(ObjectInfo Data, Shape Shape, IReadOnlyList<SerializedTypeReference> References, int ClassID);
		private readonly record struct SpriteLink(int Row, int File, long Path, int AtlasKeyIndex)
		{
			// Occupies the record's existing padding. The four-argument positional constructor
			// and the 24-byte layout remain unchanged.
			public bool QuadVertexCountFour { get; init; }
		}

		private sealed class CompactCollection(LazyAssetFactory factory, AssetCollection collection, int resourceCount, int spriteCount) : IAssetResolver
		{
			private readonly List<Prototype> prototypes = [];
			private readonly Dictionary<(SerializedType?, Shape, int), int> prototypeIndices = [];
			private SpriteLink[] links = new SpriteLink[spriteCount];
			private SpriteFacts[]? spriteFacts = spriteCount == 0 ? null : new SpriteFacts[spriteCount];
			private (int Width, int Height)[]? textureSizes;
			private (ulong, ulong, ulong, ulong, long)[]? atlasKeys;
			private int nextRow, nextLink;
			private readonly List<(int Row, string Source, ulong Offset, ulong Size)> audioPrefixes = [];
			private readonly Dictionary<long, IUnityObjectBase> identities = [];
			private readonly Dictionary<int, PathMetadata> paths = [];
						private readonly record struct PathMetadata(string? Original, string? Override, string? Bundle, IUnityObjectBase? Main);
			private readonly PayloadRow[] rows = new PayloadRow[resourceCount];
			private long[]? wideLengths;
			private readonly Header[] headers = new Header[resourceCount];
			private static readonly IComparer<PayloadRow> PathComparer = Comparer<PayloadRow>.Create(static (left, right) => left.Path.CompareTo(right.Path));
			// Unpublished/incomplete tables keep the original empty lookup until Complete succeeds.
			private bool orderedLookup;
			private long[] lookupPaths = [];
			private int[] lookupRows = [];
			private UnityProjection? ReadHeader(in ObjectInfo data, Shape shape, bool patch)
			{
				if (shape.Template is null) return null;
				try
				{
					return UnityProjection.ReadBorrowed(data.BorrowedSource!, data.DataOffset, data.DataSize, shape.Template,
						collection.EndianType == EndianType.BigEndian, shape.Traversal!, patch, shape.Kind == UnityEntryKind.Texture, captureRawName: true);
				}
				catch (Exception error) when (error is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
				{
					shape.Traversal!.Scratch?.ClearScratch(); return null;
				}
			}
			public void Add(AssetRipper.Assets.Metadata.AssetInfo info, in ObjectInfo data, IReadOnlyList<SerializedTypeReference> references, Shape shape, bool patch)
			{
				UnityProjection? fields = ReadHeader(data, shape, patch);
				if (patch && fields is null && shape.Template is not null)
				{
					UnityVersion version = collection.Version;
					shape = factory.GetShape(info, new(version.Major, version.Minor, unchecked((ushort)(version.Build + 1u))));
					fields = ReadHeader(data, shape, true);
				}
				try
				{
					var key = (data.Type, shape, info.ClassID);
					if (!prototypeIndices.TryGetValue(key, out int prototype))
					{
						prototype = prototypes.Count; prototypeIndices.Add(key, prototype); prototypes.Add(new(data, shape, references, info.ClassID));
					}
					int index = nextRow++;
					uint length;
					if (data.DataSize > uint.MaxValue)
					{
						wideLengths ??= new long[rows.Length]; wideLengths[index] = data.DataSize; length = 0;
					}
					else length = checked((uint)data.DataSize);
					rows[index] = new(info.PathID, data.DataOffset, length, prototype);
					bool sprite = shape.Kind == UnityEntryKind.Sprite;
					var metadata = fields is null || sprite ? (Visible: false, Size: 0L, Extension: string.Empty) : DescribeHeader(collection, data, shape, fields, resolveResources: false);
					int nameOffset = factory.names.WrittenCount;
					ReadOnlySpan<byte> name = fields is null ? [] : fields.RawName;
					factory.names.Write(name);
					headers[index] = new(nameOffset, name.Length, metadata.Size, metadata.Extension, metadata.Visible);
					if (shape.Kind == UnityEntryKind.Texture)
					{
						textureSizes ??= new (int, int)[rows.Length];
						textureSizes[index] = fields is null
							? default
							: (unchecked((int)fields.Number("m_Width")), unchecked((int)fields.Number("m_Height")));
					}
					if (metadata.Visible && shape.Kind == UnityEntryKind.AudioClip && !shape.Audio!.Has_Type())
						audioPrefixes.Add((index, fields!.Text("m_Resource/m_Source"), fields.Unsigned("m_Resource/m_Offset"), fields.Unsigned("m_Resource/m_Size")));
					if (sprite && fields is not null)
					{
						bool atlas = fields.Number("m_SpriteAtlas/m_PathID") != 0;
						int file = (int)fields.Number(atlas ? "m_SpriteAtlas/m_FileID" : "m_RD/texture/m_FileID");
						long path = fields.Number(atlas ? "m_SpriteAtlas/m_PathID" : "m_RD/texture/m_PathID");
						int atlasKeyIndex = -1;
						if (atlas)
						{
							// Bound optional key storage by the known Sprite count; mixed/all-atlas
							// tables never incur geometric growth or lose any GUID bits.
							atlasKeys ??= new (ulong, ulong, ulong, ulong, long)[links.Length];
							atlasKeyIndex = nextLink; atlasKeys[atlasKeyIndex] = Key(fields, "m_RenderDataKey");
						}
						spriteFacts![nextLink] = new SpriteFacts
						{
							Canvas = SpriteFactsReader.ReadCanvas(fields),
							Source = atlas ? default : SpriteFactsReader.ReadLocalSource(fields),
						};
						// Compare the stored wide value directly. Narrowing to int would let
						// unrelated integer values masquerade as four.
						bool quadVertexCountFour =
							fields.TryNumber("m_RD/m_VertexData/m_VertexCount", out long vertexCount)
							&& vertexCount == 4L;
						links[nextLink++] = new(index, file, path, atlasKeyIndex)
						{
							QuadVertexCountFour = quadVertexCountFour,
						};
					}
				}
				finally { fields?.ClearScratch(); }
			}

			public void Complete()
			{
				if (nextRow != rows.Length) throw new InvalidDataException("The prepared resource table was not filled completely.");
				orderedLookup = true;
				for (int i = 1; i < rows.Length; i++)
					if (rows[i - 1].Path >= rows[i].Path) { orderedLookup = false; break; }
				if (orderedLookup) return;
				lookupPaths = new long[rows.Length]; lookupRows = new int[rows.Length];
				for (int i = 0; i < rows.Length; i++) { lookupPaths[i] = rows[i].Path; lookupRows[i] = i; }
				Array.Sort(lookupPaths, lookupRows);
			}
			private int Find(long path)
			{
				int index = orderedLookup ? Array.BinarySearch(rows, new PayloadRow(path, 0, 0, 0), PathComparer)
					: Array.BinarySearch(lookupPaths, path);
				return index < 0 ? -1 : orderedLookup ? index : lookupRows[index];
			}
			private ObjectInfo Descriptor(int index)
			{
				PayloadRow row = rows[index];
				return prototypes[row.Prototype].Data.WithDataRange(row.Path, row.Offset,
					wideLengths is { } lengths && lengths[index] != 0 ? lengths[index] : row.Length);
			}
			// ResolveSpriteReferences records Resolved only after TryTextureInfo accepted this exact
			// exportable texture header; suppression only clears Visible, so the row stays present.
			private void HideTexture(long path) => headers[Find(path)].Hide();
			public bool TryTextureInfo(long path, out long size, out int width, out int height)
			{
				size = 0; width = 0; height = 0; int index = Find(path);
				if (index < 0 || !headers[index].Exportable || prototypes[rows[index].Prototype].Shape.Kind != UnityEntryKind.Texture) return false;
				size = headers[index].Size;
				if (textureSizes is not null) (width, height) = textureSizes[index];
				return true;
			}
			public void ResolveSpriteReferences()
			{
				// Nested collections acquire their parent bundle only after parsing completes.
				// Resolve resource-dependent labels after that ownership graph is attached.
				foreach (var audio in audioPrefixes)
				{
					var file = collection.Bundle.ResolveResource(audio.Source);
					byte[]? prefix = file is null ? null : ReadPrefix(file.Stream, audio.Offset, audio.Size, 1084);
					headers[audio.Row].Extension = AudioPayload.DescribeModern(prefix, headers[audio.Row].Size, audio.Size).Extension;
				}
				audioPrefixes.Clear(); audioPrefixes.TrimExcess();
				for (int linkIndex = 0; linkIndex < nextLink; linkIndex++)
				{
					SpriteLink link = links[linkIndex];
					if (link.File < 0 || link.File >= collection.Dependencies.Count || collection.Dependencies[link.File] is not { } target) continue;
					AssetCollection textureCollection; long texturePath;
					bool atlasLink = link.AtlasKeyIndex >= 0;
					if (atlasLink)
					{
						if (target.TryGetAsset(link.Path) is not DeferredObject atlas || !atlas.Shape.Atlas || atlas.Projection is not { } fields) continue;
						atlas.AtlasTextures ??= AtlasIndex(fields);
						if (!atlas.AtlasTextures.TryGetValue(atlasKeys![link.AtlasKeyIndex], out AtlasEntry pointer)
							|| pointer.FileId < 0 || pointer.FileId >= target.Dependencies.Count
							|| target.Dependencies[pointer.FileId] is not { } foundCollection) continue;
						textureCollection = foundCollection; texturePath = pointer.PathId;
						spriteFacts![linkIndex].Source = pointer.Geometry;
					}
					else
					{
						textureCollection = target; texturePath = link.Path;
					}
					if (!factory.TryTextureInfo(textureCollection, texturePath,
						out long textureSize, out int textureWidth, out int textureHeight)) continue;
					SpriteFacts resolved = spriteFacts![linkIndex];
					resolved.SourceCollection = textureCollection; resolved.SourcePath = texturePath;
					resolved.SourceWidth = textureWidth; resolved.SourceHeight = textureHeight; resolved.Resolved = true;
					spriteFacts[linkIndex] = resolved;
					headers[link.Row].Reveal(); headers[link.Row].Extension = "png";
					headers[link.Row].Size = atlasLink ? 0 : textureSize;
				}
			}
			public void SuppressStructuralDuplicates(HashSet<SpriteKey> seen, HashSet<AssetCollection> selected)
			{
				if (spriteFacts is null) return;
				for (int index = 0; index < nextLink; index++)
				{
					SpriteFacts facts = spriteFacts[index];
					if (TryCanonicalizeFullImage(index, facts, selected, seen)) continue;
					if (facts.Source.SettingsRaw == 64u) continue;
					if (!facts.Comparable) continue;
					if (!seen.Add(facts.Key))
					{
						headers[links[index].Row].Hide();
						continue;
					}
					HideFullTextureCounterpart(facts, selected);
				}
			}
			private void HideFullTextureCounterpart(SpriteFacts facts, HashSet<AssetCollection> selected)
			{
				// SuppressStructuralDuplicates calls this only for a resolved, comparable Sprite.
				AssetCollection source = facts.SourceCollection!;
				if (!selected.Contains(source)) return;
				// A mixed Eager/metadata-only factory resolves an eager source without a compact owner.
				if (!factory.compactCollections.TryGetValue(source, out CompactCollection? owner)) return;
				if (!facts.IsFullTexture(facts.SourceWidth, facts.SourceHeight)) return;
				owner!.HideTexture(facts.SourcePath);
			}
			private bool TryCanonicalizeFullImage(int linkIndex, SpriteFacts facts, HashSet<AssetCollection> selected,
				HashSet<SpriteKey> seen)
			{
				if (!facts.Resolved || !facts.Source.Present || !facts.Canvas.Present) return false;
				if (facts.Source.SettingsRaw is not (2u or 64u)
					|| !SpriteFactsReader.IsUnitDownscale(facts.Source.Downscale)) return false;
				// Resolved facts carry the actual source identity chosen above, and only a selected
				// collection may have its texture hidden by this projection.
				AssetCollection source = facts.SourceCollection!;
				if (!selected.Contains(source)) return false;
				// A mixed Eager/metadata-only factory resolves an eager source without a compact owner.
				if (!factory.compactCollections.TryGetValue(source, out CompactCollection? owner)) return false;
				int width = facts.SourceWidth;
				int height = facts.SourceHeight;
				bool tight = facts.Source.SettingsRaw == 64u;
				SpriteFacts normalized = facts;
				normalized.Source = facts.Source with { SettingsRaw = 2u, Downscale = SpriteFactsReader.Bits(1f) };
				if (!normalized.IsFullTexture(width, height)) return false;
				if (tight)
				{
					// Missing, floating-point or non-four metadata cannot pass SpriteQuadProof.
					// Refuse before Shape.QuadTraversal or the second object read is created.
					if (!links[linkIndex].QuadVertexCountFour) return false;
					// SpriteQuadProof enforces the tighter 8,388,608-pixel proof bound.
					if (!TryProveQuad(links[linkIndex].Row, width, height)) return false;
				}
				owner!.HideTexture(facts.SourcePath);
				if (seen.Add(normalized.Key)) return true;
				headers[links[linkIndex].Row].Hide();
				return true;
			}
			private bool TryProveQuad(int sprite, int width, int height)
			{
				PayloadRow row = rows[sprite];
				Prototype prototype = prototypes[row.Prototype];
				Shape shape = prototype.Shape;
				UnityProjection.State traversal = shape.QuadTraversal!;
				try
				{
					// Add records a Sprite only after projecting it with a template, and a compact table
					// admits only borrowed descriptors; IsFullTexture already proved both dimensions
					// positive before this tight-mesh check runs.
					ObjectInfo data = Descriptor(sprite);
					Stream source = data.BorrowedSource!;
					UnityProjection fields = UnityProjection.ReadBorrowed(source, data.DataOffset, data.DataSize,
						shape.Template!, collection.EndianType == EndianType.BigEndian, traversal);
					return SpriteQuadProof.TryProve(fields, source, data.DataOffset, collection.Version,
						collection.EndianType, width, height);
				}
				catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException
					or OverflowException or NotSupportedException)
				{
					return false;
				}
				finally
				{
					traversal.Scratch?.ClearScratch();
				}
			}
			public void ReleaseSpriteScratch()
			{
				links = []; atlasKeys = null; spriteFacts = null; textureSizes = null;
			}
			public void EndOperation(bool capturePaths)
			{
				if (capturePaths)
				{
					foreach (var pair in identities)
					{
						IUnityObjectBase identity = pair.Value;
						if (identity.OriginalPath is not null || identity.OverridePath is not null || identity.AssetBundleName is not null || identity.MainAsset is not null)
							paths[Find(pair.Key)] = new(identity.OriginalPath, identity.OverridePath, identity.AssetBundleName, identity.MainAsset);
					}
				}
				identities.Clear();
				if (capturePaths) identities.TrimExcess();
			}
			public void FreezeHeaders()
			{
				prototypeIndices.Clear(); prototypeIndices.TrimExcess();
			}
			public ReadOnlySpan<byte> Name(int index) => factory.names.WrittenSpan.Slice(headers[index].NameOffset, headers[index].NameLength);
			public UnityEntryMetadata Metadata(int index, int collectionIndex)
			{
				string? original = paths.TryGetValue(index, out PathMetadata metadata) ? metadata.Original : null;
				return new(prototypes[rows[index].Prototype].Shape.Kind!.Value, collectionIndex, rows[index].Path,
					headers[index].Size, original ?? string.Empty, headers[index].Extension);
			}
			public int VisibleCount => headers.Count(header => header.Visible);
			public int[]? CreateVisibleRowMap()
			{
				int visible = VisibleCount;
				if (visible == rows.Length) return null;
				int[] map = new int[visible];
				int next = 0;
				for (int i = 0; i < rows.Length; i++)
				{
					if (!headers[i].Visible) continue;
					map[next++] = i;
				}
				return map;
			}
			public IUnityObjectBase? TryGetAsset(long pathID)
			{
				int index = Find(pathID); if (index < 0) return null;
				if (identities.TryGetValue(pathID, out IUnityObjectBase? value)) return value;
				PayloadRow row = rows[index]; Prototype prototype = prototypes[row.Prototype];
				DeferredObject result = new(new(collection, row.Path, prototype.ClassID),
					Descriptor(index), prototype.References, prototype.Shape, factory);
				Header header = headers[index];
				result.CaptureName(Encoding.UTF8.GetString(Name(index))); result.CaptureResourceMetadata(header.Size, header.Extension);
				if (paths.TryGetValue(index, out PathMetadata metadata))
				{
					result.OriginalPath = metadata.Original; result.OverridePath = metadata.Override;
					result.AssetBundleName = metadata.Bundle; result.MainAsset = metadata.Main;
				}
				// Cleanup registration may allocate; publish the identity only after it succeeds.
				factory.touchedResolvers.Add(this); identities.Add(pathID, result); return result;
			}
			public void ReplaceAsset(IUnityObjectBase replacement) => identities[replacement.PathID] = replacement;
		}

		private readonly record struct CatalogueRow(int Collection, int Row);
		private readonly record struct CatalogueSegment(int Collection, int Start, int CompactVisible, int LegacyStart);

		private sealed class CompactGraph : IUnityGraph, ICompactUnityGraph, IReadOnlyList<UnityCatalogEntry>
		{
			private readonly GameBundle root;
			private readonly FileBase selected;
			private readonly IReadOnlyList<FileBase> owned;
			private readonly IReadOnlyList<SmartStream> roots;
			private readonly LazyAssetFactory factory;
			private readonly IReadOnlyList<SerializedAssetCollection> selectedCollections;
			private readonly CatalogueSegment[] segments;
			private readonly int[] segmentStarts;
			private readonly int[]?[] visibleRows;
			private readonly List<UnityCatalogEntry> legacyEntries = [];
			private readonly int count;
			public IReadOnlyList<UnityCatalogEntry> Entries => this;
			public int Count => count;
			public UnityCatalogEntry this[int index]
			{
				get
				{
					CatalogueRow row = Resolve(index);
					if (row.Row < 0) return legacyEntries[-row.Row - 1];
					CompactCollection owner = Owner(row);
					UnityEntryMetadata metadata = owner.Metadata(row.Row, row.Collection);
					return new(index, metadata.Kind, metadata.CollectionIndex, metadata.PathId,
						Encoding.UTF8.GetString(owner.Name(row.Row)), metadata.SourceSize, metadata.OriginalPath, metadata.OutputExtension);
				}
			}
			private CompactCollection Owner(CatalogueRow row) => factory.compactCollections[selectedCollections[row.Collection]];
			private CatalogueRow Resolve(int index)
			{
				if ((uint)index >= (uint)count) throw new IndexOutOfRangeException();
				int found = Array.BinarySearch(segmentStarts, index);
				CatalogueSegment segment = segments[found >= 0 ? found : ~found - 1];
				int local = index - segment.Start;
				if (local < segment.CompactVisible)
				{
					int row = visibleRows[segment.Collection] is { } map ? map[local] : local;
					return new(segment.Collection, row);
				}
				return new(segment.Collection, -(segment.LegacyStart + local - segment.CompactVisible) - 1);
			}
			private ReadOnlySpan<byte> Name(CatalogueRow row) => Owner(row).Name(row.Row);
			public UnityEntryMetadata Metadata(int index)
			{
				CatalogueRow row = Resolve(index);
				if (row.Row >= 0) return Owner(row).Metadata(row.Row, row.Collection);
				UnityCatalogEntry legacy = legacyEntries[-row.Row - 1];
				return new(legacy.Kind, legacy.CollectionIndex, legacy.PathId, legacy.SourceSize, legacy.OriginalPath, legacy.OutputExtension);
			}
			public int NameLength(int index)
			{
				CatalogueRow row = Resolve(index);
				return row.Row >= 0 ? Encoding.UTF8.GetCharCount(Name(row)) : legacyEntries[-row.Row - 1].Name.Length;
			}
			public void CopyName(int index, Span<char> destination)
			{
				CatalogueRow row = Resolve(index);
				if (row.Row >= 0) Encoding.UTF8.GetChars(Name(row), destination);
				else legacyEntries[-row.Row - 1].Name.AsSpan().CopyTo(destination);
			}
			public IEnumerator<UnityCatalogEntry> GetEnumerator()
			{
				for (int i = 0; i < Count; i++) yield return this[i];
			}
			IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
			public IReadOnlyList<IUnityGraphCollection> Collections { get; }
			private sealed class CollectionView(AssetCollection collection) : IUnityGraphCollection
			{
				public string Identity => collection.Name;
				public IReadOnlyList<IProbeAsset> Assets => Array.Empty<IProbeAsset>();
			}
			public CompactGraph(LazyAssetFactory factory, GameBundle root, FileBase selected,
				IReadOnlyList<SerializedAssetCollection> collections, IReadOnlyList<FileBase> owned, IReadOnlyList<SmartStream> roots)
			{
				this.root = root; this.selected = selected; this.owned = owned; this.roots = roots; this.factory = factory; selectedCollections = collections;
				Collections = collections.Select(collection => (IUnityGraphCollection)new CollectionView(collection)).ToArray();
				factory.ResolveCompactCollections(collections);
				factory.EndCompactOperation(true);
				IReadOnlyList<IProbeAsset>[] legacy = collections.Select(factory.Describe).ToArray();
				List<CatalogueSegment> found = new(collections.Count);
				int[]?[]? hiddenRows = null;
				for (int i = 0; i < collections.Count; i++)
				{
					int compactVisible = 0;
					if (factory.compactCollections.TryGetValue(collections[i], out CompactCollection? rows))
					{
						compactVisible = rows.VisibleCount;
						if (rows.CreateVisibleRowMap() is { } map)
						{
							hiddenRows ??= new int[]?[collections.Count];
							hiddenRows[i] = map;
						}
					}
					int start = count;
					int legacyStart = legacyEntries.Count;
					int next = checked(start + compactVisible);
					foreach (IProbeAsset asset in legacy[i])
					{
						UnityCatalogEntry? entry = UnitySession.Describe(asset, next, i);
						if (entry is null) continue;
						legacyEntries.Add(entry.Value);
						next++;
					}
					int segmentCount = checked(compactVisible + legacyEntries.Count - legacyStart);
					if (segmentCount > 0) found.Add(new(i, start, compactVisible, legacyStart));
					count = checked(count + segmentCount);
				}
				segments = found.ToArray();
				segmentStarts = segments.Select(segment => segment.Start).ToArray();
				visibleRows = hiddenRows ?? new int[]?[collections.Count];
				foreach (CompactCollection rows in factory.compactCollections.Values) rows.FreezeHeaders();
				factory.ReleaseProjectedMetadata(root.FetchAssets());
			}
			public byte[]? Export(int index)
			{
				try
				{
					UnityEntryMetadata entry = Metadata(index);
					AssetCollection collection = selectedCollections[entry.CollectionIndex];
					IUnityObjectBase? target = collection.TryGetAsset(entry.PathId);
					if (target is DeferredObject identity) return factory.Export(identity);
					if (target is null) return null;
					IProbeAsset asset = Wrap([target]).Single();
					return asset switch { IProbeTexture texture => texture.RenderPng(), IProbeText text => text.ReadData(), IProbeResource resource => resource.ExportData(), _ => null };
				}
				finally { factory.EndCompactOperation(false); }
			}
			public void Dispose()
			{
				root.Dispose(); DisposeOwned(selected); foreach (FileBase file in owned) DisposeOwned(file); DisposeRoots(roots);
			}
		}
	}
}
