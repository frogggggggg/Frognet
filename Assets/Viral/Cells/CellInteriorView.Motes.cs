using System;
using System.Collections.Generic;
using UnityEngine;

// The stores as motes: lumps of a substance, as many as the stores hold (motesPerSlot per full slot), each one kept
// from birth to end, so the stores' changes are seen as movement, never as things popping in or out:
//   - resting spots (homes) are sampled once per mesh *inside its real shape* (SurfaceMap: a point is in when it's
//     behind the nearest surface point's normal, at least a margin deep), so a red cell's motes fill its rim and
//     thin middle, not a stretched ball; energy (Honey) with a nucleus pools in a band round it;
//   - driven by what actually moved (CellInterior's flow ledger: each organelle kind's used / made units, each
//     port's moved units; one mote per capacity / motesPerSlot units): an organelle's used inputs are the motes
//     nearest it *now* (resting or settling: where a moving one is is worked out from its trip, `Where`), sucked in
//     and swallowed; its outputs come out of it (growing in) to a free home; a membrane flow crosses at the spot
//     facing it (in: grows in there; out: the mote nearest that spot is sucked out through it). Flows net out in the store (uptake refills
//     what mitochondria use), so counting the store alone showed almost nothing being used;
//   - what flows don't cover (a store set directly, rounding) is squared with the store past 1.5 motes: from a
//     working maker / into a working user, else the mote nearest the membrane is sucked out through it;
//   - honey (ATP) drops run together like a liquid in the shader; the view keeps the list of each cell's honey
//     motes for it (`_CellHoney`, rebuilt when the cell's motes change). Honey made by a body rides its stream
//     (HoneyPath) to the free pool home nearest where the stream joins the pool (PoolPoint) and stays there (no
//     wandering: a still pool); honey spent with no sink drains away where it lies (end 3), not flown out;
//   - now and then a resting mote drifts to a free home nearby (the cytoplasm stirring).
// The CPU only acts on those events (writes the mote's trip; the cell's 20 KB goes up when one changed); the shader
// eases each trip and wobbles resting motes. Cost per shown cell: O(motes x substances) a frame, 256 motes.
public partial class CellInteriorView
{
    const int MotesPerCell = 256, HomeCount = 384, MoteStride = 80, MaxKinds = 16;

    // One mote on the GPU: from 'from' to 'to' over time.y seconds from time.x (eased, bowed), then rests there
    // (end 0), is swallowed (1), fades (2) or drains away in place (3, honey). w of from / to: the seed of the body it's tied to (drifts with it),
    // -1 none.
    struct Mote { public Vector4 from, to, time, look, color; }
    // time: x start (Time.time = the shader's _Time.y), y duration, z end, w frame slot
    // look: x SubstanceLook, y size (0: none), z seed, w 1 = born (grows in over the trip's start)

    enum MoteState : byte { Free, Settling, Resting, Leaving }

    sealed class Motes
    {
        public readonly Mote[] gpu = new Mote[MotesPerCell];
        public readonly Substance[] sub = new Substance[MotesPerCell];
        public readonly MoteState[] state = new MoteState[MotesPerCell];
        public readonly int[] home = new int[MotesPerCell];
        public readonly float[] end = new float[MotesPerCell];
        public bool dirty = true, filled;
        public Vector3[] homes; // frame
        public float[] depth;   // frame units under the membrane
        public bool[] taken;
        public readonly uint[] honey = new uint[MotesPerCell]; // global indices of the honey motes (the shader's list)
        public int honeyCount;
        public readonly List<int> near = new List<int>(), open = new List<int>(), edge = new List<int>();

        /// <summary>The home nearest 'p' at least 'minDepth' deep ('p' itself until homes exist).</summary>
        public Vector3 Snap(Vector3 p, float minDepth)
        {
            if (homes == null) return p;
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < homes.Length; i++)
            {
                if (depth[i] < minDepth) continue;
                float d = (homes[i] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best >= 0 ? homes[best] : p;
        }
    }

