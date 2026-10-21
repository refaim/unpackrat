using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetsTools.NET;

namespace Unpackrat.UnityProbe;

// AssetsTools owns the Unity layout traversal. This adapter retains only catalogue fields and
// borrowed byte ranges; no array value or composite value is materialised by the iterator.
internal sealed partial class UnityProjection
{
	private const int TextScratchLength = 256;
	// The mature iterator's visit ceiling; shared plans must not bypass it.
	private const int FieldBudget = 16_000_000;
	internal readonly record struct ByteRange(long Offset, int Length);
	internal enum ValueKind { Number, Text, Bytes }
	internal readonly record struct Slot(ValueKind Kind, int Index);
	internal readonly record struct Capture(string Path, Slot Slot);
	private readonly State state;
	private readonly Dictionary<string, object>? values;
	private readonly long[] numbers;
	private readonly string[] texts;
	private readonly ByteRange[] ranges;
	private readonly ArrayBufferWriter<byte> rawName = new();
	internal ReadOnlySpan<byte> RawName => rawName.WrittenSpan;
	private UnityProjection(State state)
	{
		this.state = state;
		if (state.DictionaryStorage)
		{
			values = new(state.DynamicPaths ? 0 : state.Paths.Count, StringComparer.Ordinal);
			numbers = []; texts = []; ranges = [];
		}
		else
		{
			numbers = state.NumberCount == 0 ? [] : new long[state.NumberCount];
			texts = state.TextCount == 0 ? [] : new string[state.TextCount];
			ranges = state.RangeCount == 0 ? [] : new ByteRange[state.RangeCount];
		}
	}
	public IEnumerable<string> Keys => values is null ? state.Slots.Keys : values.Keys;
	public string Text(string path) => values is not null
		? values.TryGetValue(path, out object? value) && value is string text ? text : string.Empty
		: TrySlot(path, ValueKind.Text, out int index) ? texts[index] : string.Empty;
	public long Number(string path) => values is not null
		? values.TryGetValue(path, out object? value) && value is long number ? number : 0
		: TrySlot(path, ValueKind.Number, out int index) ? numbers[index] : 0;
	public ulong Unsigned(string path) => unchecked((ulong)Number(path));
	public ByteRange Bytes(string path) => values is not null
		? values.TryGetValue(path, out object? value) && value is ByteRange range ? range : default
		: TrySlot(path, ValueKind.Bytes, out int index) ? ranges[index] : default;
	private bool TrySlot(string path, ValueKind kind, out int index)
	{
		if (state.Slots.TryGetValue(path, out Slot slot) && slot.Kind == kind)
		{
			index = slot.Index;
			return true;
		}
		index = 0;
		return false;
	}
	private void SetText(Capture capture, string? dynamicPath, string value)
	{
		if (values is not null) values[dynamicPath ?? capture.Path] = value;
		else texts[capture.Slot.Index] = value;
	}
	private void SetNumber(Capture capture, string? dynamicPath, long value)
	{
		if (values is not null) values[dynamicPath ?? capture.Path] = value;
		else numbers[capture.Slot.Index] = value;
	}
	private void SetBytes(Capture capture, string? dynamicPath, ByteRange value)
	{
		if (values is not null) values[dynamicPath ?? capture.Path] = value;
		else ranges[capture.Slot.Index] = value;
	}

	internal static AssetTypeTemplateField Template(TypeTreeNodeStruct node, int depth = 0)
	{
		if (depth > 128) throw new InvalidDataException("Unity type tree is too deep.");
		AssetValueType kind = AssetTypeValueField.GetValueTypeByTypeName(node.TypeName);
		AssetTypeTemplateField result = new()
		{
			Name = node.Name, Type = node.TypeName, Version = checked((ushort)node.Version),
			ValueType = kind, HasValue = kind != AssetValueType.None,
			IsArray = node.IsArray, IsAligned = node.AlignBytes, Children = [],
		};
		foreach (TypeTreeNodeStruct child in node.SubNodes) result.Children.Add(Template(child, depth + 1));
		if (result.IsArray)
		{
			result.ValueType = result.Children[1].ValueType is AssetValueType.UInt8 or AssetValueType.Int8
				? AssetValueType.ByteArray : AssetValueType.Array;
			result.HasValue = true;
		}
		return result;
	}

