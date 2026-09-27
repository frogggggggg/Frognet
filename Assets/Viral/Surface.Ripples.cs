using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;

/// <summary>
/// Impact ripples on a Surface (was CellImpactRipples).
///
/// The shader draws the ripples; this records where each impact happened, when, and how
/// strong it was. Impact positions are stored in the renderer's local space, so ripples
/// move rigidly with the surface.
///
/// Up to MaxRipples live at once. Faded ones are dropped, and only live ones are sent,
/// so the shader's cost follows how many are actually rippling. When every slot is busy
/// a new impact replaces the weakest ripple still showing. Impacts landing almost
/// together at almost the same spot (a pack of viruses touching down) merge into one
/// stronger ripple instead of each taking a slot.
///
/// Values go through a MaterialPropertyBlock, set on the first impact, so surfaces
/// sharing a material ripple independently and one never hit stays SRP-batchable.
///
/// Every impact is also published to RippleField, so anything drawn near the surface
/// with RippleField.hlsl (legs, ropes, bodies) rides the same waves.
/// </summary>
public partial class Surface
{
    /// <summary>Must match RIPPLE_COUNT in the shader.</summary>
    public const int MaxRipples = 64;

    [Header("Ripples")]
    [Tooltip("Impact speed, in units per second, that produces strength 1. Faster impacts are NOT " +
             "clamped; twice this speed produces strength 2.")]
    public float referenceSpeed = 8f;

    [Min(0f), Tooltip("Impacts closer than this (world units) within Merge Time combine into one " +
                      "stronger ripple instead of taking separate slots.")]
    public float mergeDistance = 0.6f;

    [Min(0f), Tooltip("Seconds apart two impacts can be and still merge.")]
    public float mergeTime = 0.1f;

    // A ripple counts as gone once its peak is below this (world units).
    const float GoneAmplitude = 5e-4f;

    static readonly int PointsId = Shader.PropertyToID("_RipplePoints"),
                        ValuesId = Shader.PropertyToID("_RippleValues"),
                        CountId = Shader.PropertyToID("_RippleCount"),
                        AmplitudeId = Shader.PropertyToID("_RippleAmplitude"),
                        DecayId = Shader.PropertyToID("_RippleDecay");

    struct Ripple
    {
        public Vector3 local;
        public float start, strength, end;
    }

    // None of these survive a play-mode script reload; Ripples() remakes them.
    Ripple[] _ripples;
    Vector4[] _points, _values; // always full length: the first upload fixes the array size
    MaterialPropertyBlock _block;
    int _rippleCount;
    float _nextExpiry = float.PositiveInfinity;
    bool _rippling; // in s_rippling

    // Surfaces with live ripples. One static tick a frame (in the player loop's Update) drops their faded ripples and
    // runs the collider LOD: an Update() on every Surface was ~1000 script calls a frame for a few bits of work.
    static readonly List<Surface> s_rippling = new List<Surface>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void InstallTick()
    {
        s_rippling.Clear();
        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        for (int i = 0; i < loop.subSystemList.Length; i++)
        {
            if (loop.subSystemList[i].type != typeof(UnityEngine.PlayerLoop.Update)) continue;
            var list = new List<PlayerLoopSystem>(loop.subSystemList[i].subSystemList);
            list.RemoveAll(sys => sys.type == typeof(Surface)); // already there (no domain reload)
            list.Add(new PlayerLoopSystem { type = typeof(Surface), updateDelegate = TickAll });
            loop.subSystemList[i].subSystemList = list.ToArray();
            PlayerLoop.SetPlayerLoop(loop);
            return;
        }
    }

    static void TickAll()
    {
        if (!Application.isPlaying) return;
        StepColliderLod(); // Surface.Collider.cs
        float now = Time.time;
        for (int i = s_rippling.Count - 1; i >= 0; i--)
        {
            Surface s = s_rippling[i];
            if (s && s._rippleCount > 0 && now < s._nextExpiry) continue;
            if (s)
            {
                s.DropFaded(now);
                s.PushRipples();
                if (s._rippleCount > 0) continue;
                s._rippling = false;
            }
            s_rippling[i] = s_rippling[s_rippling.Count - 1];
            s_rippling.RemoveAt(s_rippling.Count - 1);
        }
    }

    void EnsureRipples()
    {
        _ripples ??= new Ripple[MaxRipples];
        _points ??= new Vector4[MaxRipples];
        _values ??= new Vector4[MaxRipples];
        _block ??= new MaterialPropertyBlock();
    }

