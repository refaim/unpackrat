using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Unpackrat.UnityProbe;

/// <summary>Result codes and fixed field values of the frozen Unity session C ABI.</summary>
internal static class UnityAbi
{
	internal const int Ok = 0;
	internal const int InvalidArgument = -1;
	internal const int DataFailure = -2;
	internal const int InvalidHandle = -3;
	internal const int OutOfRange = -4;
	internal const int BufferTooSmall = -5;
	internal const int Cancelled = -6;
	internal const int WriteFailure = -7;
	internal const int Busy = -8;

	/// <summary>Entry kind for a Texture2D rendered to PNG.</summary>
	internal const int KindTexturePng = 1;

	/// <summary>Entry kind for a TextAsset's raw bytes.</summary>
	internal const int KindTextRaw = 2;

	/// <summary>Entry kind for a Font's embedded source font bytes.</summary>
	internal const int KindFont = 3;

	/// <summary>Entry kind for a VideoClip's raw media bytes.</summary>
	internal const int KindVideoClip = 4;

	/// <summary>Entry kind for a legacy MovieTexture's raw ogv bytes.</summary>
	internal const int KindMovieTexture = 5;

	/// <summary>Entry kind for an AudioClip's raw or decoded sound bytes.</summary>
	internal const int KindAudioClip = 6;

	/// <summary>The code a write callback returns to cancel the remaining chunks.</summary>
	internal const int CancelRequest = 1;
}

/// <summary>The fixed 32-byte entry descriptor the ABI writes into caller memory.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnityEntry
{
	internal const uint Size = 32;

	public uint StructSize;
	public int Kind;
	public int CollectionIndex;
	public uint Reserved;
	public long PathId;
	public uint NameLength;
	public uint Reserved2;
}

/// <summary>The fixed 48-byte combined metadata descriptor the ABI writes into caller memory.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct UnityMetadata
{
	internal const uint Size = 48;

	public uint StructSize;
	public int Kind;
	public int CollectionIndex;
	public uint Reserved;
	public long PathId;
	public long SourceSize;
	public int NameLength;
	public int OriginalLength;
	public int ExtensionLength;
	public uint Reserved2;
}

/// <summary>
/// The tested boundary behind the exported Unity session entry points: a fixed handle registry over
/// <see cref="UnitySession"/> instances, with one serialised operation per session.
/// </summary>
/// <remarks>
/// The registry is a fixed array of slots and does not wrap the session in a custom handle type; a
/// token is the slot index in its low 8 bits and a monotonically increasing sequence in the upper
/// bits, so a forged or stale token never resolves and an issued token is never reused. Every state
/// transition happens under one lock that is never held across session work, so a re-entrant or
/// concurrent call on a busy session observes a flag and reports busy instead of deadlocking.
/// </remarks>
internal sealed unsafe class UnitySessionAbi
{
	/// <summary>Largest callback chunk; the ABI guarantees chunks never exceed this.</summary>
	internal const int MaxChunkSize = 65536;

	/// <summary>Slot ceiling imposed by the 8-bit slot field: the low byte holds index+1.</summary>
	internal const int MaxRegistrySize = 255;

	/// <summary>Highest sequence value the bits above the slot field can hold.</summary>
	internal const long MaxSequence = (1L << SequenceBitCount) - 1;

	private const int SlotBitCount = 8;
	private const int SequenceBitCount = 64 - SlotBitCount;
	private const ulong SlotMask = (1UL << SlotBitCount) - 1;

	private readonly Func<string, UnitySession> open;
	private readonly object gate = new();
	private readonly Slot[] slots;
	private long sequence;

	/// <summary>
	/// Creates a registry of at most <paramref name="maxSessions"/> sessions supplied by
	/// <paramref name="open"/>. The factory is the only seam, so failures can be injected without a
	/// file system or a global hook; <paramref name="initialSequence"/> is a deterministic counter
	/// seam so the sequence ceiling can be exercised without issuing billions of tokens.
	/// </summary>
	internal UnitySessionAbi(Func<string, UnitySession> open, int maxSessions, long initialSequence = 0)
	{
		ArgumentNullException.ThrowIfNull(open);
		if (maxSessions < 1 || maxSessions > MaxRegistrySize)
		{
			throw new ArgumentOutOfRangeException(
				nameof(maxSessions), maxSessions, $"A registry holds between 1 and {MaxRegistrySize} sessions.");
		}

		if (initialSequence < 0 || initialSequence > MaxSequence)
		{
			throw new ArgumentOutOfRangeException(
				nameof(initialSequence), initialSequence, $"The token sequence starts between 0 and {MaxSequence}.");
		}

		this.open = open;
		sequence = initialSequence;
		slots = new Slot[maxSessions];
		for (int i = 0; i < slots.Length; i++)
		{
			slots[i] = new Slot();
		}
	}

