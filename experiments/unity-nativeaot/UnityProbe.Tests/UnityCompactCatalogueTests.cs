using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityCompactCatalogueTests(ITestOutputHelper output)
{
	private static readonly string[] Extensions = ["png", "bin", "ttf", "mp4", "ogv", "wav", "glb", "png"];

	[Fact]
	public void StoringCatalogueRowsDoesNotAllocateAnObjectPerResource()
	{
		const int count = 8192;
		UnityCatalogEntry[] rows = new UnityCatalogEntry[count];
		FillRows(new UnityCatalogEntry[8]);
		long allocated = GC.GetAllocatedBytesForCurrentThread();
		FillRows(rows);
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		for (int i = 0; i < count; i++) Assert.Equal(Expected(i), rows[i]);
		UnityCatalogEntry snapshot = rows[0];
		rows[0] = rows[0] with { Name = "changed", PathId = -1 };
		Assert.Equal(Expected(0), snapshot);
		Assert.Equal("changed", rows[0].Name);
		Assert.Equal(-1, rows[0].PathId);
		output.WriteLine($"Storing {count} catalogue rows allocated {allocated} bytes.");
		// Storage and shared strings already exist. Permit fixed runtime overhead, never an
		// allocation proportional to row count; this bound is independent of pointer width.
		Assert.True(allocated < 16 * 1024, $"Storing {count} catalogue rows allocated {allocated} bytes; immutable rows must not require per-resource heap objects.");
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void FillRows(UnityCatalogEntry[] rows)
	{
		for (int i = 0; i < rows.Length; i++) rows[i] = Expected(i);
	}

	private static UnityCatalogEntry Expected(int i) => new(i, (UnityEntryKind)(i % 8), i / 4096, i + 1,
		"shared label", i % 3 == 0 ? 0 : i, "Assets/shared", Extensions[i % 8]);

	[Fact]
	public void SessionBuildsCompactSnapshotsInOrderWithoutGrowingRowBuffers()
	{
		const int count = 8192;
		MutableResource[] resources = new MutableResource[count];
		IProbeAsset[] first = new IProbeAsset[count / 2 + 1];
		IProbeAsset[] second = new IProbeAsset[count / 2 + 1];
		first[0] = new FakeAsset();
		second[^1] = new FakeAsset();
		for (int i = 0; i < count; i++)
		{
			resources[i] = new((UnityEntryKind)(i % 8), i + 1, i % 3 == 0 ? -1 : i, Extensions[i % 8]);
			if (i < count / 2) first[i + 1] = resources[i];
			else second[i - count / 2] = resources[i];
		}
		FakeGraph graph = new(new FakeCollection("first", first), new FakeCollection("second", second));
		using (UnitySession warm = UnitySession.FromGraph(new FakeGraph(new FakeCollection("warm", resources[0]))))
			Assert.Single(warm.Entries);
		long allocated = GC.GetAllocatedBytesForCurrentThread();
		using UnitySession session = UnitySession.FromGraph(graph);
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.Equal(count, session.Entries.Count);
		for (int i = 0; i < count; i++)
		{
			Assert.Equal(Expected(i), session.Entries[i]);
			Assert.Equal(0, resources[i].ExportCalls);
		}
		UnityCatalogEntry snapshot = session.Entries[0];
		resources[0].Name = "after";
		resources[0].OriginalPath = null;
		resources[0].SourceSize = 99;
		Assert.Equal(Expected(0), snapshot);
		Assert.Equal(snapshot, session.Entries[0]);
		using MemoryStream destination = new();
		Assert.True(session.Export(count - 1, destination));
		Assert.Equal(new byte[] { 1, 2, 3 }, destination.ToArray());
		for (int i = 0; i < count; i++) Assert.Equal(i == count - 1 ? 1 : 0, resources[i].ExportCalls);
		// Two longs, three ints plus alignment, three string references and one export-target
		// reference per row. The extra int and fixed slack cover x86/x64 alignment/array headers;
		// repeated row-buffer growth or an extra heap record per entry cannot fit this budget.
		long budget = count * (2L * sizeof(long) + 4 * sizeof(int) + 4 * IntPtr.Size) + 64 * 1024;
		output.WriteLine($"Building {count} catalogue snapshots allocated {allocated} bytes; budget {budget}.");
		Assert.True(allocated < budget, $"Building {count} catalogue snapshots allocated {allocated} bytes; budget {budget} requires one compact row array and one export-target array.");
	}

	private sealed class MutableResource(UnityEntryKind kind, long pathId, long sourceSize, string extension) : IProbeResource
	{
		public UnityEntryKind Kind => kind;
		public long PathId => pathId;
		public string Name { get; set; } = "shared label";
		public long SourceSize { get; set; } = sourceSize;
		public string? OriginalPath { get; set; } = "Assets/shared";
		public string OutputExtension => extension;
		public int ExportCalls { get; private set; }
		public byte[] ExportData() { ExportCalls++; return [1, 2, 3]; }
	}
}