    /// <summary>
    /// Starts a ripple. referenceSpeed is only a scale; strength is deliberately NOT clamped:
    /// impactSpeed == referenceSpeed gives strength 1, twice that gives 2, etc.
    /// </summary>
    public void AddImpact(Vector3 worldPoint, float impactSpeed)
    {
        float strength = referenceSpeed > 0f ? impactSpeed / referenceSpeed : impactSpeed;
        if (strength <= 0f) return;
        EnsureRipples();

        // Stored in the renderer's local frame: from here on the ripple belongs to the
        // surface and follows its motion.
        Transform space = Space;
        Vector3 localPoint = space.InverseTransformPoint(worldPoint);
        float now = Time.time;

        DropFaded(now);

        if (!Merge(space, worldPoint, localPoint, strength, now))
        {
            int slot = _rippleCount < MaxRipples ? _rippleCount++ : Weakest(now);
            _ripples[slot] = new Ripple { local = localPoint, start = now, strength = strength, end = End(now, strength) };
        }

        _nextExpiry = EarliestEnd();
        PushRipples();
        if (!_rippling) { _rippling = true; s_rippling.Add(this); } // TickAll drops them as they fade

        MeshRenderer r = Renderer;
        RippleField.Add(space, localPoint, r ? r.localBounds.center : Vector3.zero, now, strength,
                        r ? r.sharedMaterial : null, mergeDistance, mergeTime);
    }

    // Fold into a ripple that started moments ago nearby. Strengths add like energy
    // (square root of the sum of squares), so a pack landing together makes a bigger
    // ripple without ten of them making it ten times bigger.
    bool Merge(Transform space, Vector3 worldPoint, Vector3 localPoint, float strength, float now)
    {
        if (mergeDistance <= 0f) return false;

        for (int i = 0; i < _rippleCount; i++)
        {
            ref Ripple r = ref _ripples[i];
            if (now - r.start > mergeTime ||
                (space.TransformPoint(r.local) - worldPoint).sqrMagnitude > mergeDistance * mergeDistance)
                continue;

            float total = r.strength + strength;
            r.local = (r.local * r.strength + localPoint * strength) / total;
            r.strength = Mathf.Sqrt(r.strength * r.strength + strength * strength);
            r.end = End(r.start, r.strength);
            return true;
        }
        return false;
    }

    // The ripple showing least right now.
    int Weakest(float now)
    {
        float decay = Decay;
        int weakest = 0;
        float least = float.MaxValue;
        for (int i = 0; i < _rippleCount; i++)
        {
            float current = _ripples[i].strength * Mathf.Exp(-(now - _ripples[i].start) * decay);
            if (current < least) { least = current; weakest = i; }
        }
        return weakest;
    }

    void DropFaded(float now)
    {
        int kept = 0;
        for (int i = 0; i < _rippleCount; i++)
            if (now < _ripples[i].end)
                _ripples[kept++] = _ripples[i];
        _rippleCount = kept;
        _nextExpiry = EarliestEnd();
    }

    float EarliestEnd()
    {
        float earliest = float.PositiveInfinity;
        for (int i = 0; i < _rippleCount; i++) earliest = Mathf.Min(earliest, _ripples[i].end);
        return earliest;
    }

    // When the peak falls below GoneAmplitude, from the material's own amplitude and decay.
    float End(float start, float strength)
    {
        float decay = Decay;
        float peak = strength * MaterialFloat(AmplitudeId, 0.12f);
        if (decay <= 0f) return start + 60f;
        return start + Mathf.Max(0f, Mathf.Log(Mathf.Max(peak, GoneAmplitude) / GoneAmplitude) / decay);
    }

    float Decay => MaterialFloat(DecayId, 1.5f);

    float MaterialFloat(int id, float fallback)
    {
        Material m = Renderer ? Renderer.sharedMaterial : null;
        return m && m.HasProperty(id) ? m.GetFloat(id) : fallback;
    }

    void PushRipples()
    {
        MeshRenderer r = Renderer;
        if (!r) return;

        for (int i = 0; i < MaxRipples; i++)
        {
            if (i < _rippleCount)
            {
                Ripple rip = _ripples[i];
                _points[i] = new Vector4(rip.local.x, rip.local.y, rip.local.z, rip.start);
                _values[i] = new Vector4(rip.strength, 0f, 0f, 0f);
            }
            else
            {
                _points[i] = Vector4.zero;
                _values[i] = Vector4.zero;
            }
        }

        r.GetPropertyBlock(_block);
        _block.SetVectorArray(PointsId, _points);
        _block.SetVectorArray(ValuesId, _values);
        _block.SetFloat(CountId, _rippleCount);
        r.SetPropertyBlock(_block);
    }
}
