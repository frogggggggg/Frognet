using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The world is the inside of one blood vessel bent into a loop (a torus): blood flows round it forever, so
/// anything that drifts downstream long enough comes back round. The WorldStreamer fills it; this holds its
/// shape, the flow, the regions along it and the atmosphere.
///
/// **Frame:** the world is simulated in the frame of the blood round the player, not the wall's: the frame turns
/// round the loop's axis at the blood's rate where the player is (<see cref="followPlayer"/>, eased), so whatever
/// is near them is nearly at rest in world space wherever they go (at the centre the base stays put; out by the
/// wall the centre and the base drift ahead instead), and the wall, the regions and every slower layer slide past.
/// Rates below are relative to the centreline blood; <see cref="Flow"/> subtracts the frame's own turn. Relative to the wall the centre flows
/// at <see cref="centreSpeed"/> and the blood at the wall is still (laminar, as a turn rate round the loop's axis:
/// (v0 / R)(1 - (r/a)^n), n = <see cref="profileExponent"/>, 1 by default: a true laminar n = 2 is flat in the middle, so
/// nothing in view sheared); relative to the centre that is -v0 (r/a)^n (rho / R) along the loop (0 at the centre, the
/// wall's own speed at the wall). The centre
/// speed is the same everywhere, narrows included: speeding it up through narrows (continuity) made the whole
/// neighbourhood of the base surge back and forth (to ~100 m/s in a narrow) as the wall's width changes slid past.
///
/// Anything floating takes the blood's velocity: Organism flight effects move relative to <see cref="FlowAt"/>
/// (Thrust / Coast / Burst), loose dynamic bodies are dragged toward it here, chunks / antibodies / white cells
/// add it to their own motion. Near the wall the flow also pushes back in (<see cref="wallMargin"/>), so nothing
/// leaves the tube. Stored (streamed out) things drift by formula: a radial band of the tube turns rigidly at
/// <see cref="AngularRate"/>, so a record is just rotated round the loop's axis when it comes back (zero cost
/// while away).
///
/// Coordinates: s = arc along the centreline in world space (0..circumference), S = the same point in the wall's
/// frame (S = s + v0 t: regions, narrows and the wall's pattern live in S), r / theta = distance and angle
/// round the tube's centreline. <see cref="Clock"/> is the world's time (saved).
///
/// Regions: the loop is cut into stretches (<see cref="regionLength"/>) of seeded types (plasma, rich, inflamed,
/// crowded, sparse...) that scale what the streamer generates there and tint the fog; each keeps an alert level
/// raised by noise (ImmuneSystem.Alarm) that decays back to its type's rest level.
///
/// Cost: <see cref="FlowAt"/> is O(1) (an atan2, a sqrt and one table lookup) per body per physics step; the drag
/// loop is O(streamed live objects) per physics step; regions tick once a second; the wall is one draw of a
/// shared grid mesh placed in the vertex stage (Custom/VesselWall).
/// </summary>
[DefaultExecutionOrder(-40)] // clock and globals before the streamer (-30), chunks (-20) and the ticker
public class Vessel : MonoBehaviour
{
    [Serializable]
    public class RegionType
    {
        public string name = "Plasma";
        [Min(0f)] public float weight = 1f;
        [Tooltip("Fog / wall tint while inside it (alpha = how strongly).")]
        public Color tint = new Color(1f, 1f, 1f, 0f);
        [Min(0f), Tooltip("Multiplies streamer layers with role Cells.")] public float cells = 1f;
        [Min(0f), Tooltip("Multiplies layers with role Resources.")] public float resources = 1f;
        [Min(0f), Tooltip("Multiplies layers with role Immune.")] public float immune = 1f;
        [Range(0f, 1f), Tooltip("Alert it rests at (an inflamed stretch is always somewhat alert).")]
        public float restAlert;
    }

    [Header("Loop")]
    [Min(1000f), Tooltip("Length of the loop along its centreline (metres). Lap time = this / Centre Speed.")]
    public float circumference = 24000f;
    [Min(50f), Tooltip("Tube radius (metres) where it's neither narrowed nor widened.")]
    public float radius = 1500f;
    [Range(0f, 0.4f), Tooltip("Gentle width change along the loop (share of the radius).")]
    public float widthVariation = 0.12f;
    [Range(0, 8), Tooltip("Narrows round the loop (chokepoints: the fast wall layer comes closer to the centre).")]
    public int narrows = 3;
    [Range(0f, 0.8f), Tooltip("How far a narrow pinches (share of the radius).")]
    public float narrowDepth = 0.5f;
    [Min(50f), Tooltip("Length of a narrow (metres).")]
    public float narrowLength = 1600f;

    [Header("Flow")]
    [Min(0f), Tooltip("Speed of the centre relative to the wall (m/s). Flight thrust below this can't go upstream in the middle.")]
    public float centreSpeed = 60f;
    [Range(0.5f, 4f), Tooltip("Shape of the speed across the tube: the blood lags the centre by (r / a)^this. 2 = true " +
                              "laminar (the middle few hundred metres move as one, so nothing in view shears); 1 = the same " +
                              "shear everywhere, so from anywhere the layers nearer the middle visibly pass the ones nearer the wall.")]
    public float profileExponent = 1f;
    [Min(0f), Tooltip("How fast loose bodies take the blood's velocity (1/s).")]
    public float drag = 1.2f;
    [Tooltip("Turn the world frame with the blood round the player, so their surroundings are at rest in world space " +
             "wherever they are (off: the centreline blood is at rest, and near the wall everything moves at ~v0).")]
    public bool followPlayer = true;
    [Min(0.01f), Tooltip("Seconds for the frame to catch up with the blood round the player.")]
    public float frameEase = 1f;
    [Min(1f), Tooltip("Within this of the wall's relief (metres) the flow pushes back in.")]
    public float wallMargin = 40f;
    [Min(0f), Tooltip("Push back per metre past the margin (m/s per m).")]
    public float wallPush = 3f;
    [Min(0f), Tooltip("Fastest the wall pushes back (m/s). A backstop: narrows squeeze the flow themselves (Flow keeps r / a).")]
    public float maxWallPush = 45f;