	/// <summary>The single instance behind the exported functions, loading real Unity files.</summary>
	internal static UnitySessionAbi CreateProduction() => new(UnitySession.Open, 64);

	/// <summary>
	/// Opens <paramref name="path"/> as a session and reports its handle and catalogue size. Both
	/// non-null outputs are zeroed before any validation.
	/// </summary>
	internal int Open(ushort* path, int pathLength, ulong* sessionOut, int* countOut)
	{
		if (sessionOut != null)
		{
			*sessionOut = 0;
		}

		if (countOut != null)
		{
			*countOut = 0;
		}

		if (sessionOut == null || countOut == null)
		{
			return UnityAbi.InvalidArgument;
		}

		if (!ArgumentReader.TryRead(path, pathLength, out string? pathValue))
		{
			return UnityAbi.InvalidArgument;
		}

		if (pathValue.Contains('\0'))
		{
			return UnityAbi.InvalidArgument;
		}

		UnitySession session;
		try
		{
			session = open(pathValue);
		}
		catch (OutOfMemoryException)
		{
			throw;
		}
		catch (Exception)
		{
			return UnityAbi.DataFailure;
		}

		RegisterOutcome outcome = TryRegister(session, out ulong token);
		if (outcome != RegisterOutcome.Registered)
		{
			// The registry never owned the rejected session, so cleanup is still ours. A cleanup
			// that fails is itself a data failure; out-of-memory stays fatal.
			try
			{
				session.Dispose();
			}
			catch (OutOfMemoryException)
			{
				throw;
			}
			catch (Exception)
			{
				return UnityAbi.DataFailure;
			}

			return outcome == RegisterOutcome.Full ? UnityAbi.Busy : UnityAbi.DataFailure;
		}

		*sessionOut = token;
		*countOut = session.Count;
		return UnityAbi.Ok;
	}

	/// <summary>
	/// Writes the metadata of catalogue entry <paramref name="index"/> and copies its UTF-16 label
	/// into <paramref name="name"/>. No conversion happens here, and no terminator is written.
	/// </summary>
	internal int GetEntry(ulong session, int index, UnityEntry* entry, ushort* name, int nameCapacity)
	{
		if (entry == null)
		{
			return UnityAbi.InvalidArgument;
		}

		if (entry->StructSize != UnityEntry.Size)
		{
			return UnityAbi.InvalidArgument;
		}

		// A caller-supplied size of exactly 32 promises 32 writable bytes, so the whole descriptor
		// is initialised before anything else can fail.
		*entry = default;
		entry->StructSize = UnityEntry.Size;

		if (nameCapacity < 0 || (name == null && nameCapacity > 0))
		{
			return UnityAbi.InvalidArgument;
		}

		Slot? slot = Acquire(session, out int status);
		if (slot is null)
		{
			return status;
		}

		try
		{
			UnitySession current = slot.Session!;
			if (index < 0 || index >= current.Count)
			{
				return UnityAbi.OutOfRange;
			}

			UnityEntryMetadata found = current.Metadata(index);
			int nameLength = current.NameLength(index);
			// The own zero-based enum maps to the frozen external kind ids by a single +1, so a new
			// resource kind cannot be mislabelled as an older one the way a texture/text ternary would.
			entry->Kind = (int)found.Kind + 1;
			entry->CollectionIndex = found.CollectionIndex;
			entry->PathId = found.PathId;
			entry->NameLength = (uint)nameLength;

			if (name == null)
			{
				// A null buffer with zero capacity is the size query: the label stays uncopied.
				return UnityAbi.Ok;
			}

			if (nameCapacity < nameLength)
			{
				return UnityAbi.BufferTooSmall;
			}

			if (nameLength > 0)
			{
				current.CopyName(index, new Span<char>((char*)name, nameLength));
			}

			return UnityAbi.Ok;
		}
		finally
		{
			Release(slot);
		}
	}

