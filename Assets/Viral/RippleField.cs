using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Every cell's live impact ripples in one global shader list, so anything drawn
/// near a cell (legs, ropes, bodies, whatever comes next) can ride the waves in its
/// vertex shader with RippleField.hlsl. Nothing per object, and one upload per
/// frame only while any are live.
///
/// Grouped by cell: a vertex first checks the few rippling cells, and only loops
/// the ripples of the one it's next to, so the total count barely matters. If more
/// cells ripple at once than the shader takes, the ones nearest the camera win.
///
/// Surfaces feed it from Surface.AddImpact. Impacts ride their cell (stored
/// cell-local, re-placed in world every frame), and drop out once faded.
/// </summary>
public static class RippleField
{
    /// <summary>Must match RIPPLE_FIELD_MAX in RippleField.hlsl.</summary>
    public const int Max = 256;

    /// <summary>Must match RIPPLE_FIELD_CELLS in RippleField.hlsl.</summary>
    public const int MaxCells = 32;

    /// <summary>How far from a cell's surface things still ride its ripples (world units).</summary>
    public static float influence = 2f;

    struct Wave
    {
        public Transform cell;
        public Vector3 localPoint, localCentre;
        public float start, end, amp, k, speed, invW2, decay, r0, ramp;
    }

    static readonly Wave[] s_waves = new Wave[Max];
    static readonly int[] s_keys = new int[Max];
    static int s_count;
    static bool s_uploadedEmpty;

    // Per cell group while uploading: first wave, count, and distance to the camera.
    static readonly int[] s_groupFirst = new int[Max], s_groupCount = new int[Max], s_groupOrder = new int[Max];
    static readonly float[] s_groupDistance = new float[Max];

    static readonly Vector4[] s_points = new Vector4[Max], s_shape = new Vector4[Max], s_shape2 = new Vector4[Max],
                              s_cells = new Vector4[MaxCells], s_ranges = new Vector4[MaxCells];

    static readonly int PointsId = Shader.PropertyToID("_RippleFieldWavePoints"),
                        ShapeId = Shader.PropertyToID("_RippleFieldWaveShape"),
                        Shape2Id = Shader.PropertyToID("_RippleFieldWaveShape2"),
                        CellsId = Shader.PropertyToID("_RippleFieldCells"),
                        RangesId = Shader.PropertyToID("_RippleFieldCellRange"),
                        CellCountId = Shader.PropertyToID("_RippleFieldCellCount"),
                        InfluenceId = Shader.PropertyToID("_RippleFieldInvInfluence");

    /// <summary>
    /// Publish an impact. 'shape' supplies the wave settings (the cell material's
    /// _Ripple* values) so riders move exactly with the cell surface.
    /// </summary>
    public static void Add(Transform cell, Vector3 localPoint, Vector3 localCentre, float startTime, float strength, Material shape,
                           float mergeDistance = 0f, float mergeTime = 0f)
    {
        if (!cell || strength <= 0f) return;

        float amplitude = Get(shape, "_RippleAmplitude", 0.12f);
        float wavelength = Mathf.Max(Get(shape, "_RippleWavelength", 0.7f), 1e-3f);
        float width = Mathf.Max(Get(shape, "_RippleWidth", 0.9f), 1e-2f);
        float decay = Get(shape, "_RippleDecay", 1.5f);
        float amp = strength * amplitude;
        if (amp <= 0f) return;

        // Same merge as the cell: an impact moments after a nearby one on the same cell
        // strengthens it (energy sum) instead of adding a wave, so riders match the cell.
        if (mergeDistance > 0f)
        {
            Vector3 world = cell.TransformPoint(localPoint);
            for (int i = 0; i < s_count; i++)
            {
                ref Wave m = ref s_waves[i];
                if (m.cell != cell || startTime - m.start > mergeTime ||
                    (cell.TransformPoint(m.localPoint) - world).sqrMagnitude > mergeDistance * mergeDistance)
                    continue;

                m.localPoint = (m.localPoint * m.amp + localPoint * amp) / (m.amp + amp);
                m.amp = Mathf.Sqrt(m.amp * m.amp + amp * amp);
                m.end = m.start + (decay > 0f ? Mathf.Log(Mathf.Max(m.amp, 1e-4f) / 5e-4f) / decay : 10f);
                return;
            }
        }

        var w = new Wave
        {
            cell = cell,
            localPoint = localPoint,
            localCentre = localCentre,
            start = startTime,
            // Gone once the peak falls below half a millimetre.
            end = startTime + (decay > 0f ? Mathf.Log(Mathf.Max(amp, 1e-4f) / 5e-4f) / decay : 10f),
            amp = amp,
            k = Mathf.PI * 2f / wavelength,
            speed = Get(shape, "_RippleSpeed", 2.5f),
            invW2 = 1f / (width * width),
            decay = decay,
            r0 = Mathf.Max(0f, Get(shape, "_RippleInitialRadius", 0.12f)),
            ramp = wavelength * 0.25f,
        };

        // Full: replace the one that fades first.
        int slot = s_count;
        if (s_count < Max) s_count++;
        else
        {
            slot = 0;
            for (int i = 1; i < Max; i++) if (s_waves[i].end < s_waves[slot].end) slot = i;
        }
        s_waves[slot] = w;
    }

