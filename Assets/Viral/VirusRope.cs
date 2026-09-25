using System.Collections;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// Multi-rope XPBD rope system with a spool on the player.
///
/// A mechanism with no input of its own: a controller calls PlaceAnchor() and sets
/// reel / feed each frame. It reads the player's ISurfaceContact (on this
/// object or a parent) to know whether it is standing, and the player's
/// Rigidbody for tugging.
///
/// - PlaceAnchor() while standing: start a rope under the player, or pin the
///   held rope's end to the ground (a piece with a loose far end keeps it loose).
/// - StartFromSpool(): start a rope to pay out, anchored under the player when
///   standing, trailing a loose end in the air.
/// - Loose ends: LooseEndNear() finds one, PhantomEnd shows a ghost rope from it to
///   the player, Grab() takes it in hand (the gap becomes rope) as the held rope. Base ends
///   work the same from closer in (BaseEndInReach, GrabBase: unanchored first).
/// - ReleaseHeld(): let go of the held rope; its end stays where it is, loose.
/// - Tug(): yank the held rope, tearing its far end loose and whipping it in; with the far end
///   already loose, slurp the whole rope in until it's gone.
/// - Mouth: the held rope attaches to one side of the body (under it by default). Placement
///   only: nothing pushes the rope there (a body pushing its own rope shoved the player and
///   whatever the rope lay on). It's *drawn* curling round the body into the mouth (CurlToMouth).
/// - The first Auto Payout Length meters pay out automatically as you move.
/// - feed: pay rope out. reel: winch it in, stalling at Reel Strength.
/// - Bases: either end anchored to a surface is a base. BaseAt() finds one under a screen
///   point and HoveredBase grows it. ToggleStraight() on either base switches the whole rope.
///   Straight with one base: it stands up off it. Straight between two bases: it becomes a rod
///   of its own length, welded square to both surfaces, pushing and turning the bodies until
///   it fits. Rods sharing bodies settle together as a network.
/// - SetLength(): ease any rope (by a base handle) to a new length; a rod pushes its bodies to fit.
/// - Cauterize(): on a rod, pump blood up it from that base (it swells as the front climbs), then
///   freeze it into a static strut and weld the two bodies with a FixedJoint. Locks its settings.
///
/// Simulation runs after the physics step (WaitForFixedUpdate) on true Rigidbody poses and is
/// interpolated for rendering. Tension is real (Newtons) and drives snapping, reel stalling,
/// player tug and forces on anchored Rigidbodies. Primitive colliders are resolved analytically;
/// others use Physics.ComputePenetration. Still ropes sleep; off-screen ropes skip mesh work.
/// </summary>
[DefaultExecutionOrder(100)]
public class VirusRope : MonoBehaviour
{
    public enum RopeQuality { Fast, Balanced, High }

    [Header("Setup")]
    public Transform playerRopePoint;
    public Material ropeMaterial;
    public LayerMask surfaceLayers = ~0;

    [Header("Ropes")]
    [Range(1, 256)] public int maxRopes = 8;
    [Min(0.06f), Tooltip("Distance between rope particles. Larger = cheaper long ropes.")]
    public float particleSpacing = 0.2f;
    [Min(0.001f)] public float ropeRadius = 0.035f;
    [Tooltip("Computational effort only. Does not change the rope's personality.")]
    public RopeQuality quality = RopeQuality.Balanced;

    [Header("Behavior")]
    [Range(0f, 1f), Tooltip("0 = floats almost forever, 1 = settles quickly.")]
    public float damping = 0.35f;
    [Range(0f, 1f), Tooltip("0 = inextensible, 1 = noticeably stretchy.")]
    public float elasticity = 0.08f;
    [Range(0f, 1f), Tooltip("0 = slippery, 1 = grippy. Taut, wrapped rope grips more.")]
    public float surfaceGrip = 0.15f;
    [Range(0f, 1f), Tooltip("Bending stiffness. 0 = limp, 1 = smooth broad curves.")]
    public float smoothness = 0.45f;
    [Min(0f), Tooltip("Tension in Newtons that snaps the rope. 0 = unbreakable.")]
    public float breakForce = 80f;
    [Min(0.01f), Tooltip("Seconds tension must stay over Break Force. Longer ignores jolts from fast moves; sustained pulls still snap.")]
    public float breakDelay = 0.25f;
    [Range(0f, 1f), Tooltip("Force transfer into dynamic Rigidbodies (not the player). 1 = physically correct.")]
    public float objectForce = 1f;
    [Range(0f, 1f), Tooltip("How strongly the rope tugs the player. 0 = passive, 1 = full tether using the player's mass.")]
    public float playerTug = 1f;

    [Header("Spool")]
    [Min(0.1f)] public float maxRopeLength = 40f;
    [Min(0f), Tooltip("The first X meters pay out automatically as you move. Beyond this, use RMB.")]
    public float autoPayoutLength = 5f;
    [Min(0.01f)] public float retractSpeed = 6f;
    [Min(0.01f)] public float feedSpeed = 6f;
    [Range(0f, 1f), Tooltip("0 = soft reel ramp, 1 = snappy.")]
    public float reelResponse = 0.45f;
    [Min(0f), Tooltip("Tension (N) at which reeling stalls. 0 = unlimited (can snap the rope).")]
    public float reelStrength = 30f;

    [Header("Loose ends")]
    [Min(0f), Tooltip("How close (world units, from the body's surface) a loose rope end must be to take it in hand.")]
    public float grabRadius = 3f;
    [Min(0f), Tooltip("How close an anchored (base) end must be to pull it up and take it in hand.")]
    public float baseGrabRadius = 1.5f;
    [Tooltip("Ghost rope shown from a loose end in reach to the player. Empty: a see-through URP Unlit.")]
    public Material phantomMaterial;

    public enum MouthSide { Below, Front, Back, Above }

    [Header("Mouth")]
    [Tooltip("The held rope attaches to one side of the body. Off: to playerRopePoint itself.")]
    public bool frontOnly = true;
    [Tooltip("Which side of the body (playerRopePoint's own axes; its up is the virus's up) the rope comes out of.")]
    public MouthSide mouthSide = MouthSide.Below;
    [Min(0f), Tooltip("How far from playerRopePoint the mouth sits. 0 = the reach of the player's colliders.")]
    public float bodyRadius;
    [Min(0f), Tooltip("How much rope near the mouth is drawn curling round the body to come out of the mouth head-on, " +
                      "in body radii. Drawn only: the physics is untouched, so it pushes nothing. 0 = off.")]
    public float curl = 2f;

    [Header("Tug")]
    [Min(0f), Tooltip("Speed (m/s) a tug whips the rope toward the player: full at the far end, fading toward the player.")]
    public float tugSpeed = 14f;
    [Min(0f), Tooltip("Impulse (N s) a tug gives the body the far end was anchored to, toward the player.")]
    public float tugImpulse = 3f;
    [Min(0.01f), Tooltip("Tugging a rope whose far end is already loose slurps it all in, at this speed (m/s).")]
    public float slurpSpeed = 40f;

    [Header("Base")]
    [Min(1f), Tooltip("Base size while hovered.")]
    public float hoverScale = 1.6f;
    [Min(0.01f), Tooltip("Seconds for the base to grow or shrink.")]
    public float hoverTime = 0.12f;
    [Min(0f), Tooltip("Focus mode: each base shows a glowing core (like a resource chunk's) so it reads as clickable. Its radius, in rope radii. 0 = none.")]
    public float baseCoreSize = 2.2f;
    [Tooltip("Custom/ResourceCore. Found by name when empty (editor only: keep it assigned for builds).")]
    public Shader baseCoreShader;
    [Min(0f), Tooltip("How hard a straightened rope pulls itself into shape (1/s). Higher = stiffer.")]
    public float straightenStrength = 25f;
    [Min(0f), Tooltip("Straightened between two bodies: how quickly the rod moves and turns them until it fits at its own length, square to both surfaces (rad/s). 0 = don't move them.")]
    public float rodStrength = 4f;

    [Header("Cauterize")]
    [Tooltip("The blood beads (a Custom/RopeBlood material). Empty: one made from that shader (assign for builds).")]
    public Material bloodMaterial;
    [Min(0.01f), Tooltip("How fast (m/s) the blood front climbs the rope from the base it was started at.")]
    public float cauterizeSpeed = 3f;
    [Min(1f), Tooltip("Rope radius once filled, times the usual.")]
    public float cauterizeSwell = 1.9f;
    [Min(0.05f), Tooltip("Length (m) of the swelling front.")]
    public float pumpFront = 0.7f;
    [Min(0f), Tooltip("Extra bulge of the front, times the rope radius.")]
    public float pumpBolus = 0.9f;
    [Min(0f), Tooltip("Heartbeat bursts rippling up the filled part, times the rope radius.")]
    public float pumpPulse = 0.5f;
    [Min(0.05f), Tooltip("Metres between heartbeats (each a lub-dub pair of bursts).")]
    public float pumpSpacing = 2.5f;
    [Tooltip("Speed (m/s) the bursts travel up the rope. Beats per second = speed / spacing.")]
    public float pumpSpeed = 4f;
    [Min(0.01f), Tooltip("Length (m) over which the blood beads snap into place, one by one.")]
    public float pumpBlend = 0.6f;
    [Min(0.01f), Tooltip("Seconds the heartbeat takes to die down once sealed.")]
    public float settleTime = 1.2f;
    [Range(3, 32), Tooltip("Strands of blood beads round the rope (a microtubule has 13).")]
    public int beadStrands = 13;
    [Min(0.1f), Tooltip("Bead size: 1 = neighbours round the rope just touch.")]
    public float beadSize = 1.25f;
    public bool beadShadows = true;
    [Tooltip("The beads wear this material's colours, lumps and lighting. Empty: the material of the cell the blood came from, so they match whatever they grow out of (else Blood Material's own).")]
    public Material beadLook;
    [Min(0f), Tooltip("How much brighter beads get as a heartbeat burst passes.")]
    public float beadBeatGlow = 0.6f;
    [Min(0f), Tooltip("How much the bead tube widens into each base, times its radius.")]
    public float baseFlare = 1f;
    [Range(0, 6), Tooltip("Rings of beads spreading onto each base's surface.")]
    public int skirtRings = 3;

    [Header("Visual")]
    [Range(3, 12)] public int tubeSides = 6;
    [Tooltip("Free and player ends get a half-sphere cap.")]
    public bool roundedEnds = true;
    [Tooltip("Ends attached to surfaces get a web-like anchor.")]
    public bool webAnchors = true;
    [Range(3, 12)] public int webStrands = 6;
    [Min(0f), Tooltip("How far web strands reach across the surface, in meters.")]
    public float webSpread = 0.18f;

    /// <summary>Set by a controller every frame. Both at once cancel out.</summary>
    [System.NonSerialized] public bool reel, feed;

    public int RopeCount => _ropes.Count;
    public bool HasRopeAttachedToPlayer => _active != null;

    /// <summary>Sound / FX hooks (RopeAudio). Anchored: an end was pinned to a surface (world point,
    /// normal). Spooled: rope run through the mouth this physics step, in metres (+ paid out,
    /// - reeled in). Stowed: a held rope was reeled all the way in.</summary>
    public event System.Action<Vector3, Vector3> Anchored;
    public event System.Action<float> Spooled;
    public event System.Action Stowed;

    // ------------------------------------------------------------------ constants

    const int HARD_MAX_SEGMENTS = 2048;
    const int CHUNK = 16;                 // segments per broadphase query
    const int MAX_PAIRS = 8192;
    const int GATHER_INTERVAL = 4;        // steps a pair list may be reused
    const float GATHER_MARGIN = 0.2f;     // drift (m) allowed before regathering
    const float LINEAR_DENSITY = 0.05f;   // kg/m
    const float TENSION_FILTER = 0.05f;   // s
    const float BREAK_GRACE = 0.12f;      // s
    const float SLEEP_DELAY = 0.75f;      // s
    const float SLEEP_SPEED = 0.01f;      // m/s
    const float GROUND_PROBE = 3f;
    const float MAX_TUG_SPEED = 30f;      // m/s

    const int CAP_RINGS = 2, STRAND_RINGS = 5, STRAND_SIDES = 3;
    const int END_NONE = 0, END_CAP = 1, END_WEB = 2;
    const int KIND_DEAD = -1, KIND_SPHERE = 0, KIND_CAPSULE = 1, KIND_BOX = 2, KIND_GENERIC = 3;

    int Substeps => quality == RopeQuality.Fast ? 6 : quality == RopeQuality.High ? 16 : 10;
    int SegmentCapacity => Mathf.Clamp(Mathf.CeilToInt(maxRopeLength / particleSpacing) + 4, 8, HARD_MAX_SEGMENTS);
    float Skin => Mathf.Max(0.004f, ropeRadius * 0.15f);
    float ParticleMass => LINEAR_DENSITY * particleSpacing;
    float MinRopeLength => Mathf.Max(ropeRadius * 4f, particleSpacing * 0.5f);
    bool IsGrounded => _contact != null && _contact.OnSurface;

    static readonly ProfilerMarker s_simMarker = new ProfilerMarker("VirusRope.Simulate");
    static readonly ProfilerMarker s_meshMarker = new ProfilerMarker("VirusRope.Mesh");
    static readonly ProfilerMarker s_gatherMarker = new ProfilerMarker("VirusRope.Gather"),
                                   s_solveMarker = new ProfilerMarker("VirusRope.Solve"),
                                   s_collideMarker = new ProfilerMarker("VirusRope.Collide"),
                                   s_sweepMarker = new ProfilerMarker("VirusRope.Sweep"),
                                   s_wakeMarker = new ProfilerMarker("VirusRope.WakeCheck");

    /// <summary>One line on what the ropes are doing, for profiling reports.</summary>
    public string DebugSummary()
    {
        int particles = 0, asleep = 0, pairs = 0, generic = 0, shapes = 0;
        foreach (Rope r in _ropes)
        {
            particles += r.n;
            if (r.sleeping) asleep++;
            pairs += r.pairCount;
            shapes += r.shapeCount;
            for (int p = 0; p < r.pairCount; p++)
                if (r.shapes[r.pairShape[p]].kind == KIND_GENERIC) generic++;
        }
        return $"{_ropes.Count} ropes ({asleep} asleep), {particles} particles, {Substeps} substeps, " +
               $"{shapes} nearby colliders, {pairs} collision pairs ({generic} against mesh/generic colliders)";
    }

    // ------------------------------------------------------------------ types

    sealed class Anchor
    {
        public Transform support;
        public Rigidbody body;
        bool _hadTarget, _hasNormal;
        Vector3 _local, _localN, _world;

        public bool Lost => _hadTarget && !support && !body;
        public bool HasSurface => _hasNormal && !Lost;

        public void Set(Transform t, Rigidbody rb, Vector3 p, Vector3 normal)
        {
            support = t; body = rb; _world = p; _hadTarget = t || rb;
            _hasNormal = normal.sqrMagnitude > 1e-6f;
            normal = _hasNormal ? normal.normalized : Vector3.up;

            if (rb)
            {
                Quaternion inv = Quaternion.Inverse(rb.rotation);
                _local = inv * (p - rb.position);
                _localN = inv * normal;
            }
            else if (t)
            {
                _local = t.InverseTransformPoint(p);
                _localN = t.InverseTransformDirection(normal);
            }
            else _localN = normal;
        }

        public void CopyFrom(Anchor o)
        {
            support = o.support; body = o.body; _hadTarget = o._hadTarget; _hasNormal = o._hasNormal;
            _local = o._local; _localN = o._localN; _world = o._world;
        }

        public void Clear() { support = null; body = null; _hadTarget = _hasNormal = false; }

        // A point held rigidly to whatever this anchor is on (sealed ropes).
        public Vector3 ToLocal(Vector3 p) =>
            body ? Quaternion.Inverse(body.rotation) * (p - body.position) :
            support ? support.InverseTransformPoint(p) : p;
        public Vector3 SimFromLocal(Vector3 l) =>
            body ? body.position + body.rotation * l : support ? support.TransformPoint(l) : l;
        public Vector3 RenderFromLocal(Vector3 l) =>
            body ? body.transform.position + body.transform.rotation * l : support ? support.TransformPoint(l) : l;

