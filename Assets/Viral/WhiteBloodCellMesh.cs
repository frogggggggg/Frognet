using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The mesh every white blood cell is drawn with (Custom/WhiteBloodCell): a unit sphere of plain
/// directions in the cell's *reach frame*, where +Z points at the absorbing spot. The shader shapes
/// it all (membrane ruffles, amoeboid flow, the pseudopod stretching out along +Z, the mouth and its lips),
/// so the mesh is only the sampling: rings of latitude round +Z, packed tight inside the cap that
/// becomes the pseudopod (<see cref="CapAngle"/>, must match CAP_ANGLE in the shader) and looser over
/// the rest of the body. Built once per detail level, shared by every cell.
/// </summary>
public static class WhiteBloodCellMesh
{
    /// <summary>Half-angle (radians) of the cap that stretches into the pseudopod. Keep equal to
    /// CAP_ANGLE in WhiteBloodCell.shader.</summary>
    public const float CapAngle = 0.7f;

    /// <summary>detail 1: near mesh (~8.6k vertices); lower is coarser (0.3: ~800).</summary>
    public static Mesh Build(float detail)
    {
        int capRings = Mathf.Max(6, Mathf.RoundToInt(44 * detail));
        int bodyRings = Mathf.Max(8, Mathf.RoundToInt(44 * detail));
        int segments = Mathf.Max(12, Mathf.RoundToInt(96 * detail / 4f) * 4);
        int rings = capRings + bodyRings; // ring 0 is the tip pole, ring 'rings' the back pole

        var vertices = new List<Vector3>((rings + 1) * (segments + 1));
        for (int i = 0; i <= rings; i++)
        {
            float theta = i <= capRings
                ? CapAngle * i / capRings
                : CapAngle + (Mathf.PI - CapAngle) * (i - capRings) / bodyRings;
            float st = Mathf.Sin(theta), ct = Mathf.Cos(theta);
            for (int j = 0; j <= segments; j++)
            {
                float phi = 2f * Mathf.PI * j / segments - Mathf.PI; // -pi..pi, like atan2 in the shader
                vertices.Add(new Vector3(st * Mathf.Cos(phi), st * Mathf.Sin(phi), ct));
            }
        }

        var triangles = new List<int>(rings * segments * 6);
        int row = segments + 1;
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < segments; j++)
            {
                int a = i * row + j, b = a + 1, c = a + row, d = c + 1;
                if (i > 0) Triangle(a, b, c);            // the pole rows are fans
                if (i < rings - 1) Triangle(b, d, c);
            }

        // Wound so Cross(b - a, c - a) faces out (Unity's front face), whichever way the rings run.
        void Triangle(int a, int b, int c)
        {
            Vector3 pa = vertices[a], n = Vector3.Cross(vertices[b] - pa, vertices[c] - pa);
            if (Vector3.Dot(n, pa + vertices[b] + vertices[c]) >= 0f) { triangles.Add(a); triangles.Add(b); triangles.Add(c); }
            else { triangles.Add(a); triangles.Add(c); triangles.Add(b); }
        }

        var mesh = new Mesh { name = $"White Blood Cell ({detail:0.##})", hideFlags = HideFlags.DontSave };
        mesh.indexFormat = vertices.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(vertices);
        mesh.SetNormals(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 8f); // the shader stretches it far past the unit sphere
        mesh.UploadMeshData(true);
        return mesh;
    }
}
