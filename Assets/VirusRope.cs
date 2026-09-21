using System.Collections;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Multi-rope XPBD rope system with a spool on the player.
///
/// Controls:
/// - Grounded + LMB (no active rope): start a rope anchored under the player.
/// - The first Auto Payout Length meters pay out automatically as you move.
/// - Flying + RMB: feed rope. Flying + LMB: reel in (a winch, stalls at Reel Strength).
/// - Grounded + LMB (active rope): anchor its end to the ground.
/// - A snapped rope leaves you holding your piece: reel it in, or click the ground to drop it.
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
    public VirusMovement movement;
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

    [Header("Visual")]
    [Range(3, 12)] public int tubeSides = 6;
    [Tooltip("Free and player ends get a half-sphere cap.")]
    public bool roundedEnds = true;
    [Tooltip("Ends attached to surfaces get a web-like anchor.")]
    public bool webAnchors = true;
    [Range(3, 12)] public int webStrands = 6;
    [Min(0f), Tooltip("How far web strands reach across the surface, in meters.")]
    public float webSpread = 0.18f;

    public int RopeCount => _ropes.Count;
    public bool HasRopeAttachedToPlayer => _active != null;

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
    bool IsFlying => movement && movement.state == VirusMovement.State.Flying;
    bool IsGrounded => movement && movement.state == VirusMovement.State.Grounded;

    static readonly ProfilerMarker s_simMarker = new ProfilerMarker("VirusRope.Simulate");
    static readonly ProfilerMarker s_meshMarker = new ProfilerMarker("VirusRope.Mesh");

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
        public int n, capacity, coilIndex;
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
    readonly Collider[] _overlap = new Collider[64];
    readonly RaycastHit[] _hits = new RaycastHit[8];
    static Vector3[] s_render = new Vector3[256];
    float[] _cos, _sin;

    Rope _active;
    Rigidbody _playerBody;
    Vector3 _playerOffset;
    Transform _root;
    Material _fallbackMat;
    CapsuleCollider _probe;
    Coroutine _loop;
    int _stepCounter;

    bool _leftPressed, _leftHeld, _rightHeld, _mustReleaseBeforeReel, _feedHeld, _reelHeld;

    // ------------------------------------------------------------------ unity

    void Awake()
    {
        if (!movement) movement = GetComponent<VirusMovement>();
        _playerBody = movement ? movement.GetComponent<Rigidbody>() : GetComponent<Rigidbody>();
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
        if (_probe) Destroy(_probe.gameObject);
        if (_fallbackMat) Destroy(_fallbackMat);
        if (_root) Destroy(_root.gameObject);
    }

    void Update()
    {
        ReadMouse();
        CachePlayerOffset();

        if (_leftPressed && IsGrounded)
        {
            if (_active == null) StartNewRope();
            else if (_active.pinA) FinishActiveRope();
            else { ReleaseActive(); StartNewRope(); }
        }

        if (!_leftHeld) _mustReleaseBeforeReel = false;

        bool flying = IsFlying;
        _feedHeld = flying && _rightHeld && !_leftHeld;
        _reelHeld = flying && _leftHeld && !_rightHeld && !_mustReleaseBeforeReel;

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
            using (s_simMarker.Auto())
                for (int i = _ropes.Count - 1; i >= 0; i--)
                    StepRope(i, _ropes[i], Time.fixedDeltaTime);
        }
    }

    void LateUpdate()
    {
        float alpha = Time.fixedDeltaTime > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) : 1f;
        Material mat = ActiveMaterial();

        if (_cos == null || _cos.Length != tubeSides)
        {
            _cos = new float[tubeSides];
            _sin = new float[tubeSides];
            for (int i = 0; i < tubeSides; i++)
            {
                float ang = Mathf.PI * 2f * i / tubeSides;
                _cos[i] = Mathf.Cos(ang);
                _sin[i] = Mathf.Sin(ang);
            }
        }

        using (s_meshMarker.Auto())
            for (int i = 0; i < _ropes.Count; i++)
                BuildMesh(_ropes[i], alpha, mat);
    }

    void ReadMouse()
    {
        _leftPressed = _leftHeld = _rightHeld = false;
#if ENABLE_INPUT_SYSTEM
        var m = Mouse.current;
        if (m != null)
        {
            _leftPressed = m.leftButton.wasPressedThisFrame;
            _leftHeld = m.leftButton.isPressed;
            _rightHeld = m.rightButton.isPressed;
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        _leftPressed = Input.GetMouseButtonDown(0);
        _leftHeld = Input.GetMouseButton(0);
        _rightHeld = Input.GetMouseButton(1);
#endif
    }

    // ------------------------------------------------------------------ public API

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
    }

    // ------------------------------------------------------------------ rope lifecycle

    void StartNewRope()
    {
        if (!TryGetGroundAnchor(out Transform support, out Rigidbody body, out Vector3 point, out Vector3 normal))
            return;

        while (_ropes.Count >= maxRopes && _ropes.Count > 0)
            DestroyRopeAt(0); // oldest (the active rope is always null here)

        Rope r = CreateRope(SegmentCapacity);
        r.a.Set(support, body, point, normal);
        r.pinA = r.pinB = r.bIsPlayer = true;

        Vector3 b = PlayerSimPoint();
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
        _mustReleaseBeforeReel = true;
    }

    void FinishActiveRope()
    {
        if (!TryGetGroundAnchor(out Transform support, out Rigidbody body, out Vector3 point, out Vector3 normal))
            return;

        Rope r = _active;
        r.b.Set(support, body, point, normal);
        r.bIsPlayer = false;
        r.reelSpeed = 0f;
        r.lastB = r.x[r.n - 1]; // glide onto the anchor over one step
        r.graceUntil = Time.time + BREAK_GRACE;
        r.topologyDirty = true;
        _active = null;
    }

    void ReleaseActive()
    {
        _active.pinB = _active.bIsPlayer = false;
        _active.b.Clear();
        _active.reelSpeed = 0f;
        _active.topologyDirty = true;
        _active = null;
    }

    Rope CreateRope(int capacity)
    {
        var r = new Rope(capacity);
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
        if (r.mesh) Destroy(r.mesh);
        if (r.go) Destroy(r.go);
    }

    // ------------------------------------------------------------------ step

    void StepRope(int index, Rope r, float dt)
    {
        if (r.pinA && r.a.Lost) { r.pinA = false; r.sleeping = false; }
        if (r.pinB && !r.bIsPlayer && r.b.Lost) { r.pinB = false; r.sleeping = false; }

        Vector3 a = r.pinA ? r.a.SimPoint : r.x[0];
        Vector3 b = r.pinB ? (r.bIsPlayer ? PlayerSimPoint() : r.b.SimPoint) : r.x[r.n - 1];

        if (r == _active && UpdateSpool(r, b, dt))
        {
            DestroyRopeAt(index); // fully reeled in
            return;
        }

        float stillSq = SLEEP_SPEED * dt * SLEEP_SPEED * dt;
        bool endsMoved = (r.pinA && (a - r.lastA).sqrMagnitude > stillSq) ||
                         (r.pinB && (b - r.lastB).sqrMagnitude > stillSq);

        if (r.sleeping)
        {
            bool wake = endsMoved || r == _active ||
                        (((_stepCounter + index) & 3) == 0 && DynamicBodyNear(r));
            if (!wake) return;
            r.sleeping = false;
            r.sleepTimer = 0f;
        }

        int n = r.n, segs = n - 1, sub = Substeps;
        float h = dt / sub;
        System.Array.Copy(r.x, r.start, n);

        if (r.topologyDirty || r.stepsSinceGather >= GATHER_INTERVAL || r.drift > GATHER_MARGIN)
        {
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

        for (int s = 0; s < sub; s++)
        {
            float t = (s + 1f) / sub;
            Integrate(r, retain);
            if (r.pinA) { r.prev[0] = r.x[0]; r.x[0] = Vector3.Lerp(r.lastA, a, t); }
            if (r.pinB) { r.prev[segs] = r.x[segs]; r.x[segs] = Vector3.Lerp(r.lastB, b, t) + r.playerCorr; }

            bool forward = (s & 1) == 0;
            SolveDistances(r, forward, w, playerW, invH2, ea);
            if (bendK > 0f) SolveBend(r, forward, bendK);
            if (r.pairCount > 0)
            {
                SolveCollisions(r, h);
                ApplyContactVelocity(r, h, gripKeep);
            }
        }

        SweepFastParticles(r);

        // Tug the player: undo the overshoot the rope resisted and remove that velocity.
        if (playerW > 0f)
        {
            Vector3 c = Vector3.ClampMagnitude(r.playerCorr, MAX_TUG_SPEED * dt);
            if (c.sqrMagnitude > 1e-12f)
            {
                _playerBody.position += c;
                _playerBody.AddForce(c / dt, ForceMode.VelocityChange);
                b += c;
            }
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

        float k = 1f - Mathf.Exp(-dt / TENSION_FILTER);
        r.peakTension += (maxT - r.peakTension) * k;
        r.endTension += (Mathf.Max(0f, r.tension[segs - 1]) - r.endTension) * k;

        r.lastA = r.pinA ? a : r.x[0];
        r.lastB = r.pinB ? b : r.x[segs];

        ApplyAnchorForces(r);

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

        if (breakForce > 0f && maxSeg >= 0 && Time.time >= r.graceUntil && r.peakTension > breakForce)
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

            if (rb && !rb.isKinematic && impulseScale > 0f)
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

        if (r.pinA && r.a.body && !r.a.body.isKinematic && r.tension[0] > 0f)
            r.a.body.AddForceAtPosition((r.x[1] - r.x[0]).normalized * (r.tension[0] * objectForce), r.x[0]);

        if (r.pinB && !r.bIsPlayer && r.b.body && !r.b.body.isKinematic && r.tension[l - 1] > 0f)
            r.b.body.AddForceAtPosition((r.x[l - 1] - r.x[l]).normalized * (r.tension[l - 1] * objectForce), r.x[l]);
    }

    // ------------------------------------------------------------------ spool

    /// <returns>True if a free-ended rope was reeled in completely.</returns>
    bool UpdateSpool(Rope r, Vector3 playerPoint, float dt)
    {
        if (!r.pinB || !r.bIsPlayer) return false;

        // Auto payout: pay out exactly what the player pulled away since last step.
        float autoBudget = Mathf.Min(autoPayoutLength, maxRopeLength) - r.length;
        if (autoBudget > 0f && !_reelHeld && r.reelSpeed <= 0.01f)
        {
            float stretch = Vector3.Distance(r.x[r.n - 2], playerPoint) - r.rest[r.n - 2];
            if (stretch > 0.001f) Feed(r, Mathf.Min(stretch, autoBudget), playerPoint);
        }

        if (_feedHeld)
        {
            Feed(r, feedSpeed * dt, playerPoint);
        }
        else if (r.reelSpeed > 0f && IsFlying)
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

        Vector3 toPlayer = playerPoint - r.x[pl - 1];
        float dist = toPlayer.magnitude;
        Vector3 dir = dist > 1e-6f ? toPlayer / dist : transform.forward;

        // Golden-angle spiral offset so fed slack coils instead of buckling straight.
        Vector3 u = Perpendicular(dir), v = Vector3.Cross(dir, u);
        float ang = r.coilIndex++ * 2.39996f;
        Vector3 offset = (u * Mathf.Cos(ang) + v * Mathf.Sin(ang)) * Mathf.Min(ropeRadius * 1.5f, second * 0.25f);

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
        r.lastB = mid;
        r.peakTension = r.endTension = 0f;
        r.graceUntil = Time.time + BREAK_GRACE;
        r.topologyDirty = true;
        SyncLength(r);

        _ropes.Insert(index + 1, right);

        // The player keeps the piece still attached to them.
        if (r == _active)
        {
            _active = right.bIsPlayer ? right : null;
            _mustReleaseBeforeReel = true;
        }
    }

    // ------------------------------------------------------------------ anchors & player

    bool TryGetGroundAnchor(out Transform support, out Rigidbody body, out Vector3 point, out Vector3 normal)
    {
        support = null;
        body = null;
        point = transform.position;
        normal = movement && movement.surfaceNormal.sqrMagnitude > 1e-6f ? movement.surfaceNormal.normalized : transform.up;

        if (!movement) return false;

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

        if (movement.cellSpace)
        {
            support = movement.cellSpace;
            body = support.GetComponentInParent<Rigidbody>();
            return true;
        }

        return false;
    }

    void CachePlayerOffset()
    {
        if (!_playerBody || !playerRopePoint) return;
        Transform bt = _playerBody.transform;
        _playerOffset = Quaternion.Inverse(bt.rotation) * (playerRopePoint.position - bt.position);
    }

    Vector3 PlayerSimPoint() =>
        _playerBody ? _playerBody.position + _playerBody.rotation * _playerOffset : playerRopePoint.position;

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

    void BuildMesh(Rope r, float alpha, Material mat)
    {
        int n = r.n, sides = tubeSides;
        if (n < 2 || !r.mesh) return;

        int endA = EndStyle(r, true), endB = EndStyle(r, false);
        int key = n + 4096 * (sides + 16 * (endA + 4 * (endB + 4 * webStrands)));

        if (r.sleeping && !r.meshDirty && r.meshKey == key) return;

        float margin = endA == END_WEB || endB == END_WEB ? webSpread + ropeRadius * 3f : ropeRadius * 2f;

        // Off-screen: keep bounds current so culling can bring it back, skip everything else.
        if (r.meshKey != 0 && !r.meshRenderer.isVisible)
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
        if (r.meshRenderer.sharedMaterial != mat) r.meshRenderer.sharedMaterial = mat;

        // Interpolated render positions; endpoints snap to their exact interpolated owners.
        Vector3[] p = s_render;
        for (int i = 0; i < n; i++) p[i] = Vector3.LerpUnclamped(r.start[i], r.x[i], alpha);
        if (r.pinA) p[0] = r.a.RenderPoint;
        if (r.pinB) p[n - 1] = r.bIsPlayer ? playerRopePoint.position : r.b.RenderPoint;

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

        // Ends (vertex order must match BuildTris).
        int vi = n * sides;
        if (endA == END_CAP) vi = WriteDome(r, vi, p[0], -t0, n0, b0, rad, rad, false, 0f);
        else if (endA == END_WEB) vi = WriteWeb(r, vi, p[0], p[1], r.a.RenderNormal);

        if (endB == END_CAP) vi = WriteDome(r, vi, p[n - 1], tL, nL, bL, rad, rad, false, v);
        else if (endB == END_WEB) vi = WriteWeb(r, vi, p[n - 1], p[n - 2], r.b.RenderNormal);

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

    // Half-sphere/dome: rings from the base (optional) toward the pole along 'axis'.
    int WriteDome(Rope r, int v, Vector3 c, Vector3 axis, Vector3 nrm, Vector3 bin,
        float rx, float ry, bool withBase, float uvV)
    {
        int sides = tubeSides;
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
    int WriteWeb(Rope r, int v, Vector3 endP, Vector3 nextP, Vector3 surfaceNormal)
    {
        float rad = ropeRadius;
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
            float spread = webSpread * (0.75f + 0.5f * Hash01(k + 17));
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