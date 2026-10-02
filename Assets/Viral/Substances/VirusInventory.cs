using System;
using System.Collections.Generic;
using UnityEngine;

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
    public List<StoreSlot> slots = new List<StoreSlot>();

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
        Fit();
        return Stores.SlotFor(slots, capacity, s);
    }

    /// <summary>(inventory, slot index) whenever a slot's contents change.</summary>
    public event Action<VirusInventory, int> Changed;
    Action<int> _changedAt;
    Action<int> ChangedAt => _changedAt ??= i => Changed?.Invoke(this, i);

    public IReadOnlyList<StoreSlot> Slots { get { Fit(); return slots; } }

    /// <summary>Stores up to 'amount' of 's': tops up the slots already holding it, then takes an empty
    /// one. Returns how much went in (less when full).</summary>
    public float Add(Substance s, float amount)
    {
        Fit();
        return Stores.Add(slots, capacity, s, amount, ChangedAt);
    }

    /// <summary>Whether any of 's' still fits.</summary>
    public bool Accepts(Substance s)
    {
        Fit();
        return s != null && Stores.Room(slots, capacity, s.name) > 1e-4f;
    }

    /// <summary>Removes up to 'amount' of the named substance (last slot first). Returns how much came out.</summary>
    public float Take(string substance, float amount)
    {
        Fit();
        return Stores.Take(slots, substance, amount, ChangedAt);
    }

    /// <summary>Removes up to 'amount' from one slot (an emptied slot is free again). Returns how much came out.</summary>
    public float TakeFrom(int slot, float amount)
    {
        Fit();
        if (slot < 0 || slot >= slots.Count || slots[slot].Empty || amount <= 0f) return 0f;
        StoreSlot s = slots[slot];
        float got = Mathf.Min(amount, s.amount);
        s.amount -= got;
        if (s.amount <= 1e-4f) s.Clear();
        Changed?.Invoke(this, slot);
        return got;
    }

    public float Total(string substance)
    {
        Fit();
        return Stores.Total(slots, substance);
    }

    /// <summary>Puts back saved stores and ring order (loading a save).</summary>
    public void Restore(List<StoreSlot> saved, List<Mount> savedRing)
    {
        slots = new List<StoreSlot>();
        if (saved != null)
            foreach (StoreSlot s in saved)
                if (s != null) slots.Add(new StoreSlot { substance = s.substance, code = s.code, color = s.color, amount = s.amount });
        ring = new List<Mount>();
        if (savedRing != null)
            foreach (Mount m in savedRing)
                if (m != null) ring.Add(new Mount { kind = m.kind, index = m.index });
        Fit();
        for (int i = 0; i < slots.Count; i++) Changed?.Invoke(this, i);
    }

    // Exactly slotCount slots (the list is also edited in the inspector).
    void Fit() => Stores.Fit(slots, slotCount);

    void OnValidate() => Fit();

    public static VirusInventory Of(Component owner)
    {
        VirusInventory inv = owner.GetComponentInChildren<VirusInventory>(true);
        return inv ? inv : owner.gameObject.AddComponent<VirusInventory>(); // not ??: Unity's fake null
    }
}
