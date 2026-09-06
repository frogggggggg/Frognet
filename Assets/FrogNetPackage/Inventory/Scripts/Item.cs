using System;
using Frognet.Data;

/// <summary>
/// One inventory slot: which item, how many, and whatever this copy has changed.
/// </summary>
/// <remarks>
/// Reads fall through <see cref="overrides"/> to the definition's <c>data</c>, so an unmodified
/// copy stores nothing. Field names are relative to <c>data</c>, so <c>Get("durability")</c> means
/// the schema leaf <c>data.durability</c>.
/// </remarks>
[Serializable]
public struct Item : IEquatable<Item>
{
    /// <summary>Registry id. Zero means no item, so <c>default(Item)</c> is an empty slot.</summary>
    public int id;

    public int quantity;

    /// <summary>What this copy changed, keyed by the same leaves as the definition's <c>data</c>.</summary>
    public Record overrides;

    public Item(int id, int quantity = 1, Record overrides = default)
    {
        this.id = id;
        this.quantity = quantity;
        this.overrides = overrides;
    }

    public Definition Def => ItemRegistry.Get(id);
    public bool IsEmpty => id <= 0 || quantity <= 0;
    public bool IsModified => overrides.Count > 0;
    public string Name => Def != null ? Def.name : string.Empty;
    public string Description => ItemRegistry.Description(id);
    public int MaxStack => ItemRegistry.MaxStack(id);

    /// <summary>This copy's own value first, then the definition's default.</summary>
    public bool TryGet(int leaf, out DataValue value)
    {
        if (overrides.TryGet(leaf, out value))
            return true;

        Definition def = Def;
        return def != null && def.record.TryGet(leaf, out value);
    }

    public bool TryGet(string field, out DataValue value) => TryGet(ItemRegistry.Leaf(field), out value);

    public bool Has(int leaf) => TryGet(leaf, out _);

    public bool Has(string field) => TryGet(field, out _);

    public float GetFloat(int leaf, float fallback = 0f, int arg = 0)
    {
        return TryGet(leaf, out DataValue value) ? value[arg] : fallback;
    }

    public float GetFloat(string field, float fallback = 0f, int arg = 0)
    {
        return GetFloat(ItemRegistry.Leaf(field), fallback, arg);
    }

    public int GetInt(int leaf, int fallback = 0, int arg = 0)
    {
        return TryGet(leaf, out DataValue value) ? (int)value[arg] : fallback;
    }

    public int GetInt(string field, int fallback = 0, int arg = 0)
    {
        return GetInt(ItemRegistry.Leaf(field), fallback, arg);
    }

    public bool GetBool(string field, bool fallback = false, int arg = 0)
    {
        return TryGet(field, out DataValue value) ? value[arg] != 0f : fallback;
    }

    public string GetText(string field, string fallback = null)
    {
        return TryGet(field, out DataValue value) && value.text != null ? value.text : fallback;
    }

    /// <summary>True when a <c>{ }</c> field is set to that branch, e.g. <c>Is("equipable", "head")</c>.</summary>
    public bool Is(string field, string branch) => Has(field + Schema.Separator + branch);

    /// <summary>Which branch of a <c>{ }</c> field is set, or null when none is.</summary>
    public string Chosen(string field)
    {
        if (!ItemRegistry.Schema.TryNode(ItemRegistry.Path(field), out NodeRef node) || node.kind != NodeKind.Choice)
            return null;

        SchemaChoice choice = ItemRegistry.Schema.Choices[node.index];

        for (int leaf = choice.start; leaf < choice.end; leaf++)
        {
            if (Has(leaf))
                return choice.branchNames[choice.BranchOf(leaf)];
        }

        return null;
    }

    /// <summary>Returns a copy with that value set. The original is untouched.</summary>
    public Item With(DataValue value)
    {
        Item copy = this;
        copy.overrides = overrides.With(value);
        return copy;
    }

    public Item With(string field, float x) => With(new DataValue(ItemRegistry.Leaf(field), x));

    /// <summary>Returns a copy with this copy's override dropped, so the default takes over again.</summary>
    public Item Without(string field)
    {
        Item copy = this;
        copy.overrides = overrides.Without(ItemRegistry.Leaf(field));
        return copy;
    }

    /// <summary>Same definition and same changes, so the two may share a stack.</summary>
    public static bool SameKind(Item a, Item b) => a.id == b.id && a.overrides.Equals(b.overrides);

    public bool Equals(Item other)
    {
        return id == other.id && quantity == other.quantity && overrides.Equals(other.overrides);
    }

    public override bool Equals(object obj) => obj is Item other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return (id * 397 ^ quantity) * 397 ^ overrides.GetHashCode();
        }
    }

    public static bool operator ==(Item a, Item b) => a.Equals(b);

    public static bool operator !=(Item a, Item b) => !a.Equals(b);

    public override string ToString()
    {
        if (IsEmpty)
            return "(empty)";

        return quantity > 1 ? $"{Name} x{quantity}" : Name;
    }
}
