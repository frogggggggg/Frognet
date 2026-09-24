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
///   crawls after the target along the surface, following the same flow field
///   flattened onto the cell (so it walks around other viruses and obstacles).
/// - Standing on any other cell: take off again.
/// - Viruses keep apart (separation): in the air in 3D, on the ground along the
///   surface of the cell they share, so a crowd spreads out around the target
///   instead of piling up. They also keep clear of the target itself, so they
///   don't shove it. (On top of PathManager, which also routes them around each other.)
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
    [Min(0.1f), Tooltip("Other flying viruses closer than this push this one away.")]
    public float airSeparationRadius = 2.5f;
    [Min(0f), Tooltip("How hard that push is, against the pull toward the goal (which is at most 1).")]
    public float airSeparationStrength = 1f;

    [Header("Ground")]
    [Min(0f), Tooltip("Stop this far from the target on the same cell.")]
    public float groundStopDistance = 2f;
    [Min(0.1f), Tooltip("Other viruses on the same cell closer than this push this one away.")]
    public float separationRadius = 1.5f;
    [Min(0f), Tooltip("How hard that push is, against the pull toward the target (which is at most 1).")]
    public float separationStrength = 1.5f;

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
    Vector3 _move;

    // Neighbours: every AI virus (flying or grounded) goes into one coarse spatial hash, rebuilt at
    // most every half think interval, so a separation query only touches nearby buckets
    // however many viruses there are.
    static readonly List<VirusAI> s_agents = new List<VirusAI>();
    static readonly Dictionary<Vector3Int, List<VirusAI>> s_grid = new Dictionary<Vector3Int, List<VirusAI>>();
    static readonly Stack<List<VirusAI>> s_pool = new Stack<List<VirusAI>>();
    static float s_gridBuilt = float.NegativeInfinity, s_gridSize = 1f;

    Transform _groundCell; // cell this virus stood on at its last think; null in the air
    Transform _groundIdCell;
    int _groundCellId;     // its flow-field id, to leave it out of surface queries
    bool _inAir;           // flying (or jumping) at its last think
    Vector3 _gridPos;      // position when the hash was built

    // Cached per goal cell: its collider (for centre and radius) and flow-field id.
    Transform _cell;
    Collider _cellCollider;
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
        _inAir = false;
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

        _o.Move = _move;
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
                _move = hold ? Hold(pos) : GroundMove(pos, goal);
                return;
            }

            // The goal is flying, or on another cell: take off.
            _move = Vector3.zero;
            _o.Press(Intent.Jump);
            return;
        }

        Vector3 move = goalCell
            ? ToCell(pos, goalCell, hold ? pos : goal)
            : AirMove(pos, goal, airStopDistance, _goalId);

        move += Separation(pos, Vector3.zero, true) * airSeparationStrength;
        if (!hold) move += KeepClear(pos, goal, airStopDistance, Vector3.zero);
        _move = Vector3.ClampMagnitude(move, 1f);

        // Far away and heading there: dash.
        if (burstDistance > 0f && _o.InState(_o.flying) && _move.sqrMagnitude > 0.8f &&
            (goal - pos).sqrMagnitude > burstDistance * burstDistance)
            _o.Press(Intent.Charge);
    }

    // On the ordered cell: stay, just spreading out from the others there.
    Vector3 Hold(Vector3 pos)
    {
        Vector3 n = _o.SurfaceNormal;
        Vector3 move = Vector3.ProjectOnPlane(Separation(pos, n, false) * separationStrength, n);
        return move.sqrMagnitude < 0.0025f ? Vector3.zero : Vector3.ClampMagnitude(move, 1f);
    }

    // Crawl toward the goal along the surface (the tangent of the shortest way round),
    // pushed apart from other viruses on the same cell.
    Vector3 GroundMove(Vector3 pos, Vector3 goal)
    {
        Vector3 n = _o.SurfaceNormal;
        Vector3 seek = Vector3.zero;

        Vector3 to = goal - pos;
        float dist = to.magnitude;
        if (dist > groundStopDistance)
        {
            Vector3 dir = SurfaceFlow(pos, n, goal);
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.ProjectOnPlane(to, n);
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.ProjectOnPlane(transform.forward, n); // directly opposite: any way round
            if (dir.sqrMagnitude > 1e-6f)
                seek = dir.normalized * Mathf.Clamp01((dist - groundStopDistance) / slowDownDistance);
        }

        Vector3 move = Vector3.ProjectOnPlane(seek + Separation(pos, n, false) * separationStrength
                                                     + KeepClear(pos, goal, groundStopDistance, n), n);
        if (move.sqrMagnitude < 0.0025f) return Vector3.zero; // don't shuffle for tiny nudges
        return Vector3.ClampMagnitude(move, 1f);
    }

    // PathManager's field flattened onto the surface: the pull toward the target (whose chord,
    // projected, is the shortest way round the cell) bent around every obstacle near this
    // spot -- other viruses, the target, neighbouring cells. The cell underfoot is left out:
    // standing on it puts us inside its margin, where the field would only push us off.
    Vector3 SurfaceFlow(Vector3 pos, Vector3 n, Vector3 goal)
    {
        if (!PathManager.I || !_groundCell) return Vector3.zero;
        if (_groundCell != _groundIdCell)
        {
            _groundIdCell = _groundCell;
            _groundCellId = PathManager.Id(_groundCell);
        }

        Vector3 field = PathManager.I.GetField(pos, goal, _selfId, _groundCellId);
        Vector3 tangent = Vector3.ProjectOnPlane(field, n);

        // Target nearly straight through the cell (its far side): the tangent is small and
        // swings from one way round to the other as we move. Keep going the way we were.
        Vector3 keep = Vector3.ProjectOnPlane(_move, n);
        if (keep.sqrMagnitude > 1e-6f && tangent.magnitude < field.magnitude * 0.35f)
            return keep.normalized * field.magnitude;
        return tangent;
    }

    // Back off from the goal if closer than 'stop' (overshoot, or it came to us), so the
    // crowd doesn't shove it. n: surface normal to stay on, or zero in the air.
    Vector3 KeepClear(Vector3 pos, Vector3 goal, float stop, Vector3 n)
    {
        if (stop <= 0f) return Vector3.zero;
        Vector3 away = pos - goal;
        if (n != Vector3.zero) away = Vector3.ProjectOnPlane(away, n);
        float d = away.magnitude;
        return d < stop && d > 1e-4f ? away / d * (1f - d / stop) : Vector3.zero;
    }

    // Push away from nearby viruses, stronger when closer: in 3D from others in the air,
    // or along the surface from others on the same cell.
    Vector3 Separation(Vector3 pos, Vector3 n, bool air)
    {
        BuildGrid(thinkInterval * 0.5f);

        Vector3 push = Vector3.zero;
        float r = air ? airSeparationRadius : separationRadius;
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
                if (d >= r) continue;

                Vector3 t = air ? away : Vector3.ProjectOnPlane(away, n);
                if (t.sqrMagnitude < 1e-8f) // same spot: split them by id
                    t = (air ? transform.right : Vector3.ProjectOnPlane(transform.right, n)) *
                        (GetInstanceID() < other.GetInstanceID() ? 1f : -1f);
                if (t.sqrMagnitude < 1e-8f) continue;

                push += t.normalized * (1f - d / r);
            }
        }
        return push;
    }

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

        // Bucket size = the largest separation radius, so 3x3x3 buckets cover every query.
        float size = 0.1f;
        foreach (VirusAI a in s_agents)
            size = Mathf.Max(size, Mathf.Max(a.separationRadius, a.airSeparationRadius));
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
            _cellCollider = cell.GetComponentInChildren<Collider>();
            _cellId = PathManager.Id(cell);
        }

        Vector3 goal = near;
        // The surface point under it, on any shape (ClosestPoint needs a convex collider).
        if (_cellCollider && !(_cellCollider is MeshCollider mc && !mc.convex))
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
