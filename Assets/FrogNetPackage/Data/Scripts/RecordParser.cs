using System;
using System.Collections.Generic;
using System.Globalization;

namespace Frognet.Data
{
    /// <summary>One named record read from a data file.</summary>
    public sealed class Definition
    {
        /// <summary>Registry id, always 1 or greater. Zero is reserved for "nothing".</summary>
        public int id;

        public string name = string.Empty;
        public Record record;

        public override string ToString() => name;
    }

    /// <summary>
    /// Reads data files into <see cref="Definition"/>s, guided entirely by a <see cref="Schema"/>.
    /// </summary>
    /// <remarks>
    /// <code>
    /// name: meat helmet
    /// maxStack: 1
    /// data:
    ///     durability: 100
    ///     consumable: speed(1, 1)
    ///     equipable: head
    /// </code>
    /// A field is written <c>path: value</c>. Leave the value off a <c>[ ]</c> or <c>{ }</c> to open
    /// a section and indent its entries underneath. A <c>{ }</c> may instead be answered on the same
    /// line by naming the branch, with its arguments in brackets. Paths may also be dotted, so
    /// <c>data.durability: 100</c> says the same thing as the two indented lines.
    /// <para>A new record starts each time the schema's <c>name</c> field appears at the left margin.</para>
    /// </remarks>
    public static class RecordParser
    {
        public static List<Definition> Parse(string text, string origin, Schema schema, List<string> errors)
        {
            var results = new List<Definition>();
            var sections = new List<KeyValuePair<string, int>>();
            List<DataValue> current = null;
            string currentName = null;

            foreach (DataTextReader.Line line in DataTextReader.Read(text))
            {
                while (sections.Count > 0 && line.indent <= sections[sections.Count - 1].Value)
                    sections.RemoveAt(sections.Count - 1);

                string basePath = sections.Count > 0 ? sections[sections.Count - 1].Key : string.Empty;
                int colon = line.content.IndexOf(':');

                if (colon < 0)
                {
                    errors.Add(Error(origin, line, $"'{line.content}' is not a 'field: value' line."));
                    continue;
                }

                string field = line.content.Substring(0, colon).Trim();
                string value = line.content.Substring(colon + 1).Trim();
                string path = Schema.Join(basePath, field);

                if (!schema.TryNode(path, out NodeRef node))
                {
                    errors.Add(Error(origin, line, $"'{path}' is not in the schema."));
                    continue;
                }

                if (node.kind == NodeKind.Leaf)
                {
                    if (node.start == schema.NameLeaf)
                    {
                        Flush(current, currentName, results, schema, origin, errors);
                        current = new List<DataValue>();
                        currentName = value;

                        if (value.Length == 0)
                            errors.Add(Error(origin, line, "'name:' is empty."));

                        continue;
                    }

                    if (current == null)
                    {
                        errors.Add(Error(origin, line, $"'{field}' appears before any 'name:' line."));
                        continue;
                    }

                    if (TryValue(schema, node.start, value, out DataValue parsed, out string problem))
                        current.Add(parsed);
                    else
                        errors.Add(Error(origin, line, problem));

                    continue;
                }

                if (value.Length == 0)
                {
                    sections.Add(new KeyValuePair<string, int>(path, line.indent));
                    continue;
                }

                if (node.kind != NodeKind.Choice)
                {
                    errors.Add(Error(origin, line, $"'{path}' holds a combination of entries, so indent them below it."));
                    continue;
                }

                if (current == null)
                {
                    errors.Add(Error(origin, line, $"'{field}' appears before any 'name:' line."));
                    continue;
                }

                if (TryBranch(schema, path, schema.Choices[node.index], value, out DataValue chosen, out string reason))
                    current.Add(chosen);
                else
                    errors.Add(Error(origin, line, reason));
            }

            Flush(current, currentName, results, schema, origin, errors);
            return results;
        }

