using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>How a lump of a substance looks wherever it's loose (a cell's cytoplasm, a resource blob's core, a
/// stream into the virus); drawn by `Substances/SubstanceLook.hlsl`. Each kind has its own shape so they read apart
/// at a glance: Chunk (plain lump, anything unknown), Honey (energy: golden liquid drops that run together, ATP),
/// Bubble (gas: two joined bubbles, O2), Crystal (a hexagonal ring, glucose), Coil (a folded chain of beads,
/// protein).</summary>
public enum SubstanceLook { Chunk, Honey, Bubble, Crystal, Coil }

/// <summary>A kind of material a virus or a cell can carry (glucose, protein, ATP...). Plain data: stores
/// match substances by <see cref="name"/>. Known ones are in <see cref="SubstanceCatalog"/>.</summary>
[Serializable]
public class Substance
{
    [Tooltip("Full name, shown in the stores. Slots match substances by it.")]
    public string name = "GLUCOSE";
    [Tooltip("Short code for tight labels.")]
    public string code = "GLU";
    public Color color = new Color(1f, 0.95f, 0.86f);
    [Tooltip("How it looks floating loose inside a cell.")]
    public SubstanceLook look;
}

/// <summary>The substances the game knows by name (cells' stores, organelle recipes, the synthesizer's costs).
/// The catalog is the truth for colour and look: chunk prefabs take theirs from it by name. Colours: no reds (red
/// blood cells are red). An unknown name gets a plain grey one.</summary>
public static class SubstanceCatalog
{
    public const string ATP = "ATP", Oxygen = "OXYGEN", Glucose = "GLUCOSE", Protein = "PROTEIN";

    static readonly Dictionary<string, Substance> s_known = new Dictionary<string, Substance>
    {
        // The universal energy: what mitochondria make. Glowing gold.
        { ATP, new Substance { name = ATP, code = "ATP", color = new Color(1f, 0.74f, 0.2f), look = SubstanceLook.Honey } },
        { Oxygen, new Substance { name = Oxygen, code = "O2", color = new Color(0.55f, 0.85f, 1f), look = SubstanceLook.Bubble } },
        { Glucose, new Substance { name = Glucose, code = "GLU", color = new Color(1f, 0.97f, 0.74f), look = SubstanceLook.Crystal } },
        { Protein, new Substance { name = Protein, code = "PRO", color = new Color(1f, 0.46f, 0.1f), look = SubstanceLook.Coil } },
    };

    /// <summary>Whether 'name' is one of the catalog's own (not a made-up grey one).</summary>
    public static bool Knows(string name) => name == ATP || name == Oxygen || name == Glucose || name == Protein;

    /// <summary>The substance called 'name' (shared: don't edit it).</summary>
    public static Substance Find(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (s_known.TryGetValue(name, out Substance s)) return s;
        s = new Substance { name = name, code = name.Length > 3 ? name.Substring(0, 3) : name, color = new Color(0.7f, 0.75f, 0.8f) };
        s_known[name] = s;
        return s;
    }

    /// <summary>A copy to edit (recipes' costs...).</summary>
    public static Substance Copy(string name)
    {
        Substance s = Find(name);
        return new Substance { name = s.name, code = s.code, color = s.color, look = s.look };
    }
}

/// <summary>One store slot (a virus's tube, a cell's store): one substance at a time up to the store's capacity;
/// an emptied slot is free again.</summary>
[Serializable]
public class StoreSlot
{
    public string substance = "";
    public string code = "";
    public Color color = Color.white;
    public float amount;
    public bool Empty => amount <= 1e-4f || string.IsNullOrEmpty(substance);
    public bool Holds(Substance s) => !Empty && s != null && substance == s.name;
    public bool Holds(string name) => !Empty && substance == name;

    public void Clear()
    {
        amount = 0f;
        substance = code = "";
    }
}

/// <summary>
/// The rules every store follows (VirusInventory's tubes, CellInterior's stores): a row of slots, each one
/// substance up to 'capacity'. Adding tops up slots already holding it, then takes empty ones; taking empties
/// the last slot first. 'changed' (optional) hears each slot index touched. No allocations.
/// </summary>
public static class Stores
{
    public static float Add(List<StoreSlot> slots, float capacity, Substance s, float amount, Action<int> changed = null)
    {
        if (s == null || amount <= 0f) return 0f;
        float left = amount;
        for (int pass = 0; pass < 2 && left > 0f; pass++)
            for (int i = 0; i < slots.Count && left > 0f; i++)
            {
                StoreSlot slot = slots[i];
                if (pass == 0 ? !slot.Holds(s) : !slot.Empty) continue;
                if (slot.Empty)
                {
                    slot.substance = s.name;
                    slot.code = s.code;
                    slot.color = s.color;
                    slot.amount = 0f;
                }
                float put = Mathf.Min(left, capacity - slot.amount);
                if (put <= 0f) continue;
                slot.amount += put;
                left -= put;
                changed?.Invoke(i);
            }
        return amount - left;
    }

    /// <summary>Removes up to 'amount' of the named substance (last slot first). Returns how much came out.</summary>
    public static float Take(List<StoreSlot> slots, string substance, float amount, Action<int> changed = null)
    {
        float left = amount;
        for (int i = slots.Count - 1; i >= 0 && left > 0f; i--)
        {
            StoreSlot slot = slots[i];
            if (!slot.Holds(substance)) continue;
            float got = Mathf.Min(left, slot.amount);
            slot.amount -= got;
            left -= got;
            if (slot.amount <= 1e-4f) slot.Clear();
            changed?.Invoke(i);
        }
        return amount - left;
    }

    public static float Total(List<StoreSlot> slots, string substance)
    {
        float sum = 0f;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].Holds(substance)) sum += slots[i].amount;
        return sum;
    }

    /// <summary>How much more of the named substance fits (its slots' headroom plus every empty slot).</summary>
    public static float Room(List<StoreSlot> slots, float capacity, string substance)
    {
        float room = 0f;
        for (int i = 0; i < slots.Count; i++)
        {
            StoreSlot slot = slots[i];
            if (slot.Empty) room += capacity;
            else if (slot.substance == substance) room += Mathf.Max(0f, capacity - slot.amount);
        }
        return room;
    }

    /// <summary>The slot 's' flows into next: one holding it with room, else an empty one (-1 none).</summary>
    public static int SlotFor(List<StoreSlot> slots, float capacity, Substance s)
    {
        if (s == null) return -1;
        for (int i = 0; i < slots.Count; i++) if (slots[i].Holds(s) && slots[i].amount < capacity - 1e-4f) return i;
        for (int i = 0; i < slots.Count; i++) if (slots[i].Empty) return i;
        return -1;
    }

    /// <summary>Exactly 'count' slots.</summary>
    public static void Fit(List<StoreSlot> slots, int count)
    {
        while (slots.Count < count) slots.Add(new StoreSlot());
        if (slots.Count > count) slots.RemoveRange(count, slots.Count - count);
    }
}
