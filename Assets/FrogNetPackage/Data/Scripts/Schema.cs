using System;
using System.Collections.Generic;
using System.Text;

namespace Frognet.Data
{
    public enum DataType { Float, Int, Bool, String }

    /// <summary>What a path in the schema turned out to be.</summary>
    public enum NodeKind { Leaf, Bag, Choice }

    public struct NodeRef
    {
        public NodeKind kind;

        /// <summary>Choice index when <see cref="kind"/> is Choice, otherwise -1.</summary>
        public int index;

        /// <summary>The contiguous run of leaf ids this node covers, <see cref="start"/> inclusive.</summary>
        public int start;
        public int end;

        public int Count => end - start;
        public bool Contains(int leaf) => leaf >= start && leaf < end;
    }

    /// <summary>One place a value can be stored. Leaves are the only thing the runtime deals in.</summary>
    public sealed class SchemaLeaf
    {
        public int id;
        public string path;
        public string name;
        public DataType[] args = Array.Empty<DataType>();
        public int NumericCount { get; internal set; }
        public bool HasText { get; internal set; }
    }

    /// <summary>
    /// A <c>{ }</c> group: pick one branch. Leaf ids are handed out depth first, so every branch
    /// owns a contiguous range and the whole group does too. That is the entire constraint,
    /// with nothing stored per leaf.
    /// </summary>
    public sealed class SchemaChoice
    {
        public string path;
        public int start;
        public int end;
        public int[] branchStarts;
        public string[] branchNames;

        public int BranchOf(int leaf)
        {
            for (int i = branchStarts.Length - 1; i >= 0; i--)
            {
                if (leaf >= branchStarts[i])
                    return i;
            }

            return -1;
        }
    }

    /// <summary>
    /// The shape of one kind of data file, read from a schema file.
    /// </summary>
    /// <remarks>
    /// <code>
    /// name = string
    /// maxStack = int
    /// data =
    /// [
    ///     durability = float
    ///     consumable =
    ///     {
    ///         food = float
    ///         speed = float float
    ///     }
    /// ]
    /// modified_data = data
    /// </code>
    /// <c>[ ]</c> holds any combination of its entries, <c>{ }</c> holds exactly one of them, and
    /// <c>=</c> either gives a leaf its argument types or points at another declaration to reuse
    /// its shape. Nesting is unlimited.
    /// <para>
    /// The tree only exists while parsing. What survives is a flat table of leaves, each with an
    /// id, because that is all the runtime needs and it keeps values cheap to store and to send.
    /// </para>
    /// </remarks>
    public sealed class Schema
    {
        /// <summary>Most numeric arguments one leaf may take, which fixes the size of a value.</summary>
        public const int MaxNumericArgs = 4;

        public const char Separator = '.';

        /// <summary>Every schema must name one string leaf <c>name</c>; it is how records are looked up.</summary>
        public const string NamePath = "name";

        private readonly SchemaLeaf[] leaves;
        private readonly SchemaChoice[] choices;
        private readonly Dictionary<string, NodeRef> nodes;

        public int Count => leaves.Length;
        public int NameLeaf { get; }
        public IReadOnlyList<SchemaLeaf> Leaves => leaves;
        public IReadOnlyList<SchemaChoice> Choices => choices;

        private Schema(SchemaLeaf[] leaves, SchemaChoice[] choices, Dictionary<string, NodeRef> nodes)
        {
            this.leaves = leaves;
            this.choices = choices;
            this.nodes = nodes;

            NameLeaf = nodes.TryGetValue(NamePath, out NodeRef found) && found.kind == NodeKind.Leaf
                ? found.start
                : -1;
        }

        public SchemaLeaf Get(int id) => id >= 0 && id < leaves.Length ? leaves[id] : null;

        /// <summary>The leaf at a dotted path, or -1.</summary>
        public int IdOf(string path)
        {
            return path != null && nodes.TryGetValue(path, out NodeRef found) && found.kind == NodeKind.Leaf
                ? found.start
                : -1;
        }

        /// <summary>Looks up any path, leaf or container.</summary>
        public bool TryNode(string path, out NodeRef node) => nodes.TryGetValue(path ?? string.Empty, out node);

        public static string Join(string parent, string child)
        {
            return string.IsNullOrEmpty(parent) ? child : parent + Separator + child;
        }

        /// <summary>A canonical dump, used for the registry hash and for reporting.</summary>
        public string Describe()
        {
            var text = new StringBuilder();

            for (int i = 0; i < leaves.Length; i++)
            {
                text.Append(leaves[i].id).Append(' ').Append(leaves[i].path);

                for (int a = 0; a < leaves[i].args.Length; a++)
                    text.Append(' ').Append(leaves[i].args[a].ToString().ToLowerInvariant());

                text.Append('\n');
            }

            for (int i = 0; i < choices.Length; i++)
                text.Append("{ ").Append(choices[i].path).Append(' ')
                    .Append(string.Join(",", choices[i].branchNames)).Append(" }\n");

            return text.ToString();
        }

