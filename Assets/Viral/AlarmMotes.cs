using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

/// <summary>
/// A cell's warning, made visible: tiny glowing motes (a hormone, an alarm) spurting out of the cell
/// where a virus lands, walks or drills in, then slowing, floating off and throbbing as they fade --
/// something out there has been called. Loud cells keep letting a few out round their hotspot while
/// they're still calling. Owned by ImmuneSystem (<see cref="ImmuneSystem.Alarm"/> emits).
///
/// Cost: the CPU only writes new motes into a ring buffer (one upload per frame of what was emitted,
/// nothing per live mote); Hidden/AlarmMote moves, stretches, throbs and fades each one from its birth
/// data in the vertex stage, one procedural draw for all of them. Emission off screen or past
/// <see cref="drawDistance"/> is dropped.
/// </summary>
[Serializable]
public class AlarmMotes
{
    [Min(0f), Tooltip("Motes per unit of signal raised by activity.")]
    public float perSignal = 4f;
    [Min(0f), Tooltip("Motes per second per unit of signal from each cell still calling (at its hotspot).")]
    public float calling = 0.06f;
    [Range(256, 32768)] public int maxMotes = 8192;
    public Vector2 life = new Vector2(2.5f, 4.5f);
    [Tooltip("Half-width of a mote's glow, metres.")]
    public Vector2 size = new Vector2(0.1f, 0.24f);
    [Tooltip("Speed it spurts out of the cell at.")]
    public Vector2 ejectSpeed = new Vector2(2.5f, 7f);
    [Range(0f, 80f), Tooltip("Cone round the surface normal they spurt in, degrees.")]
    public float spread = 35f;
    [Min(0f), Tooltip("How quickly the spurt slows (1/s).")]
    public float drag = 2.2f;
    [Min(0f), Tooltip("Speed it floats on off the cell after.")]
    public float drift = 0.4f;
    [Min(0f), Tooltip("Warning throbs per second.")]
    public float pulseRate = 2.2f;
    [ColorUsage(false, true)] public Color color = new Color(1f, 0.42f, 0.28f, 1f);
    [ColorUsage(false, true)] public Color core = new Color(1.6f, 1.25f, 0.8f, 1f);
    [Min(1f)] public float drawDistance = 250f;
    [Tooltip("Hidden/AlarmMote. Found by name when empty (editor only: keep it assigned for builds).")]
    public Shader shader;

    // Birth data, 48 bytes: origin + birth time, launch velocity + life, surface normal + seed.
    struct Mote { public Vector4 origin, launch, normal; }
    const int Stride = 48;

    Mote[] _pending = new Mote[256];
    int _pendingCount, _next, _used;
    float _lastBirth = float.NegativeInfinity;
    GraphicsBuffer _buffer;
    Material _mat;
    MaterialPropertyBlock _props;

    static readonly int MotesId = Shader.PropertyToID("_Motes"), ColorId = Shader.PropertyToID("_MoteColor"),
                        CoreId = Shader.PropertyToID("_MoteCore"), ShapeId = Shader.PropertyToID("_MoteShape"),
                        SizeId = Shader.PropertyToID("_MoteSize");

    /// <summary>'count' motes spurting out of a surface at 'at' along 'normal' (world).</summary>
    public void Emit(Vector3 at, Vector3 normal, int count)
    {
        if (count <= 0 || normal.sqrMagnitude < 1e-6f) return;
        if ((at - SimulationTicker.CameraPosition).sqrMagnitude > drawDistance * drawDistance ||
            !SimulationTicker.OnScreen(at, ejectSpeed.y / Mathf.Max(drag, 0.1f) + 2f)) return;
        normal.Normalize();
        count = Mathf.Min(count, maxMotes / 4);
        if (_pending == null || _pending.Length < _pendingCount + count)
            Array.Resize(ref _pending, Mathf.NextPowerOfTwo(_pendingCount + count));
        float now = Time.time;
        for (int i = 0; i < count; i++)
        {
            Vector3 dir = Quaternion.AngleAxis(Random.value * spread, Random.onUnitSphere) * normal;
            if (Vector3.Dot(dir, normal) < 0.2f) dir = normal;
            Vector3 v = dir.normalized * Random.Range(ejectSpeed.x, ejectSpeed.y);
            Vector3 o = at + normal * 0.05f + Random.insideUnitSphere * 0.15f;
            _pending[_pendingCount++] = new Mote
            {
                origin = new Vector4(o.x, o.y, o.z, now - Random.value * 0.03f), // a spurt, not one frame's wall
                launch = new Vector4(v.x, v.y, v.z, Random.Range(life.x, life.y)),
                normal = new Vector4(normal.x, normal.y, normal.z, Random.value),
            };
        }
        _lastBirth = now;
    }

    /// <summary>Uploads this frame's new motes and draws every live one (ImmuneSystem, once a frame).</summary>
    public void Draw()
    {
        if (!Ready()) { _pendingCount = 0; return; }
        // New motes go into the ring after the last; the oldest give way.
        int n = Mathf.Min(_pendingCount, maxMotes);
        int first = Mathf.Min(n, maxMotes - _next);
        if (first > 0) _buffer.SetData(_pending, _pendingCount - n, _next, first);
        if (n - first > 0) _buffer.SetData(_pending, _pendingCount - n + first, 0, n - first);
        _next = (_next + n) % maxMotes;
        _used = Mathf.Min(maxMotes, _used + n);
        _pendingCount = 0;
        if (_used == 0 || Time.time - _lastBirth > life.y + 0.5f) return; // all long gone

        _props.SetBuffer(MotesId, _buffer);
        _props.SetColor(ColorId, color);
        _props.SetColor(CoreId, core);
        _props.SetVector(ShapeId, new Vector4(drag, drift, pulseRate, 0f));
        _props.SetVector(SizeId, new Vector4(size.x, size.y, 0f, 0f));
        var rp = new RenderParams(_mat)
        {
            matProps = _props,
            worldBounds = new Bounds(SimulationTicker.CameraPosition, Vector3.one * (drawDistance * 2f)),
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
        };
        Graphics.RenderPrimitives(rp, MeshTopology.Triangles, _used * 6);
    }

    bool Ready()
    {
        _props ??= new MaterialPropertyBlock(); // plain fields: remade after a play-mode script reload
        _pending ??= new Mote[256];
        if (_buffer == null || !_buffer.IsValid() || _buffer.count != maxMotes)
        {
            _buffer?.Release();
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxMotes, Stride);
            _buffer.SetData(new Mote[maxMotes]); // life 0: nothing drawn until written
            _next = _used = 0;
        }
        if (!_mat)
        {
            Shader s = shader ? shader : Shader.Find("Hidden/AlarmMote");
            if (!s) return false;
            _mat = new Material(s) { name = "Alarm Motes", hideFlags = HideFlags.DontSave };
        }
        return true;
    }

    public void Release()
    {
        _buffer?.Release();
        _buffer = null;
        if (_mat) Object.Destroy(_mat);
    }
}
