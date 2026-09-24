using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>A kind of material a virus can carry (glucose, protein...). Plain data: slots and
/// chunks match substances by <see cref="name"/>, so a new one is just a new chunk prefab.</summary>
[Serializable]
public class Substance
{
    [Tooltip("Full name, shown in the stores. Slots match substances by it.")]
    public string name = "GLUCOSE";
    [Tooltip("Short code for tight labels.")]
    public string code = "GLU";
    public Color color = new Color(1f, 0.95f, 0.86f);
}

/// <summary>
/// What a virus holds besides its genes: a row of slots, each holding one substance at a time (any
/// substance; an emptied slot is free again), up to <see cref="capacity"/> units. Pure data plus a
/// change event. Filled by extracting ResourceChunks (ResourceField), shown by InventoryView
/// (E, or the head view in focus mode). Made on demand when a virus has none.
/// </summary>
public class VirusInventory : MonoBehaviour
{
    [Serializable]
    public class Slot
    {
        public string substance = "";
        public string code = "";
        public Color color = Color.white;
        public float amount;
        public bool Empty => amount <= 1e-4f || string.IsNullOrEmpty(substance);
        public bool Holds(Substance s) => !Empty && s != null && substance == s.name;
    }

    [Min(1)] public int slotCount = 4;
    [Min(1f), Tooltip("Units one slot holds.")]
    public float capacity = 100f;
    public List<Slot> slots = new List<Slot>();

    /// <summary>(inventory, slot index) whenever a slot's contents change.</summary>
    public event Action<VirusInventory, int> Changed;

    public IReadOnlyList<Slot> Slots { get { Fit(); return slots; } }

    /// <summary>Stores up to 'amount' of 's': tops up the slots already holding it, then takes an empty
    /// one. Returns how much went in (less when full).</summary>
    public float Add(Substance s, float amount)
    {
        if (s == null || amount <= 0f) return 0f;
        Fit();
        float left = amount;
        for (int pass = 0; pass < 2 && left > 0f; pass++)
            for (int i = 0; i < slots.Count && left > 0f; i++)
            {
                Slot slot = slots[i];
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
                Changed?.Invoke(this, i);
            }
        return amount - left;
    }

    /// <summary>Whether any of 's' still fits.</summary>
    public bool Accepts(Substance s)
    {
        if (s == null) return false;
        Fit();
        foreach (Slot slot in slots)
            if (slot.Empty || slot.Holds(s) && slot.amount < capacity - 1e-4f) return true;
        return false;
    }

    /// <summary>Removes up to 'amount' of the named substance (last slot first). Returns how much came out.</summary>
    public float Take(string substance, float amount)
    {
        Fit();
        float left = amount;
        for (int i = slots.Count - 1; i >= 0 && left > 0f; i--)
        {
            Slot slot = slots[i];
            if (slot.Empty || slot.substance != substance) continue;
            float got = Mathf.Min(left, slot.amount);
            slot.amount -= got;
            left -= got;
            if (slot.amount <= 1e-4f) { slot.amount = 0f; slot.substance = slot.code = ""; }
            Changed?.Invoke(this, i);
        }
        return amount - left;
    }

    public float Total(string substance)
    {
        float sum = 0f;
        foreach (Slot slot in Slots)
            if (!slot.Empty && slot.substance == substance) sum += slot.amount;
        return sum;
    }

    // Exactly slotCount slots (the list is also edited in the inspector).
    void Fit()
    {
        while (slots.Count < slotCount) slots.Add(new Slot());
        if (slots.Count > slotCount) slots.RemoveRange(slotCount, slots.Count - slotCount);
    }

    void OnValidate() => Fit();

    public static VirusInventory Of(Component owner)
    {
        VirusInventory inv = owner.GetComponentInChildren<VirusInventory>(true);
        return inv ? inv : owner.gameObject.AddComponent<VirusInventory>(); // not ??: Unity's fake null
    }
}
