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

    /// <summary>Share of the cap (from the tip) that rounds the tip, where the mouth opens and the wrap closes
    /// over a catch. Keep equal to TIP_SPLIT in WhiteBloodCell.hlsl.</summary>
    const float TipSplit = 0.3f;

    // Where cap ring fraction i/capRings sits (0 tip .. 1 rim): half the cap's rings in the tip, the rest along the
    // arm. The tip carries the most shape (mouth, lips, the skin over a whole catch); evenly spaced it had only
    // 30% of them and the wrap stretched over a few rings.
    static float CapShare(float f) => f < 0.5f ? TipSplit * f / 0.5f : TipSplit + (1f - TipSplit) * (f - 0.5f) / 0.5f;

    /// <summary>detail 1: near mesh (~15.6k vertices, dense enough for the spikes to come to points); lower is
    /// coarser (0.25: ~1k, no spikes drawn). 'directions' = its vertices (unit directions), row by row, 'rowLength'
    /// per row (the seam column repeated): what WhiteBloodCellBake.compute walks.</summary>
    public static Mesh Build(float detail, out Vector3[] directions, out int rowLength)
    {
        int capRings = Mathf.Max(6, Mathf.RoundToInt(56 * detail));
        int bodyRings = Mathf.Max(8, Mathf.RoundToInt(64 * detail));
        int segments = Mathf.Max(12, Mathf.RoundToInt(128 * detail / 4f) * 4);
        int rings = capRings + bodyRings; // ring 0 is the tip pole, ring 'rings' the back pole

        var vertices = new List<Vector3>((rings + 1) * (segments + 1));
        for (int i = 0; i <= rings; i++)
        {
            float theta = i <= capRings
                ? CapAngle * CapShare((float)i / capRings)
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

        directions = vertices.ToArray();
        rowLength = segments + 1;
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
