using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams;
using AssetRipper.IO.Files.Streams.Smart;
using AssetRipper.Processing;
using AssetRipper.Processing.Configuration;
using AssetRipper.Processing.Scenes;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_128;
using AssetRipper.SourceGenerated.Classes.ClassID_152;
using AssetRipper.SourceGenerated.Classes.ClassID_329;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Extensions;

namespace Unpackrat.UnityProbe;

/// <summary>
/// Partial adapter from the AssetRipper asset graph to <see cref="IProbeAsset"/>. These adapter files
/// confine AssetRipper types; everything upstream stays third-party free.
/// </summary>
internal static partial class UnitySource
{
	private sealed class OtherAsset : IProbeAsset
	{
		public static readonly OtherAsset Instance = new();
	}

	private sealed class TextureAsset(ITexture2D texture) : IProbeTexture
	{
		public long PathId => texture.PathID;
		public string Version => texture.Collection.Version.ToString();
		public string Name => texture.Name.ToString();
		public string? OriginalPath => texture.OriginalPath;

		public long SourceSize
		{
			get
			{
				// Metadata only: the inline image data length wins when it is present. Otherwise the
				// declared length is the streamed resource size, but only when the descriptor actually
				// names a resource; an unnamed or absent one is unknown. Neither branch renders the
				// texture or reads the resS body, so querying the length never converts the asset.
				return DeclaredTextureSourceSize(texture);
			}
		}

		public byte[]? RenderPng()
		{
			return RenderTexturePng(texture);
		}
	}

	private sealed class TextAssetAdapter(ITextAsset text) : IProbeText
	{
		public long PathId => text.PathID;
		public string Version => text.Collection.Version.ToString();
		public string Name => text.Name.ToString();
		public string? OriginalPath => text.OriginalPath;

		// Metadata only: the declared payload length is the script data span length, read without
		// copying the bytes.
		public long SourceSize => text.Script_C49.Data.Length;

		public byte[]? ReadData()
		{
			// It is expected that the source payload could be enormous. Read the span length and
			// refuse to copy an oversized asset rather than allocating an unbounded array.
			ReadOnlySpan<byte> data = text.Script_C49.Data;
			if (data.Length > Budgets.MaxTextBytes)
			{
				return null;
			}

			return data.ToArray();
		}
	}

	/// <summary>
	/// Decides from metadata alone whether a texture is a provably empty runtime placeholder, so a
	/// cleared font atlas or similar does not surface as an extractable PNG that can never render.
	/// </summary>
	/// <remarks>
	/// A texture is empty only when every field needed to draw it is absent: no dimension, no inline
	/// image bytes and no declared external payload. A positive dimension, any inline byte, or any
	/// declared streamed descriptor keeps the texture visible even when the payload is missing,
	/// corrupt or oversized. Zero dimensions alone, an unknown <c>SourceSize</c>, or a non-zero
	/// declared length are never taken as proof of emptiness.
	/// </remarks>
	private static bool IsProvablyEmptyTexture(ITexture2D texture)
	{
		if (texture.Width_C28 != 0 || texture.Height_C28 != 0)
		{
			return false;
		}

		if (texture.ImageData_C28.LongLength != 0)
		{
			return false;
		}

		return !HasDeclaredExternalData(texture);
	}

	/// <summary>
	/// Reports whether the optional streamed descriptor declares an external payload: a non-zero
	/// size or a non-empty resource path. An absent descriptor (old versions carry none) is no
	/// declaration at all.
	/// </summary>
	private static bool HasDeclaredExternalData(ITexture2D texture)
	{
		return texture.StreamData_C28 is { } streamData
			&& (streamData.Size != 0 || streamData.IsSet());
	}

	/// <summary>
	/// The declared source payload length of a texture, resolved from metadata alone: the inline
	/// image data length when present, otherwise the declared streamed resource size, or zero when
	/// it is unknown. Querying it never renders the texture or reads the resS body.
	/// </summary>
	private static long DeclaredTextureSourceSize(ITexture2D texture)
	{
		long inlineLength = texture.ImageData_C28.LongLength;
		if (inlineLength != 0)
		{
			return inlineLength;
		}

		return texture.StreamData_C28 is { } streamData && streamData.IsSet()
			? DeclaredSourceSize.Narrow(streamData.Size)
			: 0;
	}