    [Header("Regions")]
    [Min(100f), Tooltip("Length of a region along the loop (metres).")]
    public float regionLength = 1600f;
    [Min(10f), Tooltip("Blend between neighbouring regions (metres).")]
    public float regionBlend = 300f;
    [Min(1f), Tooltip("Seconds for a region's raised alert to fall halfway back to rest.")]
    public float alertHalfLife = 120f;
    [Min(0f), Tooltip("Alert per unit of cell signal raised in it (ImmuneSystem.Alarm).")]
    public float alertPerSignal = 0.0015f;
    [Min(0f), Tooltip("Most any region, alert and wall closeness together may multiply a layer (keeps inflamed walls from flooding).")]
    public float maxDensity = 4f;
    public List<RegionType> regionTypes = DefaultRegions();

    [Header("Atmosphere")]
    [Tooltip("Drive the scene fog: thin enough to see the far field, darker toward the centre, glowing toward the wall.")]
    public bool driveFog = true;
    [Min(0f), Tooltip("Exponential-squared fog density: things fade out by ~1.9 / density metres. 0.0012 = ~1600 m, the " +
                      "far field's reach (FarField.distance); 0.0045 hid everything past the loaded ~400 m.")]
    public float fogDensity = 0.0012f;
    [Tooltip("Fog in the middle of the tube. Alpha 0: take the skybox's horizon colour (so fogged things fade into the sky).")]
    public Color deepColor = new Color(0.12f, 0.2f, 0.32f, 0f);
    [Tooltip("Fog near the wall (warm, lit through the vessel wall).")]
    public Color wallGlowColor = new Color(0.5f, 0.2f, 0.2f, 1f);
    [Min(1f), Tooltip("Distance from the wall where the glow starts (metres).")]
    public float glowDistance = 600f;
    [Tooltip("Fog tint at full alert.")]
    public Color alertColor = new Color(0.65f, 0.28f, 0.1f, 1f);
    [Range(0f, 1f), Tooltip("Fog kept in focus mode (its orthographic camera sits far back).")]
    public float focusFog;

    [Header("Wall")]
    [Tooltip("The wall's look (Custom/VesselWall: colours, ink, haze, highlights), an asset you can edit live. " +
             "Shape (tiles, relief, folds) stays here: the flow's margin depends on it.")]
    public Material wallMaterial;
    [Tooltip("Size of a wall (endothelial) cell: along the loop, around it (metres).")]
    public Vector2 tileSize = new Vector2(320f, 110f);
    [Min(0f), Tooltip("How tall the wall cells' pillows and nuclei stand (metres, real geometry).")]
    public float wallRelief = 35f;
    [Min(0f), Tooltip("Height of the long folds running along the wall with the flow (metres).")]
    public float wallFolds = 70f;
    [Tooltip("Draw the wall (off: the sky shows past the fog again).")]
    public bool drawWall = true;
    [Tooltip("Raise the main camera's far plane so the longest view down the tube still ends on wall.")]
    public bool extendFarPlane = true;
    [Tooltip("Keep depth of field's far blur off the wall. The wall is always hundreds of metres away, past the blur's " +
             "end, so the camera's Gaussian DOF smeared it at half resolution. While the wall is drawn a runtime override " +
             "volume pushes the far blur out of reach (anything else that far is mostly in the fog).")]
    public bool sharpWall = true;
    [Range(32, 1024), Tooltip("Rings along the whole loop (packed toward the camera).")] public int wallSegments = 256;
    [Range(16, 1024), Tooltip("Vertices round the tube: enough for several per wall cell, so the relief is real.")]
    public int wallSides = 512;

    // ---------------- statics ----------------

    static Vessel s_active;
    public static Vessel Active => s_active && s_active.isActiveAndEnabled ? s_active : null;

    /// <summary>The blood's velocity at a world point (zero without a vessel).</summary>
    public static Vector3 FlowAt(Vector3 p) => s_active ? s_active.Flow(p) : Vector3.zero;

    /// <summary>World time (seconds, saved): what drift and the wall's position are measured by.</summary>
    public static double Clock => s_active ? s_active._clock : 0.0;

    /// <summary>Something noisy happened at p (ImmuneSystem.Alarm): its region's alert rises.</summary>
    public static void Alert(Vector3 p, float signal)
    {
        if (!s_active || signal <= 0f || s_active._alert == null) return;
        int i = s_active.RegionIndex(s_active.ToTube(p).S);
        s_active._alert[i] = Mathf.Clamp01(s_active._alert[i] + signal * s_active.alertPerSignal);
    }

    // ---------------- runtime ----------------

    const int Samples = 2048;

    public struct Tube
    {
        public float phi;   // angle round the loop's axis, world frame (-pi..pi)
        public float s;     // arc along the centreline, world frame (0..circumference)
        public float S;     // the same in the wall's frame (0..circumference)
        public float r;     // distance from the centreline
        public float theta; // angle round the centreline
        public float rho;   // distance from the loop's axis
        public Vector3 along, outward; // unit: downstream, and away from the centreline
    }

