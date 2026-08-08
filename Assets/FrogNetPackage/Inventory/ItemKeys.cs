using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One modification key. A key either has sub-keys, or holds a value.
/// <see cref="values"/> turns that value into a dropdown instead of a free text field.
/// </summary>
[Serializable]
public class ItemKey
{
    public string name;

    [Tooltip("Leave empty to type any value. Fill it in to pick from a dropdown instead.")]
    public List<string> values = new List<string>();

    [SerializeReference, Tooltip("Sub-keys. A key with sub-keys holds no value of its own.")]
    public List<ItemKey> children = new List<ItemKey>();

    public bool HasPresets => values != null && values.Count > 0;
    public bool HasChildren => children != null && children.Count > 0;
}

/// <summary>
/// The tree of modification keys items are allowed to use. Purely an authoring aid: nothing at
/// runtime checks against it, it just drives the dropdowns in the inspector so keys and values
/// stay consistent and typos stop being silent.
/// </summary>
/// <remarks>
/// A mod stores the full path to a leaf, joined by <see cref="Separator"/>,
/// e.g. <c>consumable/speed/time</c>.
/// </remarks>
[CreateAssetMenu(menuName = "Frognet/Item Keys", fileName = "ItemKeys")]
public class ItemKeys : ScriptableObject
{
    public const char Separator = '/';

    [SerializeReference]
    public List<ItemKey> keys = new List<ItemKey>
    {
        new ItemKey { name = "durability" },
        new ItemKey { name = "maxDurability" },
        new ItemKey { name = "name" },
        new ItemKey
        {
            name = "slot",
            values = new List<string> { "Head", "Chest", "Legs", "Feet", "Hands", "Weapon", "Shield" }
        },
        new ItemKey
        {
            name = "consumable",
            children = new List<ItemKey>
            {
                new ItemKey
                {
                    name = "food",
                    children = new List<ItemKey> { new ItemKey { name = "amount" } }
                },
                new ItemKey
                {
                    name = "speed",
                    children = new List<ItemKey>
                    {
                        new ItemKey { name = "amount" },
                        new ItemKey { name = "duration" }
                    }
                }
            }
        }
    };

    /// <summary>Walks a full path such as "consumable/speed/time". Null if any segment is unknown.</summary>
    public ItemKey Find(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        List<ItemKey> level = keys;
        ItemKey node = null;

        foreach (string segment in path.Split(Separator))
        {
            node = Find(level, segment);

            if (node == null)
                return null;

            level = node.children;
        }

        return node;
    }

    public static ItemKey Find(List<ItemKey> level, string name)
    {
        if (level == null || string.IsNullOrEmpty(name))
            return null;

        for (int i = 0; i < level.Count; i++)
        {
            if (level[i] != null && level[i].name == name)
                return level[i];
        }

        return null;
    }
}
