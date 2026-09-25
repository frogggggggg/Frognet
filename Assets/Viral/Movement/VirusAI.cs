using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Controller for autonomous viruses: the AI counterpart of VirusMovement. It
/// writes the same Organism intent (Move, aim, Jump, Charge), so AI viruses use
/// the same states, effects, legs and landing as the player.
///
/// For now it chases one target (the player by default):
/// - Target flying: take off if grounded, then fly to it through PathManager's
///   flow field (around every cell, no traps).
/// - Target on a cell: fly to the surface point under the target with that cell
///   left out of the flow field, so it's a destination rather than something to
///   steer around: the virus flies straight onto it (the collision lands it) and
///   crawls after the target along the surface, the shortest way round whatever
///   the shape (SurfaceField: cubes, rings, concave blobs), straight at it once close.
/// - Standing on any other cell: take off again.
/// - Crowds: viruses ease apart where they overlap (in 3D in the air, along the
///   surface on a shared cell), each taking half of every overlap over
///   crowdSettleTime, so pushes settle instead of overshooting. One that runs into a
///   stopped virus nearer the goal stops too, so a crowd rings the goal instead of
///   shoving the front row. They keep clear of the target itself, so they don't shove
///   it. Steering eases between decisions (steerSmoothing).
///
/// Orders (the command mode's CommandBoard, <see cref="Order"/>) replace that: a creature is chased
/// the same way; a cell means land on it (the nearest side) and stay, spread out; anything else means
/// go to it (onto the cell it sits on, if it's part of one). Order(null) goes back to the default.
///
/// Cheap per virus: it's its Organism's Brain, ticked by SimulationTicker just
/// before the organism and at the same (distance-based) rate; decisions and
/// flow-field queries run on a staggered timer on top of that.
/// </summary>
[RequireComponent(typeof(Organism))]
public class VirusAI : MonoBehaviour, IOrganismBrain, ICommandable
{
    [Tooltip("Who to chase. Empty: the player (the VirusMovement in the scene).")]
    public Organism target;

    [Header("Air")]
    [Min(0f), Tooltip("Stop this far from a flying target.")]
    public float airStopDistance = 3f;
    [Min(0.1f), Tooltip("Ease off over this distance before stopping (air and ground).")]
    public float slowDownDistance = 4f;
    [Min(0f), Tooltip("Dash (Charge) toward the goal when farther than this. 0 = never.")]
    public float burstDistance = 25f;
    [Min(0.1f), Tooltip("Flying viruses keep this far apart.")]
    public float airSeparationRadius = 2.5f;
    [Min(0f), Tooltip("How fast they ease apart: x1 clears an overlap in Crowd Settle Time.")]
    public float airSeparationStrength = 1f;

    [Header("Ground")]
    [Min(0f), Tooltip("Stop this far from the target on the same cell.")]
    public float groundStopDistance = 2f;
    [Min(0.1f), Tooltip("Viruses on the same cell keep this far apart.")]
    public float separationRadius = 1.5f;
    [Min(0f), Tooltip("How fast they ease apart: x1 clears an overlap in Crowd Settle Time.")]
    public float separationStrength = 1f;

    [Header("Crowd")]
    [Min(0.05f), Tooltip("Seconds to ease an overlap apart. Keep it well above the think interval: " +
                         "a faster push overshoots on stale positions and crowds vibrate.")]
    public float crowdSettleTime = 0.5f;
    [Min(0f), Tooltip("Once stopped (at the goal, or queued behind a stopped virus nearer it), the goal must " +
                      "get this much farther before it sets off again.")]
    public float regroupDistance = 1f;
    [Min(0f), Tooltip("Seconds to ease steering toward each new decision, so heading and speed don't snap between thinks.")]
    public float steerSmoothing = 0.2f;

    [Header("Thinking")]
    [Min(0f), Tooltip("Seconds between decisions and path queries. Staggered per virus.")]
    public float thinkInterval = 0.1f;

    Organism _o;
    int _selfId, _targetId;