    // Per mesh (its SurfaceMap): points inside, mesh space, w depth under the surface.
    static readonly Dictionary<SurfaceMap, Vector4[]> s_meshHomes = new Dictionary<SurfaceMap, Vector4[]>();
    static readonly Mote[] s_noMotes = new Mote[MotesPerCell];

    readonly Substance[] _kinds = new Substance[MaxKinds];
    readonly float[] _want = new float[MaxKinds];
    readonly int[] _have = new int[MaxKinds];
    uint _rng = 0x2545F491u;

    float Next()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return (_rng & 0xffffff) / 16777216f;
    }

    void ClearMotes(int slot)
    {
        if (_moteBuffer != null && _moteBuffer.IsValid())
            _moteBuffer.SetData(s_noMotes, 0, slot * MotesPerCell, MotesPerCell);
    }

    // ---------------- homes ----------------

    void Homes(Shown s, MeshFilter mf, Matrix4x4 meshToFrame)
    {
        Motes m = s.motes;
        if (m.homes != null) return;
        Surface surface = mf.GetComponent<Surface>();
        if (!surface) surface = s.cell.GetComponentInChildren<Surface>();
        SurfaceMap map = surface ? surface.Map : null;
        if (surface && map != null && !map.Ready) return; // building; motes wait for it

        int n;
        if (map != null)
        {
            Vector4[] points = MeshHomes(map, mf.sharedMesh.bounds);
            n = points.Length;
            float scale = new Vector3(meshToFrame.m00, meshToFrame.m01, meshToFrame.m02).magnitude;
            m.homes = new Vector3[n];
            m.depth = new float[n];
            for (int i = 0; i < n; i++)
            {
                m.homes[i] = meshToFrame.MultiplyPoint3x4(points[i]);
                m.depth[i] = points[i].w * scale;
            }
        }
        else
        {
            // No mesh map: the frame's ball.
            n = HomeCount;
            m.homes = new Vector3[n];
            m.depth = new float[n];
            uint rng = 0x9E3779B9u;
            for (int i = 0; i < n; i++)
            {
                Vector3 p;
                do p = new Vector3(Hash(ref rng), Hash(ref rng), Hash(ref rng)) * 2f - Vector3.one;
                while (p.sqrMagnitude > 1f);
                m.homes[i] = p * 0.92f;
                m.depth[i] = 1f - p.magnitude * 0.92f;
            }
        }
        m.taken = new bool[n];

        m.near.Clear(); m.open.Clear(); m.edge.Clear();
        for (int i = 0; i < n; i++)
        {
            float r = m.homes[i].magnitude;
            if (r >= nucleusSize + 0.03f && r <= nucleusSize + 0.2f) m.near.Add(i); // honey pools close round it
            if (r >= nucleusSize + 0.08f) m.open.Add(i);
        }
        if (m.open.Count == 0) for (int i = 0; i < n; i++) m.open.Add(i);
        if (m.near.Count == 0) m.near.AddRange(m.open);
        // The shallowest third: where things cross the membrane.
        var depths = new float[m.open.Count];
        for (int i = 0; i < depths.Length; i++) depths[i] = m.depth[m.open[i]];
        Array.Sort(depths);
        float shallow = depths[depths.Length / 3];
        foreach (int i in m.open) if (m.depth[i] <= shallow) m.edge.Add(i);
    }

    static Vector4[] MeshHomes(SurfaceMap map, Bounds b)
    {
        if (s_meshHomes.TryGetValue(map, out Vector4[] known)) return known;
        Vector3 e = b.extents;
        float margin = 0.045f * Mathf.Max(e.x, Mathf.Max(e.y, e.z));
        var points = new List<Vector4>(HomeCount);
        uint rng = 0x51ED270Bu;
        for (int tries = 0; tries < HomeCount * 40 && points.Count < HomeCount; tries++)
        {
            Vector3 p = b.min + Vector3.Scale(b.size, new Vector3(Hash(ref rng), Hash(ref rng), Hash(ref rng)));
            if (!map.Nearest(p, Vector3.zero, -2f, out Vector3 q, out Vector3 normal, out _)) continue;
            Vector3 d = p - q;
            if (Vector3.Dot(d, normal) >= 0f || d.magnitude < margin) continue; // outside, or too near the membrane
            points.Add(new Vector4(p.x, p.y, p.z, d.magnitude));
        }
        if (points.Count == 0) points.Add(new Vector4(b.center.x, b.center.y, b.center.z, margin));
        Vector4[] result = points.ToArray();
        s_meshHomes[map] = result;
        return result;
    }

