using UnityEngine;

/// <summary>
/// 3D injection drill: on Extend it screws out of the underside of the virus down to the centre
/// of whatever it stands on, spins while engaged, and winds back in on Retract. Shows focus mode
/// as an injection into the planet (the focus sweep sees inside cells, so it's traced there too).
///
/// Knows nothing about input or states: it asks the virus's ISurfaceContact what it stands on,
/// and follows a VirusMovement's focus events if there's one on it or a parent (else call
/// Extend / Retract yourself). Builds its own mesh: a fluted shaft tapering to a point, with the
/// flutes' phase counted from the tip so growing it reads as screwing in. A second material, the
/// x-ray (InjectionDrillXRay), draws it over the focus sweep inside the sweep's circle.
/// </summary>
public class InjectionDrill : MonoBehaviour
{
    [Tooltip("The virus: anything with an ISurfaceContact on it or a parent. Empty: this object.")]
    public Transform virus;
    [Tooltip("Empty: a metallic URP Lit material.")]
    public Material material;
    [Tooltip("Drawn over everything inside the focus sweep, so the drill shows through the planet " +
             "there. Empty: Custom/InjectionDrillXRay (assign one for builds).")]
    public Material xrayMaterial;

    [Header("Shape")]
    [Min(0.01f)] public float radius = 0.18f;
    [Min(0.01f), Tooltip("Length of the pointed tip.")] public float tipLength = 1.2f;
    [Range(1, 6)] public int flutes = 3;
    [Range(0f, 0.6f), Tooltip("How deep the flutes cut, as a fraction of the radius.")] public float fluteDepth = 0.3f;
    [Min(0.05f), Tooltip("Metres per turn of the flutes.")] public float pitch = 0.7f;
    [Tooltip("Where it leaves the virus: this far from the virus's centre toward the planet.")]
    public float startOffset = 0.25f;

    [Header("Motion")]
    [Min(0.01f)] public float extendTime = 0.55f;
    [Min(0.01f)] public float retractTime = 0.3f;
    [Min(0f), Tooltip("Seconds after Extend before it starts (lets the slam land).")] public float extendDelay = 0f;
    [Tooltip("Degrees per second while boring in / winding out.")] public float drillSpin = 900f;
    [Tooltip("Degrees per second once it's in.")] public float idleSpin = 120f;
    [Min(0f), Tooltip("Ripple where it breaks the surface, as an impact speed. 0 = none.")]
    public float entryRipple = 40f;

    ISurfaceContact _contact;
    Transform _surface;
    Renderer _surfaceRenderer;
    Surface _surfaceRipples;
    VirusMovement _movement;

    GameObject _go;
    Mesh _mesh;
    Material _ownMaterial;
    bool _engaged, _entered;
    float _extent;      // 0 in .. 1 at the centre
    float _extendAt;
    float _builtLength = -1f;
    float _spin;

    public bool Engaged => _engaged;

    /// <summary>Where the drill leaves the virus and where its tip is now (world); false while it's in.</summary>
    public bool Span(out Vector3 start, out Vector3 tip)
    {
        start = _start;
        tip = _tip;
        return _extent > 0f && _go && _go.activeSelf;
    }

    /// <summary>Something arrived down the drill (the head view's injection): a ripple where it enters
    /// the cell. 'strength' scales the entry ripple.</summary>
    public void Deliver(float strength = 0.6f)
    {
        if (_surfaceRipples && _entered && entryRipple > 0f) _surfaceRipples.AddImpact(_entry, entryRipple * strength);
    }

    Vector3 _start, _tip, _entry;

    /// <summary>The cell it's drilling into (null while it has none).</summary>
    public Transform Cell => _surfaceRenderer ? _surfaceRenderer.transform : null;

    public void Extend()
    {
        if (_engaged) return;
        _engaged = true;
        _entered = false;
        _extendAt = Time.time + extendDelay;
    }

    public void Retract() => _engaged = false;

    void OnEnable()
    {
        Transform v = virus ? virus : transform;
        _contact = v.GetComponentInParent<ISurfaceContact>();
        _movement = v.GetComponentInParent<VirusMovement>();
        if (_movement)
        {
            _movement.onFocusModeEnter.AddListener(Extend);
            _movement.onFocusModeExit.AddListener(Retract);
        }
    }

