using System;
using UnityEngine;

/// <summary>
/// What a kind of cell is made of and does (one asset per cell type: red blood cell, tissue cell...): whether it
/// has a nucleus, how many organelles its nucleus allows, which organelles it may hold and starts with, its
/// stores, and what it takes in from the blood. Read by CellInterior, which rolls each cell from it once (seeded
/// by the streamer, so a cell comes back the same) and keeps the state.
/// A nucleus is what allows organelles: no nucleus, limit 0, none.
/// </summary>
[CreateAssetMenu(menuName = "Viral/Cell Profile")]
public class CellProfile : ScriptableObject
{
    public enum NucleusRule { Never, Chance, Always }

    [Serializable]
    public class OrganelleStart
    {
        public OrganelleType type;
        [Tooltip("How many it starts with once it has a nucleus (min, max; rolled, then capped by the limit).")]
        public Vector2Int count = new Vector2Int(2, 3);
    }

    [Serializable]
    public class Uptake
    {
        public string substance = SubstanceCatalog.Oxygen;
        [Min(0f), Tooltip("Units per second taken in from the blood.")]
        public float rate = 1f;
        [Min(0f), Tooltip("Stops taking it in once it holds this much.")]
        public float upTo = 100f;
    }

    [Serializable]
    public class Upkeep
    {
        public string substance = SubstanceCatalog.ATP;
        [Min(0f), Tooltip("Units per second the cell itself spends (membrane pumps...), while it has any.")]
        public float rate = 1f;
    }

    [Serializable]
    public class StartAmount
    {
        public string substance = SubstanceCatalog.Oxygen;
        [Tooltip("Rolled between x and y.")]
        public Vector2 amount = new Vector2(20f, 60f);
    }

    [Tooltip("Shown in the nucleus readout.")]
    public string displayName = "CELL";
    public string code = "CEL";

    [Header("Nucleus")]
    public NucleusRule nucleus = NucleusRule.Always;
    [Range(0f, 1f), Tooltip("Chance rule: the share of these cells born with one.")]
    public float nucleusChance = 0.03f;
    [Min(0), Tooltip("Organelles a nucleus allows (min, max; rolled). Mutations can raise it.")]
    public Vector2Int organelleLimit = new Vector2Int(5, 5);
    [Tooltip("Organelles it may hold, and how many it starts with. A cell that gains a nucleus later fills up to these too.")]
    public OrganelleStart[] organelles = new OrganelleStart[0];

    [Header("Stores")]
    [Min(0)] public int slots = 4;
    [Min(1f), Tooltip("Units one slot holds.")]
    public float capacity = 100f;
    public StartAmount[] startWith = new StartAmount[0];
    [Tooltip("Taken in from the blood (floating oxygen, glucose...) until it holds 'up to'.")]
    public Uptake[] uptake = new Uptake[0];
    [Tooltip("Spent by the cell itself, out through the membrane. Without a sink for ATP the stores fill and the organelles stop.")]
    public Upkeep[] upkeep = new Upkeep[0];

    /// <summary>Whether it may hold 'type' at all.</summary>
    public bool Allows(OrganelleType type)
    {
        foreach (OrganelleStart o in organelles)
            if (o != null && o.type == type) return true;
        return false;
    }

    /// <summary>An allowed organelle type by name (loading a saved cell).</summary>
    public OrganelleType Organelle(string name)
    {
        foreach (OrganelleStart o in organelles)
            if (o != null && o.type && o.type.name == name) return o.type;
        return null;
    }
}