    static float Hash(ref uint v)
    {
        v ^= v << 13;
        v ^= v >> 17;
        v ^= v << 5;
        return (v & 0xffffff) / 16777216f;
    }

    // ---------------- events ----------------

    void UpdateMotes(Shown s, int placed, Matrix4x4 toFrame)
    {
        Motes m = s.motes;
        if (m.homes == null) return;
        CellInterior cell = s.cell;
        float now = Time.time;

        // Trips ending.
        for (int i = 0; i < MotesPerCell; i++)
        {
            if (m.state[i] == MoteState.Settling && now >= m.end[i]) m.state[i] = MoteState.Resting;
            else if (m.state[i] == MoteState.Leaving && now >= m.end[i])
            {
                m.state[i] = MoteState.Free;
                m.sub[i] = null;
                m.gpu[i].look.y = 0f;
                m.dirty = true;
            }
        }

        // How many of each substance the stores call for, against how many are in (or on their way in).
        int kinds = 0;
        float perUnit = motesPerSlot / Mathf.Max(cell.Capacity, 1e-3f), total = 0f;
        foreach (StoreSlot slot in cell.store)
        {
            if (slot.Empty) continue;
            int k = Kind(SubstanceCatalog.Find(slot.substance), ref kinds);
            if (k < 0) continue;
            _want[k] += slot.amount * perUnit;
            total += slot.amount * perUnit;
        }
        for (int i = 0; i < MotesPerCell; i++)
        {
            if (m.state[i] != MoteState.Settling && m.state[i] != MoteState.Resting) continue;
            int k = Kind(m.sub[i], ref kinds);
            if (k >= 0) _have[k]++;
        }
        float fit = total > MotesPerCell * 0.9f ? MotesPerCell * 0.9f / total : 1f;

        if (!m.filled)
        {
            // First look: everything already there, resting; flows from before don't count.
            cell.ForgetFlows();
            for (int k = 0; k < kinds; k++)
                while (_have[k] < Mathf.RoundToInt(_want[k] * fit) && Rest(s, _kinds[k])) _have[k]++;
            m.filled = true;
        }
        else
        {
            Flows(s, placed, toFrame, 1f / perUnit, ref kinds);
            // Whatever the flows didn't cover (stores set directly, rounding): a working maker / user, else the membrane.
            for (int k = 0; k < kinds; k++)
            {
                float want = _want[k] * fit;
                if (want > _have[k] + 1.5f && Arrive(s, _kinds[k], Source(s, _kinds[k], placed))) _have[k]++;
                else if (want < _have[k] - 1.5f && Leave(s, _kinds[k], Sink(_kinds[k], placed, out float end), end)) _have[k]--;
            }
        }

        // Stirring.
        float chance = Time.deltaTime / restSeconds;
        for (int i = 0; i < MotesPerCell; i++)
            if (m.state[i] == MoteState.Resting && m.sub[i].look != SubstanceLook.Honey && Next() < chance) Wander(s, i);
    }

