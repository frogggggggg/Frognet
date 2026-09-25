using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Flat 2D shapes in vertex colours, gathered into shared lists and written into a Mesh (the head
/// view's DNA diagram, the corner head readout: drawn with Hidden/GenomeStrand into render textures).
/// Clear, add shapes, Apply. Main thread only.
/// </summary>
public static class FlatMesh
{
    public static readonly List<Vector3> Verts = new List<Vector3>();
    public static readonly List<Color> Colors = new List<Color>();
    public static readonly List<int> Tris = new List<int>();

    public static void Clear()
    {
        Verts.Clear();
        Colors.Clear();
        Tris.Clear();
    }

    /// <summary>A straight bar from a to b, 'width' across, coloured from one end to the other.</summary>
    public static void Quad(Vector2 a, Vector2 b, float width, Color ca, Color cb)
    {
        Vector2 d = b - a;
        Vector2 n = d.sqrMagnitude > 1e-10f ? new Vector2(-d.y, d.x).normalized * (width * 0.5f) : new Vector2(width * 0.5f, 0f);
        int start = Verts.Count;
        Verts.Add(a + n); Verts.Add(a - n); Verts.Add(b + n); Verts.Add(b - n);
        Colors.Add(ca); Colors.Add(ca); Colors.Add(cb); Colors.Add(cb);
        Tris.Add(start); Tris.Add(start + 1); Tris.Add(start + 2);
        Tris.Add(start + 1); Tris.Add(start + 3); Tris.Add(start + 2);
    }

    /// <summary>A four-sided piece from a (halfA either side of it) to b (halfB), across 'dir'.</summary>
    public static void Taper(Vector2 a, Vector2 b, float halfA, float halfB, Color color)
    {
        Vector2 d = b - a;
        Vector2 n = d.sqrMagnitude > 1e-10f ? new Vector2(-d.y, d.x).normalized : Vector2.right;
        int start = Verts.Count;
        Verts.Add(a + n * halfA); Verts.Add(a - n * halfA); Verts.Add(b + n * halfB); Verts.Add(b - n * halfB);
        for (int i = 0; i < 4; i++) Colors.Add(color);
        Tris.Add(start); Tris.Add(start + 1); Tris.Add(start + 2);
        Tris.Add(start + 1); Tris.Add(start + 3); Tris.Add(start + 2);
    }

    /// <summary>A filled circle.</summary>
    public static void Disc(Vector2 centre, float radius, Color color, int sides = 16)
    {
        int start = Verts.Count;
        Verts.Add(centre);
        Colors.Add(color);
        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.PI * 2f / sides;
            Verts.Add(centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
            Colors.Add(color);
            Tris.Add(start); Tris.Add(start + 1 + i); Tris.Add(start + 1 + (i + 1) % sides);
        }
    }

    /// <summary>A circle's outline, 'width' thick, centred on 'radius'.</summary>
    public static void Ring(Vector2 centre, float radius, float width, Color color, int sides = 64)
    {
        int start = Verts.Count;
        for (int i = 0; i <= sides; i++)
        {
            float a = i * Mathf.PI * 2f / sides;
            Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            Verts.Add(centre + d * (radius + width * 0.5f));
            Verts.Add(centre + d * (radius - width * 0.5f));
            Colors.Add(color);
            Colors.Add(color);
        }
        for (int i = 0; i < sides; i++)
        {
            int a = start + i * 2;
            Tris.Add(a); Tris.Add(a + 1); Tris.Add(a + 2);
            Tris.Add(a + 1); Tris.Add(a + 3); Tris.Add(a + 2);
        }
    }

    public static void Apply(Mesh mesh)
    {
        mesh.Clear();
        mesh.SetVertices(Verts);
        mesh.SetColors(Colors);
        mesh.SetTriangles(Tris, 0, false);
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 1f));
    }
}