	/// <summary>
	/// Reports whether a texture's declared source payload is within the copy cap, using the same
	/// inline-first choice the converter makes: only an empty inline buffer defers to the optional
	/// streamed size. Read from metadata alone, before the converter resolves or copies anything.
	/// </summary>
	private static bool IsTextureSourceWithinBudget(ITexture2D texture)
	{
		long inlineLength = texture.ImageData_C28.LongLength;
		if (inlineLength > Budgets.MaxTextureSourceBytes)
		{
			return false;
		}

		return inlineLength != 0
			|| texture.StreamData_C28 is not { } streamData
			|| streamData.Size <= Budgets.MaxTextureSourceBytes;
	}

	/// <summary>
	/// Validates a texture from declared metadata alone and decodes it through the pinned
	/// converter. A hostile dimension or an oversized source payload is refused before the
	/// converter allocates a bitmap or copies a byte.
	/// </summary>
	internal static bool TryDecodeTexture(ITexture2D texture, out DirectBitmap bitmap)
	{
		bitmap = DirectBitmap.Empty;
		if (!Budgets.TryGetTextureBytes(texture.Width_C28, texture.Height_C28, texture.ImageCount_C28, out _))
		{
			return false;
		}

		if (!IsTextureSourceWithinBudget(texture))
		{
			return false;
		}

		return TextureConverter.TryConvertToBitmap(texture, out bitmap);
	}

	/// <summary>Renders a texture through the pinned decoder and writer, or null when it is refused.</summary>
	private static byte[]? RenderTexturePng(ITexture2D texture)
	{
		if (!TryDecodeTexture(texture, out DirectBitmap bitmap))
		{
			return null;
		}

		using MemoryStream stream = new();
		bitmap.SaveAsPng(stream);
		return stream.ToArray();
	}

	/// <summary>
	/// Loads the Unity game at <paramref name="paths"/> through the production import pipeline and
	/// exports a bounded set of resources into <paramref name="outputDirectory"/>.
	/// </summary>
	public static int LoadAndExport(IReadOnlyList<string> paths, string outputDirectory)
	{
		CoreConfiguration configuration = new()
		{
			ImportSettings = new ImportSettings
			{
				ScriptContentLevel = ScriptContentLevel.Level0,
			},
		};

		using GameStructure structure = GameStructure.Load(paths, LocalFileSystem.Instance, configuration);
		return ResourceExporter.ExportAssets(Wrap(structure.FileCollection.FetchAssets()), outputDirectory);
	}

	/// <summary>Maps each AssetRipper asset to the matching <see cref="IProbeAsset"/> view.</summary>
	public static IEnumerable<IProbeAsset> Wrap(IEnumerable<IUnityObjectBase> assets)
	{
		foreach (IUnityObjectBase asset in assets)
		{
			if (asset is ITexture2D texture)
			{
				yield return IsProvablyEmptyTexture(texture) ? OtherAsset.Instance : new TextureAsset(texture);
			}
			else if (asset is ITextAsset text)
			{
				yield return new TextAssetAdapter(text);
			}
			else if (asset is IFont font)
			{
				yield return font.FontData.Length == 0 ? OtherAsset.Instance : new FontAsset(font);
			}
			else if (asset is IVideoClip clip)
			{
				yield return HasDeclaredVideoData(clip) ? new VideoClipAsset(clip) : OtherAsset.Instance;
			}
			else if (asset is IMovieTexture movie)
			{
				yield return movie.MovieData_C152 is { Length: > 0 } ? new MovieTextureAsset(movie) : OtherAsset.Instance;
			}
			else if (asset is IAudioClip audio)
			{
				yield return (IProbeAsset?)AudioClipAsset.TryCreate(audio) ?? OtherAsset.Instance;
			}
			else if (asset is IMesh mesh)
			{
				yield return HasMeshGeometry(mesh) ? new MeshAsset(mesh) : OtherAsset.Instance;
			}
			else if (asset is ISprite sprite)
			{
				// A sprite is catalogued only when it resolves a render source that is not a
				// provably empty placeholder, so it can never be exported as an unrelated texture.
				yield return TryResolveSpriteSource(sprite, out SpriteSource spriteSource) && !IsProvablyEmptyTexture(spriteSource.Texture)
					? new SpriteAsset(sprite, spriteSource)
					: OtherAsset.Instance;
			}
			else
			{
				yield return OtherAsset.Instance;
			}
		}
	}

