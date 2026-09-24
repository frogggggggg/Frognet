using UnityEngine;

/// <summary>
/// A floating lump of some substance (glucose, protein...): a small world of its own that crawlers can
/// land on and walk round (a Surface and a MeshCollider over its smoothed walk hull, ChunkMesh.Walk), and
/// that a virus can extract into its VirusInventory. The prefab is the type: substance, shape, size range,
/// yield and how it floats.
///
/// Motion is its own, not physics' (a kinematic Rigidbody): it bobs slowly round its place and turns a
/// little, and settles still while anything stands on it, so walking it is steady and the camera calm.
/// A dynamic body bumping it (a virus flying in, a cell dragged by a rope) nudges it; the nudge dies away.
/// ResourceField steps every chunk in one loop (<see cref="Step"/>), far ones every few frames.
/// (Dynamic and feather-light, the player's own collider shoved it every physics step, and physics
/// interpolation fought the pose: that was the jitter.)
///
/// Its renderer never draws (it's only there for the Surface, command mode's box, and to carry the chunk
/// material's ripple settings): ResourceField draws every chunk in a few instanced calls. Impacts ripple it
/// through the Surface -> RippleField, like a cell; bigger chunks ripple more (<see cref="rippleReference"/>).
///
/// Extraction (focus mode: click the core inside it) drains it: it shrinks (its transform, so anyone
/// standing on it rides the surface in) and deforms, and once small enough it poofs into a cloud of its
/// colour. In command mode it's a target named after its substance.
/// </summary>
[DefaultExecutionOrder(-50)] // mesh in place before the Surface's Awake bakes it
[RequireComponent(typeof(Rigidbody), typeof(MeshCollider), typeof(Surface))]
public class ResourceChunk : MonoBehaviour
{
    public Substance substance = new Substance();
    public ChunkMesh.Shape shape = ChunkMesh.Shape.Lumpy;
    [Tooltip("Radius range (metres), picked at random when Radius is 0.")]
    public Vector2 sizeRange = new Vector2(1.2f, 5f);
    [Min(1f), Tooltip("Above 1, small chunks are commoner than big ones (the random pick is raised to this power).")]
    public float sizeSkew = 2f;
    [Min(0f), Tooltip("Radius (metres). 0: random in Size Range.")]
    public float radius;
    [Min(0.01f), Tooltip("Units held by a chunk of radius 1 (it scales with volume).")]
    public float yieldAtUnitRadius = 1.5f;
    [Range(0f, 0.5f), Tooltip("Random brightness / hue spread between chunks.")]
    public float tintVariation = 0.12f;
    [Min(0f), Tooltip("Mass per cubic metre of radius (mass = density x r^3), against what bumps it.")]
    public float density = 0.02f;

    [Header("Floating")]
    [Tooltip("How far it bobs round its place (metres, range: one picked per chunk).")]
    public Vector2 bob = new Vector2(0.15f, 0.6f);
    [Tooltip("Seconds per bob (range).")]
    public Vector2 bobPeriod = new Vector2(16f, 36f);
    [Tooltip("How fast it turns (degrees per second, range).")]
    public Vector2 spin = new Vector2(0.5f, 3f);
    [Min(0.1f), Tooltip("Seconds to settle still once something stands on it (and to start floating again after).")]
    public float settleTime = 1.5f;
    [Range(0f, 1f), Tooltip("Share of a bump's speed it takes on (times the bumper's share of the two masses).")]
    public float bumpResponse = 0.3f;
    [Min(0f), Tooltip("Fastest a bump sets it drifting (m/s).")]
    public float maxBump = 1f;
    [Min(0f), Tooltip("How fast a bump's drift dies away (per second; four times that while stood on).")]
    public float bumpDamping = 0.6f;
    [Min(1f), Tooltip("Impact speed that makes a strength-1 ripple on a chunk of radius 1 (cells use 100 at radius ~35): " +
                      "the chunk's Surface gets this / radius, so a bigger chunk ripples more. Wave shape: the chunk material's _Ripple* values.")]
    public float rippleReference = 100f;