    void OnDisable()
    {
        if (_movement)
        {
            _movement.onFocusModeEnter.RemoveListener(Extend);
            _movement.onFocusModeExit.RemoveListener(Retract);
        }
        _engaged = false;
        _extent = 0f;
        if (_go) _go.SetActive(false);
    }

    void OnDestroy()
    {
        if (_go) Destroy(_go);
        if (_mesh) Destroy(_mesh);
        if (_ownMaterial) Destroy(_ownMaterial);
        if (_ownXRay) Destroy(_ownXRay);
    }

    void LateUpdate()
    {
        Transform standingOn = _contact?.Surface;
        if (standingOn && standingOn != _surface)
        {
            _surface = standingOn;
            _surfaceRenderer = standingOn.GetComponent<Renderer>();
            _surfaceRipples = standingOn.GetComponentInParent<Surface>();
        }

        // Ease in / out. Out of reach of any surface, it winds back in.
        bool going = _engaged && Time.time >= _extendAt && _surfaceRenderer;
        float before = _extent;
        _extent = Mathf.MoveTowards(_extent, going ? 1f : 0f, Time.deltaTime / (going ? extendTime : retractTime));

        if (_extent <= 0f || !_surfaceRenderer)
        {
            if (_go && _go.activeSelf) _go.SetActive(false);
            return;
        }
        EnsureObject();
        if (!_go.activeSelf) _go.SetActive(true);

        // From under the virus to the surface's centre.
        Transform v = virus ? virus : transform;
        Transform s = _surfaceRenderer.transform;
        Vector3 centre = s.TransformPoint(_surfaceRenderer.localBounds.center);
        Vector3 toCentre = centre - v.position;
        float distance = toCentre.magnitude;
        if (distance < 1e-3f) return;
        Vector3 dir = toCentre / distance;
        Vector3 start = v.position + dir * startOffset;
        float full = Mathf.Max(0.01f, distance - startOffset);

        // Ease out going in (bites, then slows), ease in coming out.
        float k = going ? 1f - Mathf.Pow(1f - _extent, 3f) : _extent * _extent;
        float length = full * k;

        bool moving = !Mathf.Approximately(before, _extent);
        _spin += (moving ? drillSpin : idleSpin) * Time.deltaTime;

        Transform t = _go.transform;
        t.SetPositionAndRotation(start, Quaternion.LookRotation(dir, Perpendicular(dir, v.forward)) * Quaternion.Euler(0f, 0f, _spin));

        if (Mathf.Abs(length - _builtLength) > 0.01f) Build(length);
        _start = start;
        _tip = start + dir * length;

        // Ripple the cell where the tip breaks through.
        if (going && !_entered && entryRipple > 0f && _surfaceRipples)
        {
            float radiusToSurface = MaxRadius(s, _surfaceRenderer.localBounds);
            if (length >= distance - startOffset - radiusToSurface)
            {
                _entered = true;
                _entry = centre - dir * radiusToSurface;
                _surfaceRipples.AddImpact(_entry, entryRipple);
            }
        }
    }

    static float MaxRadius(Transform t, Bounds local)
    {
        Vector3 s = t.lossyScale;
        return Mathf.Max(Mathf.Abs(local.extents.x * s.x), Mathf.Max(Mathf.Abs(local.extents.y * s.y), Mathf.Abs(local.extents.z * s.z)));
    }

    static Vector3 Perpendicular(Vector3 dir, Vector3 hint)
    {
        Vector3 up = Vector3.ProjectOnPlane(hint, dir);
        return up.sqrMagnitude > 1e-6f ? up : Vector3.ProjectOnPlane(Vector3.up, dir).sqrMagnitude > 1e-6f ? Vector3.ProjectOnPlane(Vector3.up, dir) : Vector3.right;
    }