    // What actually moved since the last frame, one mote per 'unit': each organelle's used inputs swallowed into it
    // and its outputs coming out of it, each membrane flow crossing where it faces.
    void Flows(Shown s, int placed, Matrix4x4 toFrame, float unit, ref int kinds)
    {
        CellInterior cell = s.cell;
        for (int g = 0; g < cell.organelles.Count; g++)
        {
            CellInterior.Organelles group = cell.organelles[g];
            OrganelleType type = group.type;
            if (!type || BodyOf(g, placed) < 0) continue;
            for (int k = 0; group.used != null && k < group.used.Length && k < type.inputs.Length; k++)
            {
                int n = CellInterior.Claim(ref group.used[k], unit, movesPerFrame);
                Substance sub = SubstanceCatalog.Find(type.inputs[k].substance);
                for (int t = 0; t < n; t++)
                    if (Leave(s, sub, _placed[BodyOf(g, placed)].at, 1f)) Tally(sub, ref kinds, -1);
            }
            for (int k = 0; group.made != null && k < group.made.Length && k < type.outputs.Length; k++)
            {
                int n = CellInterior.Claim(ref group.made[k], unit, movesPerFrame);
                Substance sub = SubstanceCatalog.Find(type.outputs[k].substance);
                for (int t = 0; t < n; t++)
                    if (Arrive(s, sub, _placed[BodyOf(g, placed)].at)) Tally(sub, ref kinds, 1);
            }
        }
        IReadOnlyList<CellInterior.Port> ports = cell.Ports;
        for (int p = 0; p < ports.Count; p++)
        {
            int n = cell.ClaimPort(p, unit, movesPerFrame);
            if (n == 0) continue;
            CellInterior.Port port = ports[p];
            Substance sub = SubstanceCatalog.Find(port.substance);
            Vector4 spot = Pack(s.motes.homes[EdgeToward(s.motes, toFrame.MultiplyVector(port.direction))], -1f);
            for (int t = 0; t < n; t++)
            {
                if (port.rate >= 0f) { if (Arrive(s, sub, spot)) Tally(sub, ref kinds, 1); }
                else if (Leave(s, sub, spot, 2f)) Tally(sub, ref kinds, -1);
            }
        }
    }

    // One of group g's bodies, at random (-1: none shown).
    int BodyOf(int g, int placed)
    {
        int count = 0;
        for (int o = 0; o < placed; o++) if (_placed[o].group == g) count++;
        if (count == 0) return -1;
        int pick = (int)(Next() * count) % count;
        for (int o = 0; o < placed; o++)
            if (_placed[o].group == g && pick-- == 0) return o;
        return -1;
    }

    void Tally(Substance sub, ref int kinds, int by)
    {
        int k = Kind(sub, ref kinds);
        if (k >= 0) _have[k] += by;
    }

    int Kind(Substance sub, ref int kinds)
    {
        if (sub == null) return -1;
        for (int k = 0; k < kinds; k++) if (_kinds[k] == sub) return k;
        if (kinds >= MaxKinds) return -1;
        _kinds[kinds] = sub;
        _want[kinds] = 0f;
        _have[kinds] = 0;
        return kinds++;
    }

    // A mote placed straight at a home (the view opening).
    bool Rest(Shown s, Substance sub)
    {
        Motes m = s.motes;
        int i = FreeMote(m), home = PickHome(m, HomesFor(s, sub));
        if (i < 0 || home < 0) return false;
        Vector4 at = Pack(m.homes[home], -1f);
        StartTrip(s, i, sub, at, at, 0f, false, 0.01f);
        m.gpu[i].time.x = Time.time - 1f;
        m.state[i] = MoteState.Resting;
        return true;
    }

    // Where a new mote of 'sub' comes from when no flow says: a working organelle making it, else the membrane.
    Vector4 Source(Shown s, Substance sub, int placed)
    {
        Motes m = s.motes;
        int makers = 0;
        for (int o = 0; o < placed; o++) if (Working(o) && Lists(_placed[o].type.outputs, sub.name)) makers++;
        int pick = makers > 0 ? (int)(Next() * makers) % makers : -1;
        for (int o = 0; o < placed && pick >= 0; o++)
            if (Working(o) && Lists(_placed[o].type.outputs, sub.name) && pick-- == 0) return _placed[o].at;
        return Pack(m.homes[m.edge[(int)(Next() * m.edge.Count) % m.edge.Count]], -1f);
    }