        // True physics pose (solver).
        public Vector3 SimPoint
        {
            get
            {
                if (body) _world = body.position + body.rotation * _local;
                else if (support) _world = support.TransformPoint(_local);
                return _world;
            }
        }

        // Surface normal on the physics pose (solver).
        public Vector3 SimNormal =>
            body ? body.rotation * _localN :
            support ? support.TransformDirection(_localN) : _localN;

        // Interpolated transform pose (rendering).
        public Vector3 RenderPoint =>
            body ? body.transform.position + body.transform.rotation * _local :
            support ? support.TransformPoint(_local) : _world;

        public Vector3 RenderNormal =>
            body ? body.transform.rotation * _localN :
            support ? support.TransformDirection(_localN) : _localN;
    }

    struct Shape
    {
        public Collider col;
        public Rigidbody rb;
        public bool dynamic;
        public int kind;
        public Vector3 c, a, b, half, bmin, bmax;
        public Quaternion rot, inv;
        public float radius;
    }

    sealed class Rope
    {
        public readonly Anchor a = new Anchor(), b = new Anchor();
        public bool pinA, pinB, bIsPlayer, sleeping, topologyDirty = true, meshDirty = true;
        public bool straight;                      // stands up off its bases
        public bool suspended;                     // a base was pulled up: its settings wait until it's placed again
        public bool Straight => straight && !suspended;
        public bool slurping;                      // reeling itself in until gone (a tug on a loose rope)
        public float targetLength;                 // easing toward this length (SetLength); 0 = none
        public int pumpFrom = -1;                  // cauterizing from base 0 (A) or 1 (B); -1 = not
        public float pump, sealedAt;               // metres the blood front has climbed; when it sealed
        public bool isSealed;                      // filled: frozen to A's body, the bodies welded
        public Vector3[] sealedLocal;              // particles in A's anchor space once sealed
        public Joint weld;
        public GameObject bloodGo;                 // the beads' proxy (tube + base discs), drawn by Custom/RopeBlood
        public Mesh bloodMesh;
        public MeshRenderer bloodRenderer;
        public MaterialPropertyBlock bloodProps;
        public bool Cauterizing => pumpFrom >= 0;
        public float scaleA = 1f, scaleB = 1f;     // visual size of each base, eased toward hover
        public bool BaseA => pinA;                 // an end anchored to a surface is a base
        public bool BaseB => pinB && !bIsPlayer;
        public int id, n, capacity, coilIndex;
        public float length, reelSpeed, peakTension, endTension, graceUntil, sleepTimer, drift;
        public Vector3 lastA, lastB, boundsMin, boundsMax, playerCorr;

        public Vector3[] x, prev, start, contactNormal, contactVel;
        public float[] rest, tension, contactDepth;
        public bool[] contact;

        public Shape[] shapes = new Shape[8];
        public int shapeCount, pairCount, stepsSinceGather = 1000;
        public int[] pairSeg = new int[64], pairShape = new int[64];

        public GameObject go;
        public Mesh mesh;
        public MeshRenderer meshRenderer;
        public Vector3[] verts, norms;
        public Vector2[] uvs;
        public int[] tris;
        public int meshKey, meshSides = -1;
        public Vector3 frameNormal;
        public bool frameSeeded;

        public Rope(int segments) => Grow(segments);

        public void Grow(int segments)
        {
            if (segments <= capacity) return;
            capacity = segments;
            int p = segments + 1;
            System.Array.Resize(ref x, p);
            System.Array.Resize(ref prev, p);
            System.Array.Resize(ref start, p);
            System.Array.Resize(ref contactNormal, p);
            System.Array.Resize(ref contactVel, p);
            System.Array.Resize(ref contactDepth, p);
            System.Array.Resize(ref contact, p);
            System.Array.Resize(ref rest, segments);
            System.Array.Resize(ref tension, segments);
        }
    }

    // ------------------------------------------------------------------ state

    readonly List<Rope> _ropes = new List<Rope>();
    readonly HashSet<Collider> _playerCols = new HashSet<Collider>();
    readonly Dictionary<Collider, int> _shapeLookup = new Dictionary<Collider, int>(64);
    readonly Collider[] _overlap = new Collider[256]; // concave cells are many pieces (Surface.Collider.cs)
    readonly RaycastHit[] _hits = new RaycastHit[16];
    static Vector3[] s_render = new Vector3[256];
    float[] _cos, _sin;

    Rope _active;
    Rope _phantom; // ghost from a loose end to the player: drawn only, never simulated
    Material _phantomMat;
    int _nextId;
    ISurfaceContact _contact;
    Rigidbody _standingOn; // while attached the player is part of this body: rope forces on it are internal
    Rigidbody _playerBody;
    Vector3 _playerOffset, _mouthDir; // body centre and mouth direction, in the player Rigidbody's frame
    float _autoBodyRadius = 0.5f;
    Transform _root;
    Material _fallbackMat, _bloodMat;
    CapsuleCollider _probe;
    Coroutine _loop;
    int _stepCounter;

    bool _feedHeld, _reelHeld;

    // ------------------------------------------------------------------ unity

    void Awake()
    {
        _contact = GetComponentInParent<ISurfaceContact>();
        _playerBody = GetComponentInParent<Rigidbody>();
        if (!playerRopePoint) playerRopePoint = transform;
        RefreshPlayerColliders();

        _root = new GameObject("__VirusRopes").transform;

        var probe = new GameObject("__RopeProbe") { hideFlags = HideFlags.HideAndDontSave, layer = 2 };
        probe.transform.position = Vector3.one * 1e5f;
        _probe = probe.AddComponent<CapsuleCollider>();
        _probe.isTrigger = true;
        _probe.direction = 1;

        CachePlayerOffset();
    }

    void OnEnable() => _loop = StartCoroutine(PostPhysicsLoop());

    void OnDisable()
    {
        if (_loop != null) StopCoroutine(_loop);
        _loop = null;
    }

    void OnDestroy()
    {
        ClearAllRopes();
        if (_phantom != null) DestroyVisual(_phantom);
        if (_probe) Destroy(_probe.gameObject);
        if (_fallbackMat) Destroy(_fallbackMat);
        if (_phantomMat) Destroy(_phantomMat);
        if (_bloodMat) Destroy(_bloodMat);
        if (_coreMat) Destroy(_coreMat);
        _coreBuffer?.Release();
        foreach (Material m in _beadMats.Values) if (m) Destroy(m);
        _beadMats.Clear();
        if (_root) Destroy(_root.gameObject);
    }

    void Update()
    {
        CachePlayerOffset();

        _feedHeld = feed && !reel;
        _reelHeld = reel && !feed;

        float grow = (hoverScale - 1f) * Time.deltaTime / hoverTime;
        foreach (Rope r in _ropes)
        {
            float goalA = HoveredBase == r.id * 2 && r.BaseA ? hoverScale : 1f;
            float goalB = HoveredBase == r.id * 2 + 1 && r.BaseB ? hoverScale : 1f;
            if (r.scaleA == goalA && r.scaleB == goalB) continue;
            r.scaleA = Mathf.MoveTowards(r.scaleA, goalA, Mathf.Max(grow, 1e-4f));
            r.scaleB = Mathf.MoveTowards(r.scaleB, goalB, Mathf.Max(grow, 1e-4f));
            r.meshDirty = true; // redraw even while the rope sleeps
        }

        // Blood climbing cauterizing ropes; the front runs on past the far base and off.
        foreach (Rope r in _ropes)
        {
            if (!r.Cauterizing) continue;
            float end = r.length + pumpFront * 2f;
            if (r.pump < end) r.pump = Mathf.Min(end, r.pump + cauterizeSpeed * Time.deltaTime);
            if (!r.isSealed && r.pump >= r.length) Seal(r);
            r.sleeping = false; // sealed ropes follow their bodies every frame
            r.meshDirty = true;
        }

        if (_active != null)
        {
            float accel = Mathf.Lerp(Mathf.Max(0.5f, retractSpeed * 0.5f), Mathf.Max(8f, retractSpeed * 5f), reelResponse);
            _active.reelSpeed = Mathf.MoveTowards(_active.reelSpeed, _reelHeld ? retractSpeed : 0f,
                accel * (_reelHeld ? 1f : 1.25f) * Time.deltaTime);
        }
    }

    IEnumerator PostPhysicsLoop()
    {
        var wait = new WaitForFixedUpdate();
        while (true)
        {
            yield return wait;
            _stepCounter++;
            _standingOn = IsGrounded && _contact.Surface ? _contact.Surface.GetComponentInParent<Rigidbody>() : null;
            using (s_simMarker.Auto())
                for (int i = _ropes.Count - 1; i >= 0; i--)
                    StepRope(i, _ropes[i], Time.fixedDeltaTime);
        }
    }

    void LateUpdate()
    {
        float alpha = Time.fixedDeltaTime > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) : 1f;
        Material mat = ActiveMaterial();