    Vector3 _centre, _e1, _e2, _axis;
    float _R;
    double _clock;
    double _wallS;       // S0: wall-frame offset (S = s + S0), metres
    double _frameAngle;  // world frame's turn relative to the centreline blood (radians)
    float _frameRate;    // ... and its rate (rad/s)
    bool _reframing;     // easing toward the player's layer (see FollowPlayer's dead zone)
    float[] _radius;
    int[] _regions;
    float[] _alert;
    int _seed = 1;
    bool _built;
    float _nextRegionTick;
    Texture2D _radiusTex;
    float _wallStretch;  // total of the wall pattern's along-warp (see UploadRadius)
    Mesh _wallMesh;
    Material _wallMat;
    MaterialPropertyBlock _props;
    VirusMovement _player;
    Camera _camera;
    float _cameraFarWas;
    bool _clearSaved;
    CameraClearFlags _clearWas;
    Color _backgroundWas;
    Volume _sharpVolume;
    VolumeProfile _sharpProfile;

    /// <summary>The wall covers every view, so the sky isn't drawn (SkyboxCache stops baking).</summary>
    public static bool HidesSky { get; private set; }
    float _nextFind, _nextCameraFind, _focus;
    bool _fogSaved, _fog;
    Color _fogColor;
    FogMode _fogMode;
    float _fogDensityWas;
    Color _sky = new Color(-1f, 0f, 0f);

    public float LoopRadius => _R;
    public Vector3 Axis => _axis;
    public Vector3 LoopCentre => _centre;
    public int RegionCount => _regions != null ? _regions.Length : 0;
    /// <summary>Largest the tube gets anywhere (metres).</summary>
    public float MaxRadius => radius * (1f + widthVariation);
    /// <summary>How far in from the nominal wall the flow starts pushing back and nothing is generated: the
    /// margin plus the wall's own relief (folds + cells stand that far in).</summary>
    public float InnerMargin => wallMargin + wallFolds + wallRelief * 1.2f;
    /// <summary>How far the world frame has turned round the loop's axis relative to the centreline blood (radians):
    /// it follows the blood round the player. Stored records keep it with their time.</summary>
    public static double FrameAngle => s_active ? s_active._frameAngle : 0.0;

    static readonly int CentreId = Shader.PropertyToID("_VesselCentre"), E1Id = Shader.PropertyToID("_VesselE1"),
                        E2Id = Shader.PropertyToID("_VesselE2"), AxisId = Shader.PropertyToID("_VesselAxis"),
                        RadiusTexId = Shader.PropertyToID("_VesselRadiusTex"), WallPhiId = Shader.PropertyToID("_WallPhi"),
                        WallHalfId = Shader.PropertyToID("_WallHalf"), TilesId = Shader.PropertyToID("_WallTiles"),
                        WallReliefId = Shader.PropertyToID("_WallRelief"), WallFoldsId = Shader.PropertyToID("_WallFolds");

    void OnEnable()
    {
        if (s_active && s_active != this && s_active.isActiveAndEnabled)
        {
            Debug.LogWarning("Vessel: a second one in the scene; this one is off.", this);
            enabled = false;
            return;
        }
        s_active = this;
        if (!_built) Build(WorldStreamer.Instance ? WorldStreamer.Instance.Seed : _seed);
    }

    void OnDisable()
    {
        if (s_active == this) s_active = null;
        RestoreFog();
        RestoreClear();
        HidesSky = false;
        if (_sharpVolume) _sharpVolume.weight = 0f;
        if (_camera) _camera.farClipPlane = _cameraFarWas;
        _camera = null;
    }

    void OnDestroy()
    {
        if (_radiusTex) Destroy(_radiusTex);
        if (_wallMesh) Destroy(_wallMesh);
        if (_wallMat) Destroy(_wallMat);
        if (_sharpVolume) Destroy(_sharpVolume.gameObject);
        if (_sharpProfile) Destroy(_sharpProfile);
    }

    /// <summary>Lays the loop out from a seed: its frame (this object's position is on the centreline, its forward
    /// downstream, its up the loop's axis), width profile, narrows and regions. The streamer calls it with the
    /// world seed (and again after loading a save with another).</summary>
    public void Build(int seed)
    {
        _built = true;
        _seed = seed;
        _R = circumference / (2f * Mathf.PI);
        Transform t = transform;
        _axis = t.up;
        _e2 = t.forward;
        _e1 = Vector3.Cross(_e2, _axis).normalized; // from the loop's centre out to this object
        _centre = t.position - _e1 * _R;

        var rng = new System.Random(seed ^ 0x5ea1ed);
        _radius ??= new float[Samples];
        float p1 = (float)rng.NextDouble() * 6.283f, p2 = (float)rng.NextDouble() * 6.283f, p3 = (float)rng.NextDouble() * 6.283f;
        var pinch = new float[narrows];
        for (int i = 0; i < narrows; i++) pinch[i] = ((i + 0.2f + 0.6f * (float)rng.NextDouble()) / narrows) * circumference;
        for (int i = 0; i < Samples; i++)
        {
            float S = (i + 0.5f) / Samples * circumference, x = S / circumference * 2f * Mathf.PI;
            // Whole numbers of waves round the loop, so it closes without a seam.
            float wave = 0.55f * Mathf.Sin(3f * x + p1) + 0.3f * Mathf.Sin(7f * x + p2) + 0.15f * Mathf.Sin(13f * x + p3);
            float a = radius * (1f + widthVariation * wave);
            foreach (float at in pinch)
            {
                float d = Mathf.Abs(Mathf.Repeat(S - at + circumference * 0.5f, circumference) - circumference * 0.5f) / (narrowLength * 0.5f);
                if (d < 1f) a *= 1f - narrowDepth * (0.5f + 0.5f * Mathf.Cos(d * Mathf.PI));
            }
            _radius[i] = a;
        }

        int n = Mathf.Max(1, Mathf.RoundToInt(circumference / regionLength));
        _regions = new int[n];
        _alert = new float[n];
        float total = 0f;
        foreach (RegionType r in regionTypes) total += r != null ? r.weight : 0f;
        for (int i = 0; i < n; i++)
        {
            int pick = 0;
            if (total > 0f)
            {
                float w = (float)rng.NextDouble() * total;
                for (int k = 0; k < regionTypes.Count; k++)
                    if (regionTypes[k] != null && (w -= regionTypes[k].weight) <= 0f) { pick = k; break; }
            }
            // The stretch the player starts in is always plain plasma (the first type), a calm start.
            _regions[i] = i == 0 ? 0 : pick;
            _alert[i] = Type(i)?.restAlert ?? 0f;
        }
        UploadRadius();
    }