	/// <summary>
	/// Loads exactly one selected file or bundle into a project-owned graph. The returned graph's
	/// collections are the selected set only; declared dependencies may be loaded for reference
	/// resolution but never enter it. Throws when the selected input is not a supported Unity file.
	/// </summary>
	public static IUnityGraph OpenGraph(string path) => OpenGraph(path, readFile: null);

	/// <summary>
	/// Loader core with a test seam: <paramref name="readFile"/> replaces the on-disk read (and
	/// parse) of the selected input and of every declared dependency, so a failure such as
	/// <see cref="OutOfMemoryException"/> can be injected at the reader boundary and observed to
	/// escape unchanged. It is never part of the exported surface.
	/// </summary>
	internal static IUnityGraph OpenGraph(string path, Func<string, FileBase>? readFile, AssetFactoryBase? fullReader = null, bool compact = false)
	{
		ArgumentNullException.ThrowIfNull(path);

		// Root input streams opened for the selected file and its dependencies own the underlying
		// file handles. They are released last, after the parsed tree that reads through them; other
		// orders leave the handles open because the entry streams are slices that only free the
		// shared stream when the outer root is disposed last.
		List<SmartStream> roots = [];
		Func<string, FileBase> reader = readFile ?? (p => ReadFile(p, roots, compact ? FileReadMode.CompactMetadataOnly : FileReadMode.MetadataOnly));

		FileBase selected;
		try
		{
			selected = LoadSelected(path, reader);
		}
		catch
		{
			DisposeRoots(roots);
			throw;
		}

		string directory = DependencyDirectory(path);
		IAssemblyManager manager = new BaseManager(static _ => { });
		LazyAssetFactory factory = new(fullReader ?? new UnityAssetFactory(new GameAssetFactory(manager)), compact);
		GameBundle root = new() { ResourceProvider = new SiblingResourceProvider(directory) };
		List<FileBase> owned = [];
		try
		{
			Selection selection = SelectSelected(root, factory, selected);
			LoadDeclaredDependencies(root, factory, directory, selection, reader, owned);

			int selectedCollections = selection.Collections.Count;
			root.InitializeAllDependencyLists();
			// AssetRipper's own path processor records each asset's original resource or bundle
			// container path as metadata; DirectExport preserves the container hierarchy without
			// synthesising per-type directories. It only populates IUnityObjectBase.OriginalPath,
			// and it never decodes, converts or exports anything.
			new OriginalPathProcessor(BundledAssetsExportMode.DirectExport)
				.Process(new GameData(root, root.GetMaxUnityVersion(), manager, null));
			int dependencyCollections = root.FetchAssetCollections().Count() - selectedCollections;
			return compact ? factory.BuildCompactGraph(root, selected, selection.Collections, owned, roots)
				: new UnityGraph(root, selected, selection.Collections, dependencyCollections, owned, roots, factory);
		}
		catch (Exception exception)
		{
			root.Dispose();
			DisposeOwned(selected);
			DisposeAll(owned);
			DisposeRoots(roots);
			if (exception is OutOfMemoryException)
			{
				// A real memory shortage is not an input-format failure; it must reach the caller without
				// being relabelled, but only after every resource this attempt acquired has been released.
				throw;
			}

			throw new UnityLoadException($"Failed to build the Unity graph for '{path}'.", exception);
		}
	}

	/// <summary>
	/// Reads and parses one input file, owning the opened stream until the returned file has taken
	/// its own reference. The upstream single-shot loader leaves a reference behind; opening the
	/// stream here lets the outer reference be released so an unreadable or fallback resource does
	/// not keep a file handle open for the lifetime of the process.
	/// </summary>
	internal static FileBase ReadFile(string path)
		=> ReadFile(path, roots: null);