    // The command board's order: chase it if it's a creature, else go to it / onto it.
    Transform _order;
    Organism _orderOrganism;
    Transform _orderCell; // the cell it is or is part of (null: floating free)
    int _orderId;
    CommandBoard.Job _job;

    // This think's goal, from the order or the default target.
    Organism _chase;
    int _goalId;
    float _nextThink;
    Vector3 _move;   // this think's decision
    Vector3 _steer;  // eased toward it every tick; what the organism gets
    bool _settled;   // not heading for the goal (there, or queued): others queue behind it

    // Neighbours: every AI virus (flying or grounded) goes into one coarse spatial hash, rebuilt at
    // most every half think interval, so a separation query only touches nearby buckets
    // however many viruses there are.
    static readonly List<VirusAI> s_agents = new List<VirusAI>();
    static readonly Dictionary<Vector3Int, List<VirusAI>> s_grid = new Dictionary<Vector3Int, List<VirusAI>>();
    static readonly Stack<List<VirusAI>> s_pool = new Stack<List<VirusAI>>();
    static float s_gridBuilt = float.NegativeInfinity, s_gridSize = 1f;

    Transform _groundCell; // cell this virus stood on at its last think; null in the air
    bool _inAir;          // flying (or jumping) at its last think
    Vector3 _gridPos;      // position when the hash was built

    // Cached per goal cell: its collider (for centre and radius) and flow-field id.
    Transform _cell;
    Collider _cellCollider;
    Surface _cellSurface;
    int _cellId;

    void Awake()
    {
        // A copy of the player keeps its VirusMovement, which would read the player's input and
        // switch the player's camera modes whenever this virus lands or takes off.
        if (TryGetComponent(out VirusMovement leftover) && leftover.enabled)
        {
            Debug.LogWarning($"{name}: VirusAI and VirusMovement on one virus; disabling VirusMovement. " +
                             "Remove it (and any camera pieces) from AI viruses.", this);
            leftover.enabled = false;
        }

        _o = GetComponent<Organism>();
        _selfId = PathManager.Id(this);
        _nextThink = Time.time + Random.value * thinkInterval; // stagger
    }

    void OnEnable()
    {
        s_agents.Add(this);
        _o.Brain = this;
    }

    void OnDisable()
    {
        s_agents.Remove(this);
        if (_o.Brain == (IOrganismBrain)this) _o.Brain = null;
        _groundCell = null;
        _inAir = _settled = false;
        _move = _steer = Vector3.zero;
    }

    public void Order(Transform goal, CommandBoard.Job job)
    {
        _job = job; // how it goes about it: the same for now (attack = chase, extract / move = go there and stay)
        if (goal == _order) return;
        _order = goal;
        _orderOrganism = goal ? goal.GetComponentInParent<Organism>() : null;
        Surface cell = goal && !_orderOrganism ? goal.GetComponentInParent<Surface>() : null;
        _orderCell = cell ? cell.transform : null;
        _orderId = goal ? PathManager.Id(goal) : 0;
        _nextThink = 0f; // act on it now
    }

    // Called by SimulationTicker right before this virus's Organism ticks.
    public void BrainTick(float dt)
    {
        if (_order && !_order.gameObject.activeInHierarchy) Order(null, _job); // gone
        if (!_order && !ResolveTarget())
        {
            _o.Move = Vector3.zero;
            return;
        }

        if (Time.time >= _nextThink)
        {
            _nextThink = Time.time + thinkInterval;
            Think();
        }

        // Ease toward the decision: a snap every think turned crowd pushes into a shiver.
        _steer = steerSmoothing > 0f ? Vector3.Lerp(_steer, _move, 1f - Mathf.Exp(-dt / steerSmoothing)) : _move;
        if (_move == Vector3.zero && _steer.sqrMagnitude < 1e-4f) _steer = Vector3.zero;
        _o.Move = _steer;
        if (_move.sqrMagnitude > 1e-4f) _o.aimForward = _move.normalized; // Burst dashes along aim
    }

