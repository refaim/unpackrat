using System;
using System.Collections.Generic;
using System.IO;

namespace Unpackrat.UnityProbe;

/// <summary>
/// Disposable catalogue over one selected Unity file or bundle. Enumerating <see cref="Entries"/>
/// never decodes or copies an asset; a conversion happens only for the entry named in
/// <see cref="Export"/>.
/// </summary>
internal sealed class UnitySession : IDisposable
{
	private readonly IUnityGraph graph;
	private readonly IReadOnlyList<UnityCatalogEntry> entries;
	private readonly IProbeAsset[] exportTargets;
	private bool disposed;

	private UnitySession(IUnityGraph graph, IReadOnlyList<UnityCatalogEntry> entries, IProbeAsset[] exportTargets)
	{
		this.graph = graph;
		this.entries = entries;
		this.exportTargets = exportTargets;
	}

	/// <summary>Opens the selected file or bundle at <paramref name="path"/> for on-demand export.</summary>
	public static UnitySession Open(string path) => FromGraph(UnitySource.OpenGraph(path, readFile: null, compact: true));

	/// <summary>Builds a catalogue over an already-loaded project-owned graph.</summary>
	public static UnitySession FromGraph(IUnityGraph graph)
	{
		ArgumentNullException.ThrowIfNull(graph);
		if (graph is ICompactUnityGraph compact) return new(graph, compact.Entries, []);

		// Graph views are stable snapshots. Count eligibility without touching metadata so each
		// immutable row is captured once directly into its final array, without growing/copying it.
		int count = 0;
		foreach (IUnityGraphCollection collection in graph.Collections)
		{
			foreach (IProbeAsset asset in collection.Assets)
			{
				if (asset is IProbeTexture or IProbeText or IProbeResource) count = checked(count + 1);
			}
		}
		UnityCatalogEntry[] entries = new UnityCatalogEntry[count];
		IProbeAsset[] exportTargets = new IProbeAsset[count];
		int index = 0;
		for (int collectionIndex = 0; collectionIndex < graph.Collections.Count; collectionIndex++)
		{
			foreach (IProbeAsset asset in graph.Collections[collectionIndex].Assets)
			{
				UnityCatalogEntry? entry = Describe(asset, index, collectionIndex);
				if (entry is null)
				{
					continue;
				}

				entries[index] = entry.Value;
				exportTargets[index] = asset;
				index++;
			}
		}

		return new UnitySession(graph, entries, exportTargets);
	}

	internal int Count => entries.Count;
	internal UnityEntryMetadata Metadata(int index)
	{
		if (graph is ICompactUnityGraph compact) return compact.Metadata(index);
		UnityCatalogEntry row = entries[index];
		return new(row.Kind, row.CollectionIndex, row.PathId, row.SourceSize, row.OriginalPath, row.OutputExtension);
	}
	internal int NameLength(int index) => graph is ICompactUnityGraph compact ? compact.NameLength(index) : entries[index].Name.Length;
	internal void CopyName(int index, Span<char> destination)
	{
		if (graph is ICompactUnityGraph compact) compact.CopyName(index, destination);
		else entries[index].Name.AsSpan().CopyTo(destination);
	}

	/// <summary>The catalogue in stable session order.</summary>
	public IReadOnlyList<UnityCatalogEntry> Entries => entries;

	/// <summary>
	/// Materialises exactly one catalogued entry into <paramref name="destination"/>. Returns
	/// <see langword="false"/> for an out-of-range index, a closed session or an entry that cannot be
	/// exported, so callers get one testable failure path instead of an exception.
	/// </summary>
	public bool Export(int index, Stream destination)
	{
		ArgumentNullException.ThrowIfNull(destination);
		if (disposed || index < 0 || index >= entries.Count)
		{
			return false;
		}

		if (graph is ICompactUnityGraph compact)
		{
			byte[]? data = compact.Export(index);
			if (data is null) return false;
			destination.Write(data);
			return true;
		}
		IProbeAsset asset = exportTargets[index];
		// Describe admits only these interfaces. Preserve its precedence for overlapping views;
		// the remaining accepted target is a resource, with no per-entry delegate or metadata copy.
		byte[]? payload = asset switch
		{
			IProbeTexture texture => texture.RenderPng(),
			IProbeText text => text.ReadData(),
			_ => ((IProbeResource)asset).ExportData(),
		};
		if (payload is null)
		{
			return false;
		}

		destination.Write(payload, 0, payload.Length);
		return true;
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}

		disposed = true;
		graph.Dispose();
	}

	/// <summary>Captures immutable metadata once, without allocating an export wrapper or delegate.</summary>
	internal static UnityCatalogEntry? Describe(IProbeAsset asset, int index, int collectionIndex) => asset switch
	{
		IProbeTexture texture => new(index, UnityEntryKind.Texture, collectionIndex, texture.PathId, texture.Name, KnownSourceSize(texture.SourceSize), texture.OriginalPath ?? string.Empty, OutputExtensions.TexturePng),
		IProbeText text => new(index, UnityEntryKind.Text, collectionIndex, text.PathId, text.Name, KnownSourceSize(text.SourceSize), text.OriginalPath ?? string.Empty, OutputExtensions.TextBin),
		IProbeResource resource => new(index, resource.Kind, collectionIndex, resource.PathId, resource.Name, KnownSourceSize(resource.SourceSize), resource.OriginalPath ?? string.Empty, resource.OutputExtension),
		_ => null,
	};

	/// <summary>
	/// Clamps a declared length to the immutable catalogue contract: a negative value (which a
	/// test provider may report) is normalised to the unknown sentinel zero, and a real
	/// non-negative length is kept unchanged.
	/// </summary>
	private static long KnownSourceSize(long declared) => declared < 0 ? 0 : declared;
}
