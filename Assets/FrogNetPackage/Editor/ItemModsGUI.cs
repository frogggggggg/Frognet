using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Draws an <c>ItemMod[]</c> as one row per key group. Dropdowns walk down the ItemKeys tree,
/// and once a key is reached that only has plain sub-keys, every one of them appears as its own
/// labelled box on the same row. Each box is still stored as its own entry in the array.
/// </summary>
public static class ItemModsGUI
{
    private const string DefaultAssetPath = "Assets/FrogNetPackage/Inventory/ItemKeys.asset";
    private const float Pad = 4f;
    private const float RemoveWidth = 20f;
    private const float LabelWidth = 58f;
    private const float KeyShare = 0.45f;

    private static readonly Color RowTint = new Color(0.5f, 0.5f, 0.5f, 0.12f);
    private static readonly Dictionary<string, List<string>> drafts = new Dictionary<string, List<string>>();
    private static ItemKeys cached;

    public static float Height(SerializedProperty mods)
    {
        ItemKeys registry = Registry();

        if (registry == null)
            return EditorGUI.GetPropertyHeight(mods, true);

        int lines = Rows(mods, registry).Count + Drafts(mods).Count + 1;

        return lines * EditorGUIUtility.singleLineHeight
             + (lines - 1) * EditorGUIUtility.standardVerticalSpacing;
    }

    public static void Draw(Rect position, SerializedProperty mods)
    {
        ItemKeys registry = Registry();

        if (registry == null)
        {
            DrawWithoutRegistry(position, mods);
            return;
        }

        float step = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
        var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

        List<string> rows = Rows(mods, registry);
        List<string> pending = Drafts(mods);

        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (pending[i].Length > 0 && rows.Contains(pending[i]))
                pending.RemoveAt(i);
        }

        bool restructured = false;

        for (int i = 0; i < rows.Count && !restructured; i++)
        {
            restructured = DrawRow(line, mods, registry, rows[i], pending, -1, i);
            line.y += step;
        }

        for (int i = 0; i < pending.Count && !restructured; i++)
        {
            restructured = DrawRow(line, mods, registry, pending[i], pending, i, rows.Count + i);
            line.y += step;
        }

        var addRect = new Rect(position.x, position.yMax - EditorGUIUtility.singleLineHeight, 130f, EditorGUIUtility.singleLineHeight);