    bool ResolveTarget()
    {
        if (target && target.isActiveAndEnabled) return true;
        if (target) return false;

        VirusMovement player = FindAnyObjectByType<VirusMovement>();
        if (!player) return false;
        target = player.Organism;
        _targetId = PathManager.Id(target);
        return true;
    }

    void Think()
    {
        if (_targetId == 0 && target) _targetId = PathManager.Id(target);

        Vector3 pos = transform.position;
        bool grounded = _o.InState(_o.grounded);
        _groundCell = grounded ? _o.Surface : null;
        _inAir = !grounded;

        // The goal: a creature to chase (the order's, else the default target), or the ordered place.
        // hold: ordered onto a cell itself, so anywhere on it will do.
        _chase = _order ? _orderOrganism : target;
        Vector3 goal;
        Transform goalCell;
        bool hold = false;
        if (_chase)
        {
            goal = _chase.transform.position;
            goalCell = _chase.OnSurface ? _chase.Surface : null;
            _goalId = _order ? _orderId : _targetId;
        }
        else
        {
            goal = _order.position;
            goalCell = _orderCell;
            hold = _orderCell == _order;
            _goalId = _orderId;
        }

        if (grounded)
        {
            if (goalCell && SameBody(_o.Surface, goalCell))
            {
                _move = hold ? Hold(pos) : GroundMove(pos, goal, _chase ? _chase.transform : _order);
                return;
            }

            // The goal is flying, or on another cell: take off.
            _move = Vector3.zero;
            _settled = false;
            _o.Press(Intent.Jump);
            return;
        }

        Vector3 seek = goalCell
            ? ToCell(pos, goalCell, hold ? pos : goal)
            : AirMove(pos, goal, airStopDistance + (_settled ? regroupDistance : 0f), _goalId);

        Vector3 clear = Crowd(pos, Vector3.zero, true, seek, goal, out bool queued) * airSeparationStrength;
        if (queued) seek = Vector3.zero;
        if (!hold) clear += KeepClear(pos, goal, airStopDistance, Vector3.zero);
        _settled = seek == Vector3.zero;
        _move = Settle(seek + Resolve(clear));

        // Far away and heading there: dash.
        if (burstDistance > 0f && _o.InState(_o.flying) && _move.sqrMagnitude > 0.8f &&
            (goal - pos).sqrMagnitude > burstDistance * burstDistance)
            _o.Press(Intent.Charge);
    }

    // On the ordered cell: stay, just spreading out from the others there.
    Vector3 Hold(Vector3 pos)
    {
        Vector3 n = _o.SurfaceNormal;
        Vector3 clear = Crowd(pos, n, false, Vector3.zero, pos, out _) * separationStrength;
        _settled = true;
        return Settle(Vector3.ProjectOnPlane(Resolve(clear), n));
    }

    // Crawl toward the goal the shortest way round the surface, easing apart from other viruses on the
    // same cell and queueing behind stopped ones. Everything here is in the shown frame (tangent to
    // Organism.up, which Crawl expects; it eases round hard edges).
    Vector3 GroundMove(Vector3 pos, Vector3 goal, Transform goalTransform)
    {
        Vector3 n = _o.SurfaceNormal;
        Vector3 seek = Vector3.zero;

        float dist = Vector3.Distance(pos, goal);
        if (dist > groundStopDistance + (_settled ? regroupDistance : 0f))
            seek = SurfaceDirection(pos, goal, goalTransform) * Mathf.Clamp01((dist - groundStopDistance) / slowDownDistance);

        Vector3 clear = Crowd(pos, n, false, seek, goal, out bool queued) * separationStrength;
        if (queued) seek = Vector3.zero;
        clear += KeepClear(pos, goal, groundStopDistance, n);
        _settled = seek == Vector3.zero;
        return Settle(Vector3.ProjectOnPlane(seek + Resolve(clear), n));
    }