        private static void Flush(List<DataValue> values, string name, List<Definition> results,
            Schema schema, string origin, List<string> errors)
        {
            if (values == null || string.IsNullOrEmpty(name))
                return;

            var definition = new Definition { name = name, record = Record.Build(values) };

            if (definition.record.TryFindConflict(schema, out string conflict))
                errors.Add($"{origin}: '{name}' {conflict}");

            results.Add(definition);
        }

        /// <summary>Answers a <c>{ }</c> written on one line, as <c>branch</c> or <c>branch(args)</c>.</summary>
        private static bool TryBranch(Schema schema, string path, SchemaChoice choice, string raw,
            out DataValue value, out string problem)
        {
            value = default;
            problem = null;

            string branch = raw;
            string args = string.Empty;
            int open = raw.IndexOf('(');

            if (open >= 0)
            {
                int close = raw.LastIndexOf(')');

                if (close < open)
                {
                    problem = $"'{raw}' is missing a closing ')'.";
                    return false;
                }

                branch = raw.Substring(0, open).Trim();
                args = raw.Substring(open + 1, close - open - 1);
            }

            branch = branch.Trim();
            string branchPath = Schema.Join(path, branch);

            if (!schema.TryNode(branchPath, out NodeRef node))
            {
                problem = $"'{branch}' is not a choice for '{path}'. Expected one of {string.Join(", ", choice.branchNames)}.";
                return false;
            }

            if (node.kind != NodeKind.Leaf)
            {
                problem = $"'{branchPath}' holds entries of its own, so indent them below it instead.";
                return false;
            }

            return TryValue(schema, node.start, args, out value, out problem);
        }

        /// <summary>Reads the arguments of one leaf.</summary>
        public static bool TryValue(Schema schema, int leafId, string raw, out DataValue value, out string problem)
        {
            value = new DataValue { leaf = leafId };
            problem = null;

            SchemaLeaf leaf = schema.Get(leafId);

            if (leaf == null)
            {
                problem = $"Unknown leaf {leafId}.";
                return false;
            }

            raw = raw.Trim();
            DataType[] args = leaf.args;

            if (args.Length == 0)
            {
                if (raw.Length == 0)
                    return true;

                problem = $"'{leaf.path}' takes no value, but got '{raw}'.";
                return false;
            }

            // A lone string keeps its spaces and commas, so it is never split.
            if (args.Length == 1 && args[0] == DataType.String)
            {
                value.text = raw;
                return true;
            }

            string[] parts = raw.IndexOf(',') >= 0
                ? raw.Split(',')
                : raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (raw.Length == 0 || parts.Length != args.Length)
            {
                problem = $"'{leaf.path}' takes {args.Length} value(s), but got {(raw.Length == 0 ? 0 : parts.Length)}.";
                return false;
            }

            int numeric = 0;

            for (int i = 0; i < args.Length; i++)
            {
                string part = parts[i].Trim();

                if (args[i] == DataType.String)
                {
                    value.text = part;
                    continue;
                }

                if (!TryNumber(part, args[i], out float number))
                {
                    problem = $"'{leaf.path}' expects a {args[i].ToString().ToLowerInvariant()} but got '{part}'.";
                    return false;
                }

                value = value.WithArg(numeric++, number);
            }

            return true;
        }

        private static bool TryNumber(string part, DataType type, out float number)
        {
            number = 0f;

            switch (type)
            {
                case DataType.Bool:
                    if (bool.TryParse(part, out bool flag))
                    {
                        number = flag ? 1f : 0f;
                        return true;
                    }

                    if (part == "1" || part == "0")
                    {
                        number = part == "1" ? 1f : 0f;
                        return true;
                    }

                    return false;

                case DataType.Int:
                    if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int whole))
                    {
                        number = whole;
                        return true;
                    }

                    return false;

                default:
                    return float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            }
        }

        private static string Error(string origin, DataTextReader.Line line, string problem)
        {
            return $"{origin}({line.number}): {problem}";
        }
    }
}