    // Where a mote of 'sub' goes when no flow says: a working organelle using it (end 1: swallowed), else out
    // through the membrane nearest the mote (end 2; w = -2 marks it).
    Vector4 Sink(Substance sub, int placed, out float end)
    {
        int users = 0;
        for (int o = 0; o < placed; o++) if (Working(o) && Lists(_placed[o].type.inputs, sub.name)) users++;
        int pick = users > 0 ? (int)(Next() * users) % users : -1;
        for (int o = 0; o < placed && pick >= 0; o++)
            if (Working(o) && Lists(_placed[o].type.inputs, sub.name) && pick-- == 0) { end = 1f; return _placed[o].at; }
        end = 2f;
        return new Vector4(0f, 0f, 0f, -2f);
    }

    // A new mote: from 'from' (grows in as it leaves) to a free home.
    bool Arrive(Shown s, Substance sub, Vector4 from)
    {
        Motes m = s.motes;
        bool stream = sub.look == SubstanceLook.Honey && from.w >= 0f && s.cell.HasNucleus;
        int i = FreeMote(m), home = stream ? NearestFree(m, m.near, PoolPoint(from)) : PickHome(m, HomesFor(s, sub));
        if (i < 0 || home < 0) return false;
        Vector4 to = Pack(m.homes[home], -1f);
        StartTrip(s, i, sub, from, to, 0f, true, Trip(from, to, 1f));
        m.state[i] = MoteState.Settling;
        return true;
    }

    // A mote used up: the resting one of 'sub' nearest 'to' goes there and is swallowed (end 1) or fades (2).
    // to.w == -2: out through the membrane nearest the mote.
    bool Leave(Shown s, Substance sub, Vector4 to, float end)
    {
        Motes m = s.motes;
        bool anywhere = to.w < -1.5f;
        float now = Time.time;
        int best = -1;
        float bestD = float.MaxValue;
        Vector3 bestAt = default;
        for (int i = 0; i < MotesPerCell; i++)
        {
            if (m.sub[i] != sub || !Available(m, i, now)) continue;
            Vector3 at = Where(m, i, now);
            // The closest: to the drain, or (out anywhere) to the membrane (the shallowest).
            float d = anywhere ? m.depth[m.home[i]] : (at - (Vector3)to).sqrMagnitude;
            if (d < bestD) { bestD = d; best = i; bestAt = at; }
        }
        if (best < 0) return false;

        Vector4 from = Pack(bestAt, -1f);
        if (anywhere && sub.look == SubstanceLook.Honey) { to = from; end = 3f; } // the pool just thins
        else if (anywhere) { to = Pack(m.homes[EdgeToward(m, bestAt)], -1f); end = 2f; }
        m.taken[m.home[best]] = false;
        StartTrip(s, best, sub, from, to, end, false, end > 2.5f ? DrainSeconds : Pull(from, to));
        m.state[best] = MoteState.Leaving;
        return true;
    }

    // Can be sucked in: resting, or settling once past growing in (a mote still growing out of its maker would
    // jump to full size).
    static bool Available(Motes m, int i, float now)
    {
        if (m.state[i] == MoteState.Resting) return true;
        if (m.state[i] != MoteState.Settling) return false;
        Mote g = m.gpu[i];
        return g.look.w < 0.5f || now - g.time.x > g.time.y * 0.3f;
    }

    // Where mote i is now (frame units): the shader's MoteAt without the wobble (<= 0.02), so a moving mote can be
    // picked and sent on from where it really is.
    static Vector3 Where(Motes m, int i, float now)
    {
        if (m.state[i] == MoteState.Resting) return m.homes[m.home[i]];
        Mote g = m.gpu[i];
        float k = Mathf.Clamp01((now - g.time.x) / Mathf.Max(g.time.y, 1e-3f));
        Vector3 a = (Vector3)g.from + Drift(g.from.w, now), b = (Vector3)g.to + Drift(g.to.w, now), ab = b - a;
        Vector3 perp = new Vector3(-ab.y, ab.x, 0f);
        perp = perp.sqrMagnitude > 1e-8f ? perp.normalized : Vector3.right;
        float seed = g.look.z, span = ab.magnitude;
        bool honey = Mathf.Abs(g.look.x - (float)SubstanceLook.Honey) < 0.5f;
        if (g.time.z > 0.5f)
            return Vector3.Lerp(a, b, k * k * (0.35f + 0.65f * k)) + perp * (Mathf.Sin(k * Mathf.PI) * (1f - k) * (seed - 0.5f) * 0.5f * span);
        if (honey && g.look.w > 0.5f && g.from.w >= 0f)
            return HoneyPath(a, b, k * k * (3f - 2f * k), g.from.w, now);
        return Vector3.Lerp(a, b, k * k * (3f - 2f * k)) + perp * (Mathf.Sin(k * Mathf.PI) * (seed - 0.5f) * (honey ? 0.1f : 0.3f) * span);
    }