        using (s_meshMarker.Auto())
        {
            for (int i = 0; i < _ropes.Count; i++)
                BuildMesh(_ropes[i], alpha, mat);
            DrawPhantom(alpha);
        }
        DrawBaseCores();
    }

    // ------------------------------------------------------------------ base cores

    // Focus mode: a glowing core in each base, drawn by the resource cores' own shader (over the sweep,
    // only inside its circle), so bases read as clickable like a chunk's core. Terminal colours: cyan,
    // acid green once straightened, blood red while cauterizing; grows and brightens when hovered.
    // Hidden behind a cell (a raycast per base every BaseSightInterval) unless hovered.
    struct CoreInstance { public Vector4 positionScale, color, state; }
    const int CoreStride = 48;
    const float BaseSightInterval = 0.15f;
    CoreInstance[] _coreData = new CoreInstance[16];
    GraphicsBuffer _coreBuffer;
    Material _coreMat;
    MaterialPropertyBlock _coreProps;
    readonly Dictionary<int, Vector2> _baseSight = new Dictionary<int, Vector2>(); // next check, hidden (0/1)
    static readonly int CoresId = Shader.PropertyToID("_Cores");

    void DrawBaseCores()
    {
        if (!ShowBaseCores || baseCoreSize <= 0f)
        {
            if (_baseSight.Count > 0) _baseSight.Clear();
            return;
        }
        if (!_coreMat)
        {
            Shader shader = baseCoreShader ? baseCoreShader : Shader.Find("Custom/ResourceCore");
            if (!shader) return;
            _coreMat = new Material(shader) { name = "Rope Base Core", hideFlags = HideFlags.DontSave };
        }
        _coreData ??= new CoreInstance[16]; // plain fields: remade after a play-mode script reload
        _coreProps ??= new MaterialPropertyBlock();

        Vector3 eye = SimulationTicker.CameraPosition;
        float now = Time.time;
        int n = 0;
        var bounds = new Bounds();
        foreach (Rope r in _ropes)
            for (int end = 0; end < 2; end++)
            {
                if (!(end == 0 ? r.BaseA : r.BaseB)) continue;
                Anchor an = end == 0 ? r.a : r.b;
                int handle = r.id * 2 + end;
                float scale = end == 0 ? r.scaleA : r.scaleB;
                float radius = ropeRadius * baseCoreSize * scale;
                Vector3 p = an.RenderPoint;
                if (!SimulationTicker.OnScreen(p, radius)) continue;

                if (!_baseSight.TryGetValue(handle, out Vector2 sight) || now >= sight.x)
                {
                    bool hidden = Occluded(eye, p + an.RenderNormal * (ropeRadius * 2f));
                    sight = new Vector2(now + BaseSightInterval * Random.Range(0.8f, 1.2f), hidden ? 1f : 0f);
                    _baseSight[handle] = sight;
                }
                if (sight.y > 0.5f && HoveredBase != handle) continue;

                Color c = r.Cauterizing ? TerminalUI.Blood : r.straight ? TerminalUI.Live : TerminalUI.Line;
                float hover = hoverScale > 1f ? Mathf.Clamp01((scale - 1f) / (hoverScale - 1f)) : 0f;
                if (n == _coreData.Length) System.Array.Resize(ref _coreData, n * 2);
                _coreData[n++] = new CoreInstance
                {
                    positionScale = new Vector4(p.x, p.y, p.z, radius),
                    color = new Vector4(c.r, c.g, c.b, hover),
                    state = new Vector4(0f, r.straight ? 0.5f : 0f, r.id * 0.37f + end * 0.5f, 0f), // full, pulse speed, seed
                };
                var b = new Bounds(p, Vector3.one * (radius * 3f));
                if (n == 1) bounds = b; else bounds.Encapsulate(b);
            }
        if (n == 0) return;

        if (_coreBuffer == null || !_coreBuffer.IsValid() || _coreBuffer.count < n)
        {
            _coreBuffer?.Release();
            _coreBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _coreData.Length, CoreStride);
        }
        _coreBuffer.SetData(_coreData, 0, 0, n);
        _coreProps.SetBuffer(CoresId, _coreBuffer);
        var rp = new RenderParams(_coreMat)
        {
            worldBounds = bounds, matProps = _coreProps, receiveShadows = false,
            shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off,
        };
        Graphics.RenderMeshPrimitives(rp, ChunkMesh.Ball(), 0, n);
    }

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// Standing: start a rope under the player, or pin the active rope's end
    /// to the ground. A loose piece in hand is dropped and a new rope starts.
    /// </summary>
    public bool PlaceAnchor()
    {
        if (!IsGrounded) return false;
        return _active == null ? StartNewRope() : FinishActiveRope();
    }

    /// <summary>
    /// Have a rope in hand to pay out: the held one, else a new one, anchored under the
    /// player when standing, or trailing a loose end in the air.
    /// </summary>
    public bool StartFromSpool()
    {
        if (_active != null) return true;
        return IsGrounded ? StartNewRope() : StartFreeRope();
    }

    /// <summary>
    /// A big tug on the held rope: its far end tears off whatever it was anchored to (which
    /// gets a jerk toward the player) and the rope whips in, fastest at the far end.
    /// </summary>
    public bool Tug()
    {
        Rope r = _active;
        if (r == null || r.n < 2) return false;

        // Nothing at the far end to tear loose: slurp it all in.
        if (!r.pinA) r.slurping = true;

        Vector3 player = PlayerSimPoint();
        if (r.pinA)
        {
            Vector3 at = r.a.SimPoint;
            if (Pullable(r.a.body) && tugImpulse > 0f)
                r.a.body.AddForceAtPosition(SafeDir(player - at, Vector3.zero) * tugImpulse, at, ForceMode.Impulse);
            r.pinA = false;
            r.a.Clear();
            r.suspended = true; // keeps its settings for when it's placed again
            r.scaleA = 1f;
        }

        // Velocity is x - prev per step: set it toward the player.
        float dt = Mathf.Max(Time.fixedDeltaTime, 1e-4f);
        int last = r.n - 1;
        for (int i = 0; i < last; i++)
        {
            float t = 1f - (float)i / last; // 1 at the far end
            Vector3 dir = SafeDir(player - r.x[i], Vector3.zero);
            r.prev[i] = r.x[i] - dir * (tugSpeed * t * dt);
        }

        r.sleeping = false;
        r.sleepTimer = 0f;
        r.peakTension = r.endTension = 0f;
        r.graceUntil = Time.time + BREAK_GRACE * 3f; // the yank itself mustn't snap it
        r.topologyDirty = r.meshDirty = true;
        return true;
    }

    /// <summary>Where the rope leaves the player.</summary>
    public Vector3 PlayerPoint => PlayerRenderPoint;

    /// <summary>Let go of the held rope: its player end stays where it is, loose (grabbable again).</summary>
    public bool ReleaseHeld()
    {
        Rope r = _active;
        if (r == null) return false;
        r.pinB = r.bIsPlayer = false;
        r.b.Clear();
        r.reelSpeed = 0f;
        r.slurping = false;
        r.sleeping = false;
        r.graceUntil = Time.time + BREAK_GRACE;
        r.topologyDirty = r.meshDirty = true;
        _active = null;
        return true;
    }

    /// <summary>Throw away the held rope entirely (e.g. one a click only just started).</summary>
    public void DiscardHeld()
    {
        int i = _active != null ? _ropes.IndexOf(_active) : -1;
        if (i >= 0) DestroyRopeAt(i);
    }

    /// <summary>Handle of the anchored (base) end within baseGrabRadius of the player's body surface; -1 if none.</summary>
    public int BaseEndInReach()
    {
        Vector3 centre = playerRopePoint.position;
        float radius = baseGrabRadius + BodyRadius;
        int best = -1;
        float bestSq = radius * radius;
        foreach (Rope r in _ropes)
        {
            if (r == _active || r.Cauterizing) continue;
            for (int end = 0; end < 2; end++)
            {
                if (!(end == 0 ? r.BaseA : r.BaseB)) continue;
                float d = ((end == 0 ? r.a.RenderPoint : r.b.RenderPoint) - centre).sqrMagnitude;
                if (d >= bestSq) continue;
                bestSq = d;
                best = r.id * 2 + end;
            }
        }
        return best;
    }

    /// <summary>Take any rope end in hand: a loose one as it is, an anchored one pulled off its surface first.</summary>
    public bool GrabEnd(int handle) => Grab(handle) || GrabBase(handle);

    /// <summary>Pull an anchored end off its surface and take it in hand, like a loose end.</summary>
    public bool GrabBase(int handle)
    {
        if (_active != null || handle < 0) return false;
        Rope r = Find(handle >> 1);
        bool endB = (handle & 1) == 1;
        if (r == null || !(endB ? r.BaseB : r.BaseA) || r.Cauterizing) return false;

        if (endB) { r.pinB = false; r.b.Clear(); r.scaleB = 1f; }
        else { r.pinA = false; r.a.Clear(); r.scaleA = 1f; }
        r.suspended = true; // keeps its settings (straighten, length) for when it's placed again
        if (Grab(handle)) return true;

        r.topologyDirty = true; // too long to take: it just comes loose
        return false;
    }

    const float LeanLookAhead = 2f; // metres along the rope that say which way it runs off

    /// <summary>
    /// While the held rope is being reeled in, paid out or slurped: the mouth's direction (world),
    /// the way the rope runs off from it, and how strongly that's happening (0-1), for a
    /// controller to turn the body toward (Organism.Lean). False when nothing's moving it.
    /// </summary>
    public bool LeanRequest(out Vector3 mouth, out Vector3 toward, out float strength)
    {
        mouth = toward = Vector3.zero;
        strength = 0f;
        Rope r = _active;
        if (r == null || !frontOnly || r.n < 2) return false;

        strength = r.slurping || _feedHeld ? 1f : Mathf.Clamp01(r.reelSpeed / Mathf.Max(retractSpeed, 1e-3f));
        if (strength <= 0.01f) return false;

        int k = Mathf.Max(0, r.n - 1 - Mathf.Max(1, Mathf.RoundToInt(LeanLookAhead / particleSpacing)));
        Vector3 off = r.x[k] - PlayerRenderPoint;
        if (off.sqrMagnitude < 1e-6f) return false;
        mouth = MouthDirRender;
        toward = off;
        return true;
    }

    /// <summary>Loose end within grabRadius of the player's body surface (not just the mouth, so one beside or behind counts); -1 if none.</summary>
    public int LooseEndInReach() => LooseEndNear(playerRopePoint.position, grabRadius + BodyRadius);

    /// <summary>Handle (rope id * 2 + end) of the loose, unanchored rope end nearest 'point' within 'radius'; -1 if none. The held rope doesn't count.</summary>
    public int LooseEndNear(Vector3 point, float radius)
    {
        int best = -1;
        float bestSq = radius * radius;
        foreach (Rope r in _ropes)
        {
            if (r == _active) continue;
            for (int end = 0; end < 2; end++)
            {
                if (end == 0 ? r.pinA : r.pinB) continue;
                float d = ((end == 0 ? r.x[0] : r.x[r.n - 1]) - point).sqrMagnitude;
                if (d >= bestSq) continue;
                bestSq = d;
                best = r.id * 2 + end;
            }
        }
        return best;
    }

    /// <summary>Loose end shown as a ghost rope reaching to the player; -1 for none. Hidden while a rope is held.</summary>
    public int PhantomEnd { get; set; } = -1;

    /// <summary>
    /// Take a loose end in hand: the gap to the player becomes rope, and it's the held
    /// rope from then on (reel, pay out, anchor), as if it had come off the spool.
    /// </summary>
    public bool Grab(int handle)
    {
        if (_active != null || handle < 0) return false;
        Rope r = Find(handle >> 1);
        bool endB = (handle & 1) == 1;
        if (r == null || (endB ? r.pinB : r.pinA)) return false;

        Vector3 player = PlayerSimPoint();
        if (r.length + Vector3.Distance(endB ? r.x[r.n - 1] : r.x[0], player) > maxRopeLength) return false;

        if (!endB) Reverse(r); // the held end is always B
        Bridge(r, player);

        r.pinB = r.bIsPlayer = true;
        r.b.Clear();
        r.lastB = player;
        r.reelSpeed = 0f;
        r.sleeping = false;
        r.sleepTimer = 0f;
        r.peakTension = r.endTension = 0f;
        r.graceUntil = Time.time + BREAK_GRACE;
        r.topologyDirty = r.meshDirty = true;
        _active = r;
        return true;
    }

    // A base is one anchored end of one rope: handle = rope id * 2 + (0 start, 1 end).

    /// <summary>Handle of the base (either anchored end of any rope) nearest a screen point within radiusPx and not hidden behind something; -1 if none.</summary>
    public int BaseAt(Camera cam, Vector2 screenPoint, float radiusPx)
    {
        if (!cam) return -1;
        int best = -1;
        float bestSq = radiusPx * radiusPx;
        Vector3 eye = cam.transform.position;

        foreach (Rope r in _ropes)
            for (int end = 0; end < 2; end++)
            {
                if (!(end == 0 ? r.BaseA : r.BaseB)) continue;
                Anchor an = end == 0 ? r.a : r.b;
                Vector3 basePoint = an.RenderPoint;
                Vector3 sp = cam.WorldToScreenPoint(basePoint);
                if (sp.z <= 0f) continue;

                float d = ((Vector2)sp - screenPoint).sqrMagnitude;
                if (d >= bestSq || Occluded(eye, basePoint + an.RenderNormal * (ropeRadius * 2f))) continue;
                bestSq = d;
                best = r.id * 2 + end;
            }
        return best;
    }

    /// <summary>Handle of the base shown enlarged; -1 for none.</summary>
    public int HoveredBase { get; set; } = -1;

    /// <summary>Show every base's core (VirusMovement: while bases are clickable, in focus mode).</summary>
    public bool ShowBaseCores { get; set; }

    /// <summary>Switch the rope a base belongs to between hanging and standing up. Returns the new state.</summary>
    public bool ToggleStraight(int handle)
    {
        Rope r = handle < 0 ? null : Find(handle >> 1);
        if (r == null || !((handle & 1) == 1 ? r.BaseB : r.BaseA)) return false;
        if (r.Cauterizing) return r.straight;

        r.straight = !r.straight;
        r.sleeping = false;
        r.sleepTimer = 0f;
        return r.straight;
    }

    public bool IsStraight(int handle)
    {
        Rope r = handle < 0 ? null : Find(handle >> 1);
        return r != null && r.straight;
    }

    /// <summary>True while the handle still names an anchored end (its rope may have been cut, reeled in or pulled up).</summary>
    public bool IsBase(int handle)
    {
        Rope r = handle < 0 ? null : Find(handle >> 1);
        return r != null && ((handle & 1) == 1 ? r.BaseB : r.BaseA);
    }

    /// <summary>Where a base sits (interpolated, for UI).</summary>
    public Vector3 BasePoint(int handle)
    {
        Rope r = handle < 0 ? null : Find(handle >> 1);
        if (r == null) return Vector3.zero;
        return (handle & 1) == 1 ? r.b.RenderPoint : r.a.RenderPoint;
    }

    /// <summary>Length of the rope a handle belongs to: where it's heading if it's being resized, else what it is. 0 if gone.</summary>
    public float LengthOf(int handle)
    {
        Rope r = handle < 0 ? null : Find(handle >> 1);
        return r == null ? 0f : r.targetLength > 0f ? r.targetLength : r.length;
    }

    /// <summary>
    /// Ease the rope a handle belongs to toward a new length (at feedSpeed), keeping its shape.
    /// A straightened rod pushes and turns its bodies until it fits. Paying out or reeling the
    /// held rope takes over again.
    /// </summary>
    public void SetLength(int handle, float meters)
    {
        Rope r = handle < 0 ? null : Find(handle >> 1);
        if (r == null || r.Cauterizing) return;
        r.targetLength = Mathf.Clamp(meters, Mathf.Max(MinRopeLength, particleSpacing * 2f), maxRopeLength);
        r.sleeping = false;
        r.sleepTimer = 0f;
    }

    /// <summary>A straightened rope between two bases, not already cauterizing.</summary>
    public bool CanCauterize(int handle)
    {
        Rope r = handle < 0 ? null : Find(handle >> 1);
        return r != null && !r.Cauterizing && r.Straight && r.BaseA && r.BaseB && IsBase(handle);
    }

    /// <summary>
    /// Pump blood up a straightened rope from this base: it swells as the front climbs to the other
    /// base, then freezes into a static strut that rigidly joins the two bodies (a FixedJoint).
    /// Its straighten and length settings are locked from then on.
    /// </summary>
    public bool Cauterize(int handle)
    {
        if (!CanCauterize(handle)) return false;
        Rope r = Find(handle >> 1);
        r.pumpFrom = handle & 1;
        r.pump = 0f;
        r.targetLength = 0f;
        r.meshKey = -1; // rebuild with the blood submesh
        return true;
    }

    /// <summary>-1 not cauterized, 0..1 blood climbing, 1 sealed.</summary>
    public float CauterizeProgress(int handle)
    {
        Rope r = handle < 0 ? null : Find(handle >> 1);
        if (r == null || !r.Cauterizing) return -1f;
        return r.isSealed ? 1f : Mathf.Clamp01(r.pump / Mathf.Max(r.length, 1e-4f));
    }

    // Filled: freeze the rope onto A's body and weld the bodies together as they are now.
    void Seal(Rope r)
    {
        r.isSealed = true;
        r.sealedAt = Time.time;
        if (r.sealedLocal == null || r.sealedLocal.Length < r.n) r.sealedLocal = new Vector3[r.n];
        for (int i = 0; i < r.n; i++) r.sealedLocal[i] = r.a.ToLocal(r.x[i]);

        Rigidbody ba = r.a.body, bb = r.b.body;
        Rigidbody host = ba == bb ? null : Pullable(ba) ? ba : Pullable(bb) ? bb : null;
        if (host)
        {
            var joint = host.gameObject.AddComponent<FixedJoint>();
            joint.connectedBody = host == ba ? bb : ba; // none: welded to the world (a static surface)
            joint.enableCollision = false;
            r.weld = joint;
        }
    }

    // A cauterized rope lost a base: back to an ordinary rope.
    void Unseal(Rope r)
    {
        if (r.weld) Destroy(r.weld);
        r.weld = null;
        r.pumpFrom = -1;
        r.pump = 0f;
        r.isSealed = false;
        r.sleeping = false;
        r.meshKey = -1;
        r.topologyDirty = true;
        if (r.bloodGo) r.bloodGo.SetActive(false);
    }

    Rope Find(int id)
    {
        foreach (Rope r in _ropes) if (r.id == id) return r;
        return null;
    }

    bool Occluded(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        float dist = d.magnitude;
        if (dist < 1e-4f) return false;
        int count = Physics.RaycastNonAlloc(from, d / dist, _hits, dist, surfaceLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
            if (!IsPlayerCollider(_hits[i].collider)) return true;
        return false;
    }

    public void ClearAllRopes()
    {
        foreach (var r in _ropes) DestroyVisual(r);
        _ropes.Clear();
        _active = null;
    }

    public void RemoveLastRope()
    {
        if (_ropes.Count > 0) DestroyRopeAt(_ropes.Count - 1);
    }

    /// <summary>Call if colliders are added to or removed from the player at runtime.</summary>
    public void RefreshPlayerColliders()
    {
        _playerCols.Clear();
        foreach (var c in GetComponentsInChildren<Collider>(true)) _playerCols.Add(c);
        if (_playerBody)
            foreach (var c in _playerBody.GetComponentsInChildren<Collider>(true)) _playerCols.Add(c);

        // Body radius for the mouth: how far the solid colliders reach from the rope point.
        float radius = 0f;
        Vector3 centre = playerRopePoint ? playerRopePoint.position : transform.position;
        foreach (Collider c in _playerCols)
        {
            if (!c || c.isTrigger || !c.enabled) continue;
            Bounds b = c.bounds;
            radius = Mathf.Max(radius, Vector3.Distance(centre, b.center) + Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)));
        }
        if (radius > 0f) _autoBodyRadius = radius;
    }

    // ------------------------------------------------------------------ rope lifecycle

    bool StartNewRope()
    {
        if (!TryGetGroundAnchor(out Transform support, out Rigidbody body, out Vector3 point, out Vector3 normal))
            return false;

        while (_ropes.Count >= maxRopes && _ropes.Count > 0)
            DestroyRopeAt(0); // oldest (the active rope is always null here)

        Rope r = CreateRope(SegmentCapacity);
        r.a.Set(support, body, point, normal);
        r.pinA = r.pinB = r.bIsPlayer = true;

        Lay(r, point, PlayerSimPoint(), normal);
        Anchored?.Invoke(point, normal);
        return true;
    }

    // In the air: a short rope off the spool, its far end loose, trailing behind.
    bool StartFreeRope()
    {
        while (_ropes.Count >= maxRopes && _ropes.Count > 0)
            DestroyRopeAt(0);

        Rope r = CreateRope(SegmentCapacity);
        r.pinB = r.bIsPlayer = true; // A hangs free

        Vector3 b = PlayerSimPoint();
        Vector3 v = _playerBody ? _playerBody.linearVelocity : Vector3.zero;
        Vector3 away = frontOnly ? MouthDirSim : v.sqrMagnitude > 0.01f ? -v.normalized : -transform.forward;
        Lay(r, b + away * Mathf.Max(MinRopeLength, particleSpacing * 2f), b, transform.up);
        return true;
    }

    // Lay a new held rope from 'point' to the player at 'b' and hold it.
    void Lay(Rope r, Vector3 point, Vector3 b, Vector3 normal)
    {
        float minLen = Mathf.Max(MinRopeLength, particleSpacing * 2f);
        float len = Mathf.Clamp(Vector3.Distance(point, b), minLen, Mathf.Max(maxRopeLength, minLen));
        int segs = Mathf.Clamp(Mathf.RoundToInt(len / particleSpacing), 2, r.capacity);

        // Slight bow so the initial slack isn't an unstable straight line.
        Vector3 bow = Perpendicular(SafeDir(b - point, normal)) * Mathf.Min(particleSpacing * 0.25f, ropeRadius * 2f);

        r.n = segs + 1;
        for (int i = 0; i < r.n; i++)
        {
            float t = (float)i / segs;
            r.x[i] = r.prev[i] = r.start[i] = Vector3.Lerp(point, b, t) + bow * Mathf.Sin(t * Mathf.PI);
        }
        for (int s = 0; s < segs; s++) r.rest[s] = len / segs;

        r.length = len;
        r.lastA = point;
        r.lastB = b;
        r.graceUntil = Time.time + BREAK_GRACE;

        _ropes.Add(r);
        _active = r;
    }

    // Flip a rope end for end, so what was its start is now its end (B, the held end).
    static void Reverse(Rope r)
    {
        int n = r.n, segs = n - 1;
        System.Array.Reverse(r.x, 0, n);
        System.Array.Reverse(r.prev, 0, n);
        System.Array.Reverse(r.start, 0, n);
        System.Array.Reverse(r.contactNormal, 0, n);
        System.Array.Reverse(r.contactVel, 0, n);
        System.Array.Reverse(r.contactDepth, 0, n);
        System.Array.Reverse(r.contact, 0, n);
        System.Array.Reverse(r.rest, 0, segs);
        System.Array.Reverse(r.tension, 0, segs);

        var a = new Anchor();
        a.CopyFrom(r.a);
        r.a.CopyFrom(r.b);
        r.b.CopyFrom(a);
        (r.pinA, r.pinB) = (r.pinB, r.pinA);
        (r.scaleA, r.scaleB) = (r.scaleB, r.scaleA);
        (r.lastA, r.lastB) = (r.lastB, r.lastA);
        r.frameSeeded = false;
        r.topologyDirty = r.meshDirty = true;
    }

    // Extend the rope's end straight to 'to' with new particles, so taking a loose end
    // from a distance turns the gap into rope instead of yanking the end across it.
    void Bridge(Rope r, Vector3 to)
    {
        Vector3 from = r.x[r.n - 1];
        float dist = Vector3.Distance(from, to);
        int add = Mathf.CeilToInt(dist / particleSpacing - 0.25f);
        if (add <= 0) return;

        int need = r.n - 1 + add;
        if (need > r.capacity) r.Grow(Mathf.Min(HARD_MAX_SEGMENTS, Mathf.Max(need, r.capacity * 2)));
        add = Mathf.Min(add, r.capacity - (r.n - 1));

        for (int k = 1; k <= add; k++)
        {
            int i = r.n;
            Vector3 p = Vector3.Lerp(from, to, (float)k / add);
            r.x[i] = r.prev[i] = r.start[i] = p;
            r.contact[i] = false;
            r.contactDepth[i] = 0f;
            r.rest[i - 1] = dist / add;
            r.tension[i - 1] = 0f;
            r.n++;
        }
        SyncLength(r);
    }

    bool FinishActiveRope()
    {
        if (!TryGetGroundAnchor(out Transform support, out Rigidbody body, out Vector3 point, out Vector3 normal))
            return false;

        Rope r = _active;
        r.b.Set(support, body, point, normal);
        r.bIsPlayer = false;
        r.suspended = false; // placed: its settings apply again
        r.reelSpeed = 0f;
        r.lastB = r.x[r.n - 1]; // glide onto the anchor over one step
        r.graceUntil = Time.time + BREAK_GRACE;
        r.topologyDirty = true;
        _active = null;
        Anchored?.Invoke(point, normal);
        return true;
    }

    Rope CreateRope(int capacity)
    {
        var r = new Rope(capacity) { id = ++_nextId };
        r.go = new GameObject("Rope");
        r.go.transform.SetParent(_root, false);
        r.go.AddComponent<MeshFilter>().sharedMesh = r.mesh = new Mesh { name = "Rope Mesh" };
        r.mesh.MarkDynamic();
        r.meshRenderer = r.go.AddComponent<MeshRenderer>();
        r.meshRenderer.sharedMaterial = ActiveMaterial();
        return r;
    }

    void DestroyRopeAt(int index)
    {
        if (_ropes[index] == _active) _active = null;
        DestroyVisual(_ropes[index]);
        _ropes.RemoveAt(index);
    }

    static void DestroyVisual(Rope r)
    {
        if (r.weld) Destroy(r.weld);
        if (r.mesh) Destroy(r.mesh);
        if (r.go) Destroy(r.go);
        if (r.bloodMesh) Destroy(r.bloodMesh);
        if (r.bloodGo) Destroy(r.bloodGo);
    }

    // ------------------------------------------------------------------ step

    void StepRope(int index, Rope r, float dt)
    {
        if (r.pinA && r.a.Lost) { r.pinA = false; r.sleeping = false; }
        if (r.pinB && !r.bIsPlayer && r.b.Lost) { r.pinB = false; r.sleeping = false; }
        if (r.Cauterizing && !(r.BaseA && r.BaseB)) Unseal(r);

        // Sealed: a static strut riding A's body (the weld holds B in place).
        if (r.isSealed)
        {
            for (int i = 0; i < r.n; i++) r.x[i] = r.prev[i] = r.start[i] = r.a.SimFromLocal(r.sealedLocal[i]);
            r.lastA = r.x[0];
            r.lastB = r.x[r.n - 1];
            return;
        }

        Vector3 a =r.pinA ? r.a.SimPoint : r.x[0];
        Vector3 b = r.pinB ? (r.bIsPlayer ? PlayerSimPoint() : r.b.SimPoint) : r.x[r.n - 1];

        float lengthBefore = r.length;
        if (r == _active && UpdateSpool(r, b, dt))
        {
            Spooled?.Invoke(r.length - lengthBefore);
            DestroyRopeAt(index); // fully reeled in
            Stowed?.Invoke();
            return;
        }
        if (r == _active && r.length != lengthBefore) Spooled?.Invoke(r.length - lengthBefore);
        if (r.length != lengthBefore) r.targetLength = 0f; // reeled or paid out by hand: that's its length now
        if (r.targetLength > 0f && !r.suspended) AdjustLength(r, dt);

        float stillSq = SLEEP_SPEED * dt * SLEEP_SPEED * dt;
        bool endsMoved = (r.pinA && (a - r.lastA).sqrMagnitude > stillSq) ||
                         (r.pinB && (b - r.lastB).sqrMagnitude > stillSq);

        if (r.sleeping)
        {
            bool wake = endsMoved || r == _active;
            if (!wake && ((_stepCounter + index) & 3) == 0)
                using (s_wakeMarker.Auto())
                    wake = DynamicBodyNear(r);
            if (!wake) return;
            r.sleeping = false;
            r.sleepTimer = 0f;
        }

        int n = r.n, segs = n - 1, sub = Substeps;
        float h = dt / sub;
        System.Array.Copy(r.x, r.start, n);

        if (r.topologyDirty || r.stepsSinceGather >= GATHER_INTERVAL || r.drift > GATHER_MARGIN)
        {
            using (s_gatherMarker.Auto())
                GatherPairs(r, a, b, sub);
            r.stepsSinceGather = 0;
            r.drift = 0f;
            r.topologyDirty = false;
        }
        else
        {
            r.stepsSinceGather++;
            RefreshDynamicShapes(r);
        }

        System.Array.Clear(r.tension, 0, segs);

        float retain = Mathf.Exp(-damping * 4.5f * h);
        float bendK = 1f - Mathf.Exp(-smoothness * smoothness * 60f * h);
        float gripKeep = Mathf.Exp(-surfaceGrip * surfaceGrip * 30f * h);
        float ea = Mathf.Pow(10f, Mathf.Lerp(6f, 2.3f, elasticity)); // axial stiffness EA (N)
        float w = 1f / ParticleMass;
        float invH2 = 1f / (h * h);

        float playerW = r.pinB && r.bIsPlayer && _playerBody && !_playerBody.isKinematic && playerTug > 0f
            ? playerTug / Mathf.Max(0.01f, _playerBody.mass)
            : 0f;
        r.playerCorr = Vector3.zero;

        bool rod = r.Straight && r.BaseA && r.BaseB;


        bool upA = r.Straight && r.BaseA && straightenStrength > 0f;
        bool upB = r.Straight && r.BaseB && straightenStrength > 0f;
        Vector3 dirA = upA ? SafeDir(r.a.SimNormal, Vector3.up) : Vector3.up;
        Vector3 dirB = upB ? SafeDir(r.b.SimNormal, Vector3.up) : Vector3.up;
        float uprightK = 1f - Mathf.Exp(-straightenStrength * h);

        s_solveMarker.Begin();
        for (int s = 0; s < sub; s++)
        {
            float t = (s + 1f) / sub;
            Integrate(r, retain);
            if (r.pinA) { r.prev[0] = r.x[0]; r.x[0] = Vector3.Lerp(r.lastA, a, t); }
            if (r.pinB) { r.prev[segs] = r.x[segs]; r.x[segs] = Vector3.Lerp(r.lastB, b, t) + r.playerCorr; }
            if (upA || upB) PullUpright(r, upA, dirA, upB, dirB, uprightK);

            bool forward = (s & 1) == 0;
            SolveDistances(r, forward, w, playerW, invH2, ea);
            if (bendK > 0f) SolveBend(r, forward, bendK);
            if (r.pairCount > 0)
            {
                s_collideMarker.Begin();
                SolveCollisions(r, h);
                ApplyContactVelocity(r, h, gripKeep);
                s_collideMarker.End();
            }
        }
        s_solveMarker.End();

        using (s_sweepMarker.Auto())
            SweepFastParticles(r);

        // Tug the player: the overshoot the rope resisted becomes velocity. Velocity only, not a
        // position shift too: moving the body directly skips collisions, so a rope running into
        // the planet dragged the player into it and the physics threw it back out.
        if (playerW > 0f)
        {
            Vector3 c = Vector3.ClampMagnitude(r.playerCorr, MAX_TUG_SPEED * dt);
            if (c.sqrMagnitude > 1e-12f) _playerBody.AddForce(c / dt, ForceMode.VelocityChange);
            r.x[segs] = b;
        }

        // Step-averaged tension -> filtered peak.
        float maxT = 0f;
        int maxSeg = -1;
        for (int s = 0; s < segs; s++)
        {
            float T = r.tension[s] /= sub;
            if (T > maxT) { maxT = T; maxSeg = s; }
        }

        // Break load averages over breakDelay so short jolts don't snap; reel stall stays responsive.
        r.peakTension += (maxT - r.peakTension) * (1f - Mathf.Exp(-dt / breakDelay));
        r.endTension += (Mathf.Max(0f, r.tension[segs - 1]) - r.endTension) * (1f - Mathf.Exp(-dt / TENSION_FILTER));

        r.lastA = r.pinA ? a : r.x[0];
        r.lastB = r.pinB ? b : r.x[segs];

        ApplyAnchorForces(r);
        if (rod) RodForces(r, a, b);

        // Sleep bookkeeping.
        float maxDispSq = 0f;
        for (int i = 0; i < n; i++) maxDispSq = Mathf.Max(maxDispSq, (r.x[i] - r.start[i]).sqrMagnitude);

        r.drift += Mathf.Sqrt(maxDispSq);
        r.meshDirty = true;

        if (r != _active && !endsMoved && maxDispSq < stillSq)
        {
            if ((r.sleepTimer += dt) > SLEEP_DELAY)
            {
                r.sleeping = true;
                for (int i = 0; i < n; i++) r.prev[i] = r.start[i] = r.x[i];
            }
        }
        else r.sleepTimer = 0f;

        if (breakForce > 0f && !rod && maxSeg >= 0 && Time.time >= r.graceUntil && r.peakTension > breakForce)
            Split(index, r, maxSeg);
    }

    // ------------------------------------------------------------------ solver

    static void Integrate(Rope r, float retain)
    {
        Vector3[] x = r.x, pv = r.prev;
        int last = r.pinB ? r.n - 2 : r.n - 1;
        for (int i = r.pinA ? 1 : 0; i <= last; i++)
        {
            Vector3 p = x[i], q = pv[i];
            pv[i] = p;
            p.x += (p.x - q.x) * retain;
            p.y += (p.y - q.y) * retain;
            p.z += (p.z - q.z) * retain;
            x[i] = p;
        }
    }

    // Straighten mode: nudge each free particle toward its spot on a line straight out of a base,
    // at its rest distance along the rope from that base. A single base owns the whole rope;
    // with both ends anchored, each owns its half. Moving x without prev acts as a force; the distance
    // and collision passes still have the last word.
    static void PullUpright(Rope r, bool fromA, Vector3 upA, bool fromB, Vector3 upB, float k)
    {
        Vector3[] x = r.x;

        // Two bases: a straight line between them, spaced like the rope.
        if (fromA && fromB)
        {
            Vector3 a = x[0], b = x[r.n - 1];
            float total = Mathf.Max(r.length, 1e-4f), at = 0f;
            for (int i = 1; i < r.n - 1; i++)
            {
                at += r.rest[i - 1];
                x[i] += (Vector3.LerpUnclamped(a, b, at / total) - x[i]) * k;
            }
            return;
        }

        int first = r.pinA ? 1 : 0, last = r.pinB ? r.n - 2 : r.n - 1;
        int split = fromA && fromB ? (r.n - 1) / 2 : fromA ? last : first - 1; // A owns first..split

        float along = 0f;
        if (fromA)
            for (int i = first; i <= split; i++)
            {
                along += r.rest[i - 1];
                x[i] += (x[0] + upA * along - x[i]) * k;
            }

        along = 0f;
        if (fromB)
            for (int i = last; i > split; i--)
            {
                along += r.rest[i];
                x[i] += (x[r.n - 1] + upB * along - x[i]) * k;
            }
    }

    // XPBD distance constraints. Tension (N) = -lambda / h^2.
    static void SolveDistances(Rope r, bool forward, float w, float wPlayer, float invH2, float ea)
    {
        Vector3[] x = r.x;
        float[] rest = r.rest, ten = r.tension;
        int segs = r.n - 1;
        float compliance = invH2 / ea;
        float wFirst = r.pinA ? 0f : w;
        float wLast = r.pinB ? (r.bIsPlayer ? wPlayer : 0f) : w;
        bool trackPlayer = r.bIsPlayer && wLast > 0f;

        for (int k = 0; k < segs; k++)
        {
            int i = forward ? k : segs - 1 - k, j = i + 1;
            float wi = i == 0 ? wFirst : w;
            float wj = j == segs ? wLast : w;

            Vector3 xi = x[i], xj = x[j];
            float dx = xj.x - xi.x, dy = xj.y - xi.y, dz = xj.z - xi.z;
            float len2 = dx * dx + dy * dy + dz * dz;
            if (len2 < 1e-14f) continue;

            float len = Mathf.Sqrt(len2);
            float L = rest[i];
            float C = len - L;

            if (wi + wj <= 0f)
            {
                if (C > 0f) ten[i] += C * ea / Mathf.Max(L, 1e-4f);
                continue;
            }

            float dl = -C / (wi + wj + L * compliance);
            float f = dl / len;
            float cx = dx * f, cy = dy * f, cz = dz * f;

            if (wi > 0f) { xi.x -= cx * wi; xi.y -= cy * wi; xi.z -= cz * wi; x[i] = xi; }
            if (wj > 0f)
            {
                xj.x += cx * wj; xj.y += cy * wj; xj.z += cz * wj; x[j] = xj;
                if (trackPlayer && j == segs) r.playerCorr += new Vector3(cx, cy, cz) * wj;
            }

            ten[i] -= dl * invH2;
        }
    }

    // Momentum-conserving Laplacian bending.
    void SolveBend(Rope r, bool forward, float k)
    {
        int last = r.n - 1;
        if (last < 2) return;

        Vector3[] x = r.x;
        float maxMove = particleSpacing * 0.25f;

        for (int q = 1; q < last; q++)
        {
            int i = forward ? q : last - q;
            if (r.contact[i]) continue; // keep wrap corners sharp

            float wa = i == 1 && r.pinA ? 0f : 1f;
            float wc = i + 1 == last && r.pinB ? 0f : 1f;

            Vector3 xa = x[i - 1], xb = x[i], xc = x[i + 1];
            Vector3 d = Vector3.ClampMagnitude(((xa + xc) * 0.5f - xb) * (k / (1f + 0.25f * (wa + wc))), maxMove);

            x[i] = xb + d;
            if (wa > 0f) x[i - 1] = xa - d * 0.5f;
            if (wc > 0f) x[i + 1] = xc - d * 0.5f;
        }
    }

    // ------------------------------------------------------------------ collision: broadphase

    void GatherPairs(Rope r, Vector3 a, Vector3 b, int sub)
    {
        r.pairCount = 0;
        r.shapeCount = 0;
        _shapeLookup.Clear();

        int segs = r.n - 1;
        float basePad = ropeRadius + Skin + GATHER_MARGIN;
        Vector3 gMin = Vector3.one * float.MaxValue, gMax = Vector3.one * float.MinValue;

        for (int c0 = 0; c0 < segs; c0 += CHUNK)
        {
            int c1 = Mathf.Min(segs, c0 + CHUNK);
            Vector3 mn = r.x[c0], mx = mn;
            float motionSq = 0f;

            for (int i = c0; i <= c1; i++)
            {
                mn = Vector3.Min(mn, r.x[i]);
                mx = Vector3.Max(mx, r.x[i]);
                motionSq = Mathf.Max(motionSq, (r.x[i] - r.prev[i]).sqrMagnitude);
            }
            if (c0 == 0) { mn = Vector3.Min(mn, Vector3.Min(a, r.lastA)); mx = Vector3.Max(mx, Vector3.Max(a, r.lastA)); }
            if (c1 == segs) { mn = Vector3.Min(mn, Vector3.Min(b, r.lastB)); mx = Vector3.Max(mx, Vector3.Max(b, r.lastB)); }

            Vector3 pad = Vector3.one * (basePad + Mathf.Sqrt(motionSq) * sub);
            mn -= pad;
            mx += pad;
            gMin = Vector3.Min(gMin, mn);
            gMax = Vector3.Max(gMax, mx);

            int count = Physics.OverlapBoxNonAlloc((mn + mx) * 0.5f, (mx - mn) * 0.5f, _overlap,
                Quaternion.identity, surfaceLayers, QueryTriggerInteraction.Ignore);

            for (int k = 0; k < count; k++)
            {
                Collider col = _overlap[k];
                if (!col || col is TerrainCollider || IsPlayerCollider(col)) continue;

                if (!_shapeLookup.TryGetValue(col, out int si))
                    _shapeLookup[col] = si = AddShape(r, col);

                Vector3 cmin = r.shapes[si].bmin - pad, cmax = r.shapes[si].bmax + pad;

                for (int s = c0; s < c1; s++)
                {
                    if (s == 0 && r.pinA && BelongsToAnchor(col, r.a)) continue;
                    if (s == segs - 1 && r.pinB && !r.bIsPlayer && BelongsToAnchor(col, r.b)) continue;
                    // The segment into the player: the mouth may sit in the ground (below the body,
                    // facing down), and pushing a segment pinned there out of it only fights.
                    if (s == segs - 1 && r.pinB && r.bIsPlayer) continue;
                    if (!SegmentOverlaps(r.x[s], r.x[s + 1], 0f, cmin, cmax)) continue;
                    if (r.pairCount >= MAX_PAIRS) break;

                    if (r.pairCount == r.pairSeg.Length)
                    {
                        System.Array.Resize(ref r.pairSeg, r.pairCount * 2);
                        System.Array.Resize(ref r.pairShape, r.pairCount * 2);
                    }
                    r.pairSeg[r.pairCount] = s;
                    r.pairShape[r.pairCount++] = si;
                }
            }
        }

        r.boundsMin = gMin;
        r.boundsMax = gMax;
    }

    static bool SegmentOverlaps(Vector3 p0, Vector3 p1, float r, Vector3 mn, Vector3 mx) =>
        Mathf.Max(p0.x, p1.x) + r >= mn.x && Mathf.Min(p0.x, p1.x) - r <= mx.x &&
        Mathf.Max(p0.y, p1.y) + r >= mn.y && Mathf.Min(p0.y, p1.y) - r <= mx.y &&
        Mathf.Max(p0.z, p1.z) + r >= mn.z && Mathf.Min(p0.z, p1.z) - r <= mx.z;

    static int AddShape(Rope r, Collider col)
    {
        if (r.shapeCount == r.shapes.Length) System.Array.Resize(ref r.shapes, r.shapeCount * 2);
        int si = r.shapeCount++;
        r.shapes[si].col = col;
        r.shapes[si].rb = col.attachedRigidbody;
        r.shapes[si].dynamic = r.shapes[si].rb != null;
        FillShape(ref r.shapes[si]);
        return si;
    }

    static void RefreshDynamicShapes(Rope r)
    {
        for (int i = 0; i < r.shapeCount; i++)
        {
            ref Shape sh = ref r.shapes[i];
            if (!sh.dynamic || sh.kind == KIND_DEAD) continue;
            if (sh.col) FillShape(ref sh);
            else sh.kind = KIND_DEAD;
        }
    }

    static void FillShape(ref Shape sh)
    {
        Transform t = sh.col.transform;
        Bounds bb = sh.col.bounds;
        sh.bmin = bb.min;
        sh.bmax = bb.max;
        Vector3 ls = t.lossyScale;
        ls = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));

        switch (sh.col)
        {
            case SphereCollider sc:
                sh.kind = KIND_SPHERE;
                sh.c = t.TransformPoint(sc.center);
                sh.radius = sc.radius * Mathf.Max(ls.x, Mathf.Max(ls.y, ls.z));
                break;

            case CapsuleCollider cc:
            {
                int d = cc.direction;
                float axisScale = ls[d];
                float radScale = Mathf.Max(ls[(d + 1) % 3], ls[(d + 2) % 3]);
                Vector3 axis = Vector3.zero;
                axis[d] = 1f;
                Vector3 c = t.TransformPoint(cc.center);
                Vector3 ax = t.rotation * axis;
                sh.kind = KIND_CAPSULE;
                sh.radius = cc.radius * radScale;
                float halfH = Mathf.Max(0f, cc.height * 0.5f * axisScale - sh.radius);
                sh.a = c - ax * halfH;
                sh.b = c + ax * halfH;
                break;
            }

            case BoxCollider bc:
                sh.kind = KIND_BOX;
                sh.c = t.TransformPoint(bc.center);
                sh.rot = t.rotation;
                sh.inv = Quaternion.Inverse(sh.rot);
                sh.half = Vector3.Scale(bc.size * 0.5f, ls);
                break;

            default:
                sh.kind = KIND_GENERIC;
                sh.c = t.position;
                sh.rot = t.rotation;
                break;
        }
    }

    // ------------------------------------------------------------------ collision: narrowphase

    void SolveCollisions(Rope r, float h)
    {
        int n = r.n, lastP = n - 1;
        System.Array.Clear(r.contact, 0, n);
        System.Array.Clear(r.contactNormal, 0, n);
        System.Array.Clear(r.contactDepth, 0, n);

        float R = ropeRadius + Skin;
        float maxCorr = Mathf.Max(0.05f, particleSpacing * 0.5f);
        float impulseScale = objectForce * ParticleMass / h;
        _probe.radius = R;

        for (int p = 0; p < r.pairCount; p++)
        {
            int i = r.pairSeg[p], j = i + 1;
            if (j > lastP) continue;

            ref Shape sh = ref r.shapes[r.pairShape[p]];
            if (sh.kind == KIND_DEAD) continue;

            bool freeI = !(i == 0 && r.pinA), freeJ = !(j == lastP && r.pinB);
            if (!freeI && !freeJ) continue;

            Vector3 p0 = r.x[i], p1 = r.x[j];
            if (!SegmentOverlaps(p0, p1, R, sh.bmin, sh.bmax)) continue;

            Vector3 nrm;
            float depth;

            if (sh.kind == KIND_GENERIC)
            {
                if (!sh.col) { sh.kind = KIND_DEAD; continue; }
                Vector3 d = p1 - p0;
                float len = d.magnitude;
                _probe.height = len + 2f * R;
                Quaternion rot = len > 1e-6f ? Quaternion.FromToRotation(Vector3.up, d / len) : Quaternion.identity;
                if (!Physics.ComputePenetration(_probe, (p0 + p1) * 0.5f, rot, sh.col, sh.c, sh.rot, out nrm, out depth) ||
                    depth <= 0f)
                    continue;
            }
            else if (!CollideAnalytic(ref sh, p0, p1, R, out nrm, out depth)) continue;

            float applied = Mathf.Min(depth, maxCorr);
            Vector3 corr = nrm * applied;
            Vector3 mid = (p0 + p1) * 0.5f;

            Rigidbody rb = null;
            if (sh.dynamic)
            {
                if (sh.rb) rb = sh.rb;
                else sh.dynamic = false;
            }
            Vector3 surfVel = rb ? rb.GetPointVelocity(mid) : Vector3.zero;

            int moved = 0;
            if (freeI) { r.x[i] = p0 + corr; MarkContact(r, i, nrm, applied, surfVel); moved++; }
            if (freeJ) { r.x[j] = p1 + corr; MarkContact(r, j, nrm, applied, surfVel); moved++; }

            if (rb && rb != _standingOn && !rb.isKinematic && impulseScale > 0f)
                rb.AddForceAtPosition(-corr * (moved * impulseScale), mid - nrm * ropeRadius, ForceMode.Impulse);
        }
    }

    static void MarkContact(Rope r, int i, Vector3 n, float depth, Vector3 surfVel)
    {
        r.contact[i] = true;
        r.contactNormal[i] += n;
        r.contactDepth[i] += depth;
        r.contactVel[i] = surfVel;
    }

    static bool CollideAnalytic(ref Shape sh, Vector3 p0, Vector3 p1, float R, out Vector3 n, out float depth)
    {
        switch (sh.kind)
        {
            case KIND_SPHERE:
                return Resolve(ClosestOnSegment(p0, p1, sh.c) - sh.c, R + sh.radius, out n, out depth);

            case KIND_CAPSULE:
                ClosestSegSeg(p0, p1, sh.a, sh.b, out Vector3 q1, out Vector3 q2);
                return Resolve(q1 - q2, R + sh.radius, out n, out depth);

            default: // box, in local space
            {
                Vector3 l0 = sh.inv * (p0 - sh.c), l1 = sh.inv * (p1 - sh.c), hb = sh.half;
                Vector3 q = (l0 + l1) * 0.5f;
                for (int it = 0; it < 3; it++) q = ClosestOnSegment(l0, l1, ClampBox(q, hb));

                Vector3 d = q - ClampBox(q, hb);
                if (d.sqrMagnitude > 1e-12f)
                {
                    bool hit = Resolve(d, R, out Vector3 ln, out depth);
                    n = sh.rot * ln;
                    return hit;
                }

                // Inside the box: push out through the nearest face.
                Vector3 e = hb - new Vector3(Mathf.Abs(q.x), Mathf.Abs(q.y), Mathf.Abs(q.z));
                int ax = e.x <= e.y && e.x <= e.z ? 0 : e.y <= e.z ? 1 : 2;
                Vector3 fn = Vector3.zero;
                fn[ax] = q[ax] >= 0f ? 1f : -1f;
                n = sh.rot * fn;
                depth = e[ax] + R;
                return true;
            }
        }
    }

    static bool Resolve(Vector3 d, float reach, out Vector3 n, out float depth)
    {
        float sq = d.sqrMagnitude;
        if (sq >= reach * reach) { n = default; depth = 0f; return false; }
        float dist = Mathf.Sqrt(sq);
        n = dist > 1e-6f ? d / dist : Vector3.up;
        depth = reach - dist;
        return true;
    }

    static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float l2 = ab.sqrMagnitude;
        return l2 < 1e-12f ? a : a + ab * Mathf.Clamp01(Vector3.Dot(p - a, ab) / l2);
    }

    static Vector3 ClampBox(Vector3 p, Vector3 h) =>
        new Vector3(Mathf.Clamp(p.x, -h.x, h.x), Mathf.Clamp(p.y, -h.y, h.y), Mathf.Clamp(p.z, -h.z, h.z));

    static void ClosestSegSeg(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, rr = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, rr);
        float s = 0f, t = 0f;

        if (a > 1e-9f || e > 1e-9f)
        {
            if (a <= 1e-9f) t = Mathf.Clamp01(f / e);
            else
            {
                float c = Vector3.Dot(d1, rr);
                if (e <= 1e-9f) s = Mathf.Clamp01(-c / a);
                else
                {
                    float bb = Vector3.Dot(d1, d2), denom = a * e - bb * bb;
                    s = denom > 1e-9f ? Mathf.Clamp01((bb * f - c * e) / denom) : 0f;
                    t = (bb * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((bb - c) / a); }
                }
            }
        }

        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    // Inelastic contact + Coulomb friction (scales with press depth -> capstan grip).
    void ApplyContactVelocity(Rope r, float h, float gripKeep)
    {
        float maxOut = 0.5f * h;
        for (int i = 0; i < r.n; i++)
        {
            if (!r.contact[i]) continue;

            Vector3 nrm = r.contactNormal[i].normalized;
            if (nrm == Vector3.zero) continue;

            Vector3 vs = r.contactVel[i] * h;
            Vector3 rel = r.x[i] - r.prev[i] - vs;
            float vn = Vector3.Dot(rel, nrm);
            Vector3 vt = rel - nrm * vn;

            float vtLen = vt.magnitude;
            if (vtLen > 1e-9f)
                vt *= Mathf.Max(0f, 1f - surfaceGrip * r.contactDepth[i] / vtLen) * gripKeep;

            r.prev[i] = r.x[i] - (vs + nrm * Mathf.Clamp(vn, 0f, maxOut) + vt);
        }
    }

    // Anti-tunnelling for fast particles against static geometry.
    void SweepFastParticles(Rope r)
    {
        float rad = ropeRadius, offset = rad + Skin;
        int last = r.pinB ? r.n - 2 : r.n - 1;

        for (int i = r.pinA ? 1 : 0; i <= last; i++)
        {
            Vector3 from = r.start[i], m = r.x[i] - from;
            float sq = m.sqrMagnitude;
            if (sq < rad * rad) continue;

            float dist = Mathf.Sqrt(sq);
            int count = Physics.RaycastNonAlloc(from, m / dist, _hits, dist + rad, surfaceLayers, QueryTriggerInteraction.Ignore);

            int best = -1;
            for (int k = 0; k < count; k++)
            {
                Collider c = _hits[k].collider;
                if (!c || c.attachedRigidbody || IsPlayerCollider(c)) continue;
                if (best < 0 || _hits[k].distance < _hits[best].distance) best = k;
            }

            if (best >= 0) r.x[i] = r.prev[i] = _hits[best].point + _hits[best].normal * offset;
        }
    }

    bool DynamicBodyNear(Rope r)
    {
        int count = Physics.OverlapBoxNonAlloc((r.boundsMin + r.boundsMax) * 0.5f, (r.boundsMax - r.boundsMin) * 0.5f,
            _overlap, Quaternion.identity, surfaceLayers, QueryTriggerInteraction.Ignore);

        for (int k = 0; k < count; k++)
        {
            Rigidbody rb = _overlap[k].attachedRigidbody;
            if (rb && !rb.IsSleeping() && !IsPlayerCollider(_overlap[k])) return true;
        }
        return false;
    }

    void ApplyAnchorForces(Rope r)
    {
        if (objectForce <= 0f) return;
        int l = r.n - 1;

        // A standing player is fixed to _standingOn, so the player end pulls that body instead.
        Rigidbody bodyB = r.bIsPlayer ? _standingOn : r.b.body;
        if (r.pinA && bodyB == r.a.body) return; // both ends on one body: the forces cancel

        if (r.pinA && Pullable(r.a.body) && r.tension[0] > 0f)
            r.a.body.AddForceAtPosition((r.x[1] - r.x[0]).normalized * (r.tension[0] * objectForce), r.x[0]);

        if (r.pinB && Pullable(bodyB) && r.tension[l - 1] > 0f)
            bodyB.AddForceAtPosition((r.x[l - 1] - r.x[l]).normalized * (r.tension[l - 1] * objectForce), r.x[l]);
    }

    static bool Pullable(Rigidbody rb) => rb && !rb.isKinematic;

    // ------------------------------------------------------------------ spool

    /// <returns>True if a free-ended rope was reeled in completely.</returns>
    bool UpdateSpool(Rope r, Vector3 playerPoint, float dt)
    {
        if (!r.pinB || !r.bIsPlayer) return false;

        // Auto payout: pay out exactly what the player pulled away since last step.
        float autoBudget = Mathf.Min(autoPayoutLength, maxRopeLength) - r.length;
        if (autoBudget > 0f && !_reelHeld && r.reelSpeed <= 0.01f)
        {
            // Capped per step, so a sudden yank can't pay out a flood of rope at once.
            float stretch = Vector3.Distance(r.x[r.n - 2], playerPoint) - r.rest[r.n - 2];
            if (stretch > 0.001f) Feed(r, Mathf.Min(stretch, autoBudget, feedSpeed * dt), playerPoint);
        }

        if (r.slurping)
        {
            if (r.pinA || _feedHeld) r.slurping = false; // anchored again, or paying out: stop
            else
            {
                Retract(r, slurpSpeed * dt);
                return r.length <= MinRopeLength;
            }
        }

        if (_feedHeld)
        {
            Feed(r, feedSpeed * dt, playerPoint);
        }
        else if (r.reelSpeed > 0f)
        {
            float stall = reelStrength > 0f
                ? 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(reelStrength * 0.5f, reelStrength, r.endTension))
                : 1f;
            Retract(r, r.reelSpeed * dt * stall);
            if (!r.pinA && r.length <= MinRopeLength) return true;
        }

        return false;
    }

    void Feed(Rope r, float amount, Vector3 playerPoint)
    {
        amount = Mathf.Min(amount, maxRopeLength - r.length);
        float split = particleSpacing * 1.5f;

        for (int guard = 0; amount > 1e-6f && guard < 64; guard++)
        {
            int last = r.n - 2;
            float add = Mathf.Min(amount, Mathf.Max(0f, split - r.rest[last]));
            r.rest[last] += add;
            amount -= add;

            if (r.rest[last] < split - 1e-5f) break;
            if (r.n - 1 >= r.capacity)
            {
                if (r.capacity >= HARD_MAX_SEGMENTS) break;
                r.Grow(Mathf.Min(HARD_MAX_SEGMENTS, r.capacity * 2));
            }
            InsertAtSpool(r, playerPoint);
        }

        SyncLength(r);
    }

    void InsertAtSpool(Rope r, Vector3 playerPoint)
    {
        int pl = r.n - 1;
        float second = Mathf.Max(r.rest[pl - 1] - particleSpacing, particleSpacing * 0.2f);

        r.x[pl + 1] = r.x[pl];
        r.prev[pl + 1] = r.prev[pl];

        // Along the line the rope already runs into the player, so the new piece stretches
        // nothing. Placing it out of the mouth instead, with the rope lying elsewhere, stretched
        // the rope at once; the solver snapped it back, the rope whipped and tugged the player,
        // auto payout paid out more, and the loop flung the player off. (The mouth end is *drawn*
        // coming out head-on: CurlToMouth.)
        Vector3 toPlayer = playerPoint - r.x[pl - 1];
        float dist = toPlayer.magnitude;
        Vector3 dir = dist > 1e-6f ? toPlayer / dist : frontOnly ? -MouthDirSim : transform.forward;

        // Without a mouth, a golden-angle spiral offset so fed slack coils instead of buckling
        // straight. With one, straight: the spiral spilled rope all round the body.
        Vector3 offset = Vector3.zero;
        if (!frontOnly)
        {
            Vector3 u = Perpendicular(dir), v = Vector3.Cross(dir, u);
            float ang = r.coilIndex++ * 2.39996f;
            offset = (u * Mathf.Cos(ang) + v * Mathf.Sin(ang)) * Mathf.Min(ropeRadius * 1.5f, second * 0.25f);
        }

        Vector3 p = playerPoint - dir * Mathf.Min(second, dist * 0.5f) + offset;
        r.x[pl] = p;
        r.prev[pl] = p - (r.x[pl - 1] - r.prev[pl - 1]); // inherit neighbour velocity
        r.contact[pl] = false;

        r.rest[pl - 1] = particleSpacing;
        r.rest[pl] = second;
        r.tension[pl] = r.tension[pl - 1];
        r.n++;
        r.topologyDirty = true;
        r.graceUntil = Mathf.Max(r.graceUntil, Time.time + 0.04f);
    }

    void Retract(Rope r, float amount)
    {
        float minTail = Mathf.Max(ropeRadius * 2.2f, particleSpacing * 0.12f);

        for (int guard = 0; amount > 1e-6f && guard < 64; guard++)
        {
            int last = r.n - 2;
            float rem = Mathf.Min(amount, Mathf.Max(0f, r.rest[last] - minTail));
            r.rest[last] -= rem;
            amount -= rem;

            if (amount <= 1e-6f || r.n <= 2) break;

            // Merge the spool-side particle away.
            int pl = r.n - 1;
            r.rest[pl - 2] += r.rest[pl - 1];
            r.x[pl - 1] = r.x[pl];
            r.prev[pl - 1] = r.prev[pl];
            r.n--;
            r.topologyDirty = true;
            r.graceUntil = Mathf.Max(r.graceUntil, Time.time + 0.04f);
        }

        SyncLength(r);
    }

    // Rod: a rigid stick of the rope's length welded square to each surface. Two welds, one per
    // end: the tip of the stick standing out of A should sit on B's anchor, and vice versa. Each
    // weld is a critically damped spring between those two points, applied equal and opposite at
    // the points themselves, so it both moves and turns the bodies (the stick is the lever) and
    // conserves momentum. Effective mass at the points keeps the rate at rodStrength however big
    // the bodies or long the rod, so it stays stable; many rods on one body just add up.
    void RodForces(Rope r, Vector3 pa, Vector3 pb)
    {
        Rigidbody ba = r.a.body, bb = r.b.body;
        if (rodStrength <= 0f || ba == bb || (!Pullable(ba) && !Pullable(bb))) return;

        float len = r.length;
        Vector3 na = SafeDir(r.a.SimNormal, pb - pa), nb = SafeDir(r.b.SimNormal, pa - pb);
        Weld(ba, pa + na * len, bb, pb); // A's stick tip onto B's anchor
        Weld(bb, pb + nb * len, ba, pa); // B's stick tip onto A's anchor
    }

    // Pull point p (on body 'from') and point q (on body 'to') together.
    void Weld(Rigidbody from, Vector3 p, Rigidbody to, Vector3 q)
    {
        Vector3 err = p - q;
        Vector3 vRel = PointVelocity(to, q) - PointVelocity(from, p);
        Vector3 dir = err.sqrMagnitude > 1e-8f ? err.normalized : vRel.sqrMagnitude > 1e-8f ? vRel.normalized : Vector3.zero;
        if (dir == Vector3.zero) return;

        float inv = InvMassAt(from, p, dir) + InvMassAt(to, q, dir);
        if (inv <= 0f) return;

        float w = rodStrength;
        Vector3 f = (err * (w * w) - vRel * (2f * w)) * (0.5f / inv); // half: the two welds share the job
        if (Pullable(to)) to.AddForceAtPosition(f, q);
        if (Pullable(from)) from.AddForceAtPosition(-f, p);
    }

    static Vector3 PointVelocity(Rigidbody rb, Vector3 p) => rb && !rb.isKinematic ? rb.GetPointVelocity(p) : Vector3.zero;

    // 1 / (mass felt when pushing body rb at 'point' along 'dir'), rotation included.
    static float InvMassAt(Rigidbody rb, Vector3 point, Vector3 dir)
    {
        if (!Pullable(rb)) return 0f;
        Vector3 rn = Vector3.Cross(point - rb.worldCenterOfMass, dir);
        Quaternion q = rb.rotation * rb.inertiaTensorRotation;
        Vector3 l = Quaternion.Inverse(q) * rn, it = rb.inertiaTensor;
        l = new Vector3(it.x > 1e-8f ? l.x / it.x : 0f, it.y > 1e-8f ? l.y / it.y : 0f, it.z > 1e-8f ? l.z / it.z : 0f);
        return 1f / rb.mass + Vector3.Dot(rn, q * l);
    }

    // Toward targetLength: every segment scaled alike, so the rope keeps its shape; re-spaced
    // when its particles drift too far from the usual spacing.
    void AdjustLength(Rope r, float dt)
    {
        if (r == _active && (_feedHeld || _reelHeld || r.slurping)) { r.targetLength = 0f; return; } // the spool has it

        float goal = r.targetLength;
        float next = Mathf.MoveTowards(r.length, goal, Mathf.Max(feedSpeed, retractSpeed) * dt);
        if (Mathf.Abs(next - goal) < 1e-4f) { next = goal; r.targetLength = 0f; }

        float k = next / Mathf.Max(r.length, 1e-5f);
        for (int s = 0; s < r.n - 1; s++) r.rest[s] *= k;
        SyncLength(r);

        float each = r.length / (r.n - 1);
        if (each > particleSpacing * 1.6f || each < particleSpacing * 0.6f) Resample(r);
        r.sleeping = false;
        r.sleepTimer = 0f;
    }

    static Vector3[] s_resampleX = new Vector3[64], s_resampleP = new Vector3[64];

    // Same path, same ends, particles re-placed evenly at the usual spacing.
    void Resample(Rope r)
    {
        int oldN = r.n;
        int segs = Mathf.Clamp(Mathf.RoundToInt(r.length / particleSpacing), 2, HARD_MAX_SEGMENTS);
        if (segs > r.capacity) r.Grow(Mathf.Min(HARD_MAX_SEGMENTS, Mathf.Max(segs, r.capacity * 2)));
        segs = Mathf.Min(segs, r.capacity);

        if (s_resampleX.Length < segs + 1)
        {
            s_resampleX = new Vector3[Mathf.NextPowerOfTwo(segs + 1)];
            s_resampleP = new Vector3[s_resampleX.Length];
        }

        float arc = 0f;
        for (int i = 0; i < oldN - 1; i++) arc += Vector3.Distance(r.x[i], r.x[i + 1]);

        int seg = 0;
        float segStart = 0f, segLen = oldN > 1 ? Vector3.Distance(r.x[0], r.x[1]) : 0f;
        for (int i = 0; i <= segs; i++)
        {
            float at = arc * i / segs;
            while (seg < oldN - 2 && at > segStart + segLen)
            {
                segStart += segLen;
                seg++;
                segLen = Vector3.Distance(r.x[seg], r.x[seg + 1]);
            }
            float t = segLen > 1e-6f ? Mathf.Clamp01((at - segStart) / segLen) : 0f;
            s_resampleX[i] = Vector3.Lerp(r.x[seg], r.x[seg + 1], t);
            s_resampleP[i] = Vector3.Lerp(r.prev[seg], r.prev[seg + 1], t);
        }
        s_resampleX[segs] = r.x[oldN - 1];
        s_resampleP[segs] = r.prev[oldN - 1];

        r.n = segs + 1;
        for (int i = 0; i < r.n; i++)
        {
            r.x[i] = r.start[i] = s_resampleX[i];
            r.prev[i] = s_resampleP[i];
            r.contact[i] = false;
            r.contactDepth[i] = 0f;
        }
        float each = r.length / segs;
        for (int s = 0; s < segs; s++) { r.rest[s] = each; r.tension[s] = 0f; }
        r.topologyDirty = r.meshDirty = true;
    }

    static void SyncLength(Rope r)
    {
        float total = 0f;
        for (int s = 0; s < r.n - 1; s++) total += Mathf.Max(1e-4f, r.rest[s]);
        r.length = total;
    }

    // ------------------------------------------------------------------ breaking

    void Split(int index, Rope r, int seg)
    {
        Vector3 mid = (r.x[seg] + r.x[seg + 1]) * 0.5f;
        Vector3 midPrev = (r.prev[seg] + r.prev[seg + 1]) * 0.5f;
        float halfRest = r.rest[seg] * 0.5f;

        // Right piece: midpoint -> old end.
        Rope right = CreateRope(r.capacity);
        right.n = r.n - seg;
        System.Array.Copy(r.x, seg, right.x, 0, right.n);
        System.Array.Copy(r.prev, seg, right.prev, 0, right.n);
        System.Array.Copy(r.start, seg, right.start, 0, right.n);
        System.Array.Copy(r.rest, seg, right.rest, 0, right.n - 1);
        right.x[0] = right.start[0] = mid;
        right.prev[0] = midPrev;
        right.rest[0] = halfRest;
        right.pinB = r.pinB;
        right.bIsPlayer = r.bIsPlayer;
        right.b.CopyFrom(r.b);
        right.straight = r.straight;
        right.suspended = r.suspended;
        right.scaleB = r.scaleB;
        right.lastA = mid;
        right.lastB = r.lastB;
        right.graceUntil = Time.time + BREAK_GRACE;
        SyncLength(right);

        // Left piece (this rope): old start -> midpoint.
        r.n = seg + 2;
        r.x[seg + 1] = r.start[seg + 1] = mid;
        r.prev[seg + 1] = midPrev;
        r.rest[seg] = halfRest;
        r.pinB = r.bIsPlayer = false;
        r.b.Clear();
        r.scaleB = 1f;
        r.lastB = mid;
        r.peakTension = r.endTension = 0f;
        r.graceUntil = Time.time + BREAK_GRACE;
        r.topologyDirty = true;
        SyncLength(r);

        _ropes.Insert(index + 1, right);

        // The player keeps the piece still attached to them.
        if (r == _active) _active = right.bIsPlayer ? right : null;
    }

    // ------------------------------------------------------------------ anchors & player

    bool TryGetGroundAnchor(out Transform support, out Rigidbody body, out Vector3 point, out Vector3 normal)
    {
        support = null;
        body = null;
        point = transform.position;
        normal = transform.up;

        if (_contact == null) return false;
        if (_contact.SurfaceNormal.sqrMagnitude > 1e-6f) normal = _contact.SurfaceNormal.normalized;

        int count = Physics.RaycastNonAlloc(transform.position + normal * 0.1f, -normal, _hits,
            GROUND_PROBE + 0.1f, surfaceLayers, QueryTriggerInteraction.Ignore);

        int best = -1;
        for (int i = 0; i < count; i++)
        {
            Collider c = _hits[i].collider;
            if (!c || IsPlayerCollider(c)) continue;
            if (best < 0 || _hits[i].distance < _hits[best].distance) best = i;
        }

        if (best >= 0)
        {
            RaycastHit hit = _hits[best];
            body = hit.collider.attachedRigidbody;
            support = body ? body.transform : hit.collider.transform;
            normal = hit.normal;
            point = hit.point + normal * ropeRadius; // sit on the surface, not in it
            return true;
        }

        // No hit (e.g. the surface has no collider under us): pin to whatever we're standing on.
        support = _contact.Surface;
        if (!support) return false;
        body = support.GetComponentInParent<Rigidbody>();
        return true;
    }

    void CachePlayerOffset()
    {
        if (!_playerBody || !playerRopePoint) return;
        Transform bt = _playerBody.transform;
        Quaternion inv = Quaternion.Inverse(bt.rotation);
        _playerOffset = inv * (playerRopePoint.position - bt.position);
        _mouthDir = inv * MouthDirRender;
    }

    // The way the rope comes out, from the rope point's axes.
    Vector3 MouthDirRender => MouthDir(playerRopePoint);

    Vector3 MouthDir(Transform t) => mouthSide switch
    {
        MouthSide.Front => t.forward,
        MouthSide.Back => -t.forward,
        MouthSide.Above => t.up,
        _ => -t.up,
    };

    float BodyRadius => bodyRadius > 0f ? bodyRadius : _autoBodyRadius;

    // Body centre and facing on the physics pose (solver).
    Vector3 BodyCentreSim => _playerBody ? _playerBody.position + _playerBody.rotation * _playerOffset : playerRopePoint.position;
    Vector3 MouthDirSim => _playerBody ? _playerBody.rotation * _mouthDir : MouthDirRender;

    // Where the held rope meets the player: the mouth on the body's surface, or the rope point itself.
    Vector3 PlayerSimPoint() => frontOnly ? BodyCentreSim + MouthDirSim * BodyRadius : BodyCentreSim;
    Vector3 PlayerRenderPoint => frontOnly ? playerRopePoint.position + MouthDirRender * BodyRadius : playerRopePoint.position;

    // Drawn positions of a rope held at the mouth: the stretch nearest it bends to come out of
    // the mouth head-on (blending from the mouth's own line into wherever the rope really goes),
    // and nothing is drawn through the body, so it curls round the body into the mouth. Only
    // what's drawn moves: the simulated rope, and everything it pushes, is untouched.
    void CurlToMouth(Vector3[] p, int n)
    {
        float radius = BodyRadius;
        float reach = curl * radius;
        Vector3 centre = playerRopePoint.position, dir = MouthDirRender, mouth = p[n - 1];
        float clear = radius + ropeRadius, clearSq = clear * clear;

        float s = 0f;
        Vector3 before = p[n - 1];
        for (int i = n - 2; i >= 0; i--)
        {
            s += Vector3.Distance(p[i], before); // along the simulated rope, before bending
            before = p[i];

            Vector3 q = p[i];
            if (s < reach)
            {
                float t = 1f - s / reach;
                q = Vector3.Lerp(q, mouth + dir * s, t * t * (3f - 2f * t));
            }

            Vector3 d = q - centre;
            float sq = d.sqrMagnitude;
            if (sq < clearSq) q = centre + (sq > 1e-10f ? d / Mathf.Sqrt(sq) : dir) * clear;

            p[i] = q;
            if (s >= reach && sq >= clearSq * 4f) break; // well clear of the body: the rest is as simulated
        }
    }

    // Scene view: the mouth and the way the rope comes out of it.
    void OnDrawGizmosSelected()
    {
        if (!frontOnly) return;
        Transform point = playerRopePoint ? playerRopePoint : transform;
        float radius = bodyRadius > 0f ? bodyRadius : _autoBodyRadius;
        Vector3 dir = MouthDir(point);
        Vector3 mouth = point.position + dir * radius;
        Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.5f);
        Gizmos.DrawWireSphere(point.position, radius);
        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(mouth, ropeRadius * 1.5f);
        Gizmos.DrawLine(mouth, mouth + dir * radius * 0.75f);
    }

    bool IsPlayerCollider(Collider c) =>
        ((object)_playerBody != null && ReferenceEquals(c.attachedRigidbody, _playerBody)) || _playerCols.Contains(c);

    static bool BelongsToAnchor(Collider c, Anchor anchor)
    {
        if (anchor.body && c.attachedRigidbody == anchor.body) return true;
        return anchor.support && (c.transform == anchor.support || c.transform.IsChildOf(anchor.support));
    }

    // ------------------------------------------------------------------ rendering

    int EndStyle(Rope r, bool start)
    {
        bool web = start ? r.pinA && r.a.HasSurface : r.pinB && !r.bIsPlayer && r.b.HasSurface;
        return web && webAnchors ? END_WEB : roundedEnds ? END_CAP : END_NONE;
    }

    static int DomeVerts(int sides, bool withBase) => (CAP_RINGS + (withBase ? 1 : 0)) * sides + 1;
    int EndVerts(int style, int sides) =>
        style == END_CAP ? DomeVerts(sides, false) :
        style == END_WEB ? DomeVerts(sides, true) + webStrands * STRAND_RINGS * STRAND_SIDES : 0;

    static readonly Dictionary<int, (float[] cos, float[] sin)> s_circles = new Dictionary<int, (float[], float[])>();

    // Unit circle for a tube of 'sides' (cached per count).
    void UseCircle(int sides)
    {
        if (_cos != null && _cos.Length == sides) return;
        if (!s_circles.TryGetValue(sides, out var c))
        {
            c = (new float[sides], new float[sides]);
            for (int i = 0; i < sides; i++)
            {
                float ang = Mathf.PI * 2f * i / sides;
                c.cos[i] = Mathf.Cos(ang);
                c.sin[i] = Mathf.Sin(ang);
            }
            s_circles.Add(sides, c);
        }
        _cos = c.cos;
        _sin = c.sin;
    }

    // Per ring of the mesh being built: its frame and distance along from end A (for the beads).
    static Vector3[] s_frameN = new Vector3[256], s_frameB = new Vector3[256], s_frameT = new Vector3[256];
    static float[] s_arcAt = new float[256];

    void BuildMesh(Rope r, float alpha, Material mat)
    {
        int n = r.n, sides = tubeSides;
        if (n < 2 || !r.mesh) return;
        UseCircle(sides);

        int endA = EndStyle(r, true), endB = EndStyle(r, false);
        int key = System.HashCode.Combine(n, sides, endA, endB, webStrands);

        if (r.sleeping && !r.meshDirty && r.meshKey == key) return;

        float margin = (endA == END_WEB || endB == END_WEB ? webSpread + ropeRadius * 3f : ropeRadius * 2f) * Mathf.Max(1f, Mathf.Max(r.scaleA, r.scaleB));

        // Off-screen: keep bounds current so culling can bring it back, skip everything else.
        if (r.meshKey != 0 && !r.meshRenderer.isVisible && !r.Cauterizing)
        {
            Vector3 mn = r.x[0], mx = mn;
            for (int i = 1; i < n; i++) { mn = Vector3.Min(mn, r.x[i]); mx = Vector3.Max(mx, r.x[i]); }
            r.mesh.bounds = new Bounds((mn + mx) * 0.5f, mx - mn + Vector3.one * (margin * 2f));
            return;
        }

        int need = n * sides + EndVerts(endA, sides) + EndVerts(endB, sides);
        if (r.verts == null || r.verts.Length < need)
        {
            int cap = Mathf.NextPowerOfTwo(need);
            r.verts = new Vector3[cap];
            r.norms = new Vector3[cap];
            r.uvs = new Vector2[cap];
        }
        if (s_render.Length < n) s_render = new Vector3[Mathf.NextPowerOfTwo(n)];
        if (s_arcAt.Length < n)
        {
            int cap = Mathf.NextPowerOfTwo(n);
            s_frameN = new Vector3[cap];
            s_frameB = new Vector3[cap];
            s_frameT = new Vector3[cap];
            s_arcAt = new float[cap];
        }
        if (r.meshRenderer.sharedMaterial != mat) r.meshRenderer.sharedMaterial = mat;

        // Interpolated render positions; endpoints snap to their exact interpolated owners.
        Vector3[] p = s_render;
        if (r.isSealed) for (int i = 0; i < n; i++) p[i] = r.a.RenderFromLocal(r.sealedLocal[i]);
        else for (int i = 0; i < n; i++) p[i] = Vector3.LerpUnclamped(r.start[i], r.x[i], alpha);
        if (r.pinA) p[0] = r.a.RenderPoint;
        if (r.pinB) p[n - 1] = r.bIsPlayer ? PlayerRenderPoint : r.b.RenderPoint;
        if (frontOnly && r.pinB && r.bIsPlayer) CurlToMouth(p, n);

        // Tube with a rotation-minimising frame, seeded from last frame to avoid twisting.
        Vector3 tangent = SafeDir(p[1] - p[0], Vector3.forward);
        Vector3 nrm = r.frameSeeded ? r.frameNormal : playerRopePoint.up;
        Vector3 t0 = tangent, n0 = nrm, b0 = nrm, tL = tangent, nL = nrm, bL = nrm;
        Vector3 min = p[0], max = p[0];
        float rad = ropeRadius, invSides = 1f / sides, v = 0f;
        Vector3[] verts = r.verts, norms = r.norms;
        Vector2[] uvs = r.uvs;

        for (int ring = 0; ring < n; ring++)
        {
            Vector3 c = p[ring];
            if (ring > 0) v += Vector3.Distance(p[ring - 1], c);

            tangent = SafeDir(p[Mathf.Min(ring + 1, n - 1)] - p[Mathf.Max(ring - 1, 0)], tangent);
            nrm = Vector3.ProjectOnPlane(nrm, tangent);
            nrm = nrm.sqrMagnitude < 1e-8f ? Perpendicular(tangent) : nrm.normalized;
            Vector3 bin = Vector3.Cross(tangent, nrm);
            s_frameN[ring] = nrm;
            s_frameT[ring] = tangent;
            s_frameB[ring] = bin;
            s_arcAt[ring] = v;

            if (ring == 0) { t0 = tangent; n0 = nrm; b0 = bin; }
            if (ring == n - 1) { tL = tangent; nL = nrm; bL = bin; }

            min = Vector3.Min(min, c);
            max = Vector3.Max(max, c);

            int bv = ring * sides;
            for (int s = 0; s < sides; s++)
            {
                Vector3 radial = nrm * _cos[s] + bin * _sin[s];
                verts[bv + s] = c + radial * rad;
                norms[bv + s] = radial;
                uvs[bv + s] = new Vector2(s * invSides, v);
            }
        }

        r.frameNormal = n0;
        r.frameSeeded = true;
        if (r.Cauterizing) BuildBlood(r, p, n, v);

        // Ends (vertex order must match BuildTris).
        int vi = n * sides;
        float sa = r.scaleA, sb = r.scaleB;
        if (endA == END_CAP) vi = WriteDome(r, vi, p[0], -t0, n0, b0, rad * sa, rad * sa, false, 0f);
        else if (endA == END_WEB) vi = WriteWeb(r, vi, p[0], p[1], r.a.RenderNormal, sa);

        if (endB == END_CAP) vi = WriteDome(r, vi, p[n - 1], tL, nL, bL, rad * sb, rad * sb, false, v);
        else if (endB == END_WEB) vi = WriteWeb(r, vi, p[n - 1], p[n - 2], r.b.RenderNormal, sb);

        bool rebuild = r.meshKey != key;
        if (rebuild) r.mesh.Clear();

        r.mesh.SetVertices(verts, 0, vi);
        r.mesh.SetNormals(norms, 0, vi);
        r.mesh.SetUVs(0, uvs, 0, vi);

        if (rebuild)
        {
            int triCount = BuildTris(r, n, sides, endA, endB); // allocates r.tris, so call first
            r.mesh.SetTriangles(r.tris, 0, triCount, 0, false);
            r.meshKey = key;
        }

        r.mesh.bounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * (margin * 2f));
        if (r.sleeping) r.meshDirty = false;
    }

    // ------------------------------------------------------------------ blood beads

    // The filled stretch is a lattice of beads round the rope, like a microtubule, but none of them
    // exist: this builds a see-through proxy (a tube just wider than the beads could reach, plus a
    // disc over each base) and Custom/RopeBlood ray-traces the beads inside it per pixel, from the
    // lattice rules and a few per-rope numbers (_Bead* below). Same rules as before: beadStrands
    // strands in a 3-start helix, alternating shades, sitting on the swollen shell (bursts, bolus),
    // snapping in one by one across pumpBlend, flaring into each base with a skirt on its surface.
    const int ProxySides = 12, SkirtSides = 20;
    static readonly int BeadPumpId = Shader.PropertyToID("_BeadPump"), BeadShapeId = Shader.PropertyToID("_BeadShape"),
                        BeadHeartId = Shader.PropertyToID("_BeadHeart"), BeadLatticeId = Shader.PropertyToID("_BeadLattice"),
                        BeadBaseId = Shader.PropertyToID("_BeadBase");
    static readonly List<Vector3> s_bPos = new List<Vector3>(), s_bNrm = new List<Vector3>();
    static readonly List<Vector4> s_bInfo = new List<Vector4>(), s_bAxis = new List<Vector4>(),
                                  s_bDir = new List<Vector4>(), s_bRef = new List<Vector4>();
    static int[] s_bTris = new int[256];

    void BuildBlood(Rope r, Vector3[] p, int n, float arc)
    {
        Material mat = BeadMaterial(r);
        if (!r.bloodGo)
        {
            r.bloodGo = new GameObject("Rope Blood");
            r.bloodGo.transform.SetParent(_root, false);
            r.bloodMesh = new Mesh { name = "Rope Blood Proxy", hideFlags = HideFlags.DontSave };
            r.bloodMesh.MarkDynamic();
            r.bloodGo.AddComponent<MeshFilter>().sharedMesh = r.bloodMesh;
            r.bloodRenderer = r.bloodGo.AddComponent<MeshRenderer>();
        }
        if (!mat) { r.bloodGo.SetActive(false); return; }
        if (!r.bloodGo.activeSelf) r.bloodGo.SetActive(true);
        if (r.bloodRenderer.sharedMaterial != mat) r.bloodRenderer.sharedMaterial = mat;
        r.bloodRenderer.shadowCastingMode = beadShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;

        bool fromA = r.pumpFrom == 0;
        float shell = ropeRadius * cauterizeSwell;
        float perShell = Mathf.PI / beadStrands * beadSize;
        float bead = shell * perShell;
        float flareLength = shell * 5f;
        float widest = ropeRadius * (cauterizeSwell + pumpBolus + pumpPulse) * (1f + perShell) / Mathf.Cos(Mathf.PI / ProxySides);
        float ext = bead * 2f * (1f + baseFlare);

        s_bPos.Clear(); s_bNrm.Clear(); s_bInfo.Clear(); s_bAxis.Clear(); s_bDir.Clear(); s_bRef.Clear();

        // Tube: a ring per rope ring, plus one past each end (the beads run into the bases).
        int rings = n + 2;
        for (int q = 0; q < rings; q++)
        {
            int i = Mathf.Clamp(q - 1, 0, n - 1);
            Vector3 T = s_frameT[i], N = s_frameN[i], B = Vector3.Cross(T, N);
            float at = q == 0 ? -ext : q == rings - 1 ? arc + ext : s_arcAt[i];
            Vector3 c = q == 0 ? p[0] - T * ext : q == rings - 1 ? p[n - 1] + T * ext : p[i];
            float s = fromA ? at : arc - at;                       // metres from the source base
            Vector3 up = fromA ? T : -T;                           // the way s grows
            float f = Mathf.Clamp01(1f - Mathf.Min(s, arc - s) / flareLength);
            float outer = widest * (1f + baseFlare * f * f);
            for (int k = 0; k < ProxySides; k++)
            {
                float a = k * Mathf.PI * 2f / ProxySides;
                Vector3 radial = N * Mathf.Cos(a) + B * Mathf.Sin(a);
                s_bPos.Add(c + radial * outer);
                s_bNrm.Add(radial);
                s_bInfo.Add(new Vector4(0f, s, 0f, outer));
                s_bAxis.Add(c);
                s_bDir.Add(up);
                s_bRef.Add(N);
            }
        }
        int tubeTris = (rings - 1) * ProxySides * 6;

        // Skirts: a closed puck round each base -- lid as high as its beads stand, a wall and a floor
        // as deep as their undersides, so they're traced from low angles and from below too (the
        // surface curves and dips away under them) (sized for the tube at its widest: the skirt
        // starts from the tube's live radius at the base).
        float size = bead * (1f + baseFlare * 0.8f) * (cauterizeSwell + pumpBolus + pumpPulse) / cauterizeSwell;
        float skirtR = shell * (1f + baseFlare) * (cauterizeSwell + pumpBolus + pumpPulse) / cauterizeSwell;
        for (int i = 1; i < skirtRings; i++) skirtR += size * (1f - 0.22f * i) * 1.7f;
        skirtR = (skirtR + size) / Mathf.Cos(Mathf.PI / SkirtSides);
        float top = size * 1.4f, floor = size; // beads sit 0.35 up: they reach 1.35 above, 0.65 below
        int skirts = 0;
        for (int end = 0; end < 2 && skirtRings > 0; end++)
        {
            if (!(end == 0 ? r.BaseA : r.BaseB)) continue;
            Vector3 N = SafeDir(end == 0 ? r.a.RenderNormal : r.b.RenderNormal, Vector3.up);
            Vector3 u = Perpendicular(N), w = Vector3.Cross(N, u);
            Vector3 surface = (end == 0 ? p[0] : p[n - 1]) - N * ropeRadius;
            bool source = (end == 0) == fromA;
            var info = new Vector4(1f, source ? 0f : arc, source ? -1f : 1f, skirtR);
            // Lid centre, lid rim, wall foot, floor centre.
            for (int k = -1; k <= SkirtSides * 2; k++)
            {
                int side = k % SkirtSides;
                float a = side * Mathf.PI * 2f / SkirtSides;
                Vector3 radial = u * Mathf.Cos(a) + w * Mathf.Sin(a);
                s_bPos.Add(k < 0 ? surface + N * top
                         : k < SkirtSides ? surface + N * top + radial * skirtR
                         : k < SkirtSides * 2 ? surface - N * floor + radial * skirtR
                         : surface - N * floor);
                s_bNrm.Add(k < 0 ? N : k < SkirtSides * 2 ? radial : -N);
                s_bInfo.Add(info);
                s_bAxis.Add(surface);
                s_bDir.Add(N);
                s_bRef.Add(u);
            }
            skirts++;
        }

        int triCount = tubeTris + skirts * SkirtSides * 12;
        if (s_bTris.Length < triCount) s_bTris = new int[Mathf.NextPowerOfTwo(triCount)];
        int tt = 0;
        for (int q = 0; q < rings - 1; q++) Strip(s_bTris, ref tt, q * ProxySides, (q + 1) * ProxySides, ProxySides);
        int v0 = rings * ProxySides;
        for (int sk = 0; sk < skirts; sk++, v0 += SkirtSides * 2 + 2)
        for (int k = 0; k < SkirtSides; k++)
        {
            int a = v0 + 1 + k, b = v0 + 1 + (k + 1) % SkirtSides;             // lid rim
            int fa = a + SkirtSides, fb = b + SkirtSides;                       // wall foot
            int bottom = v0 + 1 + SkirtSides * 2;                               // floor centre
            Vector3 up = s_bNrm[v0], outward = s_bNrm[a] + s_bNrm[b];
            ProxyTri(ref tt, v0, a, b, up);
            ProxyTri(ref tt, a, fa, b, outward);
            ProxyTri(ref tt, b, fa, fb, outward);
            ProxyTri(ref tt, bottom, fa, fb, -up);
        }

        Mesh m = r.bloodMesh;
        m.Clear();
        m.SetVertices(s_bPos);
        m.SetNormals(s_bNrm);
        m.SetUVs(0, s_bInfo);   // mode (0 tube, 1 skirt), metres from the source (tube) / its base's (skirt), skirt order, proxy radius
        m.SetUVs(1, s_bAxis);   // axis point (tube) / base surface point (skirt)
        m.SetUVs(2, s_bDir);    // along the rope toward growing s (tube) / surface normal (skirt)
        m.SetUVs(3, s_bRef);    // ring frame reference (tube) / skirt's angle zero (skirt)
        m.SetTriangles(s_bTris, 0, triCount, 0, true);

        // Everything else is worked out per pixel from these.
        float front = arc * r.pump / Mathf.Max(r.length, 1e-4f);
        r.bloodProps ??= new MaterialPropertyBlock();
        r.bloodProps.SetVector(BeadPumpId, new Vector4(front, front - pumpFront * 0.5f, Mathf.Max(pumpBlend, 1e-3f),
            r.isSealed ? 1f - Mathf.Clamp01((Time.time - r.sealedAt) / settleTime) : 1f));
        r.bloodProps.SetVector(BeadShapeId, new Vector4(ropeRadius, cauterizeSwell, pumpFront, pumpBolus));
        r.bloodProps.SetVector(BeadHeartId, new Vector4(pumpPulse, pumpSpacing, pumpSpeed, beadBeatGlow));
        r.bloodProps.SetVector(BeadLatticeId, new Vector4(beadStrands, perShell, arc, r.id));
        r.bloodProps.SetVector(BeadBaseId, new Vector4(baseFlare, flareLength, skirtRings, floor));
        r.bloodRenderer.SetPropertyBlock(r.bloodProps);
    }

    // One proxy triangle, wound to face 'facing' (Unity's front faces: cross(b - a, c - a) toward the viewer).
    static void ProxyTri(ref int tt, int a, int b, int c, Vector3 facing)
    {
        bool flip = Vector3.Dot(Vector3.Cross(s_bPos[b] - s_bPos[a], s_bPos[c] - s_bPos[a]), facing) < 0f;
        s_bTris[tt++] = a; s_bTris[tt++] = flip ? c : b; s_bTris[tt++] = flip ? b : c;
    }

    readonly Dictionary<Material, Material> _beadMats = new Dictionary<Material, Material>();

    // The bead material for a rope, wearing a cell look: beadLook if set, else the material of the
    // cell the blood came from (so beads match whatever planet they grow out of). One runtime copy
    // per look, refreshed every frame in the editor (live edits), once in a build.
    Material BeadMaterial(Rope r)
    {
        Material bead = BloodMaterial();
        if (!bead) return null;
        Material look = beadLook ? beadLook : SourceLook(r);
        if (!look) return bead;

        bool fresh = !_beadMats.TryGetValue(look, out Material m) || !m || m.shader != bead.shader;
        if (fresh)
        {
            if (m) Destroy(m);
            m = new Material(bead) { name = "__Runtime Rope Beads (" + look.name + ")", hideFlags = HideFlags.DontSave };
            _beadMats[look] = m;
        }
#if !UNITY_EDITOR
        if (!fresh) return m;
#endif
        m.CopyPropertiesFromMaterial(bead);
        m.CopyMatchingPropertiesFromMaterial(look);
        m.shaderKeywords = look.shaderKeywords;
        return m;
    }

    // The cell-family material (has a deep colour) of the surface the blood's source base is on.
    Material SourceLook(Rope r)
    {
        Anchor source = r.pumpFrom == 1 ? r.b : r.a;
        Transform t = source.body ? source.body.transform : source.support;
        Renderer renderer = t ? t.GetComponentInChildren<Renderer>() : null;
        Material m = renderer ? renderer.sharedMaterial : null;
        return m && m.HasProperty("_DeepColor") ? m : null;
    }

    // Half-sphere/dome: rings from the base (optional) toward the pole along 'axis'.
    int WriteDome(Rope r, int v, Vector3 c, Vector3 axis, Vector3 nrm, Vector3 bin,
        float rx, float ry, bool withBase, float uvV)
    {
        int sides = _cos.Length; // the circle BuildMesh is using
        for (int k = withBase ? 0 : 1; k <= CAP_RINGS; k++)
        {
            float th = k / (CAP_RINGS + 1f) * Mathf.PI * 0.5f;
            float cr = Mathf.Cos(th), sr = Mathf.Sin(th);
            for (int s = 0; s < sides; s++, v++)
            {
                Vector3 radial = nrm * _cos[s] + bin * _sin[s];
                r.verts[v] = c + radial * (rx * cr) + axis * (ry * sr);
                r.norms[v] = (radial * (cr / rx) + axis * (sr / ry)).normalized;
                r.uvs[v] = new Vector2(s / (float)sides, uvV);
            }
        }

        r.verts[v] = c + axis * ry;
        r.norms[v] = axis;
        r.uvs[v] = new Vector2(0.5f, uvV);
        return v + 1;
    }

    // Web anchor: flattened glue dome plus tapered strands splaying from the rope onto the surface.
    int WriteWeb(Rope r, int v, Vector3 endP, Vector3 nextP, Vector3 surfaceNormal, float scale)
    {
        float rad = ropeRadius * scale;
        Vector3 N = SafeDir(surfaceNormal, Vector3.up);
        Vector3 dirIn = SafeDir(nextP - endP, N);
        Vector3 surf = endP - N * rad;

        Vector3 u = Vector3.ProjectOnPlane(dirIn, N);
        u = u.sqrMagnitude < 1e-6f ? Perpendicular(N) : u.normalized;
        Vector3 w = Vector3.Cross(N, u);

        v = WriteDome(r, v, surf, N, u, w, rad * 2.2f, rad * 1.6f, true, 0f);

        Vector3 S = endP + dirIn * Mathf.Min(rad * 4f, Vector3.Distance(nextP, endP) * 0.9f);
        float step = Mathf.PI * 2f / webStrands;

        for (int k = 0; k < webStrands; k++)
        {
            float phi = (k + (Hash01(k) - 0.5f) * 0.5f) * step;
            float spread = webSpread * scale * (0.75f + 0.5f * Hash01(k + 17));
            Vector3 E = surf + (u * Mathf.Cos(phi) + w * Mathf.Sin(phi)) * spread + N * (rad * 0.15f);
            Vector3 C = Vector3.Lerp(S, E, 0.55f) + N * (rad * 1.2f);
            Vector3 sn = Vector3.zero;

            for (int j = 0; j < STRAND_RINGS; j++)
            {
                float t = j / (STRAND_RINGS - 1f), it = 1f - t;
                Vector3 pos = S * (it * it) + C * (2f * it * t) + E * (t * t);
                Vector3 tan = SafeDir((C - S) * it + (E - C) * t, N);

                sn = j == 0 ? Perpendicular(tan) : Vector3.ProjectOnPlane(sn, tan);
                sn = sn.sqrMagnitude < 1e-8f ? Perpendicular(tan) : sn.normalized;
                Vector3 sb = Vector3.Cross(tan, sn);
                float sr = Mathf.Lerp(rad * 0.5f, rad * 0.15f, t);

                for (int s = 0; s < STRAND_SIDES; s++, v++)
                {
                    float ang = s * (Mathf.PI * 2f / STRAND_SIDES);
                    Vector3 radial = sn * Mathf.Cos(ang) + sb * Mathf.Sin(ang);
                    r.verts[v] = pos + radial * sr;
                    r.norms[v] = radial;
                    r.uvs[v] = new Vector2(s / (float)STRAND_SIDES, t);
                }
            }
        }

        return v;
    }

    int BuildTris(Rope r, int n, int sides, int endA, int endB)
    {
        int capTris = CAP_RINGS * sides * 6 + sides * 3;
        int webTris = capTris + webStrands * (STRAND_RINGS - 1) * STRAND_SIDES * 6;
        int total = (n - 1) * sides * 6
                    + (endA == END_CAP ? capTris : endA == END_WEB ? webTris : 0)
                    + (endB == END_CAP ? capTris : endB == END_WEB ? webTris : 0);

        if (r.tris == null || r.tris.Length < total) r.tris = new int[Mathf.NextPowerOfTwo(total)];
        int[] tr = r.tris;
        int t = 0;

        for (int ring = 0; ring < n - 1; ring++) Strip(tr, ref t, ring * sides, (ring + 1) * sides, sides);

        int v = n * sides;

        // Start cap, ordered along +tangent: pole, ring K..1, tube ring 0.
        if (endA == END_CAP)
        {
            Fan(tr, ref t, v + CAP_RINGS * sides, v + (CAP_RINGS - 1) * sides, sides, true);
            for (int k = CAP_RINGS - 1; k >= 1; k--) Strip(tr, ref t, v + k * sides, v + (k - 1) * sides, sides);
            Strip(tr, ref t, v, 0, sides);
            v += DomeVerts(sides, false);
        }
        else if (endA == END_WEB) v = WebTris(tr, ref t, v, sides);

        // End cap: tube ring n-1, ring 1..K, pole.
        if (endB == END_CAP)
        {
            Strip(tr, ref t, (n - 1) * sides, v, sides);
            for (int k = 1; k < CAP_RINGS; k++) Strip(tr, ref t, v + (k - 1) * sides, v + k * sides, sides);
            Fan(tr, ref t, v + CAP_RINGS * sides, v + (CAP_RINGS - 1) * sides, sides, false);
        }
        else if (endB == END_WEB) WebTris(tr, ref t, v, sides);

        return t;
    }

    int WebTris(int[] tr, ref int t, int v, int sides)
    {
        for (int k = 0; k < CAP_RINGS; k++) Strip(tr, ref t, v + k * sides, v + (k + 1) * sides, sides);
        Fan(tr, ref t, v + (CAP_RINGS + 1) * sides, v + CAP_RINGS * sides, sides, false);
        v += DomeVerts(sides, true);

        for (int k = 0; k < webStrands; k++, v += STRAND_RINGS * STRAND_SIDES)
            for (int j = 0; j < STRAND_RINGS - 1; j++)
                Strip(tr, ref t, v + j * STRAND_SIDES, v + (j + 1) * STRAND_SIDES, STRAND_SIDES);

        return v;
    }

    // Quad strip between two rings; ringB lies further along the tube direction.
    static void Strip(int[] tr, ref int t, int ringA, int ringB, int sides)
    {
        for (int s = 0; s < sides; s++)
        {
            int ns = s + 1 == sides ? 0 : s + 1;
            tr[t++] = ringA + s; tr[t++] = ringA + ns; tr[t++] = ringB + s;
            tr[t++] = ringA + ns; tr[t++] = ringB + ns; tr[t++] = ringB + s;
        }
    }

    // Triangle fan closing a ring to a pole. poleFirst = pole lies before the ring along the tube.
    static void Fan(int[] tr, ref int t, int pole, int ring, int sides, bool poleFirst)
    {
        for (int s = 0; s < sides; s++)
        {
            int ns = s + 1 == sides ? 0 : s + 1;
            if (poleFirst) { tr[t++] = pole; tr[t++] = ring + ns; tr[t++] = ring + s; }
            else { tr[t++] = ring + s; tr[t++] = ring + ns; tr[t++] = pole; }
        }
    }

    const int PhantomSegments = 16;

    // A ghost rope from PhantomEnd's loose end to the player, sagging a little and breathing,
    // drawn with the rope's own tube builder (as a held rope: capped at the loose end, snapped
    // to the player). Only while no rope is held.
    void DrawPhantom(float alpha)
    {
        Rope src = _active == null && PhantomEnd >= 0 ? Find(PhantomEnd >> 1) : null;
        bool endB = (PhantomEnd & 1) == 1;
        if (src == null || (endB && src.bIsPlayer))
        {
            if (_phantom != null && _phantom.go.activeSelf) _phantom.go.SetActive(false);
            return;
        }

        if (_phantom == null)
        {
            _phantom = CreateRope(PhantomSegments);
            _phantom.go.name = "Phantom Rope";
            _phantom.pinB = _phantom.bIsPlayer = true;
        }
        if (!_phantom.go.activeSelf) _phantom.go.SetActive(true);

        int e = endB ? src.n - 1 : 0;
        Vector3 from = (endB ? src.pinB : src.pinA) ? (endB ? src.b.RenderPoint : src.a.RenderPoint)
                     : Vector3.LerpUnclamped(src.start[e], src.x[e], alpha);
        Vector3 to = PlayerRenderPoint;
        float dist = Vector3.Distance(from, to);
        float time = Time.time;
        Vector3 sag = -playerRopePoint.up * (dist * (0.1f + 0.03f * Mathf.Sin(time * 3f)));

        _phantom.n = PhantomSegments + 1;
        for (int i = 0; i <= PhantomSegments; i++)
        {
            float t = (float)i / PhantomSegments;
            _phantom.x[i] = _phantom.start[i] = Vector3.Lerp(from, to, t) + sag * Mathf.Sin(t * Mathf.PI);
        }
        _phantom.meshDirty = true;

        Material mat = PhantomMaterial();
        if (mat == _phantomMat && mat.HasProperty("_BaseColor"))
        {
            Color c = mat.GetColor("_BaseColor");
            c.a = 0.3f + 0.12f * Mathf.Sin(time * 4f);
            mat.SetColor("_BaseColor", c);
        }
        BuildMesh(_phantom, 1f, mat);
    }

    // See-through URP Unlit in the rope's colour (keep that shader in builds, or assign one).
    Material PhantomMaterial()
    {
        if (phantomMaterial) return phantomMaterial;
        if (_phantomMat) return _phantomMat;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (!shader) return ActiveMaterial();

        Material rope = ActiveMaterial();
        Color c = rope && rope.HasProperty("_BaseColor") ? rope.GetColor("_BaseColor")
                : rope && rope.HasProperty("_Color") ? rope.GetColor("_Color") : Color.white;
        c.a = 0.35f;

        var m = new Material(shader) { name = "__Runtime Phantom Rope", renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent };
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Surface", 1f); // transparent
        m.SetFloat("_Blend", 0f);   // alpha
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_SrcBlendAlpha")) m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        if (m.HasProperty("_DstBlendAlpha")) m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        return _phantomMat = m;
    }

    Material BloodMaterial()
    {
        if (bloodMaterial) return bloodMaterial;
        if (_bloodMat) return _bloodMat;
        Shader beads = Shader.Find("Custom/RopeBlood");
        return beads ? _bloodMat = new Material(beads) { name = "__Runtime Rope Blood", hideFlags = HideFlags.DontSave } : null;
    }

    Material ActiveMaterial()
    {
        if (ropeMaterial) return ropeMaterial;
        if (_fallbackMat) return _fallbackMat;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (!shader) shader = Shader.Find("Unlit/Color");
        if (!shader) shader = Shader.Find("Standard");
        if (!shader) return null;

        _fallbackMat = new Material(shader) { name = "__Runtime Rope Material" };
        if (_fallbackMat.HasProperty("_BaseColor")) _fallbackMat.SetColor("_BaseColor", Color.white);
        if (_fallbackMat.HasProperty("_Color")) _fallbackMat.SetColor("_Color", Color.white);
        return _fallbackMat;
    }

    // ------------------------------------------------------------------ helpers

    static float Hash01(int k)
    {
        float h = Mathf.Sin(k * 12.9898f + 78.233f) * 43758.5453f;
        return h - Mathf.Floor(h);
    }

    static Vector3 SafeDir(Vector3 v, Vector3 fallback)
    {
        float sq = v.sqrMagnitude;
        return sq > 1e-12f ? v / Mathf.Sqrt(sq) : fallback;
    }

    static Vector3 Perpendicular(Vector3 v) =>
        Vector3.Cross(v, Mathf.Abs(v.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
}