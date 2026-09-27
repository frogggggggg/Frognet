using UnityEngine;

/// <summary>
/// Tendrils creeping over a Surface from one point (a blight gene injected there, GeneEffects).
///
/// Set once: the entry point and normal (renderer-local, so they ride the cell), start time, spread speed,
/// colour and the cell's radius go into the renderer's MaterialPropertyBlock (the same one the ripples
/// use), and the cell shader grows the tendrils from the clock (CellTendrils.hlsl). No per-frame cost on
/// the CPU; only infected cells run the shader's tendril code. A second infection restarts from its point.
/// </summary>
public partial class Surface
{
    static readonly int TendrilOriginId = Shader.PropertyToID("_TendrilOrigin"),
                        TendrilNormalId = Shader.PropertyToID("_TendrilNormal"),
                        TendrilColorId = Shader.PropertyToID("_TendrilColor");

    /// <summary>Tendrils are spreading over it.</summary>
    public bool Infected { get; private set; }

    /// <summary>Starts tendrils at 'worldPoint' ('worldNormal' out of the surface), in 'color', their front
    /// taking 'radiusTime' seconds to cross one radius of the surface.</summary>
    public void Infect(Vector3 worldPoint, Vector3 worldNormal, Color color, float radiusTime)
    {
        MeshRenderer r = Renderer;
        if (!r) return;
        EnsureRipples(); // the shared property block

        Transform space = r.transform;
        Bounds local = r.localBounds;
        float radius = Mathf.Max(local.extents.x, Mathf.Max(local.extents.y, local.extents.z)); // renderer-local units
        Vector3 p = space.InverseTransformPoint(worldPoint);
        Vector3 n = space.localToWorldMatrix.transpose.MultiplyVector(worldNormal).normalized; // normals go by M^T
        float speed = radius / Mathf.Max(radiusTime, 0.01f);
        if (QualitySettings.activeColorSpace == ColorSpace.Linear) color = color.linear; // SetVector doesn't convert

        r.GetPropertyBlock(_block);
        _block.SetVector(TendrilOriginId, new Vector4(p.x, p.y, p.z, Mathf.Max(Time.time, 1e-3f))); // w 0 = none
        _block.SetVector(TendrilNormalId, new Vector4(n.x, n.y, n.z, speed));
        _block.SetVector(TendrilColorId, new Vector4(color.r, color.g, color.b, radius));
        r.SetPropertyBlock(_block);
        Infected = true;
    }
}
