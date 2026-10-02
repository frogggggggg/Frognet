using UnityEngine;

/// <summary>
/// Tendrils creeping over a Surface from one point (a blight gene injected there, GeneEffects).
///
/// Set once: the entry point and normal (renderer-local, so they ride the cell), start time, spread speed,
/// colour and the cell's radius go into the renderer's MaterialPropertyBlock (the same one the ripples
/// use), and the cell shader grows the tendrils from the clock (CellTendrils.hlsl). No per-frame cost on
/// the CPU; only infected cells run the shader's tendril code. A second infection restarts from its point.
/// Streamed and saved (IWorldState: the three vectors, with the start as an age so the clock can differ).
/// </summary>
public partial class Surface : IWorldState
{
    static readonly int TendrilOriginId = Shader.PropertyToID("_TendrilOrigin"),
                        TendrilNormalId = Shader.PropertyToID("_TendrilNormal"),
                        TendrilColorId = Shader.PropertyToID("_TendrilColor");

    /// <summary>Tendrils are spreading over it.</summary>
    public bool Infected { get; private set; }

    Vector4 _tendrilOrigin, _tendrilNormal, _tendrilColor; // as set in the block (origin.w = start time)

    /// <summary>Starts tendrils at 'worldPoint' ('worldNormal' out of the surface), in 'color', their front
    /// taking 'radiusTime' seconds to cross one radius of the surface.</summary>
    public void Infect(Vector3 worldPoint, Vector3 worldNormal, Color color, float radiusTime)
    {
        MeshRenderer r = Renderer;
        if (!r) return;

        Transform space = r.transform;
        Bounds local = r.localBounds;
        float radius = Mathf.Max(local.extents.x, Mathf.Max(local.extents.y, local.extents.z)); // renderer-local units
        Vector3 p = space.InverseTransformPoint(worldPoint);
        Vector3 n = space.localToWorldMatrix.transpose.MultiplyVector(worldNormal).normalized; // normals go by M^T
        float speed = radius / Mathf.Max(radiusTime, 0.01f);
        if (QualitySettings.activeColorSpace == ColorSpace.Linear) color = color.linear; // SetVector doesn't convert

        SetTendrils(new Vector4(p.x, p.y, p.z, Mathf.Max(Time.time, 1e-3f)), // w 0 = none
                    new Vector4(n.x, n.y, n.z, speed), new Vector4(color.r, color.g, color.b, radius));
    }

    void SetTendrils(Vector4 origin, Vector4 normal, Vector4 color)
    {
        MeshRenderer r = Renderer;
        if (!r) return;
        EnsureRipples();
        r.GetPropertyBlock(_block);
        _block.SetVector(TendrilOriginId, origin);
        _block.SetVector(TendrilNormalId, normal);
        _block.SetVector(TendrilColorId, color);
        r.SetPropertyBlock(_block);
        _tendrilOrigin = origin;
        _tendrilNormal = normal;
        _tendrilColor = color;
        Infected = true;
    }

    [System.Serializable]
    struct SavedTendrils { public Vector4 origin, normal, color; public float age; }

    string IWorldState.SaveState() => Infected
        ? JsonUtility.ToJson(new SavedTendrils { origin = _tendrilOrigin, normal = _tendrilNormal, color = _tendrilColor, age = Time.time - _tendrilOrigin.w })
        : "";

    void IWorldState.LoadState(string state)
    {
        SavedTendrils t = JsonUtility.FromJson<SavedTendrils>(state);
        Vector4 origin = t.origin;
        origin.w = Mathf.Max(Time.time - t.age, 1e-3f); // grown as far as it had
        SetTendrils(origin, t.normal, t.color);
    }

    bool IWorldState.Pinned => false;
}
