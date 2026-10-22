using System;
using System.Collections.Generic;
using System.IO;
using AssetsTools.NET;
namespace Unpackrat.UnityProbe;
internal sealed partial class UnityProjection
{
	internal sealed class SchemaRoute
	{
		private const int MaxScalarRunBytes = 512;
		private const int MaxSteps = 65_536;
		private enum StepKind : byte { Skip, Scalars, Text, Bytes, ArraySkip, ArrayGuard }
		private readonly record struct Scalar(int Offset, int Width, bool Signed, bool Boolean, int Slot);
		private readonly record struct Shape(int Width, bool Signed, bool Boolean);
		private readonly record struct Step(StepKind Kind, int Length, Scalar[] Scalars, int Slot,
			bool Capture, bool RawNameCandidate, bool Last, bool Align, int Rewind, int Visits,
			List<Scalar>? SkipFields = null, AssetTypeTemplateField? VariableArray = null);
		private readonly AssetTypeTemplateField template;
		private readonly Step[] steps;
		private SchemaRoute(AssetTypeTemplateField template, Step[] steps) => (this.template, this.steps) = (template, steps);
		public bool Matches(AssetTypeTemplateField candidate) => ReferenceEquals(template, candidate);
		public static SchemaRoute? TryCompile(AssetTypeTemplateField template, State state)
		{
			if (state.LastField is null || state.DynamicPaths || state.DictionaryStorage) return null;
			if (!WithinFieldBudget(template)) return null;
			Builder builder = new(state);
			foreach (AssetTypeTemplateField field in template.Children) builder.Field(field);
			return builder.Finish(template);
		}
		// Count the fields the mature iterator would visit, expanding None containers and
		// counting a shared alias at every occurrence. Strings and byte arrays are leaves in
		// that iterator, so their children are not counted. An ordinary array contributes one
		// element layout here; the route adds the runtime remainder when it reads an array.
		// A compiled route therefore keeps each fixed element layout below the shared budget.
		private static bool WithinFieldBudget(AssetTypeTemplateField template)
		{
			Dictionary<AssetTypeTemplateField, long> counted = [];
			long visits = 0;
			foreach (AssetTypeTemplateField field in template.Children)
			{
				visits = SaturatingAdd(visits, VisitCount(field, counted));
				if (visits > FieldBudget) return false;
			}
			return true;
		}
		private static long VisitCount(AssetTypeTemplateField field, Dictionary<AssetTypeTemplateField, long> counted)
		{
			if (counted.TryGetValue(field, out long known)) return known;
			long visits = 1;
			if (field.ValueType == AssetValueType.Array)
			{
				if (field.Children.Count > 1)
					visits = SaturatingAdd(visits, VisitCount(field.Children[1], counted));
			}
			else if (field.ValueType == AssetValueType.None)
			{
				foreach (AssetTypeTemplateField child in field.Children)
				{
					visits = SaturatingAdd(visits, VisitCount(child, counted));
					if (visits > FieldBudget) break;
				}
			}
			counted[field] = visits;
			return visits;
		}
		private static long SaturatingAdd(long left, long right)
			=> left > FieldBudget - right ? FieldBudget + 1 : left + right;
		public bool Read(UnityProjection target, AssetsFileReader reader, ProjectionStream bounded, bool captureRawName)
		{
			Span<byte> scalarBytes = stackalloc byte[MaxScalarRunBytes];
			Span<byte> textScratch = stackalloc byte[TextScratchLength];
			long visited = 0;
			foreach (Step step in steps)
			{
				switch (step.Kind)
				{
					case StepKind.Skip:
						ReadSkipStep(reader, bounded, step, ref visited);
						break;
					case StepKind.Scalars:
						if (step.Visits > FieldBudget - visited)
						{
							ReadScalarsPerField(target, reader, bounded, scalarBytes[..step.Length], step, ref visited);
						}
						else
						{
							AddVisits(ref visited, step.Visits);
							ReadScalars(target, reader, bounded, scalarBytes[..step.Length], step);
						}
						break;
					case StepKind.Text:
						AddVisits(ref visited, step.Visits);
						if (!ReadTextStep(target, reader, bounded, step, captureRawName, textScratch)) return true;
						break;
					case StepKind.Bytes:
						AddVisits(ref visited, step.Visits);
						if (!ReadBytesStep(target, reader, bounded, step)) return true;
						break;
					case StepKind.ArraySkip:
						ReadArraySkipStep(reader, bounded, step, ref visited);
						break;
					case StepKind.ArrayGuard:
						if (!ReadArrayGuardStep(target.state, reader, bounded, step, ref visited)) return false;
						break;
				}
			}
			return true;
		}
		private static void AddVisits(ref long visited, int count)
		{
			visited += count;
			if (visited > FieldBudget) throw new InvalidDataException("Unity projection exceeded its field budget.");
		}
		private static void ReadSkipStep(AssetsFileReader reader, ProjectionStream bounded, Step step, ref long visited)
		{
			long start = reader.Position;
			if (step.Visits > FieldBudget - visited)
			{
				// Replay a merged skip field by field so a budget failure leaves the borrowed
				// source on the same field boundary as the mature iterator. The normal path
				// remains one seek. Selection guarantees the budget fails before this replay
				// can exhaust its fields: every iteration consumes one visit.
				int index = 0;
				for (;;)
				{
					Scalar field = step.SkipFields![index++];
					long fieldStart = checked(start + field.Offset);
					reader.Position = fieldStart;
					AddVisits(ref visited, 1);
					reader.Position = checked(fieldStart + field.Width);
				}
			}
			else
			{
				AddVisits(ref visited, step.Visits);
				if (step.Length != 0) reader.Position = checked(start + step.Length);
			}
			if (step.Align) reader.Align();
		}
		private static void ReadArraySkipStep(AssetsFileReader reader, ProjectionStream bounded, Step step, ref long visited)
		{
			int count = reader.ReadInt32();
			AddVisits(ref visited, step.Visits);
			if (count < 0 || count > FieldBudget) throw new InvalidDataException("Invalid Unity array count.");
			long elementFields = (long)count * step.Scalars.Length;
			long allowed = FieldBudget - visited;
			long overflowField = elementFields > allowed ? allowed : -1;
			long dataStart = reader.Position;
			long bytes = (long)count * step.Length;
			long available = bounded.Length - dataStart;
			long eofField = -1;
			long eofCursor = dataStart;
			if (bytes > available)
			{
				long complete = available / step.Length;
				long elementBase = dataStart + complete * step.Length;
				long within = available - complete * step.Length;
				for (int index = 0; index < step.Scalars.Length; index++)
				{
					Scalar field = step.Scalars[index];
					if (field.Offset + (long)field.Width > within)
					{
						eofField = complete * step.Scalars.Length + index;
						eofCursor = elementBase + field.Offset;
						break;
					}
				}
			}
			if (overflowField >= 0 && (eofField < 0 || overflowField <= eofField))
			{
				long element = overflowField / step.Scalars.Length;
				int index = (int)(overflowField % step.Scalars.Length);
				reader.Position = checked(dataStart + element * step.Length + step.Scalars[index].Offset);
				throw new InvalidDataException("Unity projection exceeded its field budget.");
			}
			if (eofField >= 0)
			{
				reader.Position = eofCursor;
				throw new EndOfStreamException();
			}
			visited += elementFields;
			if (bytes != 0) reader.Position = checked(dataStart + bytes);
			if (step.Align) reader.Align();
		}
		// The mature iterator owns traversal for variable arrays with a skippable element layout.
		// It starts at the count prefix, so the prefix is read exactly once and nested counts,
		// string and byte-array ranges, and alignment stay on the mature code path without
		// materializing values. Unsupported element schemas keep the whole-object fallback.
		private static bool ReadArrayGuardStep(State state, AssetsFileReader reader, ProjectionStream bounded,
			Step step, ref long visited)
		{
			if (step.VariableArray is null) return false;
			ReusableAssetTypeValueIterator iterator = state.Iterator ??= new(step.VariableArray, reader);
			iterator.Reset(step.VariableArray, reader);
			bool first = true;
			while (iterator.ReadNext())
			{
				AddVisits(ref visited, first ? step.Visits : 1);
				first = false;
				AssetTypeTemplateField field = iterator.TempField;
				if (field.IsArray && field.ValueType != AssetValueType.ByteArray)
				{
					int nested = iterator.CurrentArrayCount;
					if (nested < 0 || nested > state.VisitBudget)
						throw new InvalidDataException("Invalid Unity array count.");
				}
				else if (field.ValueType is AssetValueType.String or AssetValueType.ByteArray)
				{
					long position = reader.Position;
					int length = reader.ReadInt32();
					if (length < 0 || length > bounded.Length - reader.Position)
						throw new InvalidDataException("Invalid Unity byte range.");
					reader.Position = position;
				}
			}
			// The synthetic root has one supported array child, so ReadNext yields that
			// array node even when its count is zero; first is never true after the loop.
			if (step.Align) reader.Align();
			return true;
		}
		// Selected only when the run exceeds the remaining shared budget, so replaying each
		// captured field ends at the mature iterator's budget failure and source cursor.
		private static void ReadScalarsPerField(UnityProjection target, AssetsFileReader reader, ProjectionStream bounded,
			Span<byte> bytes, Step step, ref long visited)
		{
			// Every iteration consumes one visit, so AddVisits fails before this fixed layout
			// can be exhausted.
			int index = 0;
			for (;;)
			{
				AddVisits(ref visited, 1);
				Scalar scalar = step.Scalars[index++];
				bounded.SyncBorrowedSource();
				reader.BaseStream.ReadExactly(bytes.Slice(scalar.Offset, scalar.Width));
				target.numbers[scalar.Slot] = Decode(bytes.Slice(scalar.Offset, scalar.Width), scalar, reader.BigEndian);
			}
		}
		private static void ReadScalars(UnityProjection target, AssetsFileReader reader, ProjectionStream bounded,
			Span<byte> bytes, Step step)
		{
			long start = reader.Position;
			if (step.Scalars.Length == 1 || bytes.Length > bounded.Length - start)
			{
				// The mature reader visits captured scalars one at a time. A single scalar needs
				// no batch, and a run crossing the logical end must reproduce that field-at-a-time
				// source cursor and EndOfStreamException boundary rather than failing as a batch.
				foreach (Scalar scalar in step.Scalars)
				{
					bounded.SyncBorrowedSource();
					reader.BaseStream.ReadExactly(bytes.Slice(scalar.Offset, scalar.Width));
					target.numbers[scalar.Slot] = Decode(bytes.Slice(scalar.Offset, scalar.Width), scalar, reader.BigEndian);
				}
			}
			else
			{
				reader.BaseStream.ReadExactly(bytes);
				foreach (Scalar scalar in step.Scalars)
					target.numbers[scalar.Slot] = Decode(bytes.Slice(scalar.Offset, scalar.Width), scalar, reader.BigEndian);
			}
			if (step.Last) reader.Position = checked(start + step.Rewind);
			else if (step.Align) reader.Align();
		}
		private static bool ReadTextStep(UnityProjection target, AssetsFileReader reader, ProjectionStream bounded,
			Step step, bool captureRawName, Span<byte> textScratch)
		{
			long start = reader.Position;
			int length = reader.ReadInt32();
			if (length < 0 || length > bounded.Length - reader.Position)
				throw new InvalidDataException("Invalid Unity byte range.");
			if (step.Capture)
			{
				// Match the old peek: a raw name consumes its bytes even when it is the last field.
				if (length > 1_048_576) throw new InvalidDataException("Unity metadata string is too long.");
				if (step.RawNameCandidate && captureRawName)
				{
					Span<byte> name = target.rawName.GetSpan(length)[..length];
					int read = reader.BaseStream.ReadAtLeast(name, length, throwOnEndOfStream: false);
					target.rawName.Advance(read);
				}
				else target.texts[step.Slot] = ReadText(reader, length, textScratch);
			}
			if (step.Last) return false;
			reader.Position = checked(start + 4 + length);
			if (step.Align) reader.Align();
			return true;
		}
		private static bool ReadBytesStep(UnityProjection target, AssetsFileReader reader, ProjectionStream bounded, Step step)
		{
			long start = reader.Position;
			int length = reader.ReadInt32();
			if (length < 0 || length > bounded.Length - reader.Position)
				throw new InvalidDataException("Invalid Unity byte range.");
			if (step.Capture) target.ranges[step.Slot] = new ByteRange(reader.Position, length);
			if (step.Last)
			{
				reader.Position = start;
				return false;
			}
			reader.Position = checked(start + 4 + length);
			if (step.Align) reader.Align();
			return true;
		}
		// Widths are fixed at compile time to 1, 2, 4, or 8 bytes; a bit-preserving fold keeps
		// signed/unsigned and both endiannesses on one path without an invalid-kind branch.
		private static long Decode(ReadOnlySpan<byte> bytes, in Scalar scalar, bool bigEndian)
		{
			if (scalar.Boolean) return bytes[0] != 0 ? 1 : 0;
			ulong raw = 0;
			for (int index = 0; index < bytes.Length; index++)
			{
				if (bigEndian) raw = (raw << 8) | bytes[index];
				else raw |= (ulong)bytes[index] << (index * 8);
			}
			if (!scalar.Signed || bytes.Length == 8) return unchecked((long)raw);
			int shift = 64 - bytes.Length * 8;
			return (long)(raw << shift) >> shift;
		}
		private sealed class Builder(State state)
		{
			private readonly List<Step> steps = [];
			private readonly List<Scalar> scalars = [];
			private bool supported = true;
			private bool finished;
			private int scalarBytes;
			public void Field(AssetTypeTemplateField field)
			{
				if (!supported || finished) return;
				bool last = ReferenceEquals(field, state.LastField);
				bool captured = state.Paths.TryGetValue(field, out Capture capture);
				if (TryShape(field.ValueType, out Shape shape))
				{
					AddNumber(field, shape, captured, capture, last);
					return;
				}
				switch (field.ValueType)
				{
					case AssetValueType.None:
						if (field.IsArray)
						{
							AddArray(field);
							return;
						}
						AddSkip(0, align: false, visit: true);
						foreach (AssetTypeTemplateField child in field.Children) Field(child);
						if (!finished && field.IsAligned) AddSkip(0, align: true);
						return;
					case AssetValueType.Array:
						AddArray(field);
						return;
					case AssetValueType.String:
						FlushScalars(terminal: false, align: false);
						AddString(field, captured, capture, last);
						return;
					case AssetValueType.ByteArray:
						FlushScalars(terminal: false, align: false);
						AddBytes(field, captured, capture, last);
						return;
					default:
						supported = false;
						return;
				}
			}
			private void AddNumber(AssetTypeTemplateField field, Shape shape, bool captured, Capture capture, bool last)
			{
				if (!captured)
				{
					FlushScalars(terminal: false, align: false);
					AddSkip(shape.Width, field.IsAligned, visit: true);
					return;
				}
				if (scalarBytes + shape.Width > MaxScalarRunBytes) FlushScalars(terminal: false, align: false);
				scalars.Add(new Scalar(scalarBytes, shape.Width, shape.Signed, shape.Boolean, capture.Slot.Index));
				scalarBytes += shape.Width;
				if (last)
				{
					FlushScalars(terminal: true, align: false);
					finished = true;
				}
				else if (field.IsAligned) FlushScalars(terminal: false, align: true);
			}
			private void AddArray(AssetTypeTemplateField field)
			{
				// State never binds an array node itself; captured element fields disable the
				// route through DictionaryStorage before this builder runs.
				if (field.Children.Count < 2)
				{
					supported = false;
					return;
				}
				FlushScalars(terminal: false, align: false);
				if (TryFixedElement(field.Children[1], out int stride, out Scalar[] layout))
				{
					AddStep(StepKind.ArraySkip, stride, -1, capture: false, raw: false, last: false,
						align: field.IsAligned, scalars: layout);
				}
				else
				{
					// A variable element layout is traversed by the mature iterator. Unsupported
					// element schemas keep the whole-object fallback.
					AssetTypeTemplateField? variable = CanSkip(field.Children[1]) ? VariableArrayRoot(field) : null;
					AddStep(StepKind.ArrayGuard, 0, -1, capture: false, raw: false, last: false,
						align: field.IsAligned, variableArray: variable);
				}
			}
			private static bool CanSkip(AssetTypeTemplateField field)
			{
				if (field.ValueType == AssetValueType.Array) return CanSkip(field.Children[1]);
				if (field.ValueType == AssetValueType.None)
				{
					foreach (AssetTypeTemplateField child in field.Children)
						if (!CanSkip(child)) return false;
					return true;
				}
				return field.ValueType is AssetValueType.Bool or AssetValueType.Int8 or AssetValueType.UInt8
					or AssetValueType.Int16 or AssetValueType.UInt16 or AssetValueType.Int32 or AssetValueType.UInt32
					or AssetValueType.Int64 or AssetValueType.UInt64 or AssetValueType.Float or AssetValueType.Double
					or AssetValueType.String or AssetValueType.ByteArray;
			}
			private static AssetTypeTemplateField VariableArrayRoot(AssetTypeTemplateField field)
				=> new()
				{
					Name = "VariableArray",
					Type = "VariableArray",
					ValueType = AssetValueType.None,
					HasValue = false,
					IsArray = false,
					Children = [field],
				};
			private bool TryFixedElement(AssetTypeTemplateField field, out int stride, out Scalar[] layout)
			{
				List<Scalar> fields = [];
				long width = 0;
				if (!TryFixedField(field, fields, ref width))
				{
					stride = 0;
					layout = [];
					return false;
				}
				// VisitCount bounds a successful layout below FieldBudget, and each scalar is
				// at most 8 bytes, so width is non-negative and fits the stride.
				stride = (int)width;
				layout = fields.ToArray();
				return true;
			}
			private bool TryFixedField(AssetTypeTemplateField field, List<Scalar> layout, ref long width)
			{
				// VisitCount bounds this layout below FieldBudget before compilation, so no
				// runtime width or count guard is reachable here. Captured fields mark repeated
				// visits as DictionaryStorage, which prevents the route from being compiled.
				if (field.IsArray || field.IsAligned) return false;
				if (TryShape(field.ValueType, out Shape shape))
				{
					layout.Add(new Scalar((int)width, shape.Width, shape.Signed, shape.Boolean, -1));
					width += shape.Width;
					return true;
				}
				if (field.ValueType != AssetValueType.None) return false;
				layout.Add(new Scalar((int)width, 0, false, false, -1));
				foreach (AssetTypeTemplateField child in field.Children)
				{
					if (!TryFixedField(child, layout, ref width)) return false;
				}
				return true;
			}
			private void AddString(AssetTypeTemplateField field, bool captured, Capture capture, bool last)
			{
				bool align = (field.Children.Count > 0 ? field.Children[0].IsAligned : true) || field.IsAligned;
				if (!captured)
				{
					AddStep(StepKind.Text, 0, -1, capture: false, raw: false, last: false, align: align);
				}
				else
				{
					// State maps a captured string to Text, or to Bytes for m_Script.
					bool bytes = capture.Slot.Kind == ValueKind.Bytes;
					AddStep(bytes ? StepKind.Bytes : StepKind.Text, 0, capture.Slot.Index, capture: true,
						raw: !bytes && capture.Path == "m_Name", last: last, align: align);
				}
				if (last) finished = true;
			}
			private void AddBytes(AssetTypeTemplateField field, bool captured, Capture capture, bool last)
			{
				AddStep(StepKind.Bytes, 0, captured ? capture.Slot.Index : -1,
					capture: captured, raw: false, last: last, align: field.IsAligned);
				if (last) finished = true;
			}
			private void AddSkip(int length, bool align, bool visit = false)
			{
				FlushScalars(terminal: false, align: false);
				List<Scalar>? layout = visit ? new List<Scalar> { new(0, length, false, false, -1) } : null;
				if (steps.Count > 0 && steps[^1] is { Kind: StepKind.Skip, Align: false } last)
				{
					if (visit) last.SkipFields!.Add(new Scalar(last.Length, length, false, false, -1));
					steps[^1] = last with
					{
						Length = checked(last.Length + length),
						Align = align,
						Visits = checked(last.Visits + (visit ? 1 : 0)),
					};
					return;
				}
				// A zero-length skip still counts a traversal visit or applies alignment.
				AddStep(StepKind.Skip, length, -1, capture: false, raw: false, last: false, align,
					visits: visit ? 1 : 0, skipFields: layout);
			}
			private void AddStep(StepKind kind, int length, int slot, bool capture, bool raw, bool last, bool align,
				Scalar[]? scalars = null, int visits = 1, List<Scalar>? skipFields = null,
				AssetTypeTemplateField? variableArray = null)
			{
				if (steps.Count >= MaxSteps)
				{
					supported = false;
					return;
				}
				steps.Add(new Step(kind, length, scalars ?? [], slot, capture, raw, last, align, 0, visits,
					skipFields, variableArray));
			}
			public void FlushScalars(bool terminal, bool align)
			{
				if (scalars.Count == 0) return;
				if (steps.Count >= MaxSteps)
				{
					supported = false;
					scalars.Clear();
					scalarBytes = 0;
					return;
				}
				int rewind = terminal ? scalars[^1].Offset : 0;
				int visits = scalars.Count;
				steps.Add(new Step(StepKind.Scalars, scalarBytes, scalars.ToArray(), 0, false, false,
					terminal, align && !terminal, rewind, visits));
				scalars.Clear();
				scalarBytes = 0;
			}
			public SchemaRoute? Finish(AssetTypeTemplateField template)
			{
				if (!supported) return null;
				FlushScalars(terminal: false, align: false);
				return new SchemaRoute(template, steps.ToArray());
			}
			private static bool TryShape(AssetValueType kind, out Shape shape)
			{
				switch (kind)
				{
					case AssetValueType.Bool: shape = new(1, false, true); return true;
					case AssetValueType.Int8: shape = new(1, true, false); return true;
					case AssetValueType.UInt8: shape = new(1, false, false); return true;
					case AssetValueType.Int16: shape = new(2, true, false); return true;
					case AssetValueType.UInt16: shape = new(2, false, false); return true;
					case AssetValueType.Int32: shape = new(4, true, false); return true;
					case AssetValueType.UInt32: shape = new(4, false, false); return true;
					case AssetValueType.Int64: shape = new(8, true, false); return true;
					case AssetValueType.UInt64: shape = new(8, false, false); return true;
					// A captured float/double is stored as its raw IEEE payload, never as a
					// rounded integral value. Unsigned decoding keeps every bit unchanged.
					case AssetValueType.Float: shape = new(4, false, false); return true;
					case AssetValueType.Double: shape = new(8, false, false); return true;
					default: shape = default; return false;
				}
			}
		}
	}
}
