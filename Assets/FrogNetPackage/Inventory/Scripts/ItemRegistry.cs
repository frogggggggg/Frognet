using System.Collections.Generic;
using Frognet.Data;
using UnityEngine;

/// <summary>
/// The item side of the generic data system: names the folder, remembers the handful of leaves
/// gameplay cares about, and turns a definition into a fresh <see cref="Item"/>.
/// </summary>
/// <remarks>
/// The schema declares <c>data</c> and <c>modified_data</c> with the same shape. Leaf ids are
/// handed out depth first, so each of those is a contiguous run of the same length and one is a
/// fixed offset from the other. A copy's overrides are stored against the <c>data</c> leaves, and
/// a definition's <c>modified_data</c> is shifted onto them once at load. That way every read and
/// write at runtime deals with one set of ids.
/// </remarks>
public static class ItemRegistry
{
    public const string FolderName = "Items";
    public const string DataSection = "data";
    public const string ModifiedSection = "modified_data";

    private static bool cached;
    private static uint cachedFor;
    private static int maxStackLeaf = -1;
    private static int descriptionLeaf = -1;
    private static int prefabLeaf = -1;
    private static int modifiedOffset;
    private static NodeRef modifiedNode;
    private static Record[] starting = { default };

    public static DataRegistry Data => DataRegistry.Of(FolderName);
    public static Schema Schema => Data.Schema;
    public static int Count => Data.Count;
    public static uint Hash => Data.Hash;
    public static IReadOnlyList<Definition> All => Data.All;
    public static IReadOnlyList<string> Errors => Data.Errors;

    public static Definition Get(int id) => Data.Get(id);
    public static int IdOf(string name) => Data.IdOf(name);
    public static Definition Find(string name) => Data.Find(name);

    /// <summary>The full schema path of a gameplay field, which always sits under <c>data</c>.</summary>
    public static string Path(string field) => Schema.Join(DataSection, field);

    /// <summary>The leaf id of a gameplay field. Cache this rather than calling it per frame.</summary>
    public static int Leaf(string field) => Schema.IdOf(Path(field));

    public static int MaxStack(int id)
    {
        Cache();
        Definition def = Get(id);
        return def != null ? Mathf.Max(1, def.record.GetInt(maxStackLeaf, 1)) : 1;
    }

    public static string Description(int id)
    {
        Cache();
        Definition def = Get(id);
        return def != null ? def.record.GetText(descriptionLeaf, string.Empty) : string.Empty;
    }

    /// <summary>Resources path of the pickup prefab, or empty to use the shared default.</summary>
    public static string PrefabPath(int id)
    {
        Cache();
        Definition def = Get(id);
        return def != null ? def.record.GetText(prefabLeaf, string.Empty) : string.Empty;
    }

    /// <summary>A fresh copy, already carrying whatever <c>modified_data</c> declared.</summary>
    public static Item Create(string name, int quantity = 1) => Create(IdOf(name), quantity);

    public static Item Create(int id, int quantity = 1)
    {
        Cache();

        if (id <= 0 || id >= starting.Length)
            return default;

        return new Item(id, quantity, starting[id]);
    }

    private static void Cache()
    {
        uint hash = Data.Hash;

        if (cached && cachedFor == hash)
            return;

        cached = true;
        cachedFor = hash;

        Schema schema = Schema;
        maxStackLeaf = schema.IdOf("maxStack");
        descriptionLeaf = schema.IdOf("description");
        prefabLeaf = schema.IdOf("prefab");

        modifiedOffset = 0;
        modifiedNode = default;

        if (schema.TryNode(DataSection, out NodeRef data) && schema.TryNode(ModifiedSection, out NodeRef modified))
        {
            if (Matches(schema, data, modified))
            {
                modifiedNode = modified;
                modifiedOffset = modified.start - data.start;
            }
            else
            {
                Debug.LogError($"{FolderName}: '{ModifiedSection}' must have the same shape as "
                             + $"'{DataSection}'. Declare it as '{ModifiedSection} = {DataSection}'.");
            }
        }

        starting = new Record[Count + 1];

        foreach (Definition def in All)
            starting[def.id] = Shift(def.record);
    }

    private static bool Matches(Schema schema, NodeRef data, NodeRef modified)
    {
        if (data.Count != modified.Count)
            return false;

        for (int i = 0; i < data.Count; i++)
        {
            string left = schema.Get(data.start + i).path.Substring(DataSection.Length);
            string right = schema.Get(modified.start + i).path.Substring(ModifiedSection.Length);

            if (!string.Equals(left, right, System.StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>Moves a definition's <c>modified_data</c> values onto the matching <c>data</c> leaves.</summary>
    private static Record Shift(Record record)
    {
        if (modifiedNode.Count == 0 || record.Count == 0)
            return default;

        List<DataValue> shifted = null;

        for (int i = 0; i < record.values.Length; i++)
        {
            DataValue value = record.values[i];

            if (!modifiedNode.Contains(value.leaf))
                continue;

            value.leaf -= modifiedOffset;
            shifted ??= new List<DataValue>();
            shifted.Add(value);
        }

        return Record.Build(shifted);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Warm()
    {
        Cache();
        Debug.Log($"{FolderName}: {Count} item(s), {Schema.Count} leaf/leaves, hash {Hash:X8}.");
    }
}