    RegionType Type(int region) =>
        regionTypes.Count > 0 ? regionTypes[Mathf.Clamp(_regions[region], 0, regionTypes.Count - 1)] : null;

    void Update()
    {
        if (!_built) Build(_seed);
        float dt = Time.deltaTime;
        _clock += dt;
        _frameAngle += _frameRate * dt;
        // The wall turns at -(v0 / R) - w in world space, so its offset S0 = -R x its angle grows at v0 + w R.
        _wallS = Repeat(_wallS + (centreSpeed + _frameRate * _R) * dt, circumference);

        if (Time.time >= _nextRegionTick && _alert != null)
        {
            float step = Mathf.Max(1f, Time.time - _nextRegionTick + 1f);
            _nextRegionTick = Time.time + 1f;
            float keep = Mathf.Pow(0.5f, step / alertHalfLife);
            for (int i = 0; i < _alert.Length; i++)
            {
                float rest = Type(i)?.restAlert ?? 0f;
                _alert[i] = rest + (_alert[i] - rest) * keep;
            }
        }

        if (!_player && Time.unscaledTime >= _nextFind)
        {
            _nextFind = Time.unscaledTime + 1f;
            _player = FindAnyObjectByType<VirusMovement>();
        }

        SetGlobals();
        Atmosphere(dt);
        DrawWall();
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime, k = 1f - Mathf.Exp(-drag * dt);
        FollowPlayer(dt);

        // Loose dynamic bodies (cells...) take the blood's velocity. Organisms fly relative to it themselves.
        IReadOnlyList<WorldEntity> live = WorldStreamer.Live;
        for (int i = 0; i < live.Count; i++)
        {
            WorldEntity e = live[i];
            if (!e || !e.Drifts) continue;
            Rigidbody b = e.Body;
            if (b.isKinematic) continue;
            Vector3 u = Flow(b.position), v = b.linearVelocity;
            // Near the calm centre the flow is tiny: let resting bodies sleep (writing a velocity wakes them).
            if (b.IsSleeping() && u.sqrMagnitude < 0.04f) continue;
            // Plus what the body's own damping takes back this step, so damping doesn't hold it against the flow.
            b.linearVelocity = v + (u - v) * k + u * (b.linearDamping * dt);
        }
    }

    // The world frame turns round the loop's axis to follow the blood where the player is, so everything round
    // them is nearly at rest in world space wherever they go (near the wall the blood moves at ~v0 relative to the
    // centre, and every mismatch between interpolated bodies, script-moved things and the camera showed as rubber
    // banding). A change of the frame's turn rate is a change of reference only: every dynamic body gets the same
    // change of velocity, so nothing lurches relative to the blood. Kinematic movers read the flow each step.
    void FollowPlayer(float dt)
    {
        float target = 0f;
        if (followPlayer && _player)
        {
            Tube t = ToTube(_player.transform.position);
            float x = Mathf.Clamp01(t.r / Mathf.Max(RadiusAt(t.S), 1f));
            target = -centreSpeed * Lag(x) / _R; // the blood's turn rate there, relative to the centreline's
        }
        // A dead zone (a small share of the wall's turn rate, ~1.2 m/s at the centreline): a change writes a velocity
        // into every free body, which wakes it, so the frame only moves when the player has really changed layer,
        // and it snaps the last bit instead of creeping forever (that kept every body awake: physics 0.8 -> 3.4 ms).
        float gap = target - _frameRate, dead = 0.02f * centreSpeed / _R;
        if (!_reframing && Mathf.Abs(gap) < dead * 4f) return;
        _reframing = true;
        float next = Mathf.Abs(gap) < dead ? target : Mathf.Lerp(_frameRate, target, 1f - Mathf.Exp(-dt / Mathf.Max(frameEase, 0.01f)));
        if (next == target) _reframing = false;
        float change = next - _frameRate;
        _frameRate = next;
        if (change == 0f) return;

        // Velocity field of a turn at rate w: along * rho * (-w). Applied to every free body.
        IReadOnlyList<WorldEntity> live = WorldStreamer.Live;
        for (int i = 0; i < live.Count; i++)
        {
            WorldEntity e = live[i];
            if (e && e.Drifts && !e.Body.isKinematic) Reframe(e.Body, change);
        }
        List<Organism> all = Organism.All;
        for (int i = 0; i < all.Count; i++)
        {
            Organism o = all[i];
            if (o && o.Rb && !o.Rb.isKinematic) Reframe(o.Rb, change);
        }
    }