    // Unit direction toward the goal along the surface, in the shown frame. Far: SurfaceField (the
    // shortest way round, any shape). Near, or without a graph: straight at it, flattened onto the
    // surface; if it's nearly straight through (the far side of a thin part), keep going the way we face.
    Vector3 SurfaceDirection(Vector3 pos, Vector3 goal, Transform goalTransform)
    {
        NavSurface nav = _o.grounded.surface.nav;
        Vector3 up = nav.SurfaceNormal;

        if (nav.Surface && goalTransform)
        {
            NavSurface goalNav = _chase && _chase.OnSurface ? _chase.grounded.surface.nav : null;
            Pathfinding.TriangleMeshNode goalNode = goalNav != null && goalNav.Surface == nav.Surface ? goalNav.Node : null;
            SurfaceField field = SurfaceField.For(nav.Surface, goalTransform.GetInstanceID(), goal, goalNode);
            if (field != null && field.Direction(nav.Node, nav.GraphPosition, out Vector3 route))
            {
                Vector3 along = Vector3.ProjectOnPlane(route, up);
                if (along.sqrMagnitude > 1e-6f) return nav.ToShown(along.normalized);
            }
        }

        Vector3 chord = goal - pos;
        Vector3 flat = Vector3.ProjectOnPlane(chord, up);
        if (flat.magnitude < chord.magnitude * 0.35f) return nav.Heading;
        return nav.ToShown(flat.normalized);
    }

    // Distance to back off from the goal if closer than 'stop' (overshoot, or it came to us), so
    // the crowd doesn't shove it. n: surface normal to stay on, or zero in the air.
    static Vector3 KeepClear(Vector3 pos, Vector3 goal, float stop, Vector3 n)
    {
        if (stop <= 0f) return Vector3.zero;
        Vector3 away = pos - goal;
        if (n != Vector3.zero) away = Vector3.ProjectOnPlane(away, n);
        float d = away.magnitude;
        return d < stop && d > 1e-4f ? away / d * (stop - d) : Vector3.zero;
    }

    // A distance to cover -> a Move that covers it in crowdSettleTime (at this state's top speed).
    Vector3 Resolve(Vector3 distance) => distance / (crowdSettleTime * Mathf.Max(_o.totalSpeed, 0.5f));

    // Drop tiny nudges, with hysteresis: starting takes a real push, stopping a near-zero one,
    // so a packed crowd doesn't shuffle on and off.
    Vector3 Settle(Vector3 move)
    {
        float min = _move != Vector3.zero ? 0.03f : 0.1f;
        return move.sqrMagnitude < min * min ? Vector3.zero : Vector3.ClampMagnitude(move, 1f);
    }