    /// <summary>Units it started with, and units left.</summary>
    public float Amount { get; private set; }
    public float Remaining { get; internal set; }
    /// <summary>0..1, fixed per chunk: drives its wobble.</summary>
    public float Seed { get; private set; }
    public Color Tint { get; private set; }
    /// <summary>Who's extracting it, if anyone.</summary>
    public VirusInventory Extractor { get; internal set; }
    /// <summary>Extracting, eased 0..1 (the wobble swells and settles).</summary>
    public float Agitation { get; internal set; }
    /// <summary>Its core is pointed at (focus mode), eased 0..1.</summary>
    public float Hover { get; internal set; }
    internal bool Blocked; // the last extraction step found no room in the inventory

    /// <summary>Share extracted, 0..1.</summary>
    public float Extracted => Amount > 0f ? 1f - Remaining / Amount : 0f;

    /// <summary>Its radius now: shrinks with the volume left, down to a third before it poofs.</summary>
    public float CurrentRadius => radius * Mathf.Lerp(0.33f, 1f, Mathf.Pow(Mathf.Clamp01(1f - Extracted), 0.6f));

    /// <summary>Where it is (its centre).</summary>
    public Vector3 Centre => T.position;

    /// <summary>How fast it's moving (m/s), from its last step.</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>Its transform, cached (ResourceField reads it for every chunk every frame).</summary>
    internal Transform T { get; private set; }
    /// <summary>Frame something last stood on it (ResourceField).</summary>
    internal int occupiedFrame = -1;
    /// <summary>Settled still, 0..1 (1 = stood on long enough).</summary>
    internal float Still => _still;

    Rigidbody _body;
    Surface _surface;
    bool _ready, _placed;
    Vector3 _home, _push, _bobA, _bobB;
    Quaternion _baseRotation;
    Vector3 _spinAxis;
    float _bobAmount, _bobRate, _bobOffset, _spinRate, _phase, _still, _lastStep;

    void Awake()
    {
        T = transform;

        // The walkable hull (shared by every chunk of this shape), never drawn: ResourceField draws them.
        var filter = GetComponent<MeshFilter>();
        if (!filter) filter = gameObject.AddComponent<MeshFilter>();
        Mesh walk = ChunkMesh.Walk(shape);
        filter.sharedMesh = walk;
        var r = GetComponent<MeshRenderer>();
        if (!r) r = gameObject.AddComponent<MeshRenderer>();
        r.forceRenderingOff = true;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.sharedMaterial = ResourceField.SharedChunkMaterial(); // its _Ripple* values shape this chunk's ripples

        // Collide with exactly what's walked, so touchdown, feet and sight lines all meet the same surface.
        var old = GetComponent<SphereCollider>(); // prefabs from before
        if (old) Destroy(old);
        var col = GetComponent<MeshCollider>();
        if (!col) col = gameObject.AddComponent<MeshCollider>();
        col.convex = false;
        col.sharedMesh = walk;

        _surface = GetComponent<Surface>();
        _surface.isCell = false;

        _body = GetComponent<Rigidbody>();
        _body.useGravity = false;
        _body.isKinematic = true;
        _body.interpolation = RigidbodyInterpolation.None; // moved by transform in Update, before anyone rides it
        Init();

        if (!GetComponent<Selectable>())
            Selectable.Add(gameObject, Selectable.Category.Target, Title(substance.name),
                           CommandBoard.Jobs.Extract | CommandBoard.Jobs.MoveTo);
    }

    void OnEnable() => ResourceField.Register(this);
    void OnDisable() => ResourceField.Unregister(this);

    void Init()
    {
        if (_ready) return;
        _ready = true;
        if (radius <= 0f) radius = RandomRadius();
        Seed = Random.value;
        Color.RGBToHSV(substance.color, out float h, out float s, out float v);
        Tint = Color.HSVToRGB(Mathf.Repeat(h + Random.Range(-0.25f, 0.25f) * tintVariation, 1f),
                              Mathf.Clamp01(s * Random.Range(1f - tintVariation, 1f + tintVariation)),
                              Mathf.Clamp01(v * Random.Range(1f - tintVariation, 1f)));

        _bobA = Random.onUnitSphere;
        _bobB = Vector3.Cross(_bobA, Random.onUnitSphere).normalized;
        _bobAmount = Random.Range(bob.x, bob.y);
        _bobRate = Mathf.PI * 2f / Mathf.Max(1f, Random.Range(bobPeriod.x, bobPeriod.y));
        _bobOffset = Random.value * 20f;
        _spinAxis = Random.onUnitSphere;
        _spinRate = Random.Range(spin.x, spin.y);
        Resize(radius);
    }