    void EnsureObject()
    {
        if (_go) return;
        _go = new GameObject("Injection Drill");
        _go.hideFlags = HideFlags.DontSave;
        _mesh = new Mesh { name = "Injection Drill", hideFlags = HideFlags.DontSave };
        _mesh.MarkDynamic();
        _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
        var r = _go.AddComponent<MeshRenderer>();
        Material xray = xrayMaterial ? xrayMaterial : DefaultXRay();
        // One submesh, two materials: Unity draws it once with each.
        r.sharedMaterials = xray
            ? new[] { material ? material : DefaultMaterial(), xray }
            : new[] { material ? material : DefaultMaterial() };
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    Material _ownXRay;

    Material DefaultXRay()
    {
        if (_ownXRay) return _ownXRay;
        Shader shader = Shader.Find("Custom/InjectionDrillXRay");
        if (shader) _ownXRay = new Material(shader) { name = "Injection Drill X-Ray", hideFlags = HideFlags.DontSave };
        return _ownXRay;
    }

    Material DefaultMaterial()
    {
        if (_ownMaterial) return _ownMaterial;
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        _ownMaterial = new Material(lit ? lit : Shader.Find("Standard")) { name = "Injection Drill", hideFlags = HideFlags.DontSave };
        _ownMaterial.SetColor("_BaseColor", new Color(0.72f, 0.9f, 1f));
        _ownMaterial.SetFloat("_Metallic", 0.85f);
        _ownMaterial.SetFloat("_Smoothness", 0.75f);
        return _ownMaterial;
    }

    // ---------------- mesh ----------------

    const int Sides = 18;
    const float RingSpacing = 0.12f;
    Vector3[] _verts = new Vector3[0];
    Vector2[] _uvs = new Vector2[0];
    int[] _tris = new int[0];

    // Along local +Z from 0 (the virus) to length (the tip). Flute phase counts from the tip, so
    // as it grows the flutes stay put at the tip and the shaft seems to screw in behind it.
    void Build(float length)
    {
        _builtLength = length;
        int rings = Mathf.Max(2, Mathf.CeilToInt(length / RingSpacing) + 1);
        int vertCount = rings * (Sides + 1) + 1;
        if (_verts.Length != vertCount) { _verts = new Vector3[vertCount]; _uvs = new Vector2[vertCount]; }

        float tip = Mathf.Min(tipLength, length);
        for (int i = 0; i < rings; i++)
        {
            float z = length * i / (rings - 1);
            float fromTip = length - z;
            float taper = fromTip < tip ? fromTip / tip : 1f;
            float r = radius * Mathf.Sqrt(taper) * (z < 0.08f ? 1.25f : 1f); // collar where it leaves the body
            float phase = fromTip / pitch * Mathf.PI * 2f;
            for (int j = 0; j <= Sides; j++)
            {
                float a = j * Mathf.PI * 2f / Sides;
                float flute = 1f - fluteDepth * 0.5f * (1f + Mathf.Cos(flutes * a + phase));
                float rr = r * flute;
                _verts[i * (Sides + 1) + j] = new Vector3(Mathf.Cos(a) * rr, Mathf.Sin(a) * rr, z);
                _uvs[i * (Sides + 1) + j] = new Vector2((float)j / Sides, z); // v in metres from the virus (x-ray pulses)
            }
        }
        int capCentre = vertCount - 1;
        _verts[capCentre] = Vector3.zero;
        _uvs[capCentre] = Vector2.zero;

        int triCount = (rings - 1) * Sides * 6 + Sides * 3;
        if (_tris.Length != triCount) _tris = new int[triCount];
        int n = 0;
        for (int i = 0; i < rings - 1; i++)
        for (int j = 0; j < Sides; j++)
        {
            int a = i * (Sides + 1) + j, b = a + Sides + 1;
            _tris[n++] = a; _tris[n++] = a + 1; _tris[n++] = b;
            _tris[n++] = a + 1; _tris[n++] = b + 1; _tris[n++] = b;
        }
        for (int j = 0; j < Sides; j++) // cap at the virus end
        {
            _tris[n++] = capCentre; _tris[n++] = j + 1; _tris[n++] = j;
        }

        _mesh.Clear();
        _mesh.vertices = _verts;
        _mesh.uv = _uvs;
        _mesh.triangles = _tris;
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
    }
}
