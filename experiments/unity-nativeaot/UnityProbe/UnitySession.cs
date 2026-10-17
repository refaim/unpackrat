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
	private readonly UnityCatalogEntry[] entries;
	private readonly Candidate[] candidates;
	private bool disposed;

	private UnitySession(IUnityGraph graph, UnityCatalogEntry[] entries, Candidate[] candidates)
	{
		this.graph = graph;
		this.entries = entries;
		this.candidates = candidates;
	}

	/// <summary>Opens the selected file or bundle at <paramref name="path"/> for on-demand export.</summary>
	public static UnitySession Open(string path) => FromGraph(UnitySource.OpenGraph(path));

	/// <summary>Builds a catalogue over an already-loaded project-owned graph.</summary>
	public static UnitySession FromGraph(IUnityGraph graph)
	{
		ArgumentNullException.ThrowIfNull(graph);

		List<UnityCatalogEntry> entries = new();
		List<Candidate> candidates = new();
		for (int collectionIndex = 0; collectionIndex < graph.Collections.Count; collectionIndex++)
		{
			foreach (IProbeAsset asset in graph.Collections[collectionIndex].Assets)
			{
				Candidate? candidate = Candidate.Describe(asset);
				if (candidate is null)
				{
					continue;
				}

				entries.Add(new UnityCatalogEntry(entries.Count, candidate.Kind, collectionIndex, candidate.PathId, candidate.Name, candidate.SourceSize, candidate.OriginalPath, candidate.OutputExtension));
				candidates.Add(candidate);
			}
		}

		return new UnitySession(graph, entries.ToArray(), candidates.ToArray());
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
		if (disposed || index < 0 || index >= candidates.Length)
		{
			return false;
		}

		byte[]? payload = candidates[index].Materialise();
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

	/// <summary>One catalogued asset together with the conversion deferred until export.</summary>
	private sealed record Candidate(UnityEntryKind Kind, long PathId, string Name, long SourceSize, string OriginalPath, string OutputExtension, Func<byte[]?> Materialise)
	{
		public static Candidate? Describe(IProbeAsset asset) => asset switch
		{
			IProbeTexture texture => new Candidate(UnityEntryKind.Texture, texture.PathId, texture.Name, KnownSourceSize(texture.SourceSize), texture.OriginalPath ?? string.Empty, OutputExtensions.TexturePng, texture.RenderPng),
			IProbeText text => new Candidate(UnityEntryKind.Text, text.PathId, text.Name, KnownSourceSize(text.SourceSize), text.OriginalPath ?? string.Empty, OutputExtensions.TextBin, text.ReadData),
			IProbeResource resource => new Candidate(resource.Kind, resource.PathId, resource.Name, KnownSourceSize(resource.SourceSize), resource.OriginalPath ?? string.Empty, resource.OutputExtension, resource.ExportData),
			_ => null,
		};

		/// <summary>
		/// Clamps a declared length to the immutable catalogue contract: a negative value (which a
		/// test provider may report) is normalised to the unknown sentinel zero, and a real
		/// non-negative length is kept unchanged.
		/// </summary>
		private static long KnownSourceSize(long declared) => declared < 0 ? 0 : declared;
	}
}
