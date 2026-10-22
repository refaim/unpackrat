using System;
using System.IO;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Streams.Smart;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// The shared reference counter owns the original backing stream. A partial slice must close the
/// file it was carved from when it releases the last reference, even when the original root stream
/// was released first, because a temp-backed block stream otherwise leaks its file handle.
/// </summary>
public sealed class SmartStreamSharedOwnerTests
{
	[Fact]
	public void DisposingTheOriginalRootBeforeAPartialStillClosesTheFile()
	{
		string directory = Temp.NewDirectory();
		string path = Path.Combine(directory, "known.bin");
		File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

		SmartStream root = SmartStream.OpenRead(path, LocalFileSystem.Instance);
		SmartStream partial = root.CreatePartial(2, 4);

		// Release the original root first, exactly like a container that frees its entry stream
		// before the child slices it produced, then release the last slice.
		root.Dispose();
		partial.Dispose();

		// The excluded open only succeeds once every handle on the file is closed.
		using FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
	}
}
