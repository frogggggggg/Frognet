using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Soft coloured dust for anything: <see cref="Burst"/> (a cloud puffing out from a point and
/// settling, e.g. a chunk poofing away) and <see cref="Stream"/> (a puff drawn along a curve into a
/// target, e.g. a substance flowing into the virus being extracted).
///
/// Every puff's motion is analytic (Hidden/DustCloud works out where it is from its birth time), so
/// the CPU never touches a live puff: it writes new ones into a ring buffer (a partial upload of just
/// those) and issues one draw of billboards per frame while any are alive. Cost: nothing per live puff
/// on the CPU; the GPU draws <see cref="maxPuffs"/> quads at most. The oldest puff gives way when full.
/// Drawn after the focus sweep (Overlay queue), so it shows in focus mode too.
/// Created on first use; add one to a scene to change its settings (and assign the shader for builds).
/// </summary>
public class DustClouds : MonoBehaviour
{
    [Range(256, 16384)] public int maxPuffs = 4096;
    [Tooltip("Hidden/DustCloud. Found by name when empty (editor only: keep it assigned for builds).")]
    public Shader shader;

    // a: origin + size, b: velocity (burst) or bend point (stream) + kind, c: drift (burst) or target
    // (stream) + drag, colour, time: birth, life, seed, growth.
    struct Puff { public Vector4 a, b, c, color, time; }
    const int Stride = 80;

    Puff[] _puffs;
    int _next, _used, _dirtyFrom = -1, _dirtyCount;
    float _lastDeath;
    GraphicsBuffer _buffer;
    Material _material;
    MaterialPropertyBlock _props;
    static readonly int PuffsId = Shader.PropertyToID("_DustPuffs");

    static DustClouds s_instance;

    public static DustClouds Instance
    {
        get
        {
            if (s_instance) return s_instance;
            s_instance = FindAnyObjectByType<DustClouds>();
            if (!s_instance) s_instance = new GameObject("Dust Clouds").AddComponent<DustClouds>();
            return s_instance;
        }
    }

    /// <summary>A cloud of 'count' puffs bursting out from 'centre' to about 'radius' and drifting apart.</summary>
    public static void Burst(Vector3 centre, float radius, Color color, int count, float life = 2.2f)
    {
        DustClouds d = Instance;
        for (int i = 0; i < count; i++)
        {
            Vector3 dir = Random.onUnitSphere;
            float speed = radius * Random.Range(1.2f, 3.2f), drag = Random.Range(2.2f, 3.5f);
            Color c = Color.Lerp(color, Color.white, Random.Range(0f, 0.25f));
            c.a = color.a * Random.Range(0.55f, 0.95f);
            d.Add(new Puff
            {
                a = Pack(centre + dir * radius * Random.Range(0f, 0.5f), radius * Random.Range(0.35f, 0.65f)),
                b = Pack(dir * speed, 0f),
                c = Pack(Random.insideUnitSphere * radius * 0.08f + Vector3.up * radius * 0.05f, drag),
                color = c,
                time = new Vector4(Time.time + Random.Range(0f, 0.08f), life * Random.Range(0.7f, 1.2f), Random.value, 2.2f),
            });
        }
    }

    /// <summary>One puff drawn from 'from' into 'to' over 'life' seconds, bowing out along 'bend'
    /// (a world offset at the middle of the way), shrinking as it arrives.</summary>
    public static void Stream(Vector3 from, Vector3 to, Vector3 bend, float size, Color color, float life)
    {
        Instance.Add(new Puff
        {
            a = Pack(from, size),
            b = Pack((from + to) * 0.5f + bend, 1f),
            c = Pack(to, 0f),
            color = color,
            time = new Vector4(Time.time, life, Random.value, 0.3f),
        });
    }

    static Vector4 Pack(Vector3 v, float w) => new Vector4(v.x, v.y, v.z, w);

    void Add(Puff p)
    {
        if (_puffs == null || _puffs.Length != maxPuffs)
        {
            _puffs = new Puff[maxPuffs];
            _next = _used = 0;
            _dirtyFrom = -1;
            _buffer?.Release();
            _buffer = null;
        }
        _puffs[_next] = p;
        // One dirty run per frame; a wrap (or a gap) uploads the whole ring instead.
        if (_dirtyFrom < 0) { _dirtyFrom = _next; _dirtyCount = 1; }
        else if (_dirtyFrom + _dirtyCount == _next && _dirtyCount < maxPuffs) _dirtyCount++;
        else { _dirtyFrom = 0; _dirtyCount = maxPuffs; }
        _next = (_next + 1) % maxPuffs;
        _used = Mathf.Min(_used + 1, maxPuffs);
        _lastDeath = Mathf.Max(_lastDeath, p.time.x + p.time.y);
    }

    void OnEnable() => s_instance = this;

    void OnDestroy()
    {
        _buffer?.Release();
        _buffer = null;
        if (_material) Destroy(_material);
    }

    void LateUpdate()
    {
        if (_puffs == null) _used = 0; // a play-mode script reload dropped them
        if (_used == 0 || Time.time > _lastDeath) return;
        _props ??= new MaterialPropertyBlock(); // plain fields: remade after a play-mode script reload
        if (!_material)
        {
            Shader s = shader ? shader : Shader.Find("Hidden/DustCloud");
            if (!s) return;
            _material = new Material(s) { name = "Dust Cloud", hideFlags = HideFlags.DontSave };
        }
        if (_buffer == null || !_buffer.IsValid())
        {
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxPuffs, Stride);
            _buffer.SetData(_puffs); // everything (a reload also lands here)
            _dirtyFrom = -1;
        }
        if (_dirtyFrom >= 0)
        {
            _buffer.SetData(_puffs, _dirtyFrom, _dirtyFrom, _dirtyCount);
            _dirtyFrom = -1;
        }
        _props.SetBuffer(PuffsId, _buffer);
        var rp = new RenderParams(_material)
        {
            matProps = _props,
            worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f),
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
        };
        Graphics.RenderPrimitives(rp, MeshTopology.Triangles, _used * 6);
    }
}