    static float Get(Material m, string name, float fallback) => m && m.HasProperty(name) ? m.GetFloat(name) : fallback;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Init()
    {
        s_count = 0;
        s_uploadedEmpty = false;
        RenderPipelineManager.beginContextRendering -= Upload;
        RenderPipelineManager.beginContextRendering += Upload;
    }

    // Once per frame before any camera renders.
    static void Upload(ScriptableRenderContext context, List<Camera> cameras)
    {
        float now = Time.time; // URP drives _Time.y from Time.time in play mode

        int n = 0;
        for (int i = 0; i < s_count; i++)
        {
            Wave w = s_waves[i];
            if (!w.cell || now > w.end) continue;
            s_waves[n++] = w;
        }
        s_count = n;

        if (n == 0)
        {
            if (!s_uploadedEmpty) Shader.SetGlobalFloat(CellCountId, 0f);
            s_uploadedEmpty = true;
            return;
        }
        s_uploadedEmpty = false;

        // Group by cell (in place, no allocation).
        for (int i = 0; i < n; i++) s_keys[i] = s_waves[i].cell.GetInstanceID();
        Array.Sort(s_keys, s_waves, 0, n);

        int groups = 0;
        Camera cam = Camera.main;
        Vector3 eye = cam ? cam.transform.position : Vector3.zero;
        for (int i = 0; i < n; i++)
        {
            if (i > 0 && s_keys[i] == s_keys[i - 1]) { s_groupCount[groups - 1]++; continue; }
            Wave w = s_waves[i];
            s_groupFirst[groups] = i;
            s_groupCount[groups] = 1;
            s_groupDistance[groups] = (w.cell.TransformPoint(w.localCentre) - eye).sqrMagnitude;
            s_groupOrder[groups] = groups;
            groups++;
        }

        // More rippling cells than the shader takes: keep the nearest to the camera.
        if (groups > MaxCells)
            Array.Sort(s_groupDistance, s_groupOrder, 0, groups);
        int cells = Mathf.Min(groups, MaxCells);

        int written = 0;
        for (int g = 0; g < cells; g++)
        {
            int group = s_groupOrder[g];
            int first = s_groupFirst[group], count = s_groupCount[group];
            Wave head = s_waves[first];
            Vector3 centre = head.cell.TransformPoint(head.localCentre);
            float radius = (head.cell.TransformPoint(head.localPoint) - centre).magnitude;

            s_cells[g] = new Vector4(centre.x, centre.y, centre.z, radius);
            s_ranges[g] = new Vector4(written, count, 0f, 0f);

            for (int i = first; i < first + count; i++, written++)
            {
                Wave w = s_waves[i];
                Vector3 p = w.cell.TransformPoint(w.localPoint);
                s_points[written] = new Vector4(p.x, p.y, p.z, w.start);
                s_shape[written] = new Vector4(w.k, w.speed, w.invW2, w.decay);
                s_shape2[written] = new Vector4(w.r0, w.ramp, w.amp, 0f);
            }
        }

        // Always full-length arrays: the first upload fixes the global array size.
        Shader.SetGlobalVectorArray(PointsId, s_points);
        Shader.SetGlobalVectorArray(ShapeId, s_shape);
        Shader.SetGlobalVectorArray(Shape2Id, s_shape2);
        Shader.SetGlobalVectorArray(CellsId, s_cells);
        Shader.SetGlobalVectorArray(RangesId, s_ranges);
        Shader.SetGlobalFloat(CellCountId, cells);
        Shader.SetGlobalFloat(InfluenceId, 1f / Mathf.Max(influence, 1e-3f));
    }
}