    /// <summary>Sets its radius (and so its yield, mass and ripple strength), before any of it is extracted.</summary>
    public void Resize(float r)
    {
        Init();
        radius = Mathf.Max(r, 0.05f);
        Amount = Remaining = yieldAtUnitRadius * radius * radius * radius;
        transform.localScale = Vector3.one * radius;
        if (_surface) _surface.referenceSpeed = rippleReference / radius;
    }

    float Mass => Mathf.Max(1e-3f, density * radius * radius * radius);

    /// <summary>The transform follows the volume left (ResourceField, while it drains).</summary>
    internal void Shrink() => T.localScale = Vector3.one * CurrentRadius;

    /// <summary>Picks a random radius in the prefab's range, skewed small (for spawners).</summary>
    public float RandomRadius()
    {
        if (radius > 0f) return radius;
        float lo = Mathf.Min(sizeRange.x, sizeRange.y), hi = Mathf.Max(sizeRange.x, sizeRange.y);
        return Mathf.Lerp(lo, hi, Mathf.Pow(Random.value, sizeSkew));
    }

    /// <summary>
    /// Moves it to where it floats at 'now' (ResourceField, every frame near the camera or when stood on,
    /// every few frames otherwise; the skipped time is caught up here). Everything is a function of a
    /// phase that only advances while it isn't stood on, so settling and waking again are smooth.
    /// </summary>
    internal void Step(float now, bool occupied)
    {
        float dt = Mathf.Min(now - _lastStep, 0.5f);
        _lastStep = now;
        if (!_placed)
        {
            // Its place is wherever it was put (spawned, dragged in the editor, resized).
            _placed = true;
            _baseRotation = T.rotation;
            _home = T.position - Bob(0f);
            dt = 0f;
        }

        _still = Mathf.MoveTowards(_still, occupied ? 1f : 0f, dt / settleTime);
        float moving = 1f - _still * _still * (3f - 2f * _still);
        _push *= Mathf.Exp(-bumpDamping * (1f + 3f * _still) * dt);
        if (moving <= 0f && _push.sqrMagnitude < 1e-6f)
        {
            Velocity = Vector3.zero;
            return; // at rest: no transform write, so physics has nothing to resync
        }

        _phase += dt * moving;
        _home += _push * dt;
        Vector3 at = _home + Bob(_phase);
        if (dt > 0f) Velocity = (at - T.position) / dt;
        T.SetPositionAndRotation(at, Quaternion.AngleAxis(_spinRate * _phase, _spinAxis) * _baseRotation);
    }

    Vector3 Bob(float phase)
    {
        float a = phase * _bobRate + _bobOffset;
        return (_bobA * Mathf.Sin(a) + _bobB * (0.6f * Mathf.Sin(a * 0.73f + 1.7f))) * _bobAmount;
    }

    // Something dynamic ran into it: take some of the hit along the contact normal, by the masses.
    void OnCollisionEnter(Collision c)
    {
        Rigidbody other = c.rigidbody;
        if (!other || other.isKinematic || c.contactCount == 0) return;
        Vector3 n = c.GetContact(0).normal;
        if (Vector3.Dot(n, T.position - c.GetContact(0).point) < 0f) n = -n; // pointing into the chunk
        float speed = Mathf.Abs(Vector3.Dot(c.relativeVelocity, n)) * bumpResponse * other.mass / (other.mass + Mass);
        _push = Vector3.ClampMagnitude(_push + n * speed, maxBump);
    }

    static string Title(string s) => string.IsNullOrEmpty(s) ? "Resource" : s.Substring(0, 1).ToUpperInvariant() + s.Substring(1).ToLowerInvariant();

    void OnDrawGizmosSelected()
    {
        Gizmos.color = substance.color;
        Gizmos.DrawWireSphere(transform.position, radius > 0f ? radius : sizeRange.y);
        Gizmos.color = new Color(substance.color.r, substance.color.g, substance.color.b, 0.3f);
        Gizmos.DrawWireSphere(transform.position, sizeRange.x);
    }
}
