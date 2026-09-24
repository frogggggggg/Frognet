using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Draws the legs of every SpiderLegWalker at once. Walkers submit one small record
/// per leg each frame; this uploads them to the GPU and draws them all as instances
/// of one shared tube mesh with Custom/BloodCellLegs, which shapes each leg in its
/// vertex stage. One draw call per group (leg material, tube resolution, shadows),
/// however many walkers there are.
///
/// Created on demand the first time a walker submits: nothing to set up. The
/// walker's leg material is copied onto the leg shader, so it keeps its look and
/// edits to it show up live.
/// </summary>
[DefaultExecutionOrder(200)] // after SimulationTicker's LateUpdate (0), where walkers submit this frame's legs
public class LegRenderer : MonoBehaviour
{
    /// <summary>One leg. Layout matches LegData in BloodCellLegs.hlsl.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LegData
    {
        public Vector4 p0;     // xyz root,           w root radius
        public Vector4 p1;     // xyz control 1,      w tip radius
        public Vector4 p2;     // xyz control 2,      w writhe amplitude
        public Vector4 p3;     // xyz tip,            w phase
        public Vector4 side;   // xyz bend side axis, w radius swell
        public Vector4 clampN; // xyz ground normal,  w 1 = clear the ground
        public Vector4 ground; // xyz ground point
        public Vector4 hub;    // xyz hub
    }

    const int Stride = 16 * 8;
    static readonly int LegsId = Shader.PropertyToID("_Legs");

    readonly struct GroupKey : IEquatable<GroupKey>
    {
        readonly int _material, _shader, _rings, _sides;
        readonly bool _shadows;

        public GroupKey(Material m, Shader s, int rings, int sides, bool shadows)
        {
            _material = m.GetInstanceID();
            _shader = s.GetInstanceID();
            _rings = rings;
            _sides = sides;
            _shadows = shadows;
        }

        public bool Equals(GroupKey o) =>
            _material == o._material && _shader == o._shader && _rings == o._rings && _sides == o._sides && _shadows == o._shadows;

        public override bool Equals(object o) => o is GroupKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(_material, _shader, _rings, _sides, _shadows);
    }

    class Group
    {
        public Material source, material;
        public Mesh mesh;
        public bool shadows;
        public LegData[] data = new LegData[256];
        public int count;
        public Bounds bounds;
        public GraphicsBuffer buffer;
        public readonly MaterialPropertyBlock props = new MaterialPropertyBlock();
    }

    static LegRenderer s_instance;

    readonly Dictionary<GroupKey, Group> _groups = new Dictionary<GroupKey, Group>();
    readonly Dictionary<Vector2Int, Mesh> _meshes = new Dictionary<Vector2Int, Mesh>();

    static LegRenderer Instance
    {
        get
        {
            if (!s_instance)
            {
                var go = new GameObject("Leg Renderer") { hideFlags = HideFlags.HideAndDontSave };
                s_instance = go.AddComponent<LegRenderer>();
            }
            return s_instance;
        }
    }

    /// <summary>
    /// Worth drawing this frame: inside the main camera's view, give or take 'pad' metres. The
    /// view is sampled before the camera's own LateUpdate, so it trails a frame: pad it, or legs
    /// at the edge blink out on fast camera moves (and their shadows with them).
    /// </summary>
    public static bool Visible(Bounds bounds, float pad = 0f) =>
        SimulationTicker.OnScreen(bounds.center, bounds.extents.magnitude + pad); // shared per-frame camera

    /// <summary>Queue a walker's legs for this frame's draw.</summary>
    public static void Submit(Material source, Shader shader, int rings, int sides, bool shadows,
                              LegData[] legs, int count, Bounds bounds)
    {
        if (!source || !shader || count <= 0) return;

        Group g = Instance.GetGroup(source, shader, rings, sides, shadows);
        if (g.count + count > g.data.Length)
            Array.Resize(ref g.data, Mathf.NextPowerOfTwo(g.count + count));

        Array.Copy(legs, 0, g.data, g.count, count);
        if (g.count == 0) g.bounds = bounds;
        else g.bounds.Encapsulate(bounds);
        g.count += count;
    }

