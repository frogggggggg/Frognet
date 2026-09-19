using Pathfinding;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(NavmeshGraphBinder))]
public class NavmeshGraphBinderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var binder = (NavmeshGraphBinder)target;

        EditorGUILayout.Space();

        if (NavmeshGraphBinder.FindPathfinder() == null)
        {
            EditorGUILayout.HelpBox(
                "No Pathfinder in the scene. Add Component > Pathfinding > Pathfinder.",
                MessageType.Error);
            return;
        }

        if (binder.FindGraph() == null)
        {
            EditorGUILayout.HelpBox(
                $"No Navmesh Graph at index {binder.graphIndex}. On the Pathfinder, " +
                "open Graphs > Add New Graph > Navmesh Graph.", MessageType.Error);
            return;
        }

        DrawScaleWarning(binder);
        DrawMeshWarning(binder);

        if (binder.IsOutOfDate())
        {
            EditorGUILayout.HelpBox("Graph does not match the target. Sync to update it.",
                                    MessageType.Warning);
        }

        EditorGUILayout.Space();

        // Rescanning is the expensive half, so it is a deliberate button press
        // rather than something that happens whenever the target is nudged.
        if (GUILayout.Button("Sync Graph and Scan", GUILayout.Height(24)))
            SyncAndScan(binder, scan: true);

        if (GUILayout.Button("Sync Only"))
            SyncAndScan(binder, scan: false);
    }

    static void DrawScaleWarning(NavmeshGraphBinder binder)
    {
        // Local space applies the cell's matrix at query time, so an uneven
        // scale is no longer the graph's problem.
        if (binder.space == NavmeshGraphBinder.Space.Local)
        {
            EditorGUILayout.HelpBox(
                "Local space: assign this same Transform to the virus's " +
                "Cell Space field, or it will query the graph in world space " +
                "and find it sitting at the origin.", MessageType.Info);
            return;
        }

        Vector3 scale = binder.Cell.lossyScale;

        bool uniform = Mathf.Abs(scale.x - scale.y) < 1e-4f &&
                       Mathf.Abs(scale.y - scale.z) < 1e-4f;

        if (uniform) return;

        EditorGUILayout.HelpBox(
            $"Target scale {scale} is not uniform. A NavMeshGraph stores scale as a " +
            "single float, so the graph cannot represent this and will use X " +
            $"({scale.x}). The navmesh will not line up with the rendered shape.",
            MessageType.Warning);
    }

    static void DrawMeshWarning(NavmeshGraphBinder binder)
    {
        Mesh mesh = binder.ResolveMesh();

        if (mesh == null)
        {
            EditorGUILayout.HelpBox(
                "No mesh. Assign Source Mesh, or give the target a MeshFilter.",
                MessageType.Error);
            return;
        }

        string path = AssetDatabase.GetAssetPath(mesh);

        if (string.IsNullOrEmpty(path) || !path.Replace("\\", "/").Contains("Resources/"))
        {
            EditorGUILayout.HelpBox(
                $"'{mesh.name}' is not in a Resources folder. The graph reloads its " +
                "source mesh by path, so this reference will not survive a build. " +
                "Unity's built-in primitives can never work here.\n\n" +
                "Use Tools > Viral > Navmesh Icosphere to generate a usable one.",
                MessageType.Warning);
        }
    }

    static void SyncAndScan(NavmeshGraphBinder binder, bool scan)
    {
        AstarPath astar = NavmeshGraphBinder.FindPathfinder();

        Undo.RegisterCompleteObjectUndo(astar, "Sync Navmesh Graph");

        if (!binder.Sync())
        {
            Debug.LogWarning("Sync failed: no graph or no target.", binder);
            return;
        }

        // Graph settings live in a serialized byte blob on the Pathfinder, not
        // in normal fields, so writing the graph object is not enough -- it has
        // to be re-serialized or the change is lost on the next reload.
        astar.data.SetData(astar.data.SerializeGraphs());
        EditorUtility.SetDirty(astar);

        if (scan)
        {
            astar.Scan(binder.FindGraph());
            Debug.Log("Navmesh graph synced and scanned.", astar);
        }
        else
        {
            Debug.Log("Navmesh graph synced. Scan before using it.", astar);
        }
    }
}
