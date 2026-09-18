using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Frognet.Data
{
    /// <summary>
    /// One value stored against a schema leaf. Arguments live in <see cref="x"/>..<see cref="w"/>,
    /// with <see cref="text"/> holding the one string argument a leaf is allowed.
    /// </summary>
    /// <remarks>
    /// There is no notion of a choice here. Picking <c>speed</c> out of a <c>{ }</c> group just
    /// means setting the leaf whose path ends in <c>speed</c>, so a value stays flat and fixed size.
    /// </remarks>
    [Serializable]
    public struct DataValue : IEquatable<DataValue>
    {
        public int leaf;
        public float x, y, z, w;
        public string text;

        public DataValue(int leaf, float x = 0f, float y = 0f, float z = 0f, float w = 0f)
        {
            this.leaf = leaf;
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
            text = null;
        }

        public float this[int index]
        {
            get
            {
                switch (index)
                {
                    case 0: return x;
                    case 1: return y;
                    case 2: return z;
                    case 3: return w;
                    default: return 0f;
                }
            }
        }

        public DataValue WithArg(int index, float value)
        {
            DataValue copy = this;

            switch (index)
            {
                case 0: copy.x = value; break;
                case 1: copy.y = value; break;
                case 2: copy.z = value; break;
                case 3: copy.w = value; break;
            }

            return copy;
        }

        public bool Equals(DataValue other)
        {
            return leaf == other.leaf
                && x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z) && w.Equals(other.w)
                && string.Equals(text, other.text, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is DataValue other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = leaf;
                hash = hash * 397 ^ x.GetHashCode();
                hash = hash * 397 ^ y.GetHashCode();
                hash = hash * 397 ^ z.GetHashCode();
                hash = hash * 397 ^ w.GetHashCode();
                return hash * 397 ^ (text != null ? text.GetHashCode() : 0);
            }
        }

        /// <summary>Renders back into the form a data file would use.</summary>
        public string Describe(Schema schema)
        {
            SchemaLeaf definition = schema?.Get(leaf);

            if (definition == null)
                return $"#{leaf}";

            string body = Arguments(definition.args);
            return body.Length == 0 ? definition.path : definition.path + ": " + body;
        }

        public string Arguments(DataType[] args)
        {
            if (args == null || args.Length == 0)
                return string.Empty;

            var parts = new List<string>(args.Length);
            int numeric = 0;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == DataType.String)
                {
                    parts.Add(text ?? string.Empty);
                    continue;
                }

                float number = this[numeric++];

                switch (args[i])
                {
                    case DataType.Int:
                        parts.Add(((int)number).ToString(CultureInfo.InvariantCulture));
                        break;
                    case DataType.Bool:
                        parts.Add(number != 0f ? "true" : "false");
                        break;
                    default:
                        parts.Add(number.ToString("0.###", CultureInfo.InvariantCulture));
                        break;
                }
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// A sparse set of values, kept sorted by leaf id and never edited in place.
    /// </summary>
    /// <remarks>
    /// Copying a Record shares its array, which is safe precisely because every change goes through
    /// <see cref="With"/> and returns a new one. Do not write into <see cref="values"/> directly.
    /// </remarks>
    [Serializable]
    public struct Record : IEquatable<Record>
    {
        public DataValue[] values;

        public int Count => values != null ? values.Length : 0;
        public bool IsEmpty => Count == 0;

        public bool TryGet(int leaf, out DataValue value)
        {
            if (values != null && leaf >= 0)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i].leaf == leaf)
                    {
                        value = values[i];
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        public bool Has(int leaf) => TryGet(leaf, out _);

        public float GetFloat(int leaf, float fallback = 0f, int arg = 0)
        {
            return TryGet(leaf, out DataValue value) ? value[arg] : fallback;
        }

        public int GetInt(int leaf, int fallback = 0, int arg = 0)
        {
            return TryGet(leaf, out DataValue value) ? (int)value[arg] : fallback;
        }

        public bool GetBool(int leaf, bool fallback = false, int arg = 0)
        {
            return TryGet(leaf, out DataValue value) ? value[arg] != 0f : fallback;
        }

        public string GetText(int leaf, string fallback = null)
        {
            return TryGet(leaf, out DataValue value) && value.text != null ? value.text : fallback;
        }

        /// <summary>Returns a copy with <paramref name="value"/> set. The original is untouched.</summary>
        public Record With(DataValue value)
        {
            return value.leaf < 0 ? this : new Record { values = Set(values, value, false) };
        }

        public Record With(int leaf, float x) => With(new DataValue(leaf, x));

        /// <summary>Returns a copy with that leaf dropped.</summary>
        public Record Without(int leaf)
        {
            return leaf < 0 ? this : new Record { values = Set(values, new DataValue(leaf), true) };
        }

        /// <summary>Sorts and de-duplicates a freshly parsed list so the ordering invariant holds.</summary>
        public static Record Build(List<DataValue> parsed)
        {
            DataValue[] result = null;

            if (parsed != null)
            {
                for (int i = 0; i < parsed.Count; i++)
                    result = Set(result, parsed[i], false);
            }

            return new Record { values = result };
        }

        /// <summary>
        /// Copy-on-write set, replace or remove. Because the array is sorted by leaf, one scan finds
        /// both an existing entry and the position a new one belongs in.
        /// </summary>
        public static DataValue[] Set(DataValue[] source, DataValue entry, bool remove)
        {
            int count = source != null ? source.Length : 0;
            int index = 0;

            while (index < count && source[index].leaf < entry.leaf)
                index++;

            bool exists = index < count && source[index].leaf == entry.leaf;

            if (remove)
            {
                if (!exists)
                    return source;

                if (count == 1)
                    return null;

                var shrunk = new DataValue[count - 1];
                Array.Copy(source, 0, shrunk, 0, index);
                Array.Copy(source, index + 1, shrunk, index, count - index - 1);
                return shrunk;
            }

            if (exists)
            {
                var replaced = (DataValue[])source.Clone();
                replaced[index] = entry;
                return replaced;
            }

            var grown = new DataValue[count + 1];

            if (count > 0)
            {
                Array.Copy(source, 0, grown, 0, index);
                Array.Copy(source, index, grown, index + 1, count - index);
            }

            grown[index] = entry;
            return grown;
        }

        /// <summary>
        /// Checks every <c>{ }</c> group holds at most one branch. Each group owns a contiguous run
        /// of leaf ids, so this is a straight scan rather than anything stored per value.
        /// </summary>
        public bool TryFindConflict(Schema schema, out string problem)
        {
            problem = null;

            if (values == null || schema == null)
                return false;

            IReadOnlyList<SchemaChoice> choices = schema.Choices;

            for (int c = 0; c < choices.Count; c++)
            {
                SchemaChoice choice = choices[c];
                int chosen = -1;

                for (int i = 0; i < values.Length; i++)
                {
                    int leaf = values[i].leaf;

                    if (leaf < choice.start || leaf >= choice.end)
                        continue;

                    int branch = choice.BranchOf(leaf);

                    if (chosen < 0)
                    {
                        chosen = branch;
                    }
                    else if (chosen != branch)
                    {
                        problem = $"'{choice.path}' takes one of {string.Join(", ", choice.branchNames)}, "
                                + $"but both '{choice.branchNames[chosen]}' and '{choice.branchNames[branch]}' are set.";
                        return true;
                    }
                }
            }

            return false;
        }

        public bool Equals(Record other)
        {
            int count = Count;

            if (count != other.Count)
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!values[i].Equals(other.values[i]))
                    return false;
            }

            return true;
        }

        public override bool Equals(object obj) => obj is Record other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;

                for (int i = 0; i < Count; i++)
                    hash = hash * 397 ^ values[i].GetHashCode();

                return hash;
            }
        }

        public string Describe(Schema schema)
        {
            if (Count == 0)
                return string.Empty;

            var text = new StringBuilder();

            for (int i = 0; i < values.Length; i++)
                text.Append(values[i].Describe(schema)).Append('\n');

            return text.ToString();
        }
    }
}