	/// <summary>
	/// Reports the declared source payload length of catalogue entry <paramref name="index"/> into
	/// <paramref name="sizeOut"/>, which is zeroed before any validation. Nothing is converted or
	/// materialised here; the length is the immutable catalogue value captured at open time.
	/// </summary>
	internal int GetSourceSize(ulong session, int index, long* sizeOut)
	{
		if (sizeOut == null)
		{
			return UnityAbi.InvalidArgument;
		}

		// The caller's output is initialised before anything else can fail, so a rejected call never
		// leaves a stale length behind.
		*sizeOut = 0;

		Slot? slot = Acquire(session, out int status);
		if (slot is null)
		{
			return status;
		}

		try
		{
			UnitySession current = slot.Session!;
			if (index < 0 || index >= current.Count)
			{
				return UnityAbi.OutOfRange;
			}

			*sizeOut = current.Metadata(index).SourceSize;
			return UnityAbi.Ok;
		}
		finally
		{
			Release(slot);
		}
	}

	/// <summary>
	/// Writes the original asset path of catalogue entry <paramref name="index"/> as UTF-16 code
	/// units into <paramref name="path"/>, and the required unit count into
	/// <paramref name="pathLength"/>. A null <paramref name="path"/> is a size query. Nothing is
	/// converted here: the path is immutable catalogue metadata captured at open time.
	/// </summary>
	internal int GetEntryPath(ulong session, int index, ushort* path, int pathCapacity, int* pathLength)
	{
		if (pathLength == null)
		{
			return UnityAbi.InvalidArgument;
		}

		// The caller's output is initialised before anything else can fail, so a rejected call never
		// leaves a stale length behind.
		*pathLength = 0;

		if (pathCapacity < 0 || (path == null && pathCapacity > 0))
		{
			return UnityAbi.InvalidArgument;
		}

		Slot? slot = Acquire(session, out int status);
		if (slot is null)
		{
			return status;
		}

		try
		{
			UnitySession current = slot.Session!;
			if (index < 0 || index >= current.Count)
			{
				return UnityAbi.OutOfRange;
			}

			string original = current.Metadata(index).OriginalPath;
			*pathLength = original.Length;
			if (path == null)
			{
				// A null buffer with zero capacity is the size query: the path stays uncopied.
				return UnityAbi.Ok;
			}

			if (pathCapacity < original.Length)
			{
				return UnityAbi.BufferTooSmall;
			}

			if (original.Length > 0)
			{
				original.AsSpan().CopyTo(new Span<char>((char*)path, original.Length));
			}

			return UnityAbi.Ok;
		}
		finally
		{
			Release(slot);
		}
	}

	/// <summary>
	/// Writes the immutable output extension of catalogue entry <paramref name="index"/> as UTF-16
	/// code units into <paramref name="extension"/>, and the required unit count into
	/// <paramref name="length"/>. A null <paramref name="extension"/> is a size query. No conversion
	/// happens here: the extension is fixed catalogue metadata captured at open time.
	/// </summary>
	internal int GetEntryExtension(ulong session, int index, ushort* extension, int capacity, int* length)
	{
		if (length == null)
		{
			return UnityAbi.InvalidArgument;
		}

		// The caller's output is initialised before anything else can fail, so a rejected call never
		// leaves a stale count behind.
		*length = 0;

		if (capacity < 0 || (extension == null && capacity > 0))
		{
			return UnityAbi.InvalidArgument;
		}

		Slot? slot = Acquire(session, out int status);
		if (slot is null)
		{
			return status;
		}

		try
		{
			UnitySession current = slot.Session!;
			if (index < 0 || index >= current.Count)
			{
				return UnityAbi.OutOfRange;
			}

			string value = current.Metadata(index).OutputExtension;
			*length = value.Length;
			if (extension == null)
			{
				// A null buffer with zero capacity is the size query: the extension stays uncopied.
				return UnityAbi.Ok;
			}

			if (capacity < value.Length)
			{
				return UnityAbi.BufferTooSmall;
			}

			// A catalogue extension is never empty, so the copy is unconditional and no zero-length
			// branch can hide a partial write.
			value.AsSpan().CopyTo(new Span<char>((char*)extension, value.Length));

			return UnityAbi.Ok;
		}
		finally
		{
			Release(slot);
		}
	}

