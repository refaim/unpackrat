using System;
using System.Collections.Generic;
using System.IO;
using AssetsTools.NET;

namespace Unpackrat.UnityProbe;

internal sealed partial class UnityProjection
{
    private const int QuadIndexedElements = 32;

    internal sealed partial class State
    {
        internal QuadPlan? QuadPlan { get; set; }

        private void CompileQuadPlan(AssetTypeTemplateField template)
            => QuadPlan = QuadPlan.TryCompile(template, this);
    }

    private sealed class QuadStorage
    {
        private readonly bool[] present;
        private readonly int[] order;
        private int count;

        internal QuadStorage(QuadPlan plan)
        {
            Plan = plan;
            int slots = plan.SlotCount;
            Numbers = new long[slots];
            Texts = new string?[slots];
            Ranges = new ByteRange[slots];
            present = new bool[slots];
            order = new int[slots];
        }

        internal QuadPlan Plan { get; }
        internal long[] Numbers { get; }
        internal string?[] Texts { get; }
        internal ByteRange[] Ranges { get; }
        internal bool Active { get; set; }

        internal IEnumerable<string> Keys
        {
            get { for (int i = 0; i < count; i++) yield return Plan.Keys[order[i]]; }
        }

        internal void Clear()
        {
            Active = false;
            Array.Clear(present);
            Array.Clear(Numbers);
            Array.Clear(Texts);
            Array.Clear(Ranges);
            count = 0;
        }

        private void Mark(int slot)
        {
            if (present[slot]) return;
            present[slot] = true;
            order[count++] = slot;
        }

        internal void SetNumber(int slot, long value) { Mark(slot); Numbers[slot] = value; }
        internal void SetText(int slot, string value) { Mark(slot); Texts[slot] = value; }
        internal void SetRange(int slot, ByteRange value) { Mark(slot); Ranges[slot] = value; }

        internal bool TrySlot(string path, ValueKind kind, out int slot)
        {
            if (Plan.Slots.TryGetValue(path, out slot) && present[slot] && Plan.Kinds[slot] == kind) return true;
            slot = 0;
            return false;
        }
    }

    internal sealed class QuadPlan
    {
        private const int MaxPlanSlots = 8192;

        private readonly struct Binding(int baseSlot, int dataDepth)
        {
            public readonly int BaseSlot = baseSlot;
            public readonly int DataDepth = dataDepth;
        }

        private readonly AssetTypeTemplateField root;

        private QuadPlan(AssetTypeTemplateField root, Dictionary<AssetTypeTemplateField, Binding> bindings,
            Dictionary<string, int> slots, ValueKind[] kinds, string[] keys)
        {
            this.root = root;
            Bindings = bindings;
            Slots = slots;
            Kinds = kinds;
            Keys = keys;
        }

        private Dictionary<AssetTypeTemplateField, Binding> Bindings { get; }
        internal Dictionary<string, int> Slots { get; }
        internal ValueKind[] Kinds { get; }
        internal string[] Keys { get; }
        internal int SlotCount => Keys.Length;

        internal static QuadPlan? TryCompile(AssetTypeTemplateField template, State state)
        {
            if (state.Paths.Count == 0) return null;
            Builder builder = new(state);
            foreach (AssetTypeTemplateField field in template.Children)
                if (!builder.Walk(field, 1, 0)) return null;
            return builder.Build(template);
        }

        internal bool Read(UnityProjection target, AssetsFileReader reader, ProjectionStream bounded,
            AssetTypeTemplateField template, bool captureRawName)
        {
            if (captureRawName) return false;
            // Bindings belong to this exact root instance. A reused child object under a
            // different wrapper must use the mature runtime path rules.
            if (!ReferenceEquals(root, template)) return false;
            State state = target.state;
            QuadStorage store = target.quadStorage!;
            ReusableAssetTypeValueIterator iterator = state.Iterator ??= new(template, reader);
            iterator.Reset(template, reader);
            Array.Clear(state.Names);
            Array.Clear(state.Indices);
            int steps = 0;
            Span<byte> textScratch = stackalloc byte[TextScratchLength];

            while (iterator.ReadNext())
            {
                if (++steps > state.VisitBudget) throw new InvalidDataException("Unity projection exceeded its field budget.");
                AssetTypeTemplateField field = iterator.TempField;
                bool lastField = ReferenceEquals(field, state.LastField);
                int depth = iterator.Depth;
                if (field.Name == "data" && depth > 0 && state.Names[depth - 1] == "Array")
                {
                    int element = state.Indices[depth - 1] + 1;
                    if (element > QuadIndexedElements) return false;
                    state.Indices[depth - 1] = element;
                }
                // State.Visit rejected every template path at depth >= Names.Length;
                // the runtime iterator follows that same template.
                state.Names[depth] = field.Name;
                state.Indices[depth] = 0;

                bool captured = Bindings.TryGetValue(field, out Binding binding);
                if (field.ValueType == AssetValueType.ManagedReferencesRegistry)
                    throw new InvalidDataException("Managed-reference projection is unsupported.");
                if (field.IsArray && field.ValueType != AssetValueType.ByteArray)
                {
                    int count = iterator.CurrentArrayCount;
                    if (count < 0 || count > state.VisitBudget) throw new InvalidDataException("Invalid Unity array count.");
                    if (count > QuadIndexedElements) return false;
                }
                else if (field.ValueType is AssetValueType.String or AssetValueType.ByteArray)
                {
                    long position = reader.Position;
                    int length = reader.ReadInt32();
                    if (length < 0 || length > bounded.Length - reader.Position)
                        throw new InvalidDataException("Invalid Unity byte range.");
                    if (captured)
                    {
                        int slot = Slot(binding, state);
                        ValueKind kind = Kinds[slot];
                        if (kind == ValueKind.Text)
                        {
                            if (length > 1_048_576) throw new InvalidDataException("Unity metadata string is too long.");
                            store.SetText(slot, ReadText(reader, length, textScratch));
                            if (!lastField) reader.Position = position;
                        }
                        else
                        {
                            // State.Visit publishes String/ByteArray captures as Text or Bytes only.
                            store.SetRange(slot, new ByteRange(reader.Position, length));
                            reader.Position = position;
                        }
                    }
                    else reader.Position = position;
                }
                else if (captured)
                {
                    int slot = Slot(binding, state);
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
                        AssetValueType.Float => reader.ReadUInt32(),
                        // Captured fields exclude None/Array; ManagedReferencesRegistry is
                        // rejected above, and String/ByteArray take the branch above. Every
                        // remaining numeric variant except Double is explicit.
                        _ => unchecked((long)reader.ReadUInt64()),
                    };
                    reader.Position = position;
                    // Every non-string scalar capture compiles to Number, Float or Double.
                    store.SetNumber(slot, number);
                }
                if (lastField) break;
            }
            store.Active = true;
            return true;
        }