    Group GetGroup(Material source, Shader shader, int rings, int sides, bool shadows)
    {
        var key = new GroupKey(source, shader, rings, sides, shadows);
        if (_groups.TryGetValue(key, out Group g)) return g;

        g = new Group
        {
            source = source,
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true },
            mesh = TubeMesh(rings, sides),
            shadows = shadows,
        };
        g.material.CopyPropertiesFromMaterial(source);
        g.material.shaderKeywords = source.shaderKeywords;
        _groups.Add(key, g);
        return g;
    }

    void LateUpdate()
    {
#if UNITY_EDITOR
        bool refreshKeywords = Time.frameCount % 60 == 0; // shader keywords rarely change; skip the allocation
#endif
        foreach (Group g in _groups.Values)
        {
            if (g.count == 0 || !g.source)
            {
                g.count = 0;
                continue;
            }

#if UNITY_EDITOR
            // Follow live edits to the leg material (a build copies it once, at creation).
            g.material.CopyPropertiesFromMaterial(g.source);
            if (refreshKeywords) g.material.shaderKeywords = g.source.shaderKeywords;
#endif

            if (g.buffer == null || g.buffer.count < g.count)
            {
                g.buffer?.Release();
                g.buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(g.count), Stride);
                g.props.SetBuffer(LegsId, g.buffer);
            }
            g.buffer.SetData(g.data, 0, 0, g.count);

            Graphics.DrawMeshInstancedProcedural(g.mesh, 0, g.material, g.bounds, g.count, g.props,
                g.shadows ? ShadowCastingMode.On : ShadowCastingMode.Off, true, 0, null, LightProbeUsage.Off);
            g.count = 0;
        }
    }

    // One leg's tube: rings along it, a vertex per side around each, plus a root and a tip
    // cap. Vertices only carry where they sit on the leg; the shader does the rest.
    Mesh TubeMesh(int rings, int sides)
    {
        var key = new Vector2Int(rings, sides);
        if (_meshes.TryGetValue(key, out Mesh cached)) return cached;

        int count = rings * sides + 2;
        var shape = new Vector3[count];
        var cap = new Vector2[count];
        var tris = new int[(rings - 1) * sides * 6 + sides * 6];
        int ti = 0;

        for (int r = 0; r < rings; r++)
        for (int s = 0; s < sides; s++)
        {
            float a = s / (float)sides * Mathf.PI * 2f;
            shape[r * sides + s] = new Vector3(r / (float)(rings - 1), Mathf.Cos(a), Mathf.Sin(a));
        }

        int rootCap = rings * sides, tipCap = rootCap + 1, last = (rings - 1) * sides;
        shape[rootCap] = new Vector3(0f, 0f, 0f);
        shape[tipCap] = new Vector3(1f, 0f, 0f);
        cap[rootCap] = new Vector2(-1f, 0f);
        cap[tipCap] = new Vector2(1f, 0f);

        // Winding a->b->c gives outward faces for the ring frame (side, up, tangent).
        for (int r = 0; r < rings - 1; r++)
        for (int s = 0; s < sides; s++)
        {
            int ns = (s + 1) % sides;
            int a0 = r * sides + s, a1 = r * sides + ns;
            tris[ti++] = a0; tris[ti++] = a1; tris[ti++] = a0 + sides;
            tris[ti++] = a1; tris[ti++] = a1 + sides; tris[ti++] = a0 + sides;
        }
        for (int s = 0; s < sides; s++)
        {
            int ns = (s + 1) % sides;
            tris[ti++] = rootCap; tris[ti++] = ns; tris[ti++] = s;
            tris[ti++] = tipCap; tris[ti++] = last + s; tris[ti++] = last + ns;
        }

        var mesh = new Mesh { name = $"Leg Tube {rings}x{sides}", hideFlags = HideFlags.HideAndDontSave };
        mesh.SetVertices(shape);
        mesh.SetUVs(0, cap);
        mesh.SetTriangles(tris, 0, false);
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e4f); // placed by the shader; culled by the draw's bounds
        _meshes.Add(key, mesh);
        return mesh;
    }

    void OnDestroy()
    {
        foreach (Group g in _groups.Values)
        {
            g.buffer?.Release();
            if (g.material) Destroy(g.material);
        }
        foreach (Mesh m in _meshes.Values)
            if (m) Destroy(m);

        _groups.Clear();
        _meshes.Clear();
        if (s_instance == this) s_instance = null;
    }
}