	/// <summary>
	/// Reads the whole metadata of catalogue entry <paramref name="index"/> in one serialised
	/// operation, writing the descriptor and the three field lengths and copying each field into its
	/// caller-owned UTF-16 buffer when that buffer is large enough.
	/// </summary>
	public int GetMetadata(ulong session, int index, UnityMetadata* metadata, ushort* name,
		int nameCapacity, ushort* original, int originalCapacity, ushort* extension, int extensionCapacity)
	{
		if (metadata == null)
		{
			return UnityAbi.InvalidArgument;
		}

		if (metadata->StructSize != UnityMetadata.Size)
		{
			return UnityAbi.InvalidArgument;
		}

		// A caller-supplied size of exactly 48 promises 48 writable bytes, so the whole descriptor
		// is initialised before anything else can fail.
		*metadata = default;
		metadata->StructSize = UnityMetadata.Size;

		if (nameCapacity < 0 || (name == null && nameCapacity > 0)
			|| originalCapacity < 0 || (original == null && originalCapacity > 0)
			|| extensionCapacity < 0 || (extension == null && extensionCapacity > 0))
		{
			return UnityAbi.InvalidArgument;
		}

		Slot? slot = Acquire(session, out int status);
		if (slot is null)
		{
			return status;
		}

		try
		{
			UnitySession current = slot.Session!;
			if (index < 0 || index >= current.Count)
			{
				return UnityAbi.OutOfRange;
			}

			UnityEntryMetadata found = current.Metadata(index);
			int nameLength = current.NameLength(index);
			string originalPath = found.OriginalPath;
			string outputExtension = found.OutputExtension;

			// Every length lands in the descriptor before any buffer is touched, so a bounded
			// caller can retry with exact buffers on a short-buffer report.
			metadata->Kind = (int)found.Kind + 1;
			metadata->CollectionIndex = found.CollectionIndex;
			metadata->PathId = found.PathId;
			metadata->SourceSize = found.SourceSize;
			metadata->NameLength = nameLength;
			metadata->OriginalLength = originalPath.Length;
			metadata->ExtensionLength = outputExtension.Length;

			// Each field copies only when its caller buffer fits. A field with a short buffer is
			// left untouched and reports a single BufferTooSmall for the whole call; a null buffer
			// with zero capacity is a size query for that field.
			bool shortBuffer = false;

			if (name != null)
			{
				if (nameCapacity < nameLength)
				{
					shortBuffer = true;
				}
				else if (nameLength > 0)
				{
					current.CopyName(index, new Span<char>((char*)name, nameLength));
				}
			}

			if (original != null)
			{
				if (originalCapacity < originalPath.Length)
				{
					shortBuffer = true;
				}
				else if (originalPath.Length > 0)
				{
					originalPath.AsSpan().CopyTo(new Span<char>((char*)original, originalPath.Length));
				}
			}

			if (extension != null)
			{
				if (extensionCapacity < outputExtension.Length)
				{
					shortBuffer = true;
				}
				else if (outputExtension.Length > 0)
				{
					outputExtension.AsSpan().CopyTo(new Span<char>((char*)extension, outputExtension.Length));
				}
			}

			return shortBuffer ? UnityAbi.BufferTooSmall : UnityAbi.Ok;
		}
		finally
		{
			Release(slot);
		}
	}

	/// <summary>
	/// Materialises entry <paramref name="index"/> and hands it to <paramref name="write"/> in chunks
	/// of at most <see cref="MaxChunkSize"/> bytes, honouring cancellation and write failures.
	/// </summary>

	/// <summary>
	/// Materialises entry <paramref name="index"/> and hands it to <paramref name="write"/> in chunks
	/// of at most <see cref="MaxChunkSize"/> bytes, honouring cancellation and write failures.
	/// </summary>
	internal int Export(ulong session, int index, delegate* unmanaged[Cdecl]<void*, byte*, int, int> write, void* context)
	{
		if (write == null)
		{
			return UnityAbi.InvalidArgument;
		}

		Slot? slot = Acquire(session, out int status);
		if (slot is null)
		{
			return status;
		}

		try
		{
			return ExportCore(slot.Session!, index, write, context);
		}
		catch (OutOfMemoryException)
		{
			throw;
		}
		catch (Exception)
		{
			return UnityAbi.DataFailure;
		}
		finally
		{
			Release(slot);
		}
	}

	/// <summary>Closes <paramref name="session"/> and releases the file handles its graph owns.</summary>
	internal int Close(ulong session)
	{
		UnitySession owned;
		lock (gate)
		{
			Slot? slot = Resolve(session);
			if (slot is null)
			{
				return UnityAbi.InvalidHandle;
			}

			if (Interlocked.CompareExchange(ref slot.Busy, 1, 0) != 0)
			{
				return UnityAbi.Busy;
			}

			// The token is dropped first, so a stale token can never resolve again, and the slot is
			// free for a new session while this one is being disposed outside the lock.
			owned = slot.Session!;
			slot.Session = null;
			slot.Token = 0;
			Volatile.Write(ref slot.Busy, 0);
		}

		try
		{
			owned.Dispose();
		}
		catch (OutOfMemoryException)
		{
			throw;
		}
		catch (Exception)
		{
			return UnityAbi.DataFailure;
		}

		return UnityAbi.Ok;
	}