    // Sleeping bodies are left asleep: the drag loop wakes them if the blood where they are now moves.
    void Reframe(Rigidbody b, float change)
    {
        if (b.IsSleeping()) return;
        Tube t = ToTube(b.position);
        b.linearVelocity -= t.along * (t.rho * change);
    }

    // ---------------- geometry ----------------

    public Tube ToTube(Vector3 p)
    {
        Tube t;
        Vector3 q = p - _centre;
        float h = Vector3.Dot(q, _axis);
        Vector3 plane = q - _axis * h;
        float x = Vector3.Dot(plane, _e1), y = Vector3.Dot(plane, _e2);
        t.rho = Mathf.Sqrt(x * x + y * y);
        t.phi = t.rho > 1e-4f ? Mathf.Atan2(y, x) : 0f;
        Vector3 radial = t.rho > 1e-4f ? (_e1 * x + _e2 * y) / t.rho : _e1;
        t.along = Vector3.Cross(_axis, radial); // e1 -> e2 for phi = 0
        float d = t.rho - _R;
        t.r = Mathf.Sqrt(d * d + h * h);
        t.theta = t.r > 1e-4f ? Mathf.Atan2(h, d) : 0f;
        t.outward = t.r > 1e-4f ? (radial * d + _axis * h) / t.r : radial;
        t.s = Mathf.Repeat(t.phi * _R, circumference);
        t.S = (float)Repeat(t.s + _wallS, circumference);
        return t;
    }

    /// <summary>World point at loop angle phi (world frame), distance r and angle theta round the centreline.</summary>
    public Vector3 FromTube(float phi, float r, float theta)
    {
        Vector3 radial = _e1 * Mathf.Cos(phi) + _e2 * Mathf.Sin(phi);
        return _centre + radial * (_R + r * Mathf.Cos(theta)) + _axis * (r * Mathf.Sin(theta));
    }

    /// <summary>Tube radius at wall-frame position S.</summary>
    public float RadiusAt(float S)
    {
        if (_radius == null) return radius;
        float f = Mathf.Repeat(S / circumference, 1f) * Samples - 0.5f;
        int i = Mathf.FloorToInt(f);
        float t = f - i;
        return Mathf.Lerp(_radius[(i % Samples + Samples) % Samples], _radius[((i + 1) % Samples + Samples) % Samples], t);
    }

    /// <summary>Slope of the tube radius along the loop, da/dS, at wall-frame position S.</summary>
    public float SlopeAt(float S)
    {
        float h = circumference / Samples;
        return (RadiusAt(S + h) - RadiusAt(S - h)) / (2f * h);
    }

    /// <summary>The blood's velocity at p, in world space (the centre of the flow is at rest).</summary>
    public Vector3 Flow(Vector3 p)
    {
        Tube t = ToTube(p);
        float a = RadiusAt(t.S);
        // As an angular speed round the loop's axis: relative to the wall the blood turns at (v0 / R)(1 - Lag(x)),
        // the wall at -(v0 / R) relative to the centre, so the blood turns at -(v0 / R) Lag(x) (the same rate
        // stored records use, AngularRate). Written as linear speeds instead, the wall's rigid turn left a shear of
        // ~v0 d / R across the fat torus: things 300 m off the centre drifted up to 4 m/s.
        float x = Mathf.Clamp01(t.r / a);
        Vector3 v = t.along * (t.rho * (-centreSpeed * Lag(x) / _R - _frameRate)); // minus the frame's own turn
        // Narrows: the blood keeps its share of the width (x) as the wall's radius changes past it (it passes the wall
        // at v0 (1 - Lag(x)) along the loop), so a narrow squeezes the whole cross-section evenly and lets it out again
        // after. Pushed only by the wall margin, everything in the outer half of the tube piled into one shell just
        // inside the passing narrow and stayed there: a pack of touching cells, every one near the player on its 48
        // collider pieces (massive lag).
        v += t.outward * (x * SlopeAt(t.S) * centreSpeed * (1f - Lag(x)));
        float over = t.r - (a - InnerMargin);
        if (over > 0f) v -= t.outward * Mathf.Min(over * wallPush, maxWallPush);
        return v;
    }

    /// <summary>How fast a stored record at distance r from the centreline turns round the loop's axis
    /// (radians / s, world frame): the nominal flow there over the loop radius.</summary>
    public float AngularRate(float r)
    {
        return -centreSpeed * Lag(Mathf.Clamp01(r / radius)) / _R;
    }

    // How far the blood at x = r / a lags the centre, as a share of the centre's speed (0 middle .. 1 wall).
    float Lag(float x) => profileExponent == 2f ? x * x : profileExponent == 1f ? x : Mathf.Pow(x, profileExponent);

