using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(LegPathShape))]
public class LegPathShapeEditor : Editor
{
    private const float PointRadius = 5f;
    private const float ClickPadding = 8f;
    private const float SegmentHoverPadding = 10f;
    private const int PreviewSamples = 48;
    private const float MinLineWidth = 1.5f;
    private const float MaxLineWidth = 7f;

    private enum DragMode { None, Point, Rounding }

    private DragMode dragMode;
    private int dragIndex = -1;
    private int selectedIndex = -1;
    private float previewT;
    private bool isPlaying;
    private double lastEditorTime;
    private float zoom = 150f;
    private bool showPreciseValues;

    private void OnEnable()
    {
        EditorApplication.update += OnEditorUpdate;
        lastEditorTime = EditorApplication.timeSinceStartup;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
    }

    private void OnEditorUpdate()
    {
        double now = EditorApplication.timeSinceStartup;
        float dt = (float)(now - lastEditorTime);
        lastEditorTime = now;

        if (!isPlaying) return;
        previewT += dt;
        if (previewT > 1f) previewT -= 1f;
        Repaint();
    }

    public override void OnInspectorGUI()
    {
        var shape = (LegPathShape)target;

        EditorGUILayout.HelpBox(
            "Drag points to move them. Drag the small square to round a corner. Scroll on a line to change its speed, scroll on empty space to zoom. Double-click a line to split it, right-click a point for more options.",
            MessageType.None);

        EditorGUI.BeginChangeCheck();
        float blend = EditorGUILayout.Slider("Speed Blend", shape.speedBlend, 0f, 0.5f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(shape, "Change Speed Blend");
            shape.speedBlend = blend;
            EditorUtility.SetDirty(shape);
        }

        DrawCanvas(shape);

        EditorGUILayout.Space();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(isPlaying ? "Pause" : "Play", GUILayout.Width(60)))
            isPlaying = !isPlaying;
        EditorGUI.BeginChangeCheck();
        float t = EditorGUILayout.Slider(previewT, 0f, 1f);
        if (EditorGUI.EndChangeCheck()) previewT = t;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        showPreciseValues = EditorGUILayout.Foldout(showPreciseValues, "Precise Values", true);
        if (showPreciseValues)
        {
            DrawSegmentList(shape);
            if (selectedIndex >= 0 && selectedIndex < shape.PointCount)
                DrawSelectedPointInfo(shape);
        }
    }

    private void DrawCanvas(LegPathShape shape)
    {
        Rect rect = GUILayoutUtility.GetRect(10, 260, GUILayout.ExpandWidth(true));
        GUI.Box(rect, GUIContent.none);

        var points = shape.Points;
        if (points.Count == 0) return;

        // view is anchored on the endpoints + zoom, never on interior point positions, so it never auto-shifts
        Vector2 center = (points[0].position + points[points.Count - 1].position) * 0.5f;
        Vector2 screenCenter = new Vector2(rect.x + rect.width * 0.5f, rect.y + rect.height * 0.5f);

        Vector2 ToScreen(Vector2 p) => screenCenter + new Vector2((p.x - center.x) * zoom, -(p.y - center.y) * zoom);
        Vector2 ToPath(Vector2 s) => center + new Vector2((s.x - screenCenter.x) / zoom, -(s.y - screenCenter.y) / zoom);

        float minSpeed = float.MaxValue, maxSpeed = float.MinValue;
        for (int i = 0; i < shape.Speeds.Count; i++)
        {
            minSpeed = Mathf.Min(minSpeed, shape.Speeds[i]);
            maxSpeed = Mathf.Max(maxSpeed, shape.Speeds[i]);
        }
        if (shape.Speeds.Count == 0) { minSpeed = 1f; maxSpeed = 1f; }

        Handles.BeginGUI();

        Vector2 prevScreen = ToScreen(shape.Evaluate(0f));
        for (int i = 1; i <= PreviewSamples; i++)
        {
            float sampleT = i / (float)PreviewSamples;
            Vector2 next = ToScreen(shape.Evaluate(sampleT));
            float speed = shape.EvaluateSpeed(sampleT - 0.5f / PreviewSamples);
            float ratio = Mathf.Approximately(minSpeed, maxSpeed) ? 0.5f : Mathf.InverseLerp(minSpeed, maxSpeed, speed);
            float width = Mathf.Lerp(MinLineWidth, MaxLineWidth, ratio);
            Handles.color = new Color(0.3f, 0.8f, 1f);
            Handles.DrawAAPolyLine(width, prevScreen, next);
            prevScreen = next;
        }

        for (int i = 0; i < points.Count; i++)
        {
            Vector2 screenPos = ToScreen(points[i].position);
            bool isEndpoint = i == 0 || i == points.Count - 1;
            Handles.color = i == selectedIndex
                ? Color.yellow
                : points[i].junction == LegPathShape.JunctionType.Curve
                    ? new Color(1f, 0.6f, 0.2f)
                    : Color.white;
            Handles.DrawSolidDisc(screenPos, Vector3.forward, isEndpoint ? PointRadius * 1.3f : PointRadius);

            if (!isEndpoint && points[i].junction == LegPathShape.JunctionType.Curve)
            {
                Vector2 handlePos = ToScreen(RoundingHandlePosition(points, i));
                Handles.color = Color.cyan;
                Handles.DrawSolidRectangleWithOutline(
                    new Rect(handlePos.x - 4, handlePos.y - 4, 8, 8), Color.cyan, Color.black);
            }
        }

        Handles.color = Color.red;
        Handles.DrawSolidDisc(ToScreen(shape.Evaluate(previewT)), Vector3.forward, PointRadius * 0.8f);

        Handles.EndGUI();

        HandleCanvasInput(shape, rect, points, ToScreen, ToPath);
    }

    private static Vector2 RoundingHandlePosition(IReadOnlyList<LegPathShape.PathPoint> points, int index)
    {
        Vector2 corner = points[index].position;
        Vector2 prev = points[index - 1].position;
        Vector2 next = points[index + 1].position;
        float prevLen = Vector2.Distance(prev, corner);
        float nextLen = Vector2.Distance(corner, next);
        // mirrors BuildPieces' cut clamp exactly, so the handle never looks stuck below the true max radius
        float radius = Mathf.Min(points[index].roundingRadius, 0.5f * prevLen, 0.5f * nextLen);
        return nextLen > 0.0001f ? Vector2.MoveTowards(corner, next, radius) : corner;
    }

    private void HandleCanvasInput(LegPathShape shape, Rect rect, IReadOnlyList<LegPathShape.PathPoint> points,
        Func<Vector2, Vector2> toScreen, Func<Vector2, Vector2> toPath)
    {
        Event e = Event.current;
        bool inside = rect.Contains(e.mousePosition);
        if (!inside && dragMode == DragMode.None) return;

        if (e.type == EventType.ScrollWheel && inside)
        {
            int nearestSeg = FindNearestSegment(points, toScreen, e.mousePosition, out float segDist);
            if (nearestSeg >= 0 && segDist <= SegmentHoverPadding)
            {
                Undo.RecordObject(shape, "Change Segment Speed");
                float newSpeed = Mathf.Max(0.01f, shape.Speeds[nearestSeg] - e.delta.y * 0.02f);
                shape.SetSpeed(nearestSeg, newSpeed);
                EditorUtility.SetDirty(shape);
            }
            else
            {
                zoom = Mathf.Clamp(zoom * (1f - e.delta.y * 0.05f), 10f, 2000f);
            }
            e.Use();
            Repaint();
            return;
        }

        if (e.type == EventType.MouseDown)
        {
            int nearestPoint = -1;
            float bestDist = ClickPadding;
            for (int i = 0; i < points.Count; i++)
            {
                float d = Vector2.Distance(toScreen(points[i].position), e.mousePosition);
                if (d < bestDist) { bestDist = d; nearestPoint = i; }
            }

            int nearestHandle = -1;
            float bestHandleDist = ClickPadding;
            for (int i = 0; i < points.Count - 1; i++)
            {
                if (i == 0 || points[i].junction != LegPathShape.JunctionType.Curve) continue;
                float d = Vector2.Distance(toScreen(RoundingHandlePosition(points, i)), e.mousePosition);
                if (d < bestHandleDist) { bestHandleDist = d; nearestHandle = i; }
            }

            if (e.button == 1 && nearestPoint >= 0)
            {
                ShowPointContextMenu(shape, nearestPoint);
                e.Use();
            }
            else if (e.button == 0 && e.clickCount == 2 && nearestPoint < 0 && nearestHandle < 0)
            {
                InsertOnNearestSegment(shape, points, toScreen, e.mousePosition);
                e.Use();
            }
            else if (e.button == 0 && nearestHandle >= 0)
            {
                dragMode = DragMode.Rounding;
                dragIndex = nearestHandle;
                selectedIndex = nearestHandle;
                e.Use();
            }
            else if (e.button == 0 && nearestPoint >= 0)
            {
                dragMode = DragMode.Point;
                dragIndex = nearestPoint;
                selectedIndex = nearestPoint;
                e.Use();
            }
            Repaint();
        }
        else if (e.type == EventType.MouseDrag && dragMode != DragMode.None)
        {
            if (dragMode == DragMode.Point)
            {
                Undo.RecordObject(shape, "Move Leg Path Point");
                shape.SetPointPosition(dragIndex, toPath(e.mousePosition));
            }
            else if (dragMode == DragMode.Rounding)
            {
                Undo.RecordObject(shape, "Change Corner Rounding");
                float radius = Vector2.Distance(points[dragIndex].position, toPath(e.mousePosition));
                shape.SetRounding(dragIndex, radius);
            }
            EditorUtility.SetDirty(shape);
            e.Use();
            Repaint();
        }
        else if (e.type == EventType.MouseUp && dragMode != DragMode.None)
        {
            dragMode = DragMode.None;
            dragIndex = -1;
            e.Use();
        }
    }

    private static int FindNearestSegment(IReadOnlyList<LegPathShape.PathPoint> points,
        Func<Vector2, Vector2> toScreen, Vector2 mouseScreen, out float bestDist)
    {
        int bestSeg = -1;
        bestDist = float.MaxValue;
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector2 a = toScreen(points[i].position);
            Vector2 b = toScreen(points[i + 1].position);
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            float t = lenSq > 0f ? Mathf.Clamp01(Vector2.Dot(mouseScreen - a, ab) / lenSq) : 0f;
            Vector2 proj = a + ab * t;
            float dist = Vector2.Distance(proj, mouseScreen);
            if (dist < bestDist) { bestDist = dist; bestSeg = i; }
        }
        return bestSeg;
    }

    private void InsertOnNearestSegment(LegPathShape shape, IReadOnlyList<LegPathShape.PathPoint> points,
        Func<Vector2, Vector2> toScreen, Vector2 mouseScreen)
    {
        int bestSeg = FindNearestSegment(points, toScreen, mouseScreen, out float bestDist);
        if (bestSeg < 0 || bestDist > ClickPadding * 2f) return;

        Vector2 a = toScreen(points[bestSeg].position);
        Vector2 b = toScreen(points[bestSeg + 1].position);
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        float t = lenSq > 0f ? Mathf.Clamp01(Vector2.Dot(mouseScreen - a, ab) / lenSq) : 0f;

        Undo.RecordObject(shape, "Add Leg Path Point");
        shape.InsertPointOnSegment(bestSeg, t);
        selectedIndex = bestSeg + 1;
        EditorUtility.SetDirty(shape);
    }

    private void ShowPointContextMenu(LegPathShape shape, int index)
    {
        var menu = new GenericMenu();
        bool isEndpoint = index == 0 || index == shape.PointCount - 1;

        if (!isEndpoint)
        {
            var current = shape.Points[index].junction;
            menu.AddItem(new GUIContent("Junction/Edge"), current == LegPathShape.JunctionType.Edge,
                () => SetJunction(shape, index, LegPathShape.JunctionType.Edge));
            menu.AddItem(new GUIContent("Junction/Curve"), current == LegPathShape.JunctionType.Curve,
                () => SetJunction(shape, index, LegPathShape.JunctionType.Curve));
            menu.AddItem(new GUIContent("Delete Point"), false, () => DeletePoint(shape, index));
        }
        else
        {
            menu.AddDisabledItem(new GUIContent("Endpoints cannot be deleted"));
        }

        menu.ShowAsContext();
    }

    private void SetJunction(LegPathShape shape, int index, LegPathShape.JunctionType type)
    {
        Undo.RecordObject(shape, "Set Junction Type");
        shape.SetJunction(index, type);
        EditorUtility.SetDirty(shape);
    }

    private void DeletePoint(LegPathShape shape, int index)
    {
        Undo.RecordObject(shape, "Delete Leg Path Point");
        shape.RemovePoint(index);
        if (selectedIndex == index) selectedIndex = -1;
        EditorUtility.SetDirty(shape);
    }

    private void DrawSegmentList(LegPathShape shape)
    {
        EditorGUILayout.LabelField("Segments", EditorStyles.boldLabel);
        for (int i = 0; i < shape.SegmentCount; i++)
        {
            EditorGUI.BeginChangeCheck();
            float speed = EditorGUILayout.FloatField($"Segment {i} Speed", shape.Speeds[i]);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(shape, "Change Segment Speed");
                shape.SetSpeed(i, speed);
                EditorUtility.SetDirty(shape);
            }
        }
    }

    private void DrawSelectedPointInfo(LegPathShape shape)
    {
        var point = shape.Points[selectedIndex];
        EditorGUILayout.LabelField($"Selected Point {selectedIndex}", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        Vector2 pos = EditorGUILayout.Vector2Field("Position", point.position);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(shape, "Move Leg Path Point");
            shape.SetPointPosition(selectedIndex, pos);
            EditorUtility.SetDirty(shape);
        }

        bool isEndpoint = selectedIndex == 0 || selectedIndex == shape.PointCount - 1;
        using (new EditorGUI.DisabledScope(isEndpoint))
        {
            EditorGUI.BeginChangeCheck();
            var junction = (LegPathShape.JunctionType)EditorGUILayout.EnumPopup("Junction", point.junction);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(shape, "Set Junction Type");
                shape.SetJunction(selectedIndex, junction);
                EditorUtility.SetDirty(shape);
            }

            EditorGUI.BeginChangeCheck();
            float radius = EditorGUILayout.FloatField("Rounding Radius", point.roundingRadius);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(shape, "Change Corner Rounding");
                shape.SetRounding(selectedIndex, radius);
                EditorUtility.SetDirty(shape);
            }
        }
    }
}
