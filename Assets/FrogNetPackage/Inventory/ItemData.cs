using System;
using System.Globalization;
using UnityEngine;

/// <summary>
/// One modification: durability, an equipment slot, an enchantment level, a custom name,
/// a dye colour, a state flag, anything. The key comes from the ItemKeys asset.
/// </summary>
[Serializable]
public struct ItemMod
{
    public string key;
    public string value;
}

/// <summary>
/// One inventory slot. <see cref="data"/> supplies the shared defaults;
/// <see cref="mods"/> holds only what this copy changed, so an unmodified item costs nothing.
/// </summary>
/// <remarks>
/// The instance mods array is kept sorted by key and is never edited in place. Copying an Item
/// shares the array, which is safe precisely because every change goes through
/// <see cref="With(string,string)"/> and returns a new array. Do not write to <c>mods[i]</c> directly.
/// </remarks>
[Serializable]
public struct Item
{
    public ItemData data;
    public int quantity;
    public ItemMod[] mods;

    public bool IsEmpty => data == null || quantity <= 0;
    public bool IsModified => mods != null && mods.Length > 0;

    /// <summary>Reads this copy's own mods first, then the defaults on <see cref="data"/>.</summary>
    public string Get(string key, string fallback = null)
    {
        return Find(key, out ItemMod mod) ? mod.value : fallback;
    }

    public int GetInt(string key, int fallback = 0)
    {
        string raw = Get(key);
        return raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
    }

    public bool Has(string key) => Find(key, out _);

    /// <summary>
    /// True if anything is set under <paramref name="branch"/>, e.g. "consumable" matches
    /// "consumable/food/amount". Use this to ask whether a whole category applies at all.
    /// </summary>
    public bool HasBranch(string branch)
    {
        return HasBranch(mods, branch) || (data != null && HasBranch(data.mods, branch));
    }

    private static bool HasBranch(ItemMod[] list, string branch)
    {
        if (list == null || string.IsNullOrEmpty(branch))
            return false;

        for (int i = 0; i < list.Length; i++)
        {
            string key = list[i].key;

            if (key != null
                && key.Length > branch.Length
                && key[branch.Length] == ItemKeys.Separator
                && key.StartsWith(branch, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Returns a copy with <paramref name="key"/> set. The original is untouched.</summary>
    public Item With(string key, string value)
    {
        return WithMod(new ItemMod { key = key, value = value }, false);
    }


    public Item With(string key, int value)
    {
        return With(key, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Returns a copy with this instance's override of <paramref name="key"/> dropped,
    /// so any default on <see cref="data"/> takes over again.
    /// </summary>
    public Item Without(string key)
    {
        return WithMod(new ItemMod { key = key }, true);
    }

    /// <summary>
    /// Two items only stack if their own modifications match. Defaults are already covered by
    /// comparing <see cref="data"/>. Keys are sorted, so this is a straight linear compare.
    /// </summary>
    public static bool SameMods(Item a, Item b)
    {
        int countA = a.mods != null ? a.mods.Length : 0;
        int countB = b.mods != null ? b.mods.Length : 0;

        if (countA != countB)
            return false;

        for (int i = 0; i < countA; i++)
        {
            if (a.mods[i].key != b.mods[i].key || a.mods[i].value != b.mods[i].value)
                return false;
        }

        return true;
    }

    private bool Find(string key, out ItemMod mod)
    {
        return Find(mods, key, out mod) || (data != null && Find(data.mods, key, out mod));
    }

    private static bool Find(ItemMod[] list, string key, out ItemMod mod)
    {
        if (list != null)
        {
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].key == key)
                {
                    mod = list[i];
                    return true;
                }
            }
        }

        mod = default;
        return false;
    }

    private Item WithMod(ItemMod entry, bool remove)
    {
        Item copy = this;
        copy.mods = SetMod(mods, entry, remove);
        return copy;
    }

    /// <summary>
    /// Copy-on-write set, replace or remove. Because the array is sorted, one scan finds both
    /// an existing key and the position a new key belongs in.
    /// </summary>
    private static ItemMod[] SetMod(ItemMod[] source, ItemMod entry, bool remove)
    {
        int count = source != null ? source.Length : 0;
        int index = 0;

        while (index < count && string.CompareOrdinal(source[index].key, entry.key) < 0)
            index++;

        bool exists = index < count && source[index].key == entry.key;

        if (remove)
        {
            if (!exists)
                return source;

            if (count == 1)
                return null;

            var shrunk = new ItemMod[count - 1];
            Array.Copy(source, 0, shrunk, 0, index);
            Array.Copy(source, index + 1, shrunk, index, count - index - 1);
            return shrunk;
        }

        if (exists)
        {
            var replaced = (ItemMod[])source.Clone();
            replaced[index] = entry;
            return replaced;
        }

        var grown = new ItemMod[count + 1];

        if (count > 0)
        {
            Array.Copy(source, 0, grown, 0, index);
            Array.Copy(source, index, grown, index + 1, count - index);
        }

        grown[index] = entry;
        return grown;
    }
}

[CreateAssetMenu(menuName = "Frognet/Item", fileName = "Item")]
public class ItemData : ScriptableObject
{
    public string itemName;
    [TextArea] public string description;

    [Min(1), Tooltip("How many fit in one slot.")]
    public int maxStack = 1;

    [Tooltip("Defaults every copy of this item starts with. Instances override these by key.")]
    public ItemMod[] mods;
}