    // CellInterior.hlsl's HoneyBow + HoneyPath: the curve honey rides from its maker into the pool.
    static Vector3 HoneyPath(Vector3 a, Vector3 b, float u, float seed, float t)
    {
        Vector3 ab = b - a, perp = new Vector3(-ab.y, ab.x, 0f);
        perp = perp.sqrMagnitude > 1e-8f ? perp.normalized : Vector3.right;
        float arc = 0.08f * Mathf.Sin(t * 0.25f + seed * 9f), wave = 0.025f * Mathf.Sin(t * 0.5f + seed * 5f);
        return Vector3.Lerp(a, b, u) + perp * (ab.magnitude * (arc * 4f * u * (1f - u) + wave * Mathf.Sin(6.2832f * u)));
    }

    const float DrainSeconds = 2.5f, StrandLetGo = 0.35f;

    // Honey streams show only where honey rides them: per maker, the span [tail, head] of its path (0 at the maker,
    // 1 at the pool) its riding drops cover. A drop's strand reaches back to the maker until StrandLetGo of its trip,
    // then the tail follows it into the pool, so a stream pours while honey comes and drains away after.
    // O(waves x 256) a frame.
    void StreamSpans(Shown s, int firstWave)
    {
        Motes m = s.motes;
        float now = Time.time;
        for (int w = firstWave; w < _waveCount; w++)
        {
            if (_waves[w].to.w < 0.5f) continue;
            Vector4 at = _waves[w].from;
            float tail = 1f, head = 0f;
            for (int i = 0; i < MotesPerCell; i++)
            {
                if (m.state[i] != MoteState.Settling || m.sub[i] == null || m.sub[i].look != SubstanceLook.Honey) continue;
                Mote g = m.gpu[i];
                if (g.look.w < 0.5f || g.from != at) continue;
                float k = Mathf.Clamp01((now - g.time.x) / Mathf.Max(g.time.y, 1e-3f));
                float back = Mathf.Clamp01((k - StrandLetGo) / (1f - StrandLetGo));
                head = Mathf.Max(head, k * k * (3f - 2f * k));
                tail = Mathf.Min(tail, back * back * (3f - 2f * back));
            }
            _waves[w].to.w = head > tail + 1e-3f ? 1f : 0f;
            _waves[w].info.x = tail;
            _waves[w].info.y = head;
        }
    }

    // Where a body's honey stream joins the pool round the nucleus (frame).
    Vector3 PoolPoint(Vector3 body) =>
        (body.sqrMagnitude > 1e-6f ? body.normalized : Vector3.right) * (nucleusSize + 0.1f);

    // The free home of 'list' nearest 'p' (-1: none).
    static int NearestFree(Motes m, List<int> list, Vector3 p)
    {
        int best = -1;
        float bestD = float.MaxValue;
        foreach (int h in list)
        {
            if (m.taken[h]) continue;
            float d = (m.homes[h] - p).sqrMagnitude;
            if (d < bestD) { bestD = d; best = h; }
        }
        return best;
    }

    // CellInterior.hlsl's CellDrift.
    static Vector3 Drift(float seed, float t)
    {
        if (seed < 0f) return Vector3.zero;
        return new Vector3(Mathf.Sin(t * 0.37f + seed * 6.283f), Mathf.Sin(t * 0.29f + seed * 17.31f),
                           0.5f * Mathf.Sin(t * 0.23f + seed * 31.7f)) * 0.025f;
    }

    // A pull's length: quicker than drifting (it's sucked).
    float Pull(Vector4 from, Vector4 to) => Mathf.Clamp(Vector3.Distance(from, to) / (moteSpeed * 1.6f), 0.6f, 4f);

