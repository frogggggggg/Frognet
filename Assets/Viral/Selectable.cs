using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Something the command mode (CommandMode, Q) can box, select and put in a group:
/// an <see cref="Category.Agent"/> (your units: something that takes orders, an
/// <see cref="ICommandable"/> on it or a parent) or a <see cref="Category.Target"/> (enemies,
/// resources, cells...: what agents are sent at). <see cref="kind"/> names what it is ("Virus",
/// "Cell", "Resource"); a target group is named after it ("Resources 1").
///
/// Add it to anything. Viruses with a VirusAI (agents) and Surfaces (cells, targets) get one
/// automatically when the command mode starts.
/// </summary>
public class Selectable : MonoBehaviour
{
    public enum Category { Agent, Target }

    public Category category = Category.Target;
    [Tooltip("What it is, shown in the selection menu (singular).")]
    public string kind = "Target";
    [Tooltip("Group name (plural). Empty: \"Agents\" for agents, else the kind made plural.")]
    public string groupName = "";
    [Tooltip("Target: what agents may be told to do to it (the link menu greys out the rest).")]
    public CommandBoard.Jobs affords = CommandBoard.Jobs.MoveTo;

    /// <summary>Every enabled selectable.</summary>
    public static readonly List<Selectable> All = new List<Selectable>();

    public string GroupName => !string.IsNullOrEmpty(groupName) ? groupName
                             : category == Category.Agent ? "Agents" : Plural(kind);

    /// <summary>What takes this agent's orders (null for targets, or an agent that can't).</summary>
    public ICommandable Commandable => _commandable ??= GetComponentInParent<ICommandable>();
    ICommandable _commandable;

    /// <summary>A target that's been dealt with (an <see cref="ICompletable"/> on it says so, e.g. a
    /// cell once it's yours): tasks skip it, and a task whose targets are all done is done.</summary>
    public bool Complete => TryGetComponent(out ICompletable c) && c.Complete;

    Renderer[] _renderers;

    public static Selectable Add(GameObject go, Category category, string kind, CommandBoard.Jobs affords = CommandBoard.Jobs.MoveTo)
    {
        var s = go.AddComponent<Selectable>();
        s.category = category;
        s.kind = kind;
        s.affords = affords;
        return s;
    }

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    /// <summary>World bounds of what draws it (its renderers), or a small box at it.</summary>
    public Bounds WorldBounds
    {
        get
        {
            if (_renderers == null || _renderers.Length == 0) _renderers = GetComponentsInChildren<Renderer>();
            bool any = false;
            var b = new Bounds(transform.position, Vector3.one * 0.5f);
            foreach (Renderer r in _renderers)
            {
                if (!r || !r.enabled || r is ParticleSystemRenderer) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }
    }

    /// <summary>Its box on screen (pixels), fitted to its shape; false if it's behind the camera.</summary>
    public bool ScreenRect(Camera cam, out Rect rect)
    {
        rect = default;
        if (_hulls == null) BuildHulls();
        Matrix4x4 viewProj = cam.projectionMatrix * cam.worldToCameraMatrix;
        float w = cam.pixelWidth, h = cam.pixelHeight;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        bool any = false;
        foreach (Hull hull in _hulls)
        {
            if (!hull.renderer || !hull.renderer.enabled || !hull.renderer.gameObject.activeInHierarchy) continue;
            Matrix4x4 m = viewProj * hull.renderer.localToWorldMatrix;
            foreach (Vector3 p in hull.points)
            {
                Vector4 c = m * new Vector4(p.x, p.y, p.z, 1f);
                if (c.w <= 1e-4f) return false; // partly behind the camera
                float x = (c.x / c.w * 0.5f + 0.5f) * w, y = (c.y / c.w * 0.5f + 0.5f) * h;
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
                any = true;
            }
        }
        if (!any) // nothing drawn: a point
        {
            Vector3 s = cam.WorldToScreenPoint(transform.position);
            if (s.z <= 0f) return false;
            rect = new Rect(s.x, s.y, 0f, 0f);
            return true;
        }
        rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return true;
    }

    // Per renderer, the few points of its mesh that stick out furthest (the extreme vertex along each
    // of ~40 directions): projecting just those gives the same screen box as every vertex, from any
    // angle, closely. Unreadable meshes use their bounds' corners.
    struct Hull
    {
        public Renderer renderer;
        public Vector3[] points;
    }
    List<Hull> _hulls;
    static Vector3[] s_directions;

    /// <summary>Forget the fitted shape (call if its renderers or meshes change).</summary>
    public void Refit() => _hulls = null;

    void BuildHulls()
    {
        _hulls = new List<Hull>();
        if (s_directions == null)
        {
            const int N = 42; // a Fibonacci sphere
            s_directions = new Vector3[N];
            for (int i = 0; i < N; i++)
            {
                float y = 1f - (i + 0.5f) * 2f / N, r = Mathf.Sqrt(1f - y * y), a = i * 2.39996323f;
                s_directions[i] = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
            }
        }
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
            Mesh mesh = r is SkinnedMeshRenderer sk ? sk.sharedMesh : r.TryGetComponent(out MeshFilter f) ? f.sharedMesh : null;
            var points = new List<Vector3>();
            if (mesh && mesh.isReadable)
            {
                Vector3[] v = mesh.vertices;
                foreach (Vector3 d in s_directions)
                {
                    int best = -1;
                    float most = float.MinValue;
                    for (int i = 0; i < v.Length; i++)
                    {
                        float k = Vector3.Dot(v[i], d);
                        if (k > most) { most = k; best = i; }
                    }
                    if (best >= 0 && !points.Contains(v[best])) points.Add(v[best]);
                }
            }
            if (points.Count == 0)
            {
                Bounds b = mesh ? mesh.bounds : new Bounds(Vector3.zero, Vector3.one);
                for (int i = 0; i < 8; i++)
                    points.Add(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
            }
            _hulls.Add(new Hull { renderer = r, points = points.ToArray() });
        }
    }

    static string Plural(string word)
    {
        if (string.IsNullOrEmpty(word)) return "Targets";
        if (word.EndsWith("y") && word.Length > 1 && "aeiou".IndexOf(word[word.Length - 2]) < 0)
            return word.Substring(0, word.Length - 1) + "ies";
        if (word.EndsWith("s") || word.EndsWith("x") || word.EndsWith("ch") || word.EndsWith("sh")) return word + "es";
        return word + "s";
    }
}

/// <summary>An agent that takes orders from the command board: do this job at this target (null:
/// back to what it does on its own). Implemented by VirusAI.</summary>
public interface ICommandable
{
    void Order(Transform goal, CommandBoard.Job job);
}

/// <summary>On a target (next to its Selectable): whether it's been dealt with, which finishes the
/// command board's tasks on it. Implemented by CellSignal (converted).</summary>
public interface ICompletable
{
    bool Complete { get; }
}
