using UnityEngine;

/// <summary>
/// Feeds impact ripples to a cell's material.
///
/// The shader owns the complete ripple lifetime and decay. This component only
/// records where an impact happened, when it happened, and how strong it was.
/// Impact positions are stored in renderer-local space so existing ripples move
/// rigidly with a translating or rotating cell.
///
/// Values go through a MaterialPropertyBlock rather than the shared material,
/// so cells sharing one material can still ripple independently.
/// </summary>
[RequireComponent(typeof(Renderer))]
public class CellImpactRipples : MonoBehaviour
{
    /// <summary>Must match RIPPLE_COUNT in the shader.</summary>
    public const int MaxRipples = 4;

    [Tooltip("Impact speed, in units per second, that produces strength 1. " +
             "Faster impacts are NOT clamped; for example, twice this speed " +
             "produces strength 2.")]
    public float referenceSpeed = 8f;

    static readonly int PointsId =
        Shader.PropertyToID("_RipplePoints");

    static readonly int ValuesId =
        Shader.PropertyToID("_RippleValues");

    readonly Vector4[] _points =
        new Vector4[MaxRipples];

    readonly Vector4[] _values =
        new Vector4[MaxRipples];

    Renderer _renderer;
    MaterialPropertyBlock _block;
    int _next;

    void Awake()
    {
        _renderer =
            GetComponent<Renderer>();

        _block =
            new MaterialPropertyBlock();

        PushToRenderer();
    }

    /// <summary>
    /// Starts a ripple.
    ///
    /// referenceSpeed is only a scale. Strength is deliberately NOT clamped:
    /// impactSpeed == referenceSpeed gives strength 1,
    /// impactSpeed == referenceSpeed * 2 gives strength 2, etc.
    ///
    /// The shader decides how long the ripple remains visible through
    /// _RippleDecay. Slots are simply overwritten round-robin by later impacts.
    /// </summary>
    public void AddImpact(
        Vector3 worldPoint,
        float impactSpeed)
    {
        float strength =
            referenceSpeed > 0f
                ? impactSpeed / referenceSpeed
                : impactSpeed;

        if (strength <= 0f)
            return;

        // Store the impact in the RENDERER'S local frame, not world space.
        // From this point on the ripple belongs to the surface and follows any
        // translation/rotation/Rigidbody motion of the cell automatically.
        Transform surfaceTransform =
            _renderer
                ? _renderer.transform
                : transform;

        Vector3 localPoint =
            surfaceTransform.InverseTransformPoint(
                worldPoint);

        _points[_next] =
            new Vector4(
                localPoint.x,
                localPoint.y,
                localPoint.z,
                Time.time);

        _values[_next] =
            new Vector4(
                strength,
                0f,
                0f,
                0f);

        _next =
            (_next + 1) %
            MaxRipples;

        PushToRenderer();
    }

    void PushToRenderer()
    {
        if (!_renderer)
            return;

        _renderer.GetPropertyBlock(
            _block);

        _block.SetVectorArray(
            PointsId,
            _points);

        _block.SetVectorArray(
            ValuesId,
            _values);

        _renderer.SetPropertyBlock(
            _block);
    }
}
