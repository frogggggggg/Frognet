using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Generates an icosphere Mesh asset for use as an A* NavMeshGraph source.
///
/// Two reasons this exists rather than using Unity's built-in Sphere:
///
/// 1. A NavMeshGraph reloads its source mesh by path at runtime, so the asset
///    must live in a Resources folder. The built-in sphere lives inside
///    "Library/unity default resources", which is not a main asset, and the
///    graph editor's own validation null-refs on it.
///
/// 2. A UV sphere bunches triangles at the poles. An icosphere is near-uniform
///    everywhere, which is what a navmesh wants -- node size stays even, so
///    movement does not speed up or snag as it crosses the top.
/// </summary>
public static class NavmeshIcosphere
{
    const string Folder = "Assets/Resources";

    [MenuItem("Tools/Viral/Navmesh Icosphere/Low (80 tris)")]
    static void CreateLow() => Create(1);

    [MenuItem("Tools/Viral/Navmesh Icosphere/Medium (320 tris)")]
    static void CreateMedium() => Create(2);

    [MenuItem("Tools/Viral/Navmesh Icosphere/High (1280 tris)")]
    static void CreateHigh() => Create(3);

    static void Create(int subdivisions)
    {
        // Radius 0.5 matches Unity's primitive sphere, so a graph Scale equal
        // to the Sphere's Transform scale lines the two up exactly.
        Mesh mesh = Build(subdivisions, 0.5f);
        mesh.name = $"NavmeshIcosphere_{subdivisions}";

        if (!AssetDatabase.IsValidFolder(Folder))
            AssetDatabase.CreateFolder("Assets", "Resources");

        string path = $"{Folder}/{mesh.name}.asset";

        // CreateAsset makes this the main asset in its own file, which is what
        // gets past the graph editor's "must be the main asset" check.
        AssetDatabase.CreateAsset(mesh, path);
        AssetDatabase.SaveAssets();

        EditorGUIUtility.PingObject(mesh);
        Selection.activeObject = mesh;

        Debug.Log($"Created {path} - {mesh.triangles.Length / 3} triangles, " +
                  $"{mesh.vertexCount} vertices.", mesh);
    }

    static Mesh Build(int subdivisions, float radius)
    {
        var vertices = new List<Vector3>();
        var midpoints = new Dictionary<long, int>();

        // Icosahedron: three orthogonal golden-ratio rectangles.
        float t = (1f + Mathf.Sqrt(5f)) * 0.5f;

        void Add(float x, float y, float z) => vertices.Add(new Vector3(x, y, z).normalized);

        Add(-1, t, 0); Add(1, t, 0); Add(-1, -t, 0); Add(1, -t, 0);
        Add(0, -1, t); Add(0, 1, t); Add(0, -1, -t); Add(0, 1, -t);
        Add(t, 0, -1); Add(t, 0, 1); Add(-t, 0, -1); Add(-t, 0, 1);

        var faces = new List<int>
        {
            0, 11, 5,  0, 5, 1,   0, 1, 7,   0, 7, 10,  0, 10, 11,
            1, 5, 9,   5, 11, 4,  11, 10, 2, 10, 7, 6,  7, 1, 8,
            3, 9, 4,   3, 4, 2,   3, 2, 6,   3, 6, 8,   3, 8, 9,
            4, 9, 5,   2, 4, 11,  6, 2, 10,  8, 6, 7,   9, 8, 1
        };

        // Cache midpoints by edge so neighbouring triangles share the vertex.
        // Without this the sphere comes apart into unwelded triangles and the
        // navmesh gets no adjacency, leaving every node isolated.
        int Midpoint(int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (midpoints.TryGetValue(key, out int existing)) return existing;

            vertices.Add(((vertices[a] + vertices[b]) * 0.5f).normalized);
            midpoints[key] = vertices.Count - 1;
            return vertices.Count - 1;
        }

        for (int i = 0; i < subdivisions; i++)
        {
            var next = new List<int>(faces.Count * 4);

            for (int f = 0; f < faces.Count; f += 3)
            {
                int a = faces[f], b = faces[f + 1], c = faces[f + 2];
                int ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);

                next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }

            faces = next;
        }

        // Winding decides which way the surface faces. Rather than trusting the
        // hand-written face list, test one triangle against its own centroid --
        // on a sphere the outward direction is simply the position.
        Vector3 v0 = vertices[faces[0]], v1 = vertices[faces[1]], v2 = vertices[faces[2]];
        if (Vector3.Dot(Vector3.Cross(v1 - v0, v2 - v0), v0 + v1 + v2) < 0f)
        {
            for (int f = 0; f < faces.Count; f += 3)
                (faces[f + 1], faces[f + 2]) = (faces[f + 2], faces[f + 1]);
        }

        var scaled = new Vector3[vertices.Count];
        var normals = new Vector3[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            normals[i] = vertices[i];          // already unit length
            scaled[i] = vertices[i] * radius;
        }

        var mesh = new Mesh { vertices = scaled, normals = normals };
        mesh.SetTriangles(faces, 0);
        mesh.RecalculateBounds();
        return mesh;
    }
}
