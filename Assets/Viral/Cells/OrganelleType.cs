using System;
using UnityEngine;

/// <summary>An amount of a substance, by name (SubstanceCatalog).</summary>
[Serializable]
public struct SubstanceAmount
{
    public string substance;
    [Min(0f)] public float amount;

    public SubstanceAmount(string substance, float amount)
    {
        this.substance = substance;
        this.amount = amount;
    }
}

/// <summary>
/// A kind of organelle: a little machine inside a cell that turns inputs into outputs from the cell's stores,
/// one cycle every <see cref="cycleSeconds"/> per organelle (a mitochondrion: glucose + oxygen -> ATP). Several of
/// a kind in a cell run side by side. Something that only produces has no inputs; something that only
/// consumes (or stores) has no outputs. A new organelle = a new asset of this; cells allow it in their
/// CellProfile. Drawn by CellInteriorView in its <see cref="shape"/> and colour.
/// </summary>
[CreateAssetMenu(menuName = "Viral/Organelle Type")]
public class OrganelleType : ScriptableObject
{
    /// <summary>How it's drawn (Custom/CellInterior): the body's outline and inner pattern.</summary>
    public enum Shape { Mitochondrion, Vesicle, Ribosome }

    [Tooltip("Shown in the nucleus readout.")]
    public string displayName = "MITOCHONDRION";
    public string code = "MIT";
    public Color color = new Color(0.4f, 0.92f, 0.62f);
    public Shape shape;
    [Min(0.01f), Tooltip("Body size, as a share of the cell's radius.")]
    public float size = 0.12f;

    [Header("Process")]
    [Tooltip("Taken from the cell's stores per cycle.")]
    public SubstanceAmount[] inputs = new SubstanceAmount[0];
    [Tooltip("Put into the cell's stores per cycle.")]
    public SubstanceAmount[] outputs = new SubstanceAmount[0];
    [Min(0.1f), Tooltip("Seconds per cycle, per organelle.")]
    public float cycleSeconds = 10f;
}