	/// <summary>
	/// Reads and parses one input file for the loader. When a <paramref name="roots"/> collector is
	/// supplied, the opened root stream is added to it so the caller can release it after the parsed
	/// tree it feeds has been disposed; that deferred release is what actually closes the handle,
	/// because the entry streams the parser derives are slices that only free the shared stream when
	/// the root is disposed last. Without a collector the stream is released immediately.
	/// </summary>
	private static FileBase ReadFile(string path, List<SmartStream>? roots)
		=> ReadFile(path, roots, FileReadMode.MetadataOnly);

	private static FileBase ReadFile(string path, List<SmartStream>? roots, FileReadMode mode)
	{
		SmartStream stream = SmartStream.OpenReadMulti(path, LocalFileSystem.Instance);
		if (roots is null)
		{
			using (stream)
			{
				return SchemeReader.ReadFile(stream, MultiFileStream.GetFilePath(path), MultiFileStream.GetFileName(path), mode);
			}
		}

		roots.Add(stream);
		return SchemeReader.ReadFile(stream, MultiFileStream.GetFilePath(path), MultiFileStream.GetFileName(path), mode);
	}

	/// <summary>Releases the root input streams once the tree that reads through them is gone.</summary>
	internal static void DisposeRoots(IReadOnlyList<SmartStream> roots)
	{
		for (int i = roots.Count - 1; i >= 0; i--)
		{
			roots[i].Dispose();
		}
	}

	private static void DisposeAll(IReadOnlyList<FileBase> files)
	{
		foreach (FileBase file in files)
		{
			DisposeOwned(file);
		}
	}

	/// <summary>
	/// Releases a file this adapter owns together with every child a bundle container holds. The
	/// upstream container types never dispose their own children, so a plain dispose would leave
	/// each internal serialized file, nested container, resolved resource and failed entry of the
	/// structure we built open; walking the tree keeps the adapter's ownership contract for it. The
	/// walk is iterative so an arbitrarily deep container tree cannot exhaust the stack.
	/// </summary>
	internal static void DisposeOwned(FileBase file)
	{
		Stack<FileBase> pending = new();
		pending.Push(file);
		while (pending.Count > 0)
		{
			FileBase current = pending.Pop();
			if (current is FileContainer container)
			{
				foreach (FileBase child in container.AllFiles)
				{
					pending.Push(child);
				}
			}

			current.Dispose();
		}
	}

	/// <summary>
	/// Resolves a streamed resource by name, but only to a plain sibling file inside the selected
	/// input's own directory. Anything with a directory component, an absolute path or a traversal,
	/// and any missing file, yields no resource; a resource found this way is a fresh file-backed
	/// <see cref="ResourceFile"/> handed to the graph, which owns and disposes it.
	/// </summary>
	private sealed class SiblingResourceProvider(string directory) : IResourceProvider
	{
		public ResourceFile? FindResource(string identifier)
		{
			string name = Path.GetFileName(identifier);
			if (name != identifier)
			{
				return null;
			}

			string fullPath = Path.Combine(directory, name);
			if (!File.Exists(fullPath))
			{
				return null;
			}

			return new ResourceFile(fullPath, name, LocalFileSystem.Instance);
		}
	}

	/// <summary>
	/// Reads the selected input and accepts it only when its signature is a serialized asset file or
	/// one of the UnityFS/Raw/Web bundle containers. The scheme reader's resource-file fallback, and
	/// a parse failure, both become an explicit <see cref="UnityLoadException"/>.
	/// </summary>
	private static FileBase LoadSelected(string path, Func<string, FileBase> readFile)
	{
		// The scheme reader both opens and parses the input, so either call may throw. Any file it
		// already produced is released, and the failure becomes an explicit UnityLoadException.
		FileBase? file = null;
		try
		{
			file = readFile(path);
			file.ReadContentsRecursively();
		}
		catch (Exception exception)
		{
			if (file is not null)
			{
				DisposeOwned(file);
			}

			if (exception is OutOfMemoryException)
			{
				throw;
			}

			throw new UnityLoadException($"Failed to read Unity input '{path}'.", exception);
		}

		if (file is not SerializedFile && file is not FileContainer)
		{
			file.Dispose();
			throw new UnityLoadException($"'{path}' is not a serialized Unity file or bundle.");
		}

		return file;
	}