	public static UnityProjection Read(Stream input, AssetTypeTemplateField template, bool bigEndian, State? state = null, bool validateLayout = false, bool textureTail = false)
	{
		state ??= new State(template);
		ProjectionStream bounded = (ProjectionStream)state.Reader.BaseStream;
		bounded.Attach(input);
		try
		{
			state.Reader.BigEndian = bigEndian;
			return Read(template, state, validateLayout, textureTail);
		}
		finally
		{
			// Scratch survives the object; the borrowed input must not survive this read.
			bounded.Detach();
		}
	}

	// The returned header is scratch owned by this shape, valid only until its next read.
	internal static UnityProjection ReadBorrowed(Stream input, long offset, long length, AssetTypeTemplateField template, bool bigEndian, State state, bool validateLayout = false, bool textureTail = false, bool captureRawName = false)
	{
		ProjectionStream bounded = (ProjectionStream)state.Reader.BaseStream;
		bounded.AttachRange(input, offset, length);
		try
		{
			state.Reader.BigEndian = bigEndian;
			return Read(template, state, validateLayout, textureTail, reuse: true, captureRawName: captureRawName);
		}
		finally { bounded.Detach(); }
	}
	internal void ClearScratch()
	{
		rawName.Clear(); values?.Clear(); Array.Clear(numbers); Array.Clear(texts); Array.Clear(ranges);
	}

	private static UnityProjection Read(AssetTypeTemplateField template, State state, bool validateLayout, bool textureTail, bool reuse = false, bool captureRawName = false)
	{
		AssetsFileReader reader = state.Reader;
		ProjectionStream bounded = (ProjectionStream)reader.BaseStream;
		UnityProjection result = reuse
			? state.Scratch ??= new(state)
			: new(state);
		if (reuse)
		{
			result.ClearScratch();
			if (!validateLayout && !state.DynamicPaths && !state.DictionaryStorage
				&& state.Route is { } route && route.Matches(template))
			{
				route.Read(result, reader, bounded, captureRawName);
				return result;
			}
		}
		ReusableAssetTypeValueIterator iterator = state.Iterator ??= new(template, reader);
		iterator.Reset(template, reader);
		string[] names = state.Names;
		int[] indices = state.Indices;
		int steps = 0;
		// One bounded scratch span per object; field count must never grow stack usage.
		Span<byte> textScratch = stackalloc byte[TextScratchLength];
		while (iterator.ReadNext())
		{
			if (++steps > FieldBudget) throw new InvalidDataException("Unity projection exceeded its field budget.");
			// State validates schema depth once; the iterator only visits those bounded nodes.
			AssetTypeTemplateField field = iterator.TempField;
			bool lastField = !validateLayout && ReferenceEquals(field, state.LastField);
			bool capture = state.Paths.TryGetValue(field, out Capture binding);
			string? dynamicPath = null;
			// Fixed schemas already bind capture paths; only atlases need runtime path bookkeeping.
			if (state.DynamicPaths)
			{
				int depth = iterator.Depth;
				string name = field.Name;
				if (names[1] == "m_RenderDataMap" && name == "data" && names[depth - 1] == "Array") name += "#" + indices[depth - 1]++;
				names[depth] = name;
				indices[depth] = 0;
				if (capture) dynamicPath = PathOf(names, depth);
			}
			if (field.ValueType == AssetValueType.ManagedReferencesRegistry)
				throw new InvalidDataException("Managed-reference projection is unsupported.");
			if (field.IsArray && field.ValueType != AssetValueType.ByteArray)
			{
				int count = iterator.CurrentArrayCount;
				if (count < 0 || count > FieldBudget) throw new InvalidDataException("Invalid Unity array count.");
			}
			else if (field.ValueType is AssetValueType.String or AssetValueType.ByteArray)
			{
				long position = reader.Position;
				int length = reader.ReadInt32();
				if (length < 0 || length > bounded.Length - reader.Position) throw new InvalidDataException("Invalid Unity byte range.");
				if (capture && binding.Slot.Kind == ValueKind.Text)
				{
					if (length > 1_048_576) throw new InvalidDataException("Unity metadata string is too long.");
					if (captureRawName && binding.Path == "m_Name")
					{
						Span<byte> name = result.rawName.GetSpan(length)[..length];
						int read = reader.BaseStream.ReadAtLeast(name, length, throwOnEndOfStream: false);
						result.rawName.Advance(read);
					}
					else result.SetText(binding, dynamicPath, ReadText(reader, length, textScratch));
					// Continuing traversal leaves advancement and alignment to the iterator.
					if (!lastField) reader.Position = position;
				}
				else
				{
					if (capture) result.SetBytes(binding, dynamicPath, new ByteRange(reader.Position, length));
					reader.Position = position;
				}
			}
			else if (capture)
			{
				long position = reader.Position;
				long number = field.ValueType switch
				{
					AssetValueType.Bool => reader.ReadBoolean() ? 1 : 0,
					AssetValueType.Int8 => reader.ReadSByte(),
					AssetValueType.UInt8 => reader.ReadByte(),
					AssetValueType.Int16 => reader.ReadInt16(),
					AssetValueType.UInt16 => reader.ReadUInt16(),
					AssetValueType.Int32 => reader.ReadInt32(),
					AssetValueType.UInt32 => reader.ReadUInt32(),
					AssetValueType.Int64 => reader.ReadInt64(),
					AssetValueType.UInt64 => unchecked((long)reader.ReadUInt64()),
					_ => throw new InvalidDataException("Integral catalogue metadata has a non-integral schema."),
				};
				// Peeking avoids a temporary AssetTypeValueField/Value tree. The mature iterator
				// still advances and aligns every scalar when ReadNext is called.
				reader.Position = position;
				result.SetNumber(binding, dynamicPath, number);
			}
			if (lastField) break;
		}
		if (validateLayout)
		{
			long tail = bounded.Length - reader.Position;
			// A mismatched schema may stop inside a payload. Never scan an unbounded unknown tail;
			// at most 4KiB of alignment/padding metadata is inspected before trying the next layout.
			if (tail > 4096) throw new InvalidDataException("Unity layout has an unbounded trailing range.");
			bool nonzero = false;
			for (long i = 0; i < tail; i++) nonzero |= reader.ReadByte() != 0;
			// Match the mature reader's order: zero padding first, then its Texture2D-family
			// 24-byte Chinese extension. These fields do not affect catalogue metadata.
			if (nonzero && (!textureTail || tail != 24)) throw new InvalidDataException("Unity layout does not consume the object.");
		}
		return result;
	}