    // Nearby viruses: in 3D among those in the air, along the surface among those on the same cell.
    // Returns how far to move to clear them (each side takes half of every overlap). queued: a stopped
    // virus is touching, ahead along 'seek' and nearer the goal, so this one should stop too.
    Vector3 Crowd(Vector3 pos, Vector3 n, bool air, Vector3 seek, Vector3 goal, out bool queued)
    {
        BuildGrid(thinkInterval * 0.5f);
        queued = false;

        Vector3 clear = Vector3.zero;
        float r = air ? airSeparationRadius : separationRadius;
        float touch = r * (_settled ? QueueReach : 1f); // stay queued a little farther than it takes to join
        bool seeking = seek != Vector3.zero;
        Vector3 ahead = seeking ? seek.normalized : Vector3.zero;
        float mine = (pos - goal).sqrMagnitude;
        Vector3Int c = Key(pos);

        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (!s_grid.TryGetValue(c + new Vector3Int(dx, dy, dz), out List<VirusAI> list)) continue;

            foreach (VirusAI other in list)
            {
                if (other == this || (air ? !other._inAir : other._groundCell != _groundCell)) continue;

                Vector3 away = pos - other._gridPos;
                float d = away.magnitude;
                if (d >= touch && d >= r) continue;

                if (seeking && !queued && d < touch && other._settled &&
                    Vector3.Dot(-away, ahead) > 0.5f * d && (other._gridPos - goal).sqrMagnitude < mine)
                    queued = true;

                if (d >= r) continue;
                Vector3 t = air ? away : Vector3.ProjectOnPlane(away, n);
                if (t.sqrMagnitude < 1e-8f) // same spot: split them by id
                    t = (air ? transform.right : Vector3.ProjectOnPlane(transform.right, n)) *
                        (GetInstanceID() < other.GetInstanceID() ? 1f : -1f);
                if (t.sqrMagnitude < 1e-8f) continue;

                clear += t.normalized * ((r - d) * 0.5f);
            }
        }
        return clear;
    }

    const float QueueReach = 1.25f;

    static void BuildGrid(float maxAge)
    {
        if (Time.time - s_gridBuilt < maxAge) return;
        s_gridBuilt = Time.time;

        foreach (List<VirusAI> list in s_grid.Values)
        {
            list.Clear();
            s_pool.Push(list);
        }
        s_grid.Clear();

        // Bucket size = the largest separation radius (at queue reach), so 3x3x3 buckets cover every query.
        float size = 0.1f;
        foreach (VirusAI a in s_agents)
            size = Mathf.Max(size, Mathf.Max(a.separationRadius, a.airSeparationRadius) * QueueReach);
        s_gridSize = size;

        foreach (VirusAI a in s_agents)
        {
            if (!a._groundCell && !a._inAir) continue;
            a._gridPos = a.transform.position;

            Vector3Int k = Key(a._gridPos);
            if (!s_grid.TryGetValue(k, out List<VirusAI> list))
            {
                list = s_pool.Count > 0 ? s_pool.Pop() : new List<VirusAI>();
                s_grid[k] = list;
            }
            list.Add(a);
        }
    }

    static Vector3Int Key(Vector3 p) => Vector3Int.FloorToInt(p / s_gridSize);

    // Through the flow field toward a point, easing off before 'stop'.
    Vector3 AirMove(Vector3 pos, Vector3 goal, float stop, int ignore)
    {
        float dist = Vector3.Distance(pos, goal);
        if (dist <= stop) return Vector3.zero;

        Vector3 dir = PathManager.I
            ? PathManager.I.GetDirection(pos, goal, _selfId, ignore)
            : (goal - pos) / dist;

        return dir * Mathf.Clamp01((dist - stop) / slowDownDistance);
    }

    // Fly onto the surface under 'near' (the goal, or this virus for the nearest side). The cell is
    // left out of the flow field, so it pulls the virus in instead of pushing it away; every other
    // cell is still avoided. The goal sits on the surface, so the virus touches down (and lands)
    // before reaching it.
    Vector3 ToCell(Vector3 pos, Transform cell, Vector3 near)
    {
        if (cell != _cell)
        {
            _cell = cell;
            _cellSurface = cell.GetComponentInParent<Surface>();
            _cellCollider = cell.GetComponentInChildren<Collider>();
            _cellId = PathManager.Id(cell);
        }

        Vector3 goal = near;
        // The surface point under it, on any shape (ClosestPoint needs a convex collider; a Surface's pieces are).
        if (_cellSurface && _cellSurface.Colliders.Length > 0)
            goal = _cellSurface.ClosestPoint(goal);
        else if (_cellCollider && !(_cellCollider is MeshCollider mc && !mc.convex))
            goal = _cellCollider.ClosestPoint(goal);
        else
        {
            // Fallback: the bounding sphere under it.
            Bounds b = _cellCollider ? _cellCollider.bounds : new Bounds(cell.position, Vector3.zero);
            float radius = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z));
            Vector3 up = goal - b.center;
            goal = b.center + (up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up) * radius;
        }

        return AirMove(pos, goal, 0f, _cellId);
    }

    static bool SameBody(Transform a, Transform b)
    {
        if (!a || !b) return false;
        if (a == b || a.IsChildOf(b) || b.IsChildOf(a)) return true;
        Rigidbody ra = a.GetComponentInParent<Rigidbody>();
        return ra && ra == b.GetComponentInParent<Rigidbody>();
    }
}