        private static int Slot(Binding binding, State state)
        {
            if (binding.DataDepth == 0) return binding.BaseSlot;
            // The array-count guard rejects extra elements before any data child is visited.
            return binding.BaseSlot + state.Indices[binding.DataDepth - 1] - 1;
        }

        private sealed class Builder(State state)
        {
            private readonly string[] names = new string[state.Names.Length];
            private readonly List<string> keys = [];
            private readonly List<ValueKind> kinds = [];
            private readonly Dictionary<string, int> slots = new(StringComparer.Ordinal);
            private readonly Dictionary<AssetTypeTemplateField, Binding> bindings = [];
            private readonly Dictionary<AssetTypeTemplateField, bool> wanted = [];

            public bool Walk(AssetTypeTemplateField field, int depth, int dataDepth)
            {
                if (!HasCapture(field)) return true;
                // State.Visit rejects depth >= names.Length before TryCompile runs.
                names[depth] = field.Name;
                if (field.ValueType is not (AssetValueType.None or AssetValueType.Array))
                {
                    if (!state.Paths.TryGetValue(field, out Capture capture)) return true;
                    string path = UnityProjection.PathOf(names, depth);
                    if (path != capture.Path) return false;
                    if (dataDepth == 0)
                    {
                        int slot;
                        if (slots.TryGetValue(path, out slot))
                        {
                            if (kinds[slot] != capture.Slot.Kind) return false;
                        }
                        else
                        {
                            if (keys.Count >= MaxPlanSlots) return false;
                            slot = keys.Count;
                            Add(path, capture.Slot.Kind);
                        }
                        return bindings.TryAdd(field, new Binding(slot, 0));
                    }
                    // A non-zero dataDepth is recorded only for an exact Array/data
                    // component at that component's depth and is never changed later.
                    if (keys.Count + QuadIndexedElements > MaxPlanSlots) return false;
                    string[] parts = path.Split('/');
                    // parts.Length == depth proves no name contains '/', so parts align
                    // with names and parts[dataDepth - 1] is that exact data component.
                    if (parts.Length != depth) return false;
                    int baseSlot = keys.Count;
                    for (int i = 0; i < QuadIndexedElements; i++)
                    {
                        parts[dataDepth - 1] = "data#" + i;
                        string key = string.Join('/', parts);
                        if (slots.ContainsKey(key)) return false;
                        Add(key, capture.Slot.Kind);
                    }
                    return bindings.TryAdd(field, new Binding(baseSlot, dataDepth));
                }
                if (field.IsArray)
                {
                    // State.Visit already indexed Children[1] for every visited array.
                    if (dataDepth != 0) return false;
                    AssetTypeTemplateField element = field.Children[1];
                    bool indexed = field.Name == "Array" && element.Name == "data";
                    return Walk(element, depth + 1, indexed ? depth + 1 : 0);
                }
                // Mature Quad naming indexes data under any field named Array. A plain
                // composite cannot share that rule with an actual indexed array.
                if (field.Name == "Array") return false;
                foreach (AssetTypeTemplateField child in field.Children)
                    if (!Walk(child, depth + 1, dataDepth)) return false;
                return true;
            }

            private void Add(string key, ValueKind kind)
            {
                slots.Add(key, keys.Count);
                keys.Add(key);
                kinds.Add(kind);
            }

            private bool HasCapture(AssetTypeTemplateField field)
            {
                if (wanted.TryGetValue(field, out bool known)) return known;
                bool result = state.Paths.ContainsKey(field);
                if (!result)
                    foreach (AssetTypeTemplateField child in field.Children)
                        if (HasCapture(child)) { result = true; break; }
                wanted.Add(field, result);
                return result;
            }

            // TryCompile calls Build only after Walk accepted at least one capture.
            public QuadPlan Build(AssetTypeTemplateField root)
                => new(root, bindings, slots, kinds.ToArray(), keys.ToArray());
        }
    }
}
