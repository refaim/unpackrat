// MIT licensed upstream AssetsTools.NET iterator; see LICENSE and PROVENANCE.md.
#nullable disable
using AssetsTools.NET;
using System;
using System.Collections.Generic;
using System.IO;

namespace Unpackrat.UnityProbe
{
    internal sealed class ReusableAssetTypeValueIterator
    {
        private AssetTypeTemplateField baseTempField;
        private AssetsFileReader reader;
        private RefTypeManager refMan;
        private readonly Stack<Frame> tempFieldStack;
        private readonly List<Frame> frames = new List<Frame>();

        private AssetTypeValueField valueFieldCache;
        private readonly long basePosition;

        public AssetTypeTemplateField TempField;

        // Valid after successful ReadNext only when TempField is an ordinary (not byte) array.
        internal int CurrentArrayCount { get; private set; }

        public AssetTypeValueField ReadValueField()
        {
            if (valueFieldCache == null)
            {
                valueFieldCache = new AssetTypeValueField()
                {
                    TemplateField = TempField
                };

                // rewind since we already read the length
                if (TempField.IsArray && TempField.ValueType != AssetValueType.ByteArray)
                {
                    reader.Position -= 4;
                }

                TempField.ReadType(reader, valueFieldCache, refMan);

                // prevent iterating the children now that we've read it
                if (TempField.Children.Count > 0 && TempField.ValueType != AssetValueType.String)
                {
                    Frame topItem = tempFieldStack.Peek();
                    do
                    {
                        // special case for arrays: we need to align here rather than below
                        // because we create child iterators for arrays rather than depend
                        // on the template field.
                        if (topItem.Current != null
                            && topItem.Current.IsArray
                            && topItem.Current.ValueType != AssetValueType.ByteArray
                            && topItem.Current.IsAligned)
                        {
                            reader.Align();
                        }
                    } while (topItem.MoveNext());
                }
            }
            return valueFieldCache;
        }

        public List<AssetTypeTemplateField> TempFieldStack
        {
            get
            {
                List<AssetTypeTemplateField> stack = new List<AssetTypeTemplateField>(tempFieldStack.Count);
                foreach (Frame stackItem in tempFieldStack)
                {
                    stack.Add(stackItem.Current);
                }

                return stack;
            }
        }

        public int Depth
        {
            get
            {
                bool canHaveChildren = TempField.ValueType switch
                {
                    AssetValueType.None => true,
                    AssetValueType.Bool => false,
                    AssetValueType.Int8 => false,
                    AssetValueType.UInt8 => false,
                    AssetValueType.Int16 => false,
                    AssetValueType.UInt16 => false,
                    AssetValueType.Int32 => false,
                    AssetValueType.UInt32 => false,
                    AssetValueType.Int64 => false,
                    AssetValueType.UInt64 => false,
                    AssetValueType.Float => false,
                    AssetValueType.Double => false,
                    AssetValueType.String => false,
                    AssetValueType.Array => true,
                    AssetValueType.ByteArray => false,
                    AssetValueType.ManagedReferencesRegistry => false,
                    _ => throw new Exception("Invalid value type")
                };
                return tempFieldStack.Count - (canHaveChildren ? 1 : 0);
            }
        }

        public int ReadPosition
        {
            get
            {
                return (int)(reader.Position - basePosition);
            }
        }

        /// <summary>
        /// Create an AssetTypeValueIterator, which allows walking an asset's field tree without
        /// allocating unnecessary values. If <paramref name="reader"/> is locked behind a lock, you
        /// should manually take the lock while using the iterator. The current iterating position
        /// is not remembered by this iterator. Instead, the reader's position is relied on.
        /// </summary>
        /// <param name="templateField">The template base field to use.</param>
        /// <param name="reader">The reader to use, set the correct position.</param>
        /// <param name="refMan">The ref type manager to use, if reading a MonoBehaviour using a ref type.</param>
        public ReusableAssetTypeValueIterator(AssetTypeTemplateField templateField, AssetsFileReader reader, RefTypeManager refMan = null)
        {
            baseTempField = templateField;
            this.reader = reader;
            this.refMan = refMan;

            basePosition = reader.Position;

            tempFieldStack = new Stack<Frame>();
            Reset();
        }

        /// <summary>
        /// Create an AssetTypeValueIterator, which allows walking an asset's field tree without
        /// allocating unnecessary values. If <paramref name="reader"/> is locked behind a lock, you
        /// should manually take the lock while using the iterator. The current iterating position
        /// is not remembered by this iterator. Instead, the reader's position is relied on.
        /// </summary>
        /// <param name="templateField">The template base field to use.</param>
        /// <param name="reader">The reader to use.</param>
        /// <param name="position">The position to start reading from.</param>
        /// <param name="refMan">The ref type manager to use, if reading a MonoBehaviour using a ref type.</param>
        public ReusableAssetTypeValueIterator(AssetTypeTemplateField templateField, AssetsFileReader reader, long position, RefTypeManager refMan = null)
            : this(templateField, ReaderWithSetPos(reader, position), refMan)
        {
        }

        private static AssetsFileReader ReaderWithSetPos(AssetsFileReader reader, long position)
        {
            reader.Position = position;
            return reader;
        }

        public void Reset()
        {
            ClearStack();
            PushChildren(baseTempField.Children);

            TempField = baseTempField;
            valueFieldCache = null;
        }

