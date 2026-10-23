using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Unpackrat.UnityProbe;

/// <summary>
/// Native C ABI surface of the AssetRipper-backed probe.
/// </summary>
/// <remarks>
/// Exported symbol: <c>unpackrat_unity_probe</c>, cdecl, returning:
/// <c>&gt;=0</c> exported resource count, <c>-1</c> invalid argument, <c>-2</c> I/O or data failure.
/// Lengths are UTF-16 code unit counts excluding the terminator; every output is written beneath
/// the supplied directory. Both input and output lengths must be in the inclusive range
/// <c>1..32767</c> (<see cref="ArgumentReader.MaxLength"/>); anything else is rejected.
/// </remarks>
public static unsafe class NativeProbe
{
	public const int InvalidArgument = -1;
	public const int DataFailure = -2;

	[UnmanagedCallersOnly(EntryPoint = "unpackrat_unity_probe", CallConvs = [typeof(CallConvCdecl)])]
	public static int Run(ushort* input, int inputLength, ushort* outputDirectory, int outputLength)
	{
		if (!ArgumentReader.TryRead(input, inputLength, out string? inputPath))
		{
			return InvalidArgument;
		}

		if (!ArgumentReader.TryRead(outputDirectory, outputLength, out string? outputDirectoryPath))
		{
			return InvalidArgument;
		}

		return Execute(inputPath, outputDirectoryPath);
	}

	/// <summary>
	/// Validates and runs the export. No exception may cross the ABI boundary; out-of-memory is
	/// deliberately not swallowed because the process cannot be trusted to continue.
	/// </summary>
	internal static int Execute(string inputPath, string outputDirectory, Func<IReadOnlyList<string>, string, int>? export = null)
	{
		try
		{
			Func<IReadOnlyList<string>, string, int> exporter = export ?? UnitySource.LoadAndExport;
			return exporter(new[] { inputPath }, outputDirectory);
		}
		catch (OutOfMemoryException)
		{
			throw;
		}
		catch (Exception)
		{
			return DataFailure;
		}
	}
}

internal static class ArgumentReader
{
	/// <summary>
	/// Largest accepted UTF-16 code-unit count. A Windows extended-length path is limited to
	/// 32767 code units, so any larger length cannot name a real input/output path and must be
	/// rejected before the pointer is dereferenced or a string is allocated.
	/// </summary>
	public const int MaxLength = 32767;

	internal static unsafe bool TryRead(ushort* pointer, int length, [NotNullWhen(true)] out string? value)
	{
		if (pointer == null || length <= 0 || length > MaxLength)
		{
			value = null;
			return false;
		}

		value = new string((char*)pointer, 0, length);
		return true;
	}
}
