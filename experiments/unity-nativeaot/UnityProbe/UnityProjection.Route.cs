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
		private enum StepKind : byte { Skip, Scalars, Text, Bytes }
		private readonly record struct Scalar(int Offset, int Width, bool Signed, bool Boolean, int Slot);
		private readonly record struct Shape(int Width, bool Signed, bool Boolean, bool Decodable);
		private readonly record struct Step(StepKind Kind, int Length, Scalar[] Scalars, int Slot,
			bool Capture, bool RawNameCandidate, bool Last, bool Align, int Rewind);
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
		// that iterator, so their children are not counted. An ordinary array expands by a
		// runtime count that cannot be certified statically; the builder below always declines
		// those layouts, so the iterator keeps the runtime budget for them.
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
			if (field.ValueType == AssetValueType.None)
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
		public void Read(UnityProjection target, AssetsFileReader reader, ProjectionStream bounded, bool captureRawName)
		{
			Span<byte> scalarBytes = stackalloc byte[MaxScalarRunBytes];
			Span<byte> textScratch = stackalloc byte[TextScratchLength];
			foreach (Step step in steps)
			{
				switch (step.Kind)
				{
					case StepKind.Skip:
						if (step.Length != 0) reader.Position = checked(reader.Position + step.Length);
						if (step.Align) reader.Align();
						break;
					case StepKind.Scalars:
						ReadScalars(target, reader, bounded, scalarBytes[..step.Length], step);
						break;
					case StepKind.Text:
						if (!ReadTextStep(target, reader, bounded, step, captureRawName, textScratch)) return;
						break;
					case StepKind.Bytes:
						if (!ReadBytesStep(target, reader, bounded, step)) return;
						break;
				}
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
							supported = false;
							return;
						}
						foreach (AssetTypeTemplateField child in field.Children) Field(child);
						if (!finished && field.IsAligned) AddSkip(0, align: true);
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
					AddSkip(shape.Width, field.IsAligned);
					return;
				}
				if (!shape.Decodable)
				{
					supported = false;
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
			private void AddSkip(int length, bool align)
			{
				FlushScalars(terminal: false, align: false);
				if (steps.Count > 0 && steps[^1] is { Kind: StepKind.Skip, Align: false } last)
				{ steps[^1] = last with { Length = checked(last.Length + length), Align = align }; return; }
				// Callers supply a positive width or request alignment, so zero-length unaligned skips cannot occur.
				AddStep(StepKind.Skip, length, -1, capture: false, raw: false, last: false, align);
			}
			private void AddStep(StepKind kind, int length, int slot, bool capture, bool raw, bool last, bool align)
			{
				if (steps.Count >= MaxSteps)
				{
					supported = false;
					return;
				}
				steps.Add(new Step(kind, length, [], slot, capture, raw, last, align, 0));
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
				steps.Add(new Step(StepKind.Scalars, scalarBytes, scalars.ToArray(), 0, false, false,
					terminal, align && !terminal, rewind));
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
					case AssetValueType.Bool: shape = new(1, false, true, true); return true;
					case AssetValueType.Int8: shape = new(1, true, false, true); return true;
					case AssetValueType.UInt8: shape = new(1, false, false, true); return true;
					case AssetValueType.Int16: shape = new(2, true, false, true); return true;
					case AssetValueType.UInt16: shape = new(2, false, false, true); return true;
					case AssetValueType.Int32: shape = new(4, true, false, true); return true;
					case AssetValueType.UInt32: shape = new(4, false, false, true); return true;
					case AssetValueType.Int64: shape = new(8, true, false, true); return true;
					case AssetValueType.UInt64: shape = new(8, false, false, true); return true;
					case AssetValueType.Float: shape = new(4, false, false, false); return true;
					case AssetValueType.Double: shape = new(8, false, false, false); return true;
					default: shape = default; return false;
				}
			}
		}
	}
}
