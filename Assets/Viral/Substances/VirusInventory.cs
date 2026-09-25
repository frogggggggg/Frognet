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
/// What a virus holds besides its genes: a row of store slots, each holding one substance at a time
/// (any substance; an emptied slot is free again), up to <see cref="capacity"/> units. Also the head's
/// ring: every DNA slot and store slot mounted round the head view's sphere, in the order the player
/// arranged them, sharing <see cref="maxSlots"/> (more stores = fewer DNA slots). Pure data plus a
/// change event. Filled by extracting ResourceChunks (ResourceField), shown by GenomeView (the ring,
/// E) and InventoryView (the corner readout). Made on demand when a virus has none.
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

    public enum Kind { Gene, Store }

    /// <summary>One mount on the head's ring: a gene (Genome index; -1 an empty DNA slot) or a store
    /// (index into <see cref="slots"/>).</summary>
    [Serializable]
    public class Mount
    {
        public Kind kind;
        public int index = -1;
        public bool Empty(VirusInventory inv) => kind == Kind.Gene ? index < 0 : index >= inv.slots.Count || inv.slots[index].Empty;
    }

    [Min(0)] public int slotCount = 4;
    [Min(1f), Tooltip("Units one slot holds.")]
    public float capacity = 100f;
    public List<Slot> slots = new List<Slot>();

    [Min(1), Tooltip("Mounts round the head: DNA slots and store slots share them.")]
    public int maxSlots = 10;
    [Tooltip("The ring's order (the player drags them round). Filled in on use.")]
    public List<Mount> ring = new List<Mount>();

    public bool CanAddMount => ring.Count < maxSlots;

    /// <summary>The ring, every gene (0..genes-1) and store on it once, in the player's order.</summary>
    public IReadOnlyList<Mount> Ring(int genes)
    {
        Fit();
        for (int i = ring.Count - 1; i >= 0; i--)
        {
            Mount m = ring[i];
            bool bad = m == null || (m.kind == Kind.Gene ? m.index >= genes : m.index < 0 || m.index >= slots.Count);
            for (int j = 0; j < i && !bad; j++) bad = m.index >= 0 && ring[j] != null && ring[j].kind == m.kind && ring[j].index == m.index;
            if (bad) ring.RemoveAt(i);
        }
        for (int g = 0; g < genes; g++) if (Find(Kind.Gene, g) < 0) ring.Add(new Mount { kind = Kind.Gene, index = g });
        for (int s = 0; s < slots.Count; s++) if (Find(Kind.Store, s) < 0) ring.Add(new Mount { kind = Kind.Store, index = s });
        return ring;
    }

    public int Find(Kind kind, int index)
    {
        for (int i = 0; i < ring.Count; i++)
            if (ring[i] != null && ring[i].kind == kind && ring[i].index == index) return i;
        return -1;
    }

    /// <summary>A free mount becomes an empty store slot or an empty DNA slot.</summary>
    public void AddMount(Kind kind)
    {
        if (!CanAddMount) return;
        if (kind == Kind.Store)
        {
            slotCount++;
            Fit();
            ring.Add(new Mount { kind = Kind.Store, index = slots.Count - 1 });
            Changed?.Invoke(this, slots.Count - 1);
        }
        else ring.Add(new Mount { kind = Kind.Gene, index = -1 });
    }

    /// <summary>Frees an empty mount (a full one keeps its contents: false).</summary>
    public bool RemoveMount(int at)
    {
        if (at < 0 || at >= ring.Count || !ring[at].Empty(this)) return false;
        Mount m = ring[at];
        ring.RemoveAt(at);
        if (m.kind == Kind.Store && m.index < slots.Count)
        {
            slots.RemoveAt(m.index);
            slotCount = Mathf.Max(0, slotCount - 1);
            foreach (Mount o in ring)
                if (o.kind == Kind.Store && o.index > m.index) o.index--;
            Changed?.Invoke(this, -1);
        }
        return true;
    }

    /// <summary>A gene left the genome (Genome.Consume): its mount stays as an empty DNA slot, later
    /// genes' mounts shift down one.</summary>
    public void GeneRemoved(int gene)
    {
        foreach (Mount m in ring)
            if (m != null && m.kind == Kind.Gene)
            {
                if (m.index == gene) m.index = -1;
                else if (m.index > gene) m.index--;
            }
    }

    /// <summary>Moves a mount to another place on the ring (the rest close up round it).</summary>
    public void MoveMount(int from, int to)
    {
        if (from < 0 || from >= ring.Count) return;
        to = Mathf.Clamp(to, 0, ring.Count - 1);
        if (from == to) return;
        Mount m = ring[from];
        ring.RemoveAt(from);
        ring.Insert(to, m);
    }

    /// <summary>The store slot 's' flows into next: one holding it with room, else an empty one (-1 none).</summary>
    public int SlotFor(Substance s)
    {
        if (s == null) return -1;
        Fit();
        for (int i = 0; i < slots.Count; i++) if (slots[i].Holds(s) && slots[i].amount < capacity - 1e-4f) return i;
        for (int i = 0; i < slots.Count; i++) if (slots[i].Empty) return i;
        return -1;
    }

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

    /// <summary>Removes up to 'amount' from one slot (an emptied slot is free again). Returns how much came out.</summary>
    public float TakeFrom(int slot, float amount)
    {
        Fit();
        if (slot < 0 || slot >= slots.Count || slots[slot].Empty || amount <= 0f) return 0f;
        Slot s = slots[slot];
        float got = Mathf.Min(amount, s.amount);
        s.amount -= got;
        if (s.amount <= 1e-4f) { s.amount = 0f; s.substance = s.code = ""; }
        Changed?.Invoke(this, slot);
        return got;
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
