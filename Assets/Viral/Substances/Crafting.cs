using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What a virus can make from its stores (the head view's synthesizer, GenomeView): recipes that cost
/// substances and make a DNA strand (a gene, into an empty DNA slot or a free mount) or a substance
/// (into a store). Pure data plus the sums: what a recipe would draw from which store
/// (<see cref="Plan"/>), where its product goes (<see cref="Output"/>), and making it
/// (<see cref="Deliver"/>). Taking the material is left to the caller, so it can be drawn out over
/// time. Made on demand (with test recipes: genes from glucose and protein) when a virus has none.
/// </summary>
public class Crafting : MonoBehaviour
{
    [Serializable]
    public class Cost
    {
        [Tooltip("Matched to the stores by name; the colour is for the cost pips.")]
        public Substance substance = new Substance();
        [Min(0f)] public float amount = 20f;
    }

    /// <summary>The picture a recipe shows in the synthesizer's menu (drawn by GenomeView), so recipes are
    /// told apart at a glance, not by reading. Strand = a plain helix (the fallback).</summary>
    public enum Glyph { Strand, Burst, Copy, Shell, Spike, Scissors, Drop, Chain, Bolt, Star, Eye, Shield }

    [Serializable]
    public class Recipe
    {
        public string code = "GEN-0";
        public string name = "UNKNOWN";
        public Color color = TerminalUI.Line;
        public Glyph glyph;
        public Cost[] costs = new Cost[0];
        [Tooltip("Off: makes a DNA strand (a gene with the code, name and colour above). On: 'amount' of a " +
                 "substance with them, into a store.")]
        public bool liquid;
        [Min(0f)] public float amount = 50f;
    }

    static Substance Glucose => new Substance { name = "GLUCOSE", code = "GLU", color = new Color(1f, 0.95f, 0.86f) };
    static Substance Protein => new Substance { name = "PROTEIN", code = "PRO", color = new Color(1f, 0.64f, 0.3f) };
    static Cost[] Costs(float glucose, float protein)
    {
        var list = new List<Cost>();
        if (glucose > 0f) list.Add(new Cost { substance = Glucose, amount = glucose });
        if (protein > 0f) list.Add(new Cost { substance = Protein, amount = protein });
        return list.ToArray();
    }

    public List<Recipe> recipes = new List<Recipe>
    {
        new Recipe { code = "LYS-1", name = "LYSIS",     color = new Color(1f, 0.36f, 0.42f), glyph = Glyph.Burst,    costs = Costs(20f, 30f) },
        new Recipe { code = "REP-2", name = "REPLICASE", color = new Color(0.55f, 0.93f, 1f), glyph = Glyph.Copy,     costs = Costs(40f, 10f) },
        new Recipe { code = "CAP-3", name = "CAPSID",    color = new Color(0.62f, 1f, 0.45f), glyph = Glyph.Shell,    costs = Costs(0f, 40f) },
        new Recipe { code = "SPK-4", name = "SPIKE",     color = new Color(1f, 0.78f, 0.35f), glyph = Glyph.Spike,    costs = Costs(15f, 25f) },
        new Recipe { code = "INT-5", name = "INTEGRASE", color = new Color(0.78f, 0.55f, 1f), glyph = Glyph.Scissors, costs = Costs(30f, 30f) },
    };

    /// <summary>What the recipe would take from which store slot (last slot first, as the stores are
    /// drained), as much as there is. True if there's enough of everything.</summary>
    public static bool Plan(Recipe r, VirusInventory inv, List<(int slot, float amount)> into)
    {
        into.Clear();
        bool enough = true;
        IReadOnlyList<VirusInventory.Slot> slots = inv.Slots;
        foreach (Cost c in r.costs)
        {
            float left = c.amount;
            for (int i = slots.Count - 1; i >= 0 && left > 1e-4f; i--)
            {
                if (slots[i].Empty || slots[i].substance != c.substance.name) continue;
                float already = 0f; // a slot two costs share (the same substance listed twice)
                foreach (var p in into) if (p.slot == i) already += p.amount;
                float got = Mathf.Min(left, slots[i].amount - already);
                if (got <= 1e-4f) continue;
                into.Add((i, got));
                left -= got;
            }
            if (left > 1e-3f) enough = false;
        }
        return enough;
    }

    /// <summary>How much of a cost the stores are short (0: enough).</summary>
    public static float Short(Cost c, VirusInventory inv) => Mathf.Max(0f, c.amount - inv.Total(c.substance.name));

    /// <summary>Where the product goes: a ring index (an empty DNA slot / a store with room), -1 when a
    /// new DNA mount would have to be added (there's room for one), -2 nowhere.</summary>
    public static int Output(Recipe r, VirusInventory inv, Genome genome)
    {
        IReadOnlyList<VirusInventory.Mount> ring = inv.Ring(genome.genes.Count);
        if (r.liquid)
        {
            int slot = inv.SlotFor(AsSubstance(r));
            return slot < 0 ? -2 : inv.Find(VirusInventory.Kind.Store, slot);
        }
        for (int i = 0; i < ring.Count; i++)
            if (ring[i].kind == VirusInventory.Kind.Gene && ring[i].index < 0) return i;
        return inv.CanAddMount ? -1 : -2;
    }

    /// <summary>Whether it can be made now; if not, why (short text for the view).</summary>
    public static bool Can(Recipe r, VirusInventory inv, Genome genome, List<(int slot, float amount)> plan, out string why)
    {
        why = null;
        if (!Plan(r, inv, plan))
        {
            foreach (Cost c in r.costs)
            {
                float s = Short(c, inv);
                if (s > 1e-3f) { why = "NEED " + Mathf.CeilToInt(s) + " MORE " + c.substance.code; break; }
            }
            return false;
        }
        if (Output(r, inv, genome) == -2)
        {
            why = r.liquid ? "NO STORE FREE" : "NO FREE DNA SLOT";
            return false;
        }
        return true;
    }

    public static Substance AsSubstance(Recipe r) => new Substance { name = r.name, code = r.code, color = r.color };

    /// <summary>A strand recipe's product: the new gene, put in the given DNA mount (ring index). Returns
    /// the gene's index.</summary>
    public static int Deliver(Recipe r, VirusInventory inv, Genome genome, int mount)
    {
        genome.genes.Add(new Genome.Gene { code = r.code, name = r.name, color = r.color });
        int index = genome.genes.Count - 1;
        if (mount >= 0 && mount < inv.ring.Count && inv.ring[mount].kind == VirusInventory.Kind.Gene && inv.ring[mount].index < 0)
            inv.ring[mount].index = index;
        return index;
    }

    public static Crafting Of(Component owner)
    {
        Crafting c = owner.GetComponentInChildren<Crafting>(true);
        return c ? c : owner.gameObject.AddComponent<Crafting>(); // not ??: Unity's fake null
    }
}
