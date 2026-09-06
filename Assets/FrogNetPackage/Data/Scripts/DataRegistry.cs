using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Frognet.Data
{
    /// <summary>
    /// One folder of text files loaded into numbered records. Everything here is generic: a new
    /// kind of data costs a folder, a <c>schema.txt</c>, and no C# at all.
    /// </summary>
    /// <remarks>
    /// Files live in <c>StreamingAssets/&lt;folder&gt;</c>. <c>schema.txt</c> declares the shape;
    /// every other <c>.txt</c> holds records.
    /// <para>
    /// Ids are handed out by ordinal name order, never by file order, so the same files always
    /// produce the same numbers on every machine. That is what makes it safe to put an id on the
    /// wire instead of a name. <see cref="Hash"/> covers the whole registry so two peers running
    /// different files can notice.
    /// </para>
    /// </remarks>
    public sealed class DataRegistry
    {
        public const string SchemaExtension = ".fschema";
        public const string DataExtension = ".fdata";

        private static readonly Dictionary<string, DataRegistry> registries =
            new Dictionary<string, DataRegistry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised after any registry reloads, so caches keyed by id can rebuild.</summary>
        public static event Action onReloaded;

        private readonly string folderName;
        private Schema schema;
        private Definition[] defs = { null };
        private Dictionary<string, int> byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private string[] errors = Array.Empty<string>();
        private uint hash;
        private bool loaded;

        private DataRegistry(string folderName)
        {
            this.folderName = folderName;
        }

        /// <summary>The registry for a folder, loaded on first use and shared from then on.</summary>
        public static DataRegistry Of(string folderName)
        {
            if (!registries.TryGetValue(folderName, out DataRegistry registry))
            {
                registry = new DataRegistry(folderName);
                registries[folderName] = registry;
            }

            return registry;
        }

        public static void ReloadAll()
        {
            foreach (DataRegistry registry in registries.Values)
            {
                registry.loaded = false;
                registry.Load();
            }

            onReloaded?.Invoke();
        }

        public string Folder => Path.Combine(Application.streamingAssetsPath, folderName);

        public Schema Schema
        {
            get
            {
                Load();
                return schema;
            }
        }

        /// <summary>How many records exist. Valid ids run from 1 to this value.</summary>
        public int Count
        {
            get
            {
                Load();
                return defs.Length - 1;
            }
        }

        public IReadOnlyList<Definition> All
        {
            get
            {
                Load();
                return new ArraySegment<Definition>(defs, 1, defs.Length - 1);
            }
        }

        public IReadOnlyList<string> Errors
        {
            get
            {
                Load();
                return errors;
            }
        }

        public uint Hash
        {
            get
            {
                Load();
                return hash;
            }
        }

        /// <summary>Null when the id is zero or unknown.</summary>
        public Definition Get(int id)
        {
            Load();
            return id > 0 && id < defs.Length ? defs[id] : null;
        }

        /// <summary>Zero when the name is unknown, which is also the "nothing" id.</summary>
        public int IdOf(string name)
        {
            Load();
            return name != null && byName.TryGetValue(name, out int id) ? id : 0;
        }

        public Definition Find(string name) => Get(IdOf(name));

        /// <summary>The leaf id at a dotted path. Cache this rather than calling it per frame.</summary>
        public int Leaf(string path)
        {
            Load();
            return schema.IdOf(path);
        }

        public void Reload()
        {
            loaded = false;
            Load();
            onReloaded?.Invoke();
        }

        private void Load()
        {
            if (loaded)
                return;

            loaded = true;

            var problems = new List<string>();
            var files = new List<KeyValuePair<string, string>>();
            string schemaText = null;
            string schemaLabel = "(no schema)";

            if (!TryRead(files, ref schemaText, ref schemaLabel, problems))
            {
                Apply(Schema.Parse(string.Empty, schemaLabel, new List<string>()), new List<Definition>(), problems);
                return;
            }

            Schema parsedSchema = Schema.Parse(schemaText, schemaLabel, problems);
            var parsed = new List<Definition>();

            // Sorted so duplicate-name reporting is stable, though ids never depend on file order.
            files.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            for (int i = 0; i < files.Count; i++)
                parsed.AddRange(RecordParser.Parse(files[i].Value, files[i].Key, parsedSchema, problems));

            Apply(parsedSchema, parsed, problems);
        }

        private void Apply(Schema parsedSchema, List<Definition> parsed, List<string> problems)
        {
            schema = parsedSchema;
            parsed.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            var kept = new List<Definition>(parsed.Count);
            var names = new Dictionary<string, int>(parsed.Count, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < parsed.Count; i++)
            {
                Definition definition = parsed[i];

                if (names.ContainsKey(definition.name))
                {
                    problems.Add($"Two records are called '{definition.name}'. Only the first is kept.");
                    continue;
                }

                definition.id = kept.Count + 1;
                kept.Add(definition);
                names[definition.name] = definition.id;
            }

            defs = new Definition[kept.Count + 1];

            for (int i = 0; i < kept.Count; i++)
                defs[i + 1] = kept[i];

            byName = names;
            errors = problems.ToArray();
            hash = Fingerprint(kept);

            for (int i = 0; i < errors.Length; i++)
                Debug.LogError($"{folderName}: {errors[i]}");
        }

        /// <summary>
        /// The one place that touches the disk. Standalone and the editor read StreamingAssets
        /// directly; Android and WebGL would need UnityWebRequest, so swap this out if you ship there.
        /// </summary>
        private bool TryRead(List<KeyValuePair<string, string>> files, ref string schemaText,
            ref string schemaLabel, List<string> problems)
        {
            string folder = Folder;

            if (!Directory.Exists(folder))
            {
                problems.Add($"No folder at '{folder}'.");
                return false;
            }

            foreach (string path in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(path);
                bool isSchema = string.Equals(extension, SchemaExtension, StringComparison.OrdinalIgnoreCase);

                if (!isSchema && !string.Equals(extension, DataExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                string label = Path.GetFileName(path);
                string text;

                try
                {
                    text = File.ReadAllText(path);
                }
                catch (Exception failure)
                {
                    problems.Add($"Could not read '{label}': {failure.Message}");
                    continue;
                }

                if (!isSchema)
                {
                    files.Add(new KeyValuePair<string, string>(label, text));
                    continue;
                }

                if (schemaText != null)
                {
                    problems.Add($"Both '{schemaLabel}' and '{label}' are schemas. A folder takes one.");
                    continue;
                }

                schemaText = text;
                schemaLabel = label;
            }

            if (schemaText == null)
            {
                problems.Add($"No '*{SchemaExtension}' file in '{folder}'.");
                return false;
            }

            return true;
        }

        private uint Fingerprint(List<Definition> kept)
        {
            uint result = 2166136261u;
            Mix(ref result, schema.Describe());

            for (int i = 0; i < kept.Count; i++)
            {
                Mix(ref result, kept[i].name);
                Mix(ref result, kept[i].record.Describe(schema));
            }

            return result;
        }

        private static void Mix(ref uint result, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                result ^= text[i];
                result *= 16777619u;
            }
        }
    }
}