	private static string ReadText(AssetsFileReader reader, int length, Span<byte> scratch)
	{
		byte[]? rented = null;
		Span<byte> bytes = length <= scratch.Length ? scratch[..length]
			: (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
		try
		{
			// Match ReadBytes: decode the available prefix on early EOF, without reading ahead.
			int read = reader.BaseStream.ReadAtLeast(bytes, length, throwOnEndOfStream: false);
			return Encoding.UTF8.GetString(bytes[..read]);
		}
		finally
		{
			if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
		}
	}
	internal sealed class State
	{
		public string[] Names { get; } = new string[130];
		public int[] Indices { get; } = new int[130];
		public Dictionary<AssetTypeTemplateField, Capture> Paths { get; } = [];
		// The reader owns only managed scratch; its stream borrows an input for one Read.
		public AssetsFileReader Reader { get; } = new(new ProjectionStream(Stream.Null));
		public UnityProjection? Scratch { get; set; }
		public ReusableAssetTypeValueIterator? Iterator { get; set; }
		public AssetTypeTemplateField? LastField { get; private set; }
		public bool DynamicPaths { get; }
		public Dictionary<string, Slot> Slots { get; } = new(StringComparer.Ordinal);
		public int NumberCount { get; private set; }
		public int TextCount { get; private set; }
		public int RangeCount { get; private set; }
		public bool DictionaryStorage { get; private set; }
		private readonly SchemaRoute? route;
		internal SchemaRoute? Route => route;
		public State(AssetTypeTemplateField template)
		{
			DynamicPaths = template.Type == "SpriteAtlas";
			DictionaryStorage = DynamicPaths;
			foreach (AssetTypeTemplateField field in template.Children) Visit(field, 1, false);
			route = SchemaRoute.TryCompile(template, this);
		}
		private void Visit(AssetTypeTemplateField field, int depth, bool repeated)
		{
			if (depth >= Names.Length) throw new InvalidDataException("Unity projection schema is too deep.");
			Names[depth] = field.Name;
			bool scalar = field.ValueType is not (AssetValueType.None or AssetValueType.Array);
			if (scalar)
			{
				if (Wanted(Names, depth))
				{
					string path = PathOf(Names, depth);
					ValueKind kind = field.ValueType == AssetValueType.String && path != "m_Script" ? ValueKind.Text
						: field.ValueType is AssetValueType.String or AssetValueType.ByteArray ? ValueKind.Bytes : ValueKind.Number;
					Slot slot;
					if (Slots.TryGetValue(path, out Slot existing))
					{
						if (existing.Kind != kind) DictionaryStorage = true;
						// Conflicting kinds use dictionary storage, but each field retains its own kind.
						slot = new(kind, existing.Index);
					}
					else
					{
						int index = kind switch { ValueKind.Text => TextCount++, ValueKind.Bytes => RangeCount++, _ => NumberCount++ };
						slot = new(kind, index);
						Slots.Add(path, slot);
					}
					Paths.Add(field, new(path, slot));
					// Fixed, unrepeated paths are all visited in schema order on a successful read.
					// Repeated arrays can be empty or first encounter keys in a different order;
					// keep the mature dictionary's presence, insertion order and last-write semantics.
					if (repeated) DictionaryStorage = true;
					// A field repeated inside an array cannot terminate the whole projection.
					LastField = repeated ? null : field;
				}
				return;
			}
			if (field.IsArray)
				Visit(field.Children[1], depth + 1, true);
			else
				foreach (AssetTypeTemplateField child in field.Children) Visit(child, depth + 1, repeated);
		}
	}

	private static string PathOf(string[] names, int depth) => string.Join('/', names, 1, depth);
	private static bool Wanted(string[] names, int depth)
	{
		string root = names[1];
		return root is "m_Name" or "m_Width" or "m_Height" or "image data" or "m_StreamData"
			or "m_Script" or "m_FontData" or "m_MovieData" or "m_OriginalPath" or "m_ExternalResources"
			or "m_AudioData" or "m_Type" or "m_Resource" or "m_IndexBuffer" or "m_SpriteAtlas" or "m_RenderDataKey"
			|| root == "m_VertexData" && names[2] is "m_VertexCount" or "m_DataSize"
			|| root == "m_CompressedMesh" && names[2] == "m_Vertices" && names[depth] == "m_NumItems"
			|| root == "m_RD" && names[2] == "texture"
			|| root == "m_RenderDataMap" && (depth < 5 || names[4] == "first" || names[5] == "texture");
	}

	// MemoryStream permits out-of-bounds seeks. The parser sees a strict object-sized view instead.
	internal sealed class ProjectionStream(Stream inner) : Stream
	{
		private bool range;
		private long origin, length, position;
		public void Attach(Stream input) { inner = input; range = false; }
		public void AttachRange(Stream input, long offset, long count)
		{
			if (offset < 0 || count < 0 || offset > input.Length || count > input.Length - offset)
				throw new ArgumentOutOfRangeException(nameof(offset));
			inner = input; range = true; origin = offset; length = count; position = 0;
		}
		public void Detach() { inner = Stream.Null; range = false; origin = length = position = 0; }
		// The route runs only against a borrowed range. Keep the source cursor on the same
		// logical offset so a subsequently failing read leaves the same physical position as
		// the mature per-field reader.
		internal void SyncBorrowedSource() => inner.Position = checked(origin + position);
		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => range ? length : inner.Length;
		public override long Position
		{
			get => range ? position : inner.Position;
			set
			{
				if (value < 0 || value > Length) throw new EndOfStreamException();
				if (range) position = value; else inner.Position = value;
			}
		}
		public override int Read(byte[] buffer, int offset, int count)
		{
			Bound(count);
			if (range) inner.Position = checked(origin + position);
			int read = inner.Read(buffer, offset, count); if (range) position += read; return read;
		}
		public override int Read(Span<byte> buffer)
		{
			Bound(buffer.Length);
			if (range) inner.Position = checked(origin + position);
			int read = inner.Read(buffer); if (range) position += read; return read;
		}
		public override int ReadByte()
		{
			Bound(1); if (range) inner.Position = checked(origin + position);
			int value = inner.ReadByte(); if (range && value >= 0) position++; return value;
		}
		private void Bound(int count) { if (count < 0 || count > Length - Position) throw new EndOfStreamException(); }
		public override long Seek(long offset, SeekOrigin origin) { Position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset); return Position; }
		public override void Flush() { }
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

}
