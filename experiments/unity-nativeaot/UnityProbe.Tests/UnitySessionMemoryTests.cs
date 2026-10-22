using System;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnitySessionMemoryTests(ITestOutputHelper output)
{
	[Fact]
	public void LargeCatalogueRetainsOnlyItsSnapshotAndExistingExportTargets()
	{
		const int count = 4096;
		IProbeAsset[] assets = new IProbeAsset[count];
		UnityCatalogEntry[] expected = new UnityCatalogEntry[count];
		for (int i = 0; i < count; i++)
		{
			string name = $"asset{i}";
			string path = $"Assets/{i}";
			byte[] payload = [unchecked((byte)i)];
			assets[i] = (i % 3) switch
			{
				0 => new FakeTexture(i, payload, name: name, sourceSize: i, originalPath: path),
				1 => new FakeText(i, payload, name: name, sourceSize: i, originalPath: path),
				_ => new FakeResource(UnityEntryKind.Font, i, payload, name: name, sourceSize: i, originalPath: path, outputExtension: "ttf"),
			};
			expected[i] = new(i, (UnityEntryKind)(i % 3), i / (count / 2), i, name, i, path,
				(i % 3) switch { 0 => "png", 1 => "bin", _ => "ttf" });
		}
		FakeGraph graph = new(new FakeCollection("first", assets[..(count / 2)].Prepend(new FakeAsset()).ToArray()),
			new FakeCollection("second", assets[(count / 2)..].Append(new FakeAsset()).ToArray()));
		using (UnitySession warm = UnitySession.FromGraph(new FakeGraph(new FakeCollection("warm", new FakeText(1, [])))))
			Assert.Single(warm.Entries);
		long allocated = GC.GetAllocatedBytesForCurrentThread();
		using UnitySession session = UnitySession.FromGraph(graph);
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.Equal(expected, session.Entries);
		Assert.All(assets, asset => Assert.Equal(0, Calls(asset)));
		const int selected = count - 2;
		using MemoryStream destination = new();
		Assert.True(session.Export(selected, destination));
		Assert.Equal(new byte[] { unchecked((byte)selected) }, destination.ToArray());
		for (int i = 0; i < count; i++) Assert.Equal(i == selected ? 1 : 0, Calls(assets[i]));
		output.WriteLine($"4096 catalogued export targets allocated {allocated} bytes.");
		Assert.True(allocated < 768 * 1024, $"4096 catalogued export targets allocated {allocated} bytes; session metadata must not duplicate entries in per-target records and delegates.");
		GC.KeepAlive(session);
	}

	private static int Calls(IProbeAsset asset) => asset switch
	{
		FakeTexture texture => texture.RenderCalls,
		FakeText text => text.ReadCalls,
		_ => ((FakeResource)asset).ExportCalls,
	};

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void OverlappingInterfacesKeepTextureThenTextThenResourcePrecedence(bool texture)
	{
		TextAndResource asset = texture ? new TextureTextAndResource() : new TextAndResource();
		using UnitySession session = UnitySession.FromGraph(new FakeGraph(new FakeCollection("hybrid", asset)));
		UnityCatalogEntry entry = Assert.Single(session.Entries);
		Assert.Equal(texture ? UnityEntryKind.Texture : UnityEntryKind.Text, entry.Kind);
		Assert.Equal(texture ? "png" : "bin", entry.OutputExtension);
		Assert.Equal(0, asset.ReadCalls);
		Assert.Equal(0, asset.ExportCalls);
		using MemoryStream destination = new();
		Assert.True(session.Export(0, destination));
		Assert.Equal(new byte[] { texture ? (byte)1 : (byte)2 }, destination.ToArray());
		Assert.Equal(texture ? 0 : 1, asset.ReadCalls);
		Assert.Equal(0, asset.ExportCalls);
		if (asset is TextureTextAndResource textured) Assert.Equal(1, textured.RenderCalls);
	}

	private class TextAndResource : IProbeText, IProbeResource
	{
		public long PathId => 7;
		public string Version => "1.0";
		public string Name => "hybrid";
		public string? OriginalPath => null;
		public long SourceSize => 1;
		public UnityEntryKind Kind => UnityEntryKind.Font;
		public string OutputExtension => "ttf";
		public int ReadCalls { get; private set; }
		public int ExportCalls { get; private set; }
		public byte[] ReadData() { ReadCalls++; return [2]; }
		public byte[] ExportData() { ExportCalls++; return [3]; }
	}
	private sealed class TextureTextAndResource : TextAndResource, IProbeTexture
	{
		public int RenderCalls { get; private set; }
		public byte[] RenderPng() { RenderCalls++; return [1]; }
	}

	[Fact]
	public void CatalogueSnapshotAndExportTargetSurviveLaterGraphMetadataChanges()
	{
		MutableResource original = new();
		IProbeAsset[] assets = [original];
		FakeGraph graph = new(new FakeCollection("mutable", assets));
		using UnitySession session = UnitySession.FromGraph(graph);
		UnityCatalogEntry expected = new(0, UnityEntryKind.Font, 0, 7, "before", 3, "Assets/before.ttf", "ttf");
		Assert.Equal(expected, Assert.Single(session.Entries));
		original.Kind = UnityEntryKind.VideoClip;
		original.PathId = 99;
		original.Name = "after";
		original.SourceSize = -1;
		original.OriginalPath = null;
		original.OutputExtension = "mp4";
		FakeResource replacement = new(UnityEntryKind.VideoClip, 99, [9]);
		assets[0] = replacement;
		using MemoryStream destination = new();
		Assert.True(session.Export(0, destination));
		Assert.Equal(new byte[] { 1, 2, 3 }, destination.ToArray());
		Assert.Equal(1, original.ExportCalls);
		Assert.Equal(0, replacement.ExportCalls);
		Assert.Equal(expected, Assert.Single(session.Entries));
		session.Dispose();
		Assert.False(session.Export(0, destination));
		Assert.Equal(1, graph.DisposeCalls);
	}

	private sealed class MutableResource : IProbeResource
	{
		public UnityEntryKind Kind { get; set; } = UnityEntryKind.Font;
		public long PathId { get; set; } = 7;
		public string Name { get; set; } = "before";
		public long SourceSize { get; set; } = 3;
		public string? OriginalPath { get; set; } = "Assets/before.ttf";
		public string OutputExtension { get; set; } = "ttf";
		public int ExportCalls { get; private set; }
		public byte[] ExportData() { ExportCalls++; return [1, 2, 3]; }
	}
}
