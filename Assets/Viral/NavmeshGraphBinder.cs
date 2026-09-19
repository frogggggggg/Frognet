using Pathfinding;
using UnityEngine;

/// <summary>
/// Copies a Transform's placement onto an A* NavMeshGraph.
///
/// A graph is data inside the Pathfinder component, not a component with a
/// transform of its own, so it has no way to follow anything. Its offset,
/// rotation and scale fields are its placement, and they have to be written.
///
/// The graph composes them as: world = Euler(rotation) * (vertex * scale) +
/// offset (NavMeshGenerator.cs:98 and :237 -- the bounds.min shifts cancel),
/// which maps one-to-one onto a Transform as long as the scale is uniform.
/// </summary>
[ExecuteAlways]
public class NavmeshGraphBinder : MonoBehaviour
{
    public enum Space
    {
        /// <summary>Graph sits where the cell sits. Cell must not move.</summary>
        World,

        /// <summary>
        /// Graph is baked at the origin, unrotated, so graph space is the
        /// cell's own local space. The cell can then move, turn and scale
        /// freely without the graph ever being rebuilt -- VirusMovement
        /// converts its queries through the cell Transform instead.
        /// </summary>
        Local
    }

    [Tooltip("World bakes the graph in place and the cell must stay put. " +
             "Local bakes it at the origin so the cell can move; assign the " +
             "same Transform to VirusMovement's Cell Space.")]
    public Space space = Space.World;

    [Tooltip("The cell to match. Leave empty to use this object's own " +
             "Transform -- put the binder on the cell and it needs nothing set.")]
    public Transform target;

    /// <summary>
    /// The cell this binder describes. VirusMovement looks this component up
    /// with GetComponentInParent when it lands, so the binder belongs on the
    /// cell itself; each cell then carries its own graph index and space.
    /// </summary>
    public Transform Cell => target ? target : transform;

    [Tooltip("Mesh the graph navigates. Leave empty to use the target's own " +
             "MeshFilter mesh -- but that mesh must live in a Resources folder, " +
             "which Unity's built-in primitives do not.")]
    public Mesh sourceMesh;

    [Tooltip("Rescale so the nav mesh covers the same volume as the target's " +
             "rendered mesh. Lets a coarse nav mesh of any size line up with a " +
             "detailed visual one.")]
    public bool matchBoundsSize = true;

    [Tooltip("Which graph on the Pathfinder to write to. 0 unless you have more " +
             "than one.")]
    public int graphIndex;

    /// <summary>Resolve the Pathfinder, preferring the live one.</summary>
    public static AstarPath FindPathfinder()
    {
        return AstarPath.active != null
            ? AstarPath.active
            : FindFirstObjectByType<AstarPath>();
    }

    public NavMeshGraph FindGraph()
    {
        AstarPath astar = FindPathfinder();
        if (astar == null || astar.data == null) return null;

        NavGraph[] graphs = astar.data.graphs;
        if (graphs == null || graphIndex < 0 || graphIndex >= graphs.Length) return null;

        return graphs[graphIndex] as NavMeshGraph;
    }

    /// <summary>Mesh that will be handed to the graph, or null if there is none.</summary>
    public Mesh ResolveMesh()
    {
        if (sourceMesh) return sourceMesh;

        MeshFilter filter = Cell.GetComponent<MeshFilter>();
        return filter ? filter.sharedMesh : null;
    }

    /// <summary>
    /// Uniform scale for the graph. Reports the target's own scale, adjusted so
    /// a nav mesh authored at a different size still covers the same volume.
    /// </summary>
    public float ResolveScale()
    {
        // Local space excludes the Transform's scale on purpose: the cell
        // applies that itself when converting queries, and leaving it out is
        // what lets a non-uniformly scaled cell work at all.
        float scale = space == Space.Local ? 1f : Cell.lossyScale.x;
        if (!matchBoundsSize) return scale;

        Mesh nav = ResolveMesh();
        MeshFilter filter = Cell.GetComponent<MeshFilter>();
        Mesh rendered = filter ? filter.sharedMesh : null;

        if (!nav || !rendered || nav == rendered) return scale;

        float navSize = nav.bounds.extents.magnitude;
        float renderedSize = rendered.bounds.extents.magnitude;

        if (navSize < 1e-5f || renderedSize < 1e-5f) return scale;
        return scale * (renderedSize / navSize);
    }

    /// <summary>True when the target has moved since the last sync.</summary>
    public bool IsOutOfDate()
    {
        NavMeshGraph graph = FindGraph();
        if (graph == null) return false;

        Vector3 offset = space == Space.Local ? Vector3.zero : Cell.position;
        Quaternion rotation = space == Space.Local ? Quaternion.identity : Cell.rotation;

        return graph.sourceMesh != ResolveMesh()
            || (graph.offset - offset).sqrMagnitude > 1e-6f
            || Quaternion.Angle(Quaternion.Euler(graph.rotation), rotation) > 0.01f
            || Mathf.Abs(graph.scale - ResolveScale()) > 1e-5f;
    }

    /// <summary>
    /// Write the placement onto the graph. Does not persist or rescan -- the
    /// editor does both, because a rescan is far too slow to do casually.
    /// </summary>
    public bool Sync()
    {
        NavMeshGraph graph = FindGraph();
        if (graph == null) return false;

        graph.sourceMesh = ResolveMesh();
        graph.offset = space == Space.Local ? Vector3.zero : Cell.position;
        graph.rotation = space == Space.Local ? Vector3.zero : Cell.rotation.eulerAngles;
        graph.scale = ResolveScale();
        return true;
    }
}
