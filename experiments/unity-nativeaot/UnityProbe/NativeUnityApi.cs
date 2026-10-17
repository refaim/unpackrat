using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Unpackrat.UnityProbe;

/// <summary>
/// Native C ABI surface of the Unity session, one exported function per frozen entry point.
/// </summary>
/// <remarks>
/// Each export is a thin cdecl thunk into the same tested <see cref="UnitySessionAbi"/> boundary the
/// hermetic tests drive, so no behaviour lives above that boundary.
/// </remarks>
public static unsafe class NativeUnityApi
{
	internal static readonly UnitySessionAbi Production = UnitySessionAbi.CreateProduction();

	[UnmanagedCallersOnly(EntryPoint = "unpackrat_unity_open", CallConvs = [typeof(CallConvCdecl)])]
	internal static int Open(ushort* path, int pathLength, ulong* sessionOut, int* countOut)
		=> Production.Open(path, pathLength, sessionOut, countOut);

	[UnmanagedCallersOnly(EntryPoint = "unpackrat_unity_get_entry", CallConvs = [typeof(CallConvCdecl)])]
	internal static int GetEntry(ulong session, int index, UnityEntry* entry, ushort* name, int nameCapacity)
		=> Production.GetEntry(session, index, entry, name, nameCapacity);

	[UnmanagedCallersOnly(EntryPoint = "unpackrat_unity_get_source_size", CallConvs = [typeof(CallConvCdecl)])]
	internal static int GetSourceSize(ulong session, int index, long* sizeOut)
		=> Production.GetSourceSize(session, index, sizeOut);

	[UnmanagedCallersOnly(EntryPoint = "unpackrat_unity_get_entry_path", CallConvs = [typeof(CallConvCdecl)])]
	internal static int GetEntryPath(ulong session, int index, ushort* path, int pathCapacity, int* pathLength)
		=> Production.GetEntryPath(session, index, path, pathCapacity, pathLength);

	[UnmanagedCallersOnly(EntryPoint = "unpackrat_unity_get_entry_extension", CallConvs = [typeof(CallConvCdecl)])]
	internal static int GetEntryExtension(ulong session, int index, ushort* extension, int capacity, int* length)
		=> Production.GetEntryExtension(session, index, extension, capacity, length);

	[UnmanagedCallersOnly(EntryPoint = "unpackrat_unity_export", CallConvs = [typeof(CallConvCdecl)])]
	internal static int Export(ulong session, int index, delegate* unmanaged[Cdecl]<void*, byte*, int, int> write, void* context)
		=> Production.Export(session, index, write, context);

	[UnmanagedCallersOnly(EntryPoint = "unpackrat_unity_close", CallConvs = [typeof(CallConvCdecl)])]
	internal static int Close(ulong session) => Production.Close(session);
}