        public void Reset(AssetTypeTemplateField templateField, AssetsFileReader reader, RefTypeManager refMan = null)
        {
            baseTempField = templateField;
            this.reader = reader;
            this.refMan = refMan;

            ClearStack();
            PushChildren(baseTempField.Children);

            TempField = baseTempField;
            valueFieldCache = null;
        }

        public bool ReadNext()
        {
            if (tempFieldStack.Count == 0)
            {
                return false;
            }

            // move reader to next field if we didn't read it
            if (valueFieldCache == null)
            {
                if (TempField.IsArray)
                {
                    if (TempField.ValueType == AssetValueType.ByteArray)
                    {
                        int byteLen = reader.ReadInt32();
                        reader.Position += byteLen;

                        if (TempField.IsAligned)
                        {
                            reader.Align();
                        }
                    }
                }
                else
                {
                    if (TempField.ValueType != AssetValueType.None)
                    {
                        bool aligned = TempField.IsAligned;
                        switch (TempField.ValueType)
                        {
                            case AssetValueType.Bool:
                            case AssetValueType.Int8:
                            case AssetValueType.UInt8:
                                reader.Position += 1;
                                break;
                            case AssetValueType.Int16:
                            case AssetValueType.UInt16:
                                reader.Position += 2;
                                break;
                            case AssetValueType.Int32:
                            case AssetValueType.UInt32:
                            case AssetValueType.Float:
                                reader.Position += 4;
                                break;
                            case AssetValueType.Int64:
                            case AssetValueType.UInt64:
                            case AssetValueType.Double:
                                reader.Position += 8;
                                break;
                            case AssetValueType.String:
                                int stringLen = reader.ReadInt32();
                                reader.Position += stringLen;

                                if (TempField.Children.Count > 0)
                                    aligned = TempField.Children[0].IsAligned;
                                else
                                    aligned = true;

                                break;
                            case AssetValueType.ManagedReferencesRegistry:
                                // this allocates, but I'm skipping writing code to do this the "right" way
                                AssetTypeValueField ignored = new AssetTypeValueField();
                                TempField.ReadManagedReferencesRegistryType(reader, ignored, refMan);
                                break;
                        }

                        if (aligned)
                        {
                            reader.Align();
                        }
                    }
                }
            }

            Frame topField = tempFieldStack.Peek();
            while (true)
            {
                // special case for arrays: we need to align here rather than below
                // because we create child iterators for arrays rather than depend
                // on the template field.
                if (topField.Current != null
                    && (!topField.Current.IsArray || topField.Current.ValueType != AssetValueType.ByteArray)
                    && topField.Current.IsAligned)
                {
                    reader.Align();
                }

                if (topField.MoveNext())
                {
                    break;
                }

                tempFieldStack.Pop().Clear();
                if (tempFieldStack.Count == 0)
                {
                    return false;
                }

                topField = tempFieldStack.Peek();
            }

            AssetTypeTemplateField childField = topField.Current;
            if (childField.IsArray && childField.ValueType != AssetValueType.ByteArray)
            {
                int arrChildCount = CurrentArrayCount = reader.ReadInt32();
                PushArray(childField.Children[1], arrChildCount);
            }
            else if (childField.ValueType == AssetValueType.None)
            {
                if (childField.Children.Count > 0)
                {
                    PushChildren(childField.Children);
                }
            }

            TempField = childField;
            valueFieldCache = null;

            // increasing the position past the length does not cause an exception
            // so we'll manually check ourselves. otherwise, we can end up reading
            // an obviously incorrect length and running forever.
            if (reader.BaseStream.Position > reader.BaseStream.Length)
            {
                throw new EndOfStreamException("Iterator tried to read past reader's length.");
            }

            return true;
        }

        // Scratch belongs to one iterator. Only active frames are cleared on reset; retired
        // frames have already released their child lists and repeated template references.
        private void ClearStack()
        {
            foreach (Frame frame in tempFieldStack) frame.Clear();
            tempFieldStack.Clear();
        }

        private Frame NextFrame()
        {
            int depth = tempFieldStack.Count;
            if (depth == frames.Count) frames.Add(new Frame());
            return frames[depth];
        }

        private void PushChildren(List<AssetTypeTemplateField> children)
        {
            Frame frame = NextFrame();
            frame.Reset(children);
            tempFieldStack.Push(frame);
        }

        private void PushArray(AssetTypeTemplateField element, int count)
        {
            Frame frame = NextFrame();
            frame.Reset(element, count);
            tempFieldStack.Push(frame);
        }

        private sealed class Frame
        {
            private List<AssetTypeTemplateField>.Enumerator children;
            private AssetTypeTemplateField repeated;
            private AssetTypeTemplateField current;
            private int index;
            private int count;
            private bool isArray;

            public AssetTypeTemplateField Current => isArray ? current : children.Current;

            public void Reset(List<AssetTypeTemplateField> fields)
            {
                // Keeping the mature List enumerator unboxed preserves mutation/version checks.
                children = fields.GetEnumerator();
                isArray = false;
            }

            public void Reset(AssetTypeTemplateField element, int times)
            {
                repeated = element;
                count = times;
                index = 0;
                current = null;
                isArray = true;
            }

            public bool MoveNext()
            {
                if (!isArray) return children.MoveNext();
                if (index >= count) return false;
                index++;
                // The upstream yield iterator retains its last Current after exhaustion.
                current = repeated;
                return true;
            }

            public void Clear()
            {
                children = default;
                repeated = null;
                current = null;
            }
        }
    }
}
