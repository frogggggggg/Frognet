using System;
using System.IO;
using Frognet.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Keeps the editor in step with the data text files: re-reads every registry whenever one is
/// saved, and offers the same thing on demand from the menu.
/// </summary>
public class DataRegistryEditor : AssetPostprocessor
{
    private const string Menu = "Frognet/Data/";

    private static void OnPostprocessAllAssets(string[] imported, string[] deleted,
        string[] moved, string[] movedFrom)
    {
        if (Touches(imported) || Touches(deleted) || Touches(moved))
            DataRegistry.ReloadAll();
    }

    private static bool Touches(string[] paths)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i].Replace('\\', '/');

            if (!path.Contains("/StreamingAssets/"))
                continue;

            if (path.EndsWith(DataRegistry.SchemaExtension, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(DataRegistry.DataExtension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    [MenuItem(Menu + "Reload")]
    private static void Reload()
    {
        DataRegistry.ReloadAll();

        if (ItemRegistry.Errors.Count == 0)
            Debug.Log($"Items: {ItemRegistry.Count} record(s), {ItemRegistry.Schema.Count} leaf/leaves, hash {ItemRegistry.Hash:X8}.");
    }

    [MenuItem(Menu + "Describe Item Schema")]
    private static void Describe()
    {
        Debug.Log("Item schema leaves:\n" + ItemRegistry.Schema.Describe());
    }

    [MenuItem(Menu + "Open Folder")]
    private static void Open()
    {
        string folder = ItemRegistry.Data.Folder;
        Directory.CreateDirectory(folder);
        EditorUtility.RevealInFinder(folder + Path.DirectorySeparatorChar);
    }
}
