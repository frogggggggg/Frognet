using System;
using UnityEngine;

/// <summary>What kinds of body a gene works on (a gene's <see cref="Genome.Gene.targets"/>).</summary>
[Flags]
public enum GeneTarget
{
    None = 0,
    RedBloodCell = 1 << 0,
    WhiteBloodCell = 1 << 1,
    Resource = 1 << 2,
}

/// <summary>What a gene does to a body it works on. Add a case to <see cref="GeneEffects.Apply"/> per new one.</summary>
public enum GeneEffect
{
    None,
    /// <summary>Black tendrils creep over the cell from the injection (in the gene's colour) and it stops
    /// calling the immune system for good.</summary>
    Blight,
}

/// <summary>
/// What genes do once the head view's injection delivers them (ImmuneSystem.Deliver). A gene lists what
/// it works on (<see cref="GeneTarget"/>) and does one <see cref="GeneEffect"/>; injected into anything
/// else it only sets off that cell's alarm. Data lives on the gene (Genome.Gene, made by a Crafting
/// recipe), behaviour here, so a new gene is a recipe and a new effect is one case.
/// </summary>
public static class GeneEffects
{
    /// <summary>Seconds a blight's front takes to cross one cell radius.</summary>
    public const float BlightRadiusTime = 2.5f;

    /// <summary>What kind of body a transform belongs to (a Surface or anything under one).</summary>
    public static GeneTarget KindOf(Transform t)
    {
        Surface s = t ? t.GetComponentInParent<Surface>() : null;
        if (!s) return GeneTarget.None;
        if (s.isCell) return GeneTarget.RedBloodCell;
        if (s.GetComponentInParent<WhiteBloodCell>()) return GeneTarget.WhiteBloodCell;
        if (s.GetComponentInParent<ResourceChunk>()) return GeneTarget.Resource;
        return GeneTarget.None;
    }

    public static bool WorksOn(Genome.Gene gene, Transform body) =>
        gene != null && (gene.targets & KindOf(body)) != GeneTarget.None;

    /// <summary>The gene takes effect in 'body' (one it works on), injected at 'at' (world, on its
    /// surface, 'normal' out of it).</summary>
    public static void Apply(Genome.Gene gene, Transform body, Vector3 at, Vector3 normal)
    {
        Surface surface = body ? body.GetComponentInParent<Surface>() : null;
        switch (gene.effect)
        {
            case GeneEffect.Blight:
                CellSignal.For(body)?.Silence();
                if (surface) surface.Infect(at, normal, gene.color, BlightRadiusTime);
                break;
        }
    }
}