	/// <summary>
	/// Turns the selected file or container into its own collections, returning exactly those
	/// collection references as the catalogue candidates before any dependency is loaded.
	/// </summary>
	private static Selection SelectSelected(GameBundle root, AssetFactoryBase factory, FileBase selected)
	{
		Selection selection = new();
		if (selected is SerializedFile serializedFile)
		{
			selection.Collections.Add(root.AddCollectionFromSerializedFile(serializedFile, factory));
			selection.SerializedFiles.Add(serializedFile);
		}
		else
		{
			FileContainer container = (FileContainer)selected;
			SerializedBundle bundle = SerializedBundle.FromFileContainer(container, factory);
			root.AddBundle(bundle);
			selection.Collections.AddRange(bundle.FetchAssetCollections().OfType<SerializedAssetCollection>());
			selection.SerializedFiles.AddRange(container.FetchSerializedFiles());
		}

		return selection;
	}

	/// <summary>
	/// Loads declared dependencies of the selected serialized files, relative to the selected file's
	/// own directory, into the same root so references can resolve. Dependencies never enter the
	/// catalogue; each distinct name is visited once, every produced file is owned by the caller,
	/// and the number of parsed identifiers is bounded before anything is enqueued or read.
	/// </summary>
	private static void LoadDeclaredDependencies(GameBundle root, AssetFactoryBase factory, string directory, Selection selection, Func<string, FileBase> readFile, List<FileBase> owned)
	{
		Queue<FileIdentifier> pending = new();
		HashSet<string> visited = new();
		foreach (SerializedFile serializedFile in selection.SerializedFiles)
		{
			visited.Add(serializedFile.NameFixed);
			foreach (FileIdentifier identifier in serializedFile.Dependencies)
			{
				Enqueue(pending, identifier);
			}
		}

		string prefix = directory + Path.DirectorySeparatorChar;
		int examined = 0;
		while (pending.Count > 0 && examined < MaxDependencyIdentifiers)
		{
			string name = pending.Dequeue().GetFilePath();
			examined++;
			if (!visited.Add(name))
			{
				continue;
			}

			string dependencyPath = Path.GetFullPath(Path.Combine(directory, name));
			if (!dependencyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (!File.Exists(dependencyPath))
			{
				continue;
			}

			LoadDependency(root, factory, dependencyPath, pending, readFile, owned);
		}
	}

	/// <summary>
	/// Enqueues one pending identifier only while the queue is below the walk bound, so a hostile
	/// file that declares a huge dependency list cannot grow the queue without limit.
	/// </summary>
	private static void Enqueue(Queue<FileIdentifier> pending, FileIdentifier identifier)
	{
		if (pending.Count >= MaxDependencyIdentifiers)
		{
			return;
		}

		pending.Enqueue(identifier);
	}

	/// <summary>
	/// Reads, parses and attaches one declared dependency. A dependency that is present but cannot
	/// be read or parsed is skipped so the selected input stays usable; the produced file is only
	/// kept, and later disposed with the graph, once it has been attached successfully. A memory
	/// shortage is released and rethrown rather than swallowed.
	/// </summary>
	private static void LoadDependency(GameBundle root, AssetFactoryBase factory, string dependencyPath, Queue<FileIdentifier> pending, Func<string, FileBase> readFile, List<FileBase> owned)
	{
		FileBase dependency;
		try
		{
			dependency = readFile(dependencyPath);
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			return;
		}

		try
		{
			dependency.ReadContentsRecursively();
			foreach (SerializedFile serializedFile in AddDependencyFile(root, factory, dependency))
			{
				foreach (FileIdentifier identifier in serializedFile.Dependencies)
				{
					Enqueue(pending, identifier);
				}
			}
		}
		catch (Exception exception)
		{
			DisposeOwned(dependency);
			if (exception is OutOfMemoryException)
			{
				throw;
			}

			return;
		}

		owned.Add(dependency);
	}

	/// <summary>
	/// Adds a loaded dependency file to the graph so references can resolve, returning the internal
	/// serialized files whose own declared dependencies should still be followed.
	/// </summary>
	private static IReadOnlyList<SerializedFile> AddDependencyFile(GameBundle root, AssetFactoryBase factory, FileBase file)
	{
		switch (file)
		{
			case SerializedFile serializedFile:
				root.AddCollectionFromSerializedFile(serializedFile, factory);
				return new[] { serializedFile };
			case FileContainer container:
				root.AddBundle(SerializedBundle.FromFileContainer(container, factory));
				return container.FetchSerializedFiles().ToArray();
			default:
				return Array.Empty<SerializedFile>();
		}
	}

	/// <summary>Canonical directory of the selected file, used to resolve dependencies and resources.</summary>
	private static string DependencyDirectory(string path)
	{
		// The selected path names a file, so its directory is never the null directory of a root.
		return Path.GetDirectoryName(Path.GetFullPath(path))!;
	}

	/// <summary>Bound on dependency identifiers enqueued and parsed for one session.</summary>
	private const int MaxDependencyIdentifiers = 64;

	/// <summary>Selected collections, snapshotted by reference before dependencies are added.</summary>
	private sealed class Selection
	{
		public List<SerializedAssetCollection> Collections { get; } = [];
		public List<SerializedFile> SerializedFiles { get; } = [];
	}
}

/// <summary>
/// Avoids deserializing unused component structures while retaining their identities for the
/// upstream collection's path-ID checks. Raw serialized bytes stay owned by the parsed file;
/// the placeholder neither copies them nor retains a separately parsed script structure.
/// Every other class, including resource subclasses and future classes, uses the pinned reader.
/// </summary>
internal sealed class UnityAssetFactory(AssetFactoryBase inner) : AssetFactoryBase
{
	public override IUnityObjectBase? ReadAsset(AssetInfo assetInfo, ReadOnlyArraySegment<byte> assetData, SerializedType? assetType, IReadOnlyList<SerializedTypeReference> referenceTypes)
	{
		return assetInfo.ClassID == 114 // MonoBehaviour; negative script IDs are normalised upstream.
			? new UnknownObject(assetInfo, [])
			: inner.ReadAsset(assetInfo, assetData, assetType, referenceTypes);
	}
}

/// <summary>Project-owned graph over the selected collections of a loaded root bundle.</summary>
internal sealed class UnityGraph(
	GameBundle root,
	FileBase selected,
	IReadOnlyList<SerializedAssetCollection> selectedCollections,
	int dependencyCollectionCount,
	IReadOnlyList<FileBase> ownedFiles,
	IReadOnlyList<SmartStream> rootStreams,
	UnitySource.LazyAssetFactory factory) : IUnityGraph
{
	public IReadOnlyList<IUnityGraphCollection> Collections { get; } = Build(root, selectedCollections, factory);

	/// <summary>Dependency collections loaded for reference resolution but excluded from the catalog.</summary>
	public int DependencyCollectionCount { get; } = dependencyCollectionCount;

	public void Dispose()
	{
		root.Dispose();
		UnitySource.DisposeOwned(selected);
		foreach (FileBase file in ownedFiles)
		{
			UnitySource.DisposeOwned(file);
		}

		UnitySource.DisposeRoots(rootStreams);
	}

	private static IUnityGraphCollection[] Build(GameBundle root, IReadOnlyList<SerializedAssetCollection> collections, UnitySource.LazyAssetFactory factory)
	{
		UnityGraphCollection[] views = new UnityGraphCollection[collections.Count];
		for (int i = 0; i < collections.Count; i++)
		{
			views[i] = new UnityGraphCollection(collections[i], factory);
		}
		factory.ReleaseProjectedMetadata(root.FetchAssets());
		return views;
	}
}

/// <summary>Project-owned view of one selected collection and its asset views.</summary>
internal sealed class UnityGraphCollection(AssetCollection collection, UnitySource.LazyAssetFactory factory) : IUnityGraphCollection
{
	public string Identity => collection.Name;
	public IReadOnlyList<IProbeAsset> Assets { get; } = factory.Describe(collection);
}