        // ---- parsing -------------------------------------------------------

        private sealed class Node
        {
            public string name;
            public NodeKind kind = NodeKind.Leaf;
            public List<Node> children = new List<Node>();
            public DataType[] args = Array.Empty<DataType>();
            public string reference;
            public int line;
        }

        public static Schema Parse(string text, string origin, List<string> errors)
        {
            var root = new Node { name = string.Empty, kind = NodeKind.Bag };
            var open = new Stack<Node>();
            open.Push(root);
            Node pending = null;

            foreach (DataTextReader.Line line in DataTextReader.Read(text))
            {
                string content = line.content;

                if (content == "]" || content == "}")
                {
                    NodeKind expected = content == "]" ? NodeKind.Bag : NodeKind.Choice;

                    if (open.Count <= 1)
                        errors.Add(Error(origin, line, $"'{content}' without a matching opener."));
                    else if (open.Peek().kind != expected)
                        errors.Add(Error(origin, line, $"'{content}' closes a {open.Peek().kind.ToString().ToLowerInvariant()}."));
                    else
                        open.Pop();

                    continue;
                }

                bool opensBag = content.EndsWith("[", StringComparison.Ordinal);
                bool opensChoice = content.EndsWith("{", StringComparison.Ordinal);

                if (opensBag || opensChoice)
                {
                    content = content.Substring(0, content.Length - 1).TrimEnd().TrimEnd('=').TrimEnd();
                    Node target;

                    if (content.Length == 0)
                    {
                        if (pending == null)
                        {
                            errors.Add(Error(origin, line, "A block that does not follow a name."));
                            continue;
                        }

                        target = pending;
                        pending = null;
                    }
                    else if (!TryDeclare(open.Peek(), content, line, origin, errors, out target))
                    {
                        continue;
                    }

                    target.kind = opensBag ? NodeKind.Bag : NodeKind.Choice;
                    open.Push(target);
                    continue;
                }

                if (pending != null)
                {
                    errors.Add($"{origin}({pending.line}): '{pending.name} =' is not followed by a block.");
                    pending = null;
                }

                int equals = content.IndexOf('=');
                string name = (equals < 0 ? content : content.Substring(0, equals)).Trim();

                if (!TryDeclare(open.Peek(), name, line, origin, errors, out Node node))
                    continue;

                if (equals < 0)
                    continue;

                string right = content.Substring(equals + 1).Trim();

                if (right.Length == 0)
                {
                    pending = node;
                    continue;
                }

                string[] words = right.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);

                if (TryTypes(words, out DataType[] args))
                {
                    if (args.Length > MaxNumericArgs)
                        errors.Add(Error(origin, line, $"'{name}' takes more than {MaxNumericArgs} arguments."));

                    node.args = args;
                }
                else if (words.Length == 1)
                {
                    node.reference = words[0];
                }
                else
                {
                    errors.Add(Error(origin, line, $"'{right}' is neither a list of types nor one name to reuse."));
                }
            }

            if (pending != null)
                errors.Add($"{origin}({pending.line}): '{pending.name} =' is not followed by a block.");

            while (open.Count > 1)
                errors.Add($"{origin}: '{open.Pop().name}' is never closed.");

            var top = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);

            foreach (Node child in root.children)
                top[child.name] = child;

