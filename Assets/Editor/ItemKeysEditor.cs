using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The key tree, one row per key. Everything a key can do sits behind the single menu button
/// on its row, so nesting stays readable.
/// </summary>
[CustomEditor(typeof(ItemKeys))]
public class ItemKeysEditor : Editor
{
    private const float Indent = 16f;

    private static readonly HashSet<string> collapsed = new HashSet<string>();

    public override void OnInspectorGUI()
    {
        var asset = (ItemKeys)target;

        asset.keys ??= new List<ItemKey>();

        DrawLevel(asset, asset.keys, string.Empty, 0);

        EditorGUILayout.Space(2f);

        if (GUILayout.Button("Add Key", GUILayout.Width(90f)))
        {
            Record(asset, "Add Key");
            asset.keys.Add(new ItemKey { name = "new" });
        }

        if (GUI.changed)
            EditorUtility.SetDirty(asset);
    }

    private void DrawLevel(ItemKeys asset, List<ItemKey> level, string parentPath, int depth)
    {
        for (int i = 0; i < level.Count; i++)
        {
            ItemKey key = level[i];

            if (key == null)
            {
                level.RemoveAt(i);
                return;
            }

            key.values ??= new List<string>();
            key.children ??= new List<ItemKey>();

            string path = parentPath.Length == 0 ? key.name : parentPath + ItemKeys.Separator + key.name;
            bool open = !collapsed.Contains(path);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * Indent);

                if (key.HasChildren || key.HasPresets)
                {
                    if (GUILayout.Toggle(open, GUIContent.none, EditorStyles.foldout, GUILayout.Width(14f)))
                        collapsed.Remove(path);
                    else
                        collapsed.Add(path);
                }
                else
                {
                    GUILayout.Space(14f);
                }

                key.name = EditorGUILayout.TextField(key.name);

                if (GUILayout.Button("\u22ee", EditorStyles.miniButton, GUILayout.Width(18f)))
                    ShowMenu(asset, level, key, i);
            }

            if (!open)
                continue;

            if (key.HasChildren)
                DrawLevel(asset, key.children, path, depth + 1);
            else if (key.HasPresets)
                DrawOptions(asset, key, depth + 1);
        }
    }

    private void DrawOptions(ItemKeys asset, ItemKey key, int depth)
    {
        for (int i = 0; i < key.values.Count; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * Indent + 14f);
                GUILayout.Label("option", EditorStyles.miniLabel, GUILayout.Width(40f));

                key.values[i] = EditorGUILayout.TextField(key.values[i], EditorStyles.miniTextField);

                if (GUILayout.Button("x", EditorStyles.miniButton, GUILayout.Width(18f)))
                {
                    Record(asset, "Remove Option");
                    key.values.RemoveAt(i);
                    return;
                }
            }
        }
    }

    private void ShowMenu(ItemKeys asset, List<ItemKey> level, ItemKey key, int index)
    {
        var menu = new GenericMenu();

        menu.AddItem(new GUIContent("Add Sub Key"), false, () =>
        {
            Record(asset, "Add Sub Key");
            key.children.Add(new ItemKey { name = "new" });
            key.values.Clear();
            EditorUtility.SetDirty(asset);
        });

        if (key.HasChildren)
            menu.AddDisabledItem(new GUIContent("Add Option"));
        else
            menu.AddItem(new GUIContent("Add Option"), false, () =>
            {
                Record(asset, "Add Option");
                key.values.Add(string.Empty);
                EditorUtility.SetDirty(asset);
            });

        menu.AddSeparator(string.Empty);

        menu.AddItem(new GUIContent("Delete"), false, () =>
        {
            Record(asset, "Delete Key");
            level.Remove(key);
            EditorUtility.SetDirty(asset);
        });

        menu.ShowAsContext();
    }

    private static void Record(ItemKeys asset, string action)
    {
        Undo.RegisterCompleteObjectUndo(asset, action);
    }
}
