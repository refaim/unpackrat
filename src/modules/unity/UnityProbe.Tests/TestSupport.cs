using System;
using System.Collections.Generic;
using System.IO;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Streams.Smart;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>An asset that is neither a texture nor a text asset.</summary>
internal sealed class FakeAsset(long pathId = 0, string version = "") : IProbeAsset
{
	public long PathId { get; } = pathId;
	public string Version { get; } = version;
}

/// <summary>A texture whose PNG render is supplied by the test (<see langword="null"/> = unsupported).</summary>
internal sealed class FakeTexture(long pathId, byte[]? png, string version = "1.2.3", string name = "", long sourceSize = 0, string? originalPath = null) : IProbeTexture
{
	public long PathId { get; } = pathId;
	public string Version { get; } = version;
	public string Name { get; } = name;
	public long SourceSize { get; } = sourceSize;
	public string? OriginalPath { get; } = originalPath;
	public int RenderCalls { get; private set; }

	public byte[]? RenderPng()
	{
		RenderCalls++;
		return png;
	}
}

/// <summary>A text asset returning fixed bytes, or <see langword="null"/> for an unsupported payload.</summary>
internal sealed class FakeText(long pathId, byte[]? data, string version = "1.2.3", string name = "", long sourceSize = 0, string? originalPath = null) : IProbeText
{
	public long PathId { get; } = pathId;
	public string Version { get; } = version;
	public string Name { get; } = name;
	public long SourceSize { get; } = sourceSize;
	public string? OriginalPath { get; } = originalPath;
	public int ReadCalls { get; private set; }

	public byte[]? ReadData()
	{
		ReadCalls++;
		return data;
	}
}

/// <summary>A project-owned collection double holding a fixed set of asset views.</summary>
internal sealed class FakeCollection(string identity, params IProbeAsset[] assets) : IUnityGraphCollection
{
	public string Identity { get; } = identity;
	public IReadOnlyList<IProbeAsset> Assets { get; } = assets;
}

/// <summary>
/// A resource of any additional kind returning fixed bytes, or <see langword="null"/> for an
/// unavailable payload, with all its metadata supplied by the test.
/// </summary>
internal sealed class FakeResource(
	UnityEntryKind kind,
	long pathId,
	byte[]? data,
	string version = "1.2.3",
	string name = "",
	long sourceSize = 0,
	string? originalPath = null,
	string outputExtension = "bin") : IProbeResource
{
	public UnityEntryKind Kind { get; } = kind;
	public long PathId { get; } = pathId;
	public string Name { get; } = name;
	public string? OriginalPath { get; } = originalPath;
	public long SourceSize { get; } = sourceSize;
	public string OutputExtension { get; } = outputExtension;
	public string Version { get; } = version;
	public int ExportCalls { get; private set; }

	public byte[]? ExportData()
	{
		ExportCalls++;
		return data;
	}
}

/// <summary>A project-owned graph double that counts the disposals it receives.</summary>
internal sealed class FakeGraph(params IUnityGraphCollection[] collections) : IUnityGraph
{
	public IReadOnlyList<IUnityGraphCollection> Collections { get; } = collections;
	public int DisposeCalls { get; private set; }

	public void Dispose() => DisposeCalls++;
}

/// <summary>
/// A project-owned <see cref="FileBase"/> double for the loader's reader seam. It records how often
/// its contents are read and it is disposed, and can throw from either, so failure cleanup can be
/// observed without any real file or parser.
/// </summary>
internal sealed class FakeFileBase(Action? onReadContents = null, Action? onDispose = null) : FileBase
{
	public int ReadContentsCalls { get; private set; }
	public int DisposeCalls { get; private set; }

	public override void Read(SmartStream stream)
	{
	}

	public override void Write(Stream stream)
	{
	}

	public override void ReadContentsRecursively()
	{
		ReadContentsCalls++;
		onReadContents?.Invoke();
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			DisposeCalls++;
			onDispose?.Invoke();
		}

		base.Dispose(disposing);
	}
}

internal static class Temp
{
	public static string NewDirectory()
	{
		string path = Path.Combine(Path.GetTempPath(), "unityprobe-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}
}