            Dereference(root, top, new HashSet<string>(StringComparer.OrdinalIgnoreCase), origin, errors);
            return Flatten(root, origin, errors);
        }

        private static bool TryDeclare(Node parent, string name, DataTextReader.Line line,
            string origin, List<string> errors, out Node node)
        {
            node = null;

            if (name.Length == 0)
            {
                errors.Add(Error(origin, line, "Missing a name."));
                return false;
            }

            if (name.IndexOf(' ') >= 0 || name.IndexOf(Separator) >= 0)
            {
                errors.Add(Error(origin, line, $"'{name}' cannot contain a space or a '{Separator}'."));
                return false;
            }

            for (int i = 0; i < parent.children.Count; i++)
            {
                if (string.Equals(parent.children[i].name, name, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(Error(origin, line, $"'{name}' is declared twice in the same block."));
                    return false;
                }
            }

            node = new Node { name = name, line = line.number };
            parent.children.Add(node);
            return true;
        }

        private static bool TryTypes(string[] words, out DataType[] args)
        {
            args = new DataType[words.Length];

            for (int i = 0; i < words.Length; i++)
            {
                switch (words[i].ToLowerInvariant())
                {
                    case "float": args[i] = DataType.Float; break;
                    case "int": args[i] = DataType.Int; break;
                    case "bool": args[i] = DataType.Bool; break;
                    case "string": args[i] = DataType.String; break;
                    default: args = Array.Empty<DataType>(); return false;
                }
            }

            return words.Length > 0;
        }

        /// <summary>Copies the shape of any declaration named by another, refusing to go in circles.</summary>
        private static void Dereference(Node node, Dictionary<string, Node> top, HashSet<string> visiting,
            string origin, List<string> errors)
        {
            if (node.reference != null)
            {
                string target = node.reference;

                if (!top.TryGetValue(target, out Node source))
                {
                    errors.Add($"{origin}({node.line}): '{node.name} = {target}' names nothing declared at the top level.");
                    node.reference = null;
                }
                else if (source == node || !visiting.Add(target))
                {
                    errors.Add($"{origin}({node.line}): '{node.name} = {target}' ends up referring to itself.");
                    node.reference = null;
                }
                else
                {
                    // Resolve the target first, so chains of references work in any order. The
                    // reference is only cleared afterwards, which is what lets a loop be spotted.
                    Dereference(source, top, visiting, origin, errors);
                    visiting.Remove(target);
                    node.reference = null;

                    node.kind = source.kind;
                    node.args = source.args;
                    node.children = new List<Node>(source.children.Count);

                    for (int i = 0; i < source.children.Count; i++)
                        node.children.Add(Clone(source.children[i]));
                }
            }

            for (int i = 0; i < node.children.Count; i++)
                Dereference(node.children[i], top, visiting, origin, errors);
        }

        private static Node Clone(Node source)
        {
            var copy = new Node
            {
                name = source.name,
                kind = source.kind,
                args = source.args,
                reference = source.reference,
                line = source.line,
                children = new List<Node>(source.children.Count)
            };

            for (int i = 0; i < source.children.Count; i++)
                copy.children.Add(Clone(source.children[i]));

            return copy;
        }

        private static Schema Flatten(Node root, string origin, List<string> errors)
        {
            var leaves = new List<SchemaLeaf>();
            var choices = new List<SchemaChoice>();
            var nodes = new Dictionary<string, NodeRef>(StringComparer.OrdinalIgnoreCase);

            Walk(root, string.Empty, leaves, choices, nodes);

            var schema = new Schema(leaves.ToArray(), choices.ToArray(), nodes);

            if (schema.NameLeaf < 0)
                errors.Add($"{origin}: no top level 'name' leaf, so records cannot be looked up.");
            else if (schema.Get(schema.NameLeaf).args.Length != 1 || schema.Get(schema.NameLeaf).args[0] != DataType.String)
                errors.Add($"{origin}: 'name' must be declared 'name = string'.");

            return schema;
        }

        private static void Walk(Node node, string path, List<SchemaLeaf> leaves,
            List<SchemaChoice> choices, Dictionary<string, NodeRef> nodes)
        {
            // Sorting here is what makes leaf ids identical on every machine, whatever order the
            // file happened to list things in.
            node.children.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            int start = leaves.Count;

            if (node.children.Count == 0 && node.kind != NodeKind.Bag)
            {
                var leaf = new SchemaLeaf
                {
                    id = start,
                    path = path,
                    name = node.name,
                    args = node.args
                };

                int numeric = 0;

                for (int i = 0; i < leaf.args.Length; i++)
                {
                    if (leaf.args[i] == DataType.String)
                        leaf.HasText = true;
                    else
                        numeric++;
                }

                leaf.NumericCount = numeric;
                leaves.Add(leaf);

                if (path.Length > 0)
                    nodes[path] = new NodeRef { kind = NodeKind.Leaf, index = -1, start = start, end = start + 1 };

                return;
            }

            SchemaChoice choice = null;

            if (node.kind == NodeKind.Choice)
            {
                choice = new SchemaChoice
                {
                    path = path,
                    start = start,
                    branchStarts = new int[node.children.Count],
                    branchNames = new string[node.children.Count]
                };

                choices.Add(choice);
            }

            int choiceIndex = choice != null ? choices.Count - 1 : -1;

            for (int i = 0; i < node.children.Count; i++)
            {
                if (choice != null)
                {
                    choice.branchStarts[i] = leaves.Count;
                    choice.branchNames[i] = node.children[i].name;
                }

                Walk(node.children[i], Join(path, node.children[i].name), leaves, choices, nodes);
            }

            if (choice != null)
                choice.end = leaves.Count;

            if (path.Length > 0)
            {
                nodes[path] = new NodeRef
                {
                    kind = node.kind,
                    index = choiceIndex,
                    start = start,
                    end = leaves.Count
                };
            }
        }

        private static string Error(string origin, DataTextReader.Line line, string problem)
        {
            return $"{origin}({line.number}): {problem}";
        }
    }
}