    // The honey motes' list for the shader (rebuilt when this cell's motes changed).
    void HoneyList(Shown s)
    {
        Motes m = s.motes;
        int n = 0;
        uint base_ = (uint)(s.slot * MotesPerCell);
        for (int i = 0; i < MotesPerCell; i++)
            if (m.state[i] != MoteState.Free && m.sub[i] != null && m.sub[i].look == SubstanceLook.Honey)
                m.honey[n++] = base_ + (uint)i;
        m.honeyCount = n;
    }

    // A resting mote drifting to a free home nearby.
    void Wander(Shown s, int i)
    {
        Motes m = s.motes;
        List<int> list = HomesFor(s, m.sub[i]);
        Vector3 at = m.homes[m.home[i]];
        for (int t = 0; t < 6; t++)
        {
            int h = list[(int)(Next() * list.Count) % list.Count];
            if (m.taken[h] || (m.homes[h] - at).sqrMagnitude > 0.35f * 0.35f) continue;
            Vector4 from = Pack(at, -1f), to = Pack(m.homes[h], -1f);
            m.taken[m.home[i]] = false;
            StartTrip(s, i, m.sub[i], from, to, 0f, false, Trip(from, to, 0.35f));
            m.state[i] = MoteState.Settling;
            return;
        }
    }

    float Trip(Vector4 from, Vector4 to, float pace) =>
        Mathf.Clamp(Vector3.Distance(from, to) / (moteSpeed * pace), 0.8f, 10f);

    // Writes mote i's trip. A new mote gets its seed and size; a moving one keeps them (and so its wobble).
    void StartTrip(Shown s, int i, Substance sub, Vector4 from, Vector4 to, float end, bool born, float duration)
    {
        Motes m = s.motes;
        float now = Time.time;
        Vector4 look = m.gpu[i].look;
        if (m.state[i] == MoteState.Free)
        {
            float size = moteSize * Mathf.Lerp(0.7f, 1.2f, Next()) * (sub.look == SubstanceLook.Honey ? 1.3f : 1f);
            look = new Vector4((float)sub.look, size, Next(), 0f);
        }
        look.w = born ? 1f : 0f;
        m.gpu[i] = new Mote
        {
            from = from,
            to = to,
            time = new Vector4(now, duration, end, s.slot),
            look = look,
            color = new Vector4(sub.color.r, sub.color.g, sub.color.b, 1f),
        };
        m.sub[i] = sub;
        m.end[i] = now + duration;
        if (end == 0f)
        {
            int home = Nearest(m, to);
            m.home[i] = home;
            m.taken[home] = true;
        }
        m.dirty = true;
    }

    bool Working(int o) => _placed[o].activity > 0.02f;

    static bool Lists(SubstanceAmount[] list, string substance)
    {
        foreach (SubstanceAmount a in list) if (a.substance == substance) return true;
        return false;
    }

    List<int> HomesFor(Shown s, Substance sub) =>
        sub != null && sub.look == SubstanceLook.Honey && s.cell.HasNucleus ? s.motes.near : s.motes.open;

    static int FreeMote(Motes m)
    {
        for (int i = 0; i < MotesPerCell; i++) if (m.state[i] == MoteState.Free) return i;
        return -1;
    }

    int PickHome(Motes m, List<int> list)
    {
        if (list.Count == 0) return -1;
        int h = -1;
        for (int t = 0; t < 8; t++)
        {
            h = list[(int)(Next() * list.Count) % list.Count];
            if (!m.taken[h]) return h;
        }
        return h;
    }

    static int Nearest(Motes m, Vector3 p)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < m.homes.Length; i++)
        {
            float d = (m.homes[i] - p).sqrMagnitude;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // The membrane-side home furthest along a frame direction.
    static int EdgeToward(Motes m, Vector3 direction)
    {
        int best = m.edge[0];
        float bestD = float.MinValue;
        foreach (int i in m.edge)
        {
            float d = Vector3.Dot(m.homes[i], direction);
            if (d > bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