    /// <summary>Turns a pose round the loop's axis by an angle (radians): how drifting records move.</summary>
    public void Turn(ref Vector3 position, ref Quaternion rotation, ref Vector3 velocity, float angle)
    {
        if (angle == 0f) return;
        Quaternion q = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, _axis);
        position = _centre + q * (position - _centre);
        rotation = q * rotation;
        velocity = q * velocity;
    }

    // ---------------- regions ----------------

    int RegionIndex(float S)
    {
        int n = _regions.Length;
        return Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(S, circumference) / circumference * n), 0, n - 1);
    }

    /// <summary>The region at a world point, its neighbour toward the nearer border and the blend (0 = all
    /// this one), plus the blended alert.</summary>
    void Regions(float S, out int a, out int b, out float blend, out float alert)
    {
        int n = _regions.Length;
        float len = circumference / n, f = Mathf.Repeat(S, circumference) / len;
        a = Mathf.Clamp(Mathf.FloorToInt(f), 0, n - 1);
        float into = (f - a) * len;
        float half = Mathf.Min(regionBlend, len) * 0.5f;
        if (into < half) { b = (a - 1 + n) % n; blend = 0.5f * (1f - into / half); }
        else if (len - into < half) { b = (a + 1) % n; blend = 0.5f * (1f - (len - into) / half); }
        else { b = a; blend = 0f; }
        blend = blend * blend * (3f - 2f * blend);
        alert = Mathf.Lerp(_alert[a], _alert[b], blend);
    }

    public string RegionName(Vector3 p)
    {
        if (_regions == null) return "";
        return Type(RegionIndex(ToTube(p).S))?.name ?? "";
    }

    /// <summary>How much a streamer layer of this role is generated at p (region type, alert, place in the tube);
    /// 0 outside the tube.</summary>
    public float Density(Vector3 p, WorldStreamer.Role role, float room)
    {
        if (_regions == null) return 1f;
        Tube t = ToTube(p);
        float a = RadiusAt(t.S);
        if (t.r + room > a - InnerMargin) return 0f;
        Regions(t.S, out int ia, out int ib, out float blend, out float alert);
        RegionType A = Type(ia), B = Type(ib);
        float Of(RegionType r) => r == null ? 1f : role switch
        {
            WorldStreamer.Role.Cells => r.cells,
            WorldStreamer.Role.Resources => r.resources,
            WorldStreamer.Role.Immune => r.immune,
            _ => 1f,
        };
        float d = Mathf.Lerp(Of(A), Of(B), blend);
        if (role == WorldStreamer.Role.Immune)
        {
            float x = t.r / a;
            d *= Mathf.Lerp(0.35f, 1.8f, x * x) * (1f + alert); // white cells hug the wall (margination)
        }
        return Mathf.Min(d, maxDensity);
    }

    // ---------------- saving ----------------

    [Serializable]
    public class Save
    {
        public double clock;
        public float[] alert;
        public bool framed;      // false in saves from before the frame followed the player
        public double wallS, frameAngle;
        public float frameRate;
    }

    public Save Capture() => new Save
    {
        clock = _clock, alert = _alert != null ? (float[])_alert.Clone() : null,
        framed = true, wallS = _wallS, frameAngle = _frameAngle, frameRate = _frameRate,
    };

    public void Restore(Save save, int seed)
    {
        Build(seed);
        if (save == null) return;
        _clock = save.clock;
        _wallS = save.framed ? save.wallS : Repeat(centreSpeed * save.clock, circumference);
        _frameAngle = save.framed ? save.frameAngle : 0.0;
        _frameRate = save.framed ? save.frameRate : 0f;
        if (save.alert != null && _alert != null)
            for (int i = 0; i < _alert.Length && i < save.alert.Length; i++) _alert[i] = save.alert[i];
    }

    // ---------------- atmosphere + wall ----------------

    void SetGlobals()
    {
        float S0 = (float)_wallS;
        Shader.SetGlobalVector(CentreId, new Vector4(_centre.x, _centre.y, _centre.z, _R));
        Shader.SetGlobalVector(E1Id, new Vector4(_e1.x, _e1.y, _e1.z, circumference));
        Shader.SetGlobalVector(E2Id, new Vector4(_e2.x, _e2.y, _e2.z, S0));
        Shader.SetGlobalVector(AxisId, new Vector4(_axis.x, _axis.y, _axis.z, radius));
        if (_radiusTex) Shader.SetGlobalTexture(RadiusTexId, _radiusTex);
    }

    // The wall texture, per S sample: r = tube radius a, g = its slope da/dS, b = the wall pattern's warp along the
    // loop (tile fraction minus S / L, periodic). The pattern is laid out conformally, so wall cells keep their shape
    // everywhere and only change size: round the tube by the torus's isothermal angle (the shader, from a), along it
    // at a rate sqrt(1 + slope^2) sqrt(R^2 - a^2) / a, which follows the surface up and down a narrow's slope and
    // shrinks cells along as much as round where the tube narrows. Laid out by centreline length and plain angle, cells
    // were 2.3x longer on the loop's outer side than its inner and squashed / stretched through the narrows.
    void UploadRadius()
    {
        if (_radiusTex && _radiusTex.format != TextureFormat.RGBAFloat) Destroy(_radiusTex); // made before this layout
        if (!_radiusTex)
            _radiusTex = new Texture2D(Samples, 1, TextureFormat.RGBAFloat, false, true)
            {
                name = "Vessel Radius", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
        float dS = circumference / Samples;
        var px = new float[Samples * 4];
        var w = new float[Samples];
        double total = 0.0;
        for (int i = 0; i < Samples; i++)
        {
            float a = _radius[i];
            float g = (_radius[(i + 1) % Samples] - _radius[(i + Samples - 1) % Samples]) / (2f * dS);
            w[i] = Mathf.Sqrt(1f + g * g) * Mathf.Sqrt(Mathf.Max(_R * _R - a * a, 1f)) / Mathf.Max(a, 1f);
            px[i * 4] = a;
            px[i * 4 + 1] = g;
            total += w[i] * dS;
        }
        double x = 0.0;
        for (int i = 0; i < Samples; i++)
        {
            px[i * 4 + 2] = (float)((x + w[i] * dS * 0.5) / total - (i + 0.5) / Samples);
            x += w[i] * dS;
        }
        _wallStretch = (float)total;
        _radiusTex.SetPixelData(px, 0);
        _radiusTex.Apply(false, false);
    }

    void Atmosphere(float dt)
    {
        if (!driveFog || _regions == null) { RestoreFog(); return; }
        if (!_fogSaved)
        {
            _fogSaved = true;
            _fog = RenderSettings.fog;
            _fogColor = RenderSettings.fogColor;
            _fogMode = RenderSettings.fogMode;
            _fogDensityWas = RenderSettings.fogDensity;
        }

        bool focus = _player && _player.IsFocusMode;
        _focus = Mathf.MoveTowards(_focus, focus ? 1f : 0f, dt * 2f);

        Vector3 cam = SimulationTicker.HasCamera ? SimulationTicker.CameraPosition : transform.position;
        Tube t = ToTube(cam);
        float a = RadiusAt(t.S);
        Regions(t.S, out int ia, out int ib, out float blend, out float alert);
        Color tint = Color.Lerp(Type(ia)?.tint ?? Color.clear, Type(ib)?.tint ?? Color.clear, blend);

        Color deep = deepColor.a > 0f ? deepColor : SkyHorizon();
        float glow = Mathf.Clamp01(1f - (a - t.r) / glowDistance);
        Color c = Color.Lerp(deep, wallGlowColor, glow * glow * wallGlowColor.a);
        c = Color.Lerp(c, new Color(c.r * tint.r, c.g * tint.g, c.b * tint.b) * 1.6f, tint.a);
        c = Color.Lerp(c, alertColor, alert * 0.6f * alertColor.a);
        c.a = 1f;

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = c;
        RenderSettings.fogDensity = fogDensity * Mathf.Lerp(1f, focusFog, _focus);
    }

    Color SkyHorizon()
    {
        if (_sky.r >= 0f) return _sky;
        Material sky = SkyboxCache.Source ? SkyboxCache.Source : RenderSettings.skybox; // not the cache's stand-in
        _sky = sky && sky.HasProperty("_HorizonColor") ? sky.GetColor("_HorizonColor") : new Color(0.12f, 0.2f, 0.32f);
        _sky.a = 1f;
        return _sky;
    }

    void RestoreFog()
    {
        if (!_fogSaved) return;
        _fogSaved = false;
        RenderSettings.fog = _fog;
        RenderSettings.fogColor = _fogColor;
        RenderSettings.fogMode = _fogMode;
        RenderSettings.fogDensity = _fogDensityWas;
    }

    void DrawWall()
    {
        if (!wallMaterial && !_wallMat) // no asset assigned: the shader's defaults
        {
            Shader s = Shader.Find("Custom/VesselWall");
            if (!s) return;
            _wallMat = new Material(s) { hideFlags = HideFlags.DontSave };
        }
        _props ??= new MaterialPropertyBlock(); // a play-mode recompile keeps the material but not this
        if (_wallStretch <= 0f && _radius != null) UploadRadius(); // a play-mode recompile clears it
        if (!_wallMesh || _wallMesh.vertexCount != (wallSegments + 1) * (wallSides + 1)) BuildWallMesh();

        Vector3 cam = SimulationTicker.HasCamera ? SimulationTicker.CameraPosition : transform.position;
        Tube t = ToTube(cam);
        FarPlane();
        // Not in focus mode: its sweep outlines from depth + normals, and the wall has no normals pass.
        bool draw = drawWall && _focus <= 0.99f;
        HideSky(draw);
        SharpWall(draw);
        if (!draw) return;

        // The whole loop, centred on the camera (the shader packs rings toward it): every view down the tube
        // ends on wall, never the sky.
        _props.SetFloat(WallPhiId, t.phi);
        _props.SetFloat(WallHalfId, Mathf.PI);
        _props.SetFloat(WallReliefId, wallRelief);
        _props.SetFloat(WallFoldsId, wallFolds);
        // Whole numbers of wall cells round both ways, so the pattern closes without a seam. Along / round keeps
        // tileSize's aspect (z = the aspect after rounding; UploadRadius's warp makes it the same everywhere).
        float around = Mathf.Max(1f, Mathf.Round(2f * Mathf.PI * radius / Mathf.Max(tileSize.y, 1f)));
        float perAround = _wallStretch / (2f * Mathf.PI * _R); // along-tiles per around-tile at aspect 1
        float aspect = Mathf.Max(tileSize.x, 1f) / Mathf.Max(tileSize.y, 1f);
        float along = Mathf.Max(1f, Mathf.Round(around * perAround / aspect));
        _props.SetVector(TilesId, new Vector4(along, around, around * perAround / along, 0f));

        float extent = _R + MaxRadius;
        var rp = new RenderParams(wallMaterial ? wallMaterial : _wallMat)
        {
            matProps = _props,
            worldBounds = new Bounds(_centre, new Vector3(extent, extent, extent) * 2f),
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = true,
        };
        Graphics.RenderMesh(rp, _wallMesh, 0, Matrix4x4.identity);
    }

    // While the wall is drawn every view ends on it, so the sky (a costly procedural shader, and SkyboxCache's
    // background bakes) is never seen: the camera clears to the fog colour instead and the cache stops baking.
    void HideSky(bool hide)
    {
        HidesSky = hide && _camera;
        if (!_camera) return;
        if (hide)
        {
            if (!_clearSaved) { _clearSaved = true; _clearWas = _camera.clearFlags; _backgroundWas = _camera.backgroundColor; }
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = RenderSettings.fogColor;
        }
        else RestoreClear();
    }

    // Depth of field's far blur (Gaussian: past gaussianEnd, 60 m in this scene) would always cover the wall, so while
    // it's drawn a global volume above the scene's pushes the far blur out and stops Bokeh down. It's off in focus
    // mode (no wall there), where ScreenInvertTest fades the scene's own DOF as before.
    void SharpWall(bool on)
    {
        on &= sharpWall && Application.isPlaying;
        if (!_sharpVolume)
        {
            if (!on) return;
            _sharpProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            _sharpProfile.hideFlags = HideFlags.DontSave;
            var dof = _sharpProfile.Add<DepthOfField>();
            dof.gaussianStart.Override(1e5f);
            dof.gaussianEnd.Override(2e5f);
            dof.aperture.Override(32f);
            var go = new GameObject("Vessel Sharp Wall") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            _sharpVolume = go.AddComponent<Volume>();
            _sharpVolume.isGlobal = true;
            _sharpVolume.priority = 1000f;
            _sharpVolume.sharedProfile = _sharpProfile;
        }
        _sharpVolume.weight = on ? 1f : 0f;
    }

    void RestoreClear()
    {
        if (!_clearSaved || !_camera) return;
        _clearSaved = false;
        _camera.clearFlags = _clearWas;
        _camera.backgroundColor = _backgroundWas;
    }

    // The longest straight line inside the tube (a chord of the outer wall grazing the inner one) is
    // 2 sqrt((R + a)^2 - (R - a)^2) = 4 sqrt(R a): keep that inside the main camera's far plane.
    void FarPlane()
    {
        if (!extendFarPlane) return;
        if (!_camera && Time.unscaledTime >= _nextCameraFind)
        {
            _nextCameraFind = Time.unscaledTime + 1f;
            _camera = Camera.main;
            if (_camera) _cameraFarWas = _camera.farClipPlane;
        }
        if (!_camera) return;
        float need = 4f * Mathf.Sqrt(_R * MaxRadius) + 100f;
        if (_camera.farClipPlane < need) _camera.farClipPlane = need;
    }

    // A (segments x sides) grid of uvs: along the loop, round the tube. The shader places it.
    void BuildWallMesh()
    {
        if (!_wallMesh) _wallMesh = new Mesh { name = "Vessel Wall", hideFlags = HideFlags.DontSave };
        _wallMesh.Clear();
        int nu = wallSegments + 1, nv = wallSides + 1;
        var pos = new Vector3[nu * nv];
        var uv = new Vector2[nu * nv];
        for (int i = 0; i < nu; i++)
        for (int j = 0; j < nv; j++)
            uv[i * nv + j] = new Vector2(i / (float)wallSegments, j / (float)wallSides);
        var tris = new int[wallSegments * wallSides * 6];
        int n = 0;
        // Rings nearest the camera (u = 0.5, the middle of the grid) first: the whole loop is drawn, and far
        // stretches behind the near wall are then rejected by the depth test instead of shaded (was ~4 ms).
        var rings = new int[wallSegments];
        for (int i = 0; i < wallSegments; i++) rings[i] = i;
        Array.Sort(rings, (x, y) => Mathf.Abs(x + 0.5f - wallSegments * 0.5f).CompareTo(Mathf.Abs(y + 0.5f - wallSegments * 0.5f)));
        foreach (int i in rings)
        for (int j = 0; j < wallSides; j++)
        {
            int a = i * nv + j, b = a + nv;
            tris[n++] = a; tris[n++] = a + 1; tris[n++] = b;
            tris[n++] = b; tris[n++] = a + 1; tris[n++] = b + 1;
        }
        _wallMesh.indexFormat = pos.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        _wallMesh.SetVertices(pos);
        _wallMesh.SetUVs(0, uv);
        _wallMesh.SetTriangles(tris, 0, false);
        _wallMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f); // placed in the vertex stage
    }

    static double Repeat(double x, double length) => x - Math.Floor(x / length) * length;

    static List<RegionType> DefaultRegions() => new List<RegionType>
    {
        new RegionType { name = "Plasma", weight = 3f },
        new RegionType { name = "Rich", weight = 1.2f, tint = new Color(1f, 0.85f, 0.45f, 0.35f), cells = 0.8f, resources = 2.2f, immune = 0.8f },
        new RegionType { name = "Inflamed", weight = 1f, tint = new Color(1f, 0.55f, 0.35f, 0.5f), cells = 1.1f, resources = 0.7f, immune = 2.2f, restAlert = 0.5f },
        new RegionType { name = "Crowded", weight = 1f, tint = new Color(0.8f, 0.4f, 0.45f, 0.35f), cells = 2.2f, resources = 1f, immune = 1.2f },
        new RegionType { name = "Sparse", weight = 1f, tint = new Color(0.55f, 0.75f, 1f, 0.35f), cells = 0.35f, resources = 0.6f, immune = 0.4f },
    };

    void OnDrawGizmosSelected()
    {
        if (!_built) return;
        Gizmos.color = new Color(0.9f, 0.4f, 0.4f, 0.5f);
        const int n = 128;
        Vector3 prev = FromTube(0f, 0f, 0f);
        for (int i = 1; i <= n; i++)
        {
            Vector3 p = FromTube(i / (float)n * 2f * Mathf.PI, 0f, 0f);
            Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }
}
