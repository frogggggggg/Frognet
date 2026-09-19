using UnityEngine;

/// <summary>
/// Feeds impact ripples to a cell's material.
///
/// The ripple itself lives in the shader, which displaces geometry and
/// perturbs the normal from an expanding wave packet. This only records where
/// and when something hit, and how hard.
///
/// Values go through a MaterialPropertyBlock rather than the material, so no
/// material instance is leaked and cells sharing one material still ripple
/// independently. That does drop the renderer out of SRP batching, which is a
/// fair trade for a handful of cells.
/// </summary>
[RequireComponent(typeof(Renderer))]
public class CellImpactRipples : MonoBehaviour
{
    /// <summary>Must match RIPPLE_COUNT in the shader.</summary>
    public const int MaxRipples = 4;

    [Tooltip("Impact speed, in units per second, that counts as a full " +
             "strength hit. Anything faster is clamped to 1.")]
    public float referenceSpeed = 8f;

    [Tooltip("Seconds before a ripple is considered spent and its slot can be " +
             "reused. Should outlast what Ripple Decay leaves visible.")]
    public float lifetime = 3f;

    static readonly int PointsId = Shader.PropertyToID("_RipplePoints");
    static readonly int ValuesId = Shader.PropertyToID("_RippleValues");

    readonly Vector4[] _points = new Vector4[MaxRipples];
    readonly Vector4[] _values = new Vector4[MaxRipples];

    Renderer _renderer;
    MaterialPropertyBlock _block;
    int _next;
    bool _dirty;

    void Awake()
    {
        _renderer = GetComponent<Renderer>();
        _block = new MaterialPropertyBlock();
        _dirty = true;
    }

    /// <summary>
    /// Start a ripple. Strength is normalised against referenceSpeed, so a
    /// gentle touchdown barely registers and a fast one reads as a slap.
    /// </summary>
    public void AddImpact(Vector3 worldPoint, float impactSpeed)
    {
        float strength = referenceSpeed > 0f
            ? Mathf.Clamp01(impactSpeed / referenceSpeed)
            : 1f;

        if (strength <= 0.001f) return;

        // Round robin. Oldest slot is the one about to be overwritten anyway,
        // so there is no need to search for a free one.
        _points[_next] = new Vector4(worldPoint.x, worldPoint.y, worldPoint.z, Time.time);
        _values[_next] = new Vector4(strength, 0f, 0f, 0f);

        _next = (_next + 1) % MaxRipples;
        _dirty = true;
    }

    void LateUpdate()
    {
        // Retire spent ripples so their slots stop costing shader work, and so
        // a stale one cannot reappear if _Time wraps on a long session.
        for (int i = 0; i < MaxRipples; i++)
        {
            if (_values[i].x <= 0f) continue;

            if (Time.time - _points[i].w > lifetime)
            {
                _values[i] = Vector4.zero;
                _dirty = true;
            }
        }

        if (!_dirty) return;
        _dirty = false;

        // Read, modify, write: clobbering the block would wipe anything else
        // set on this renderer.
        _renderer.GetPropertyBlock(_block);
        _block.SetVectorArray(PointsId, _points);
        _block.SetVectorArray(ValuesId, _values);
        _renderer.SetPropertyBlock(_block);
    }
}