	private static int ExportCore(UnitySession session, int index, delegate* unmanaged[Cdecl]<void*, byte*, int, int> write, void* context)
	{
		if (index < 0 || index >= session.Count)
		{
			return UnityAbi.OutOfRange;
		}

		// The session already materialises the payload; the sink only lets the callback boundary be
		// honoured without a second copy of the bytes.
		using MemoryStream sink = new();
		if (!session.Export(index, sink))
		{
			return UnityAbi.DataFailure;
		}

		byte[] payload = sink.GetBuffer();
		int payloadLength = (int)sink.Length;
		fixed (byte* pointer = payload)
		{
			for (int offset = 0; offset < payloadLength; offset += MaxChunkSize)
			{
				int length = Math.Min(MaxChunkSize, payloadLength - offset);
				int outcome = Invoke(write, context, pointer + offset, length);
				if (outcome != UnityAbi.Ok)
				{
					return outcome;
				}
			}
		}

		return UnityAbi.Ok;
	}

	/// <summary>
	/// Calls the write callback and translates its result. The callback must not throw across the C
	/// boundary, so only its returned status is honoured: zero accepts, one cancels, anything else is
	/// a write failure.
	/// </summary>
	private static int Invoke(delegate* unmanaged[Cdecl]<void*, byte*, int, int> write, void* context, byte* bytes, int length)
	{
		int outcome = write(context, bytes, length);
		return outcome switch
		{
			UnityAbi.Ok => UnityAbi.Ok,
			UnityAbi.CancelRequest => UnityAbi.Cancelled,
			_ => UnityAbi.WriteFailure,
		};
	}

	/// <summary>Claims the session for one operation, or reports why it cannot be used.</summary>
	private Slot? Acquire(ulong session, out int status)
	{
		lock (gate)
		{
			Slot? slot = Resolve(session);
			if (slot is null)
			{
				status = UnityAbi.InvalidHandle;
				return null;
			}

			if (Interlocked.CompareExchange(ref slot.Busy, 1, 0) != 0)
			{
				status = UnityAbi.Busy;
				return null;
			}

			status = UnityAbi.Ok;
			return slot;
		}
	}

	/// <summary>Resolves a token to its slot, or <see langword="null"/> for a forged or stale token.</summary>
	private Slot? Resolve(ulong session)
	{
		int index = (int)(session & SlotMask) - 1;
		if (index < 0 || index >= slots.Length)
		{
			return null;
		}

		Slot slot = slots[index];
		return slot.Token == session ? slot : null;
	}

	private RegisterOutcome TryRegister(UnitySession session, out ulong token)
	{
		lock (gate)
		{
			token = 0;

			// The sequence occupies the bits above the slot field and must never wrap: a reused value
			// would hand out a token that an earlier session once owned. Once it reaches the ceiling,
			// no fresh token can be minted, so the registry reports permanent exhaustion.
			if (sequence >= MaxSequence)
			{
				return RegisterOutcome.Exhausted;
			}

			for (int i = 0; i < slots.Length; i++)
			{
				Slot slot = slots[i];
				if (slot.Token != 0)
				{
					continue;
				}

				ulong slotField = (ulong)(uint)i + 1;
				token = ((ulong)Interlocked.Increment(ref sequence) << SlotBitCount) | slotField;
				slot.Session = session;
				Volatile.Write(ref slot.Busy, 0);
				slot.Token = token;
				return RegisterOutcome.Registered;
			}

			return RegisterOutcome.Full;
		}
	}

	private static void Release(Slot slot) => Volatile.Write(ref slot.Busy, 0);

	/// <summary>The result of one registration attempt.</summary>
	private enum RegisterOutcome
	{
		/// <summary>The session claimed a free slot under a freshly issued token.</summary>
		Registered,

		/// <summary>Every slot is taken, so no session can be admitted right now.</summary>
		Full,

		/// <summary>The sequence ceiling is reached, so no fresh token can ever be issued again.</summary>
		Exhausted,
	}

	/// <summary>One registry slot: the issued token, the session it owns and its busy flag.</summary>
	private sealed class Slot
	{
		internal ulong Token;
		internal UnitySession? Session;
		internal int Busy;
	}
}