        if (GUI.Button(addRect, "Add Modification"))
            pending.Add(string.Empty);
    }

    /// <summary>
    /// Returns true when the row list itself changed, so the caller stops drawing this pass
    /// rather than working from stale indices.
    /// </summary>
    private static bool DrawRow(Rect line, SerializedProperty mods, ItemKeys registry, string row, List<string> pending, int draft, int order)
    {
        if (order % 2 == 1 && Event.current.type == EventType.Repaint)
            EditorGUI.DrawRect(new Rect(line.x - 2f, line.y - 1f, line.width + 4f, line.height + 2f), RowTint);

        var prefixes = new List<string>();
        var levels = new List<List<ItemKey>>();
        ItemKey node = Walk(registry, Split(row), prefixes, levels, out string path);
        List<ItemKey> fields = Fields(node);

        Widths(line.width, prefixes.Count, fields.Count, out float keyWidth, out float fieldWidth);

        float x = line.x;
        string[] segments = Split(row);

        for (int depth = 0; depth < prefixes.Count; depth++)
        {
            var rect = new Rect(x, line.y, keyWidth, line.height);
            x += keyWidth + Pad;

            string current = depth < segments.Length ? segments[depth] : null;
            string picked = Popup(rect, current, Names(levels[depth]));

            if (picked == null)
                continue;

            RemoveUnder(mods, row);
            string parent = prefixes[depth];
            string next = parent.Length == 0 ? picked : parent + ItemKeys.Separator + picked;

            if (draft >= 0)
                pending[draft] = next;
            else
                pending.Add(next);

            return true;
        }

        float previousLabel = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = Mathf.Min(LabelWidth, fieldWidth * 0.5f);

        for (int i = 0; i < fields.Count; i++)
        {
            var rect = new Rect(x, line.y, fieldWidth, line.height);
            x += fieldWidth + Pad;

            ItemKey field = fields[i];
            bool single = field == node;
            string key = single ? path : path + ItemKeys.Separator + field.name;
            string current = Value(mods, key);

            if (field.HasPresets)
            {
                string picked = Popup(rect, current, field.values, single ? null : field.name);

                if (picked != null)
                    SetValue(mods, key, picked);
            }
            else
            {
                string next = single
                    ? EditorGUI.TextField(rect, current)
                    : EditorGUI.TextField(rect, field.name, current);

                if (next != current)
                    SetValue(mods, key, next);
            }
        }

        EditorGUIUtility.labelWidth = previousLabel;

        var removeRect = new Rect(line.xMax - RemoveWidth, line.y, RemoveWidth, line.height);

        if (!GUI.Button(removeRect, "x", EditorStyles.miniButton))
            return false;

        RemoveUnder(mods, row);

        if (draft >= 0)
            pending.RemoveAt(draft);

        return true;
    }

    /// <summary>
    /// Dropdowns take a fixed share of the row so the value boxes stay wide enough to type in,
    /// and take the whole row when there are no boxes yet.
    /// </summary>
    private static void Widths(float total, int keys, int fields, out float keyWidth, out float fieldWidth)
    {
        float available = total - RemoveWidth - Pad * (keys + fields);
        float keyShare = fields == 0 ? available : available * KeyShare;

        keyWidth = keys > 0 ? keyShare / keys : 0f;
        fieldWidth = fields > 0 ? (available - keyShare) / fields : 0f;
    }

    private static List<string> Drafts(SerializedProperty mods)
    {
        if (!drafts.TryGetValue(mods.propertyPath, out List<string> pending))
        {
            pending = new List<string>();
            drafts[mods.propertyPath] = pending;
        }

        return pending;
    }

    /// <summary>
    /// Follows a row path down the tree, collecting the branch each dropdown chooses from.
    /// Stops at a leaf or at a key whose sub-keys are all plain, since those become boxes.
    /// </summary>
    private static ItemKey Walk(ItemKeys registry, string[] segments, List<string> prefixes, List<List<ItemKey>> levels, out string path)
    {
        List<ItemKey> level = registry.keys;
        ItemKey node = null;
        path = string.Empty;

        for (int depth = 0; ; depth++)
        {
            prefixes.Add(path);
            levels.Add(level);

            if (depth >= segments.Length)
                return null;

            node = ItemKeys.Find(level, segments[depth]);

            if (node == null)
                return null;

            path = path.Length == 0 ? segments[depth] : path + ItemKeys.Separator + segments[depth];

            if (!node.HasChildren || IsGroup(node))
                return node;

            level = node.children;
        }
    }

    /// <summary>The boxes a row shows: the key itself if it is plain, otherwise its sub-keys.</summary>
    private static List<ItemKey> Fields(ItemKey node)
    {
        if (node == null)
            return new List<ItemKey>();

        return node.HasChildren ? node.children : new List<ItemKey> { node };
    }

    /// <summary>True when every sub-key is plain, so they can all be shown side by side.</summary>
    private static bool IsGroup(ItemKey node)
    {
        for (int i = 0; i < node.children.Count; i++)
        {
            if (node.children[i] == null || node.children[i].HasChildren)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The distinct rows the current entries belong to, ordered by the key tree rather than by
    /// the array, which is sorted by key and would reshuffle rows as they are filled in.
    /// </summary>
    private static List<string> Rows(SerializedProperty mods, ItemKeys registry)
    {
        var rows = new List<string>();

        for (int i = 0; i < mods.arraySize; i++)
        {
            string key = Key(mods, i);

            if (string.IsNullOrEmpty(key))
                continue;

            int cut = key.LastIndexOf(ItemKeys.Separator);
            string row = key;

            if (cut > 0)
            {
                string parent = key.Substring(0, cut);
                ItemKey node = registry.Find(parent);

                if (node != null && node.HasChildren && IsGroup(node))
                    row = parent;
            }

            if (!rows.Contains(row))
                rows.Add(row);
        }

        var order = new List<string>();
        Collect(registry.keys, string.Empty, order);

        var sorted = new List<string>();

        for (int i = 0; i < order.Count; i++)
        {
            if (rows.Remove(order[i]))
                sorted.Add(order[i]);
        }

        sorted.AddRange(rows);
        return sorted;
    }

    /// <summary>Every path in the tree, in the order the ItemKeys asset lists them.</summary>
    private static void Collect(List<ItemKey> level, string prefix, List<string> order)
    {
        if (level == null)
            return;

        for (int i = 0; i < level.Count; i++)
        {
            ItemKey key = level[i];

            if (key == null || string.IsNullOrEmpty(key.name))
                continue;

            string path = prefix.Length == 0 ? key.name : prefix + ItemKeys.Separator + key.name;
            order.Add(path);
            Collect(key.children, path, order);
        }
    }

    private static string Key(SerializedProperty mods, int index)
    {
        return mods.GetArrayElementAtIndex(index).FindPropertyRelative("key").stringValue;
    }

    private static string Value(SerializedProperty mods, string key)
    {
        int index = IndexOf(mods, key);
        return index < 0 ? string.Empty : mods.GetArrayElementAtIndex(index).FindPropertyRelative("value").stringValue;
    }

    private static int IndexOf(SerializedProperty mods, string key)
    {
        for (int i = 0; i < mods.arraySize; i++)
        {
            if (Key(mods, i) == key)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Writes one entry, keeping the array sorted by key the same way Item.SetMod does.
    /// An empty value deletes the entry so unset boxes cost nothing and never block stacking.
    /// </summary>
    private static void SetValue(SerializedProperty mods, string key, string value)
    {
        int index = IndexOf(mods, key);

        if (string.IsNullOrEmpty(value))
        {
            if (index >= 0)
                mods.DeleteArrayElementAtIndex(index);

            return;
        }

        if (index >= 0)
        {
            mods.GetArrayElementAtIndex(index).FindPropertyRelative("value").stringValue = value;
            return;
        }

        int insert = 0;

        while (insert < mods.arraySize && string.CompareOrdinal(Key(mods, insert), key) < 0)
            insert++;

        mods.InsertArrayElementAtIndex(insert);
        SerializedProperty element = mods.GetArrayElementAtIndex(insert);
        element.FindPropertyRelative("key").stringValue = key;
        element.FindPropertyRelative("value").stringValue = value;
    }

    private static void RemoveUnder(SerializedProperty mods, string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        string branch = path + ItemKeys.Separator;

        for (int i = mods.arraySize - 1; i >= 0; i--)
        {
            string key = Key(mods, i);

            if (key == path || (key != null && key.StartsWith(branch, StringComparison.Ordinal)))
                mods.DeleteArrayElementAtIndex(i);
        }
    }

    /// <summary>
    /// A popup over <paramref name="choices"/>, returning the new pick or null if nothing changed.
    /// A value that is not in the list is shown first and marked missing, so renaming something
    /// in the ItemKeys asset never silently blanks data.
    /// </summary>
    private static string Popup(Rect rect, string current, List<string> choices, string label = null)
    {
        var options = new List<string>(choices);

        int index = options.IndexOf(current);
        int offset = 0;

        if (index < 0)
        {
            options.Insert(0, string.IsNullOrEmpty(current) ? "(none)" : current + "  (missing)");
            index = 0;
            offset = 1;
        }

        int picked = label == null
            ? EditorGUI.Popup(rect, index, options.ToArray())
            : EditorGUI.Popup(rect, label, index, options.ToArray());

        return picked != index && picked >= offset ? choices[picked - offset] : null;
    }

    private static List<string> Names(List<ItemKey> level)
    {
        var names = new List<string>();

        for (int i = 0; i < level.Count; i++)
        {
            if (level[i] != null && !string.IsNullOrEmpty(level[i].name))
                names.Add(level[i].name);
        }

        return names;
    }

    private static string[] Split(string path)
    {
        return string.IsNullOrEmpty(path) ? Array.Empty<string>() : path.Split(ItemKeys.Separator);
    }

    private static void DrawWithoutRegistry(Rect position, SerializedProperty mods)
    {
        var help = new Rect(position.x, position.y, position.width, position.height - EditorGUIUtility.singleLineHeight);
        EditorGUI.PropertyField(help, mods, true);

        var button = new Rect(position.x, position.yMax - EditorGUIUtility.singleLineHeight, 130f, EditorGUIUtility.singleLineHeight);

        if (GUI.Button(button, "Create Key List"))
            cached = CreateRegistry();
    }

    private static ItemKeys Registry()
    {
        if (cached != null)
            return cached;

        string[] guids = AssetDatabase.FindAssets("t:ItemKeys");

        if (guids.Length > 0)
            cached = AssetDatabase.LoadAssetAtPath<ItemKeys>(AssetDatabase.GUIDToAssetPath(guids[0]));

        return cached;
    }

    private static ItemKeys CreateRegistry()
    {
        string folder = System.IO.Path.GetDirectoryName(DefaultAssetPath).Replace('\\', '/');

        if (!AssetDatabase.IsValidFolder(folder))
            folder = "Assets";

        var asset = ScriptableObject.CreateInstance<ItemKeys>();
        AssetDatabase.CreateAsset(asset, AssetDatabase.GenerateUniqueAssetPath(folder + "/ItemKeys.asset"));
        AssetDatabase.SaveAssets();
        return asset;
    }
}
