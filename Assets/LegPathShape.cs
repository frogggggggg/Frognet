using System;
using System.Collections.Generic;
using UnityEngine;

/// A 2D path shape for a leg's foot to travel along, built from points joined by speed-tagged lines.
[CreateAssetMenu(fileName = "LegPathShape", menuName = "IK/Leg Path Shape")]
public class LegPathShape : ScriptableObject
{
    public enum JunctionType { Edge, Curve }

    [Serializable]
    public class PathPoint
    {
        public Vector2 position;
        public JunctionType junction = JunctionType.Edge;
        public float roundingRadius = 0.15f; // desired corner radius; auto-clamped to fit the adjoining segments

        public PathPoint(Vector2 position, JunctionType junction = JunctionType.Edge)
        {
            this.position = position;
            this.junction = junction;
        }
    }

    private struct Piece
    {
        public bool isCurve;
        public Vector2 a, b, c; // straight: a -> b. curve: quadratic bezier a -> b(control) -> c
        public float duration;
        public float speedValue;
    }

    [SerializeField]
    private List<PathPoint> points = new List<PathPoint>
    {
        new PathPoint(Vector2.zero),
        new PathPoint(Vector2.right)
    };

    // one speed per segment; speeds[i] applies between points[i] and points[i + 1]
    [SerializeField]
    private List<float> speeds = new List<float> { 1f };

    [Range(0f, 0.5f)]
    public float speedBlend = 0.25f; // fraction of a piece's duration used to smoothly cross-fade speed at its boundaries

    [NonSerialized] private List<Piece> pieceCache = new List<Piece>();
    [NonSerialized] private float[] segLenBuffer = Array.Empty<float>();
    [NonSerialized] private float[] cutBuffer = Array.Empty<float>();

    public IReadOnlyList<PathPoint> Points => points;
    public IReadOnlyList<float> Speeds => speeds;
    public int PointCount => points.Count;
    public int SegmentCount => points.Count - 1;

    public void SetPointPosition(int index, Vector2 position)
    {
        if (index < 0 || index >= points.Count) return;
        points[index].position = position;
    }

    public void SetJunction(int index, JunctionType type)
    {
        if (index <= 0 || index >= points.Count - 1) return; // only interior points have a junction
        points[index].junction = type;
    }

    public void SetRounding(int index, float radius)
    {
        if (index <= 0 || index >= points.Count - 1) return; // only interior points round
        points[index].roundingRadius = Mathf.Max(0f, radius);
    }

    public void SetSpeed(int segmentIndex, float speed)
    {
        if (segmentIndex < 0 || segmentIndex >= speeds.Count) return;
        speeds[segmentIndex] = Mathf.Max(0.0001f, speed);
    }

    public void InsertPointOnSegment(int segmentIndex, float t)
    {
        if (segmentIndex < 0 || segmentIndex >= SegmentCount) return;
        t = Mathf.Clamp01(t);

        var a = points[segmentIndex];
        var b = points[segmentIndex + 1];
        var newPoint = new PathPoint(Vector2.Lerp(a.position, b.position, t));
        float splitSpeed = speeds[segmentIndex];

        points.Insert(segmentIndex + 1, newPoint);
        speeds.Insert(segmentIndex, splitSpeed); // both halves inherit the original segment's speed
    }

    public void RemovePoint(int index)
    {
        if (points.Count <= 2 || index <= 0 || index >= points.Count - 1) return; // endpoints must remain
        points.RemoveAt(index);
        speeds.RemoveAt(index - 1);
    }

    /// Samples the path by elapsed time share (0-1) so each segment's speed controls how long it takes to traverse.
    public Vector2 Evaluate(float t)
    {
        if (points.Count == 0) return Vector2.zero;
        if (points.Count == 1) return points[0].position;
        if (!TryFindPiece(t, out int index, out float localT)) return points[0].position;

        var piece = pieceCache[index];
        return piece.isCurve
            ? QuadraticBezier(piece.a, piece.b, piece.c, localT)
            : Vector2.Lerp(piece.a, piece.b, localT);
    }

    /// Returns the pace at time share t, cross-fading across piece boundaries by speedBlend so it changes smoothly.
    public float EvaluateSpeed(float t)
    {
        if (points.Count < 2) return 0f;
        if (!TryFindPiece(t, out int index, out float localT)) return 0f;

        float speed = pieceCache[index].speedValue;
        if (speedBlend <= 0f) return speed;

        if (localT < speedBlend && index > 0)
        {
            float blend = Mathf.SmoothStep(0f, 1f, localT / speedBlend);
            speed = Mathf.Lerp(pieceCache[index - 1].speedValue, speed, blend);
        }
        else if (localT > 1f - speedBlend && index < pieceCache.Count - 1)
        {
            float blend = Mathf.SmoothStep(0f, 1f, (localT - (1f - speedBlend)) / speedBlend);
            speed = Mathf.Lerp(speed, pieceCache[index + 1].speedValue, blend);
        }

        return speed;
    }

    private bool TryFindPiece(float t, out int index, out float localT)
    {
        BuildPieces();
        index = -1;
        localT = 0f;
        if (pieceCache.Count == 0) return false;

        float total = 0f;
        for (int i = 0; i < pieceCache.Count; i++) total += pieceCache[i].duration;
        if (total <= 0f) { index = 0; return true; }

        float target = Mathf.Clamp01(t) * total;
        float accum = 0f;
        for (int i = 0; i < pieceCache.Count; i++)
        {
            var p = pieceCache[i];
            if (target <= accum + p.duration || i == pieceCache.Count - 1)
            {
                index = i;
                localT = p.duration > 0f ? Mathf.Clamp01((target - accum) / p.duration) : 0f;
                return true;
            }
            accum += p.duration;
        }

        index = pieceCache.Count - 1;
        localT = 1f;
        return true;
    }

    // Builds the polyline into straight/curve pieces, rounding Curve junctions using a bezier through the corner.
    private void BuildPieces()
    {
        pieceCache.Clear();
        int segCount = SegmentCount;
        if (segCount <= 0) return;

        if (segLenBuffer.Length < segCount) segLenBuffer = new float[segCount];
        if (cutBuffer.Length < points.Count) cutBuffer = new float[points.Count];

        for (int k = 0; k < segCount; k++)
            segLenBuffer[k] = Vector2.Distance(points[k].position, points[k + 1].position);

        cutBuffer[0] = 0f;
        cutBuffer[points.Count - 1] = 0f;
        for (int i = 1; i < points.Count - 1; i++)
        {
            // each corner's own radius, clamped to half of either adjoining segment so cuts never overlap
            cutBuffer[i] = points[i].junction == JunctionType.Curve
                ? Mathf.Min(points[i].roundingRadius, 0.5f * segLenBuffer[i - 1], 0.5f * segLenBuffer[i])
                : 0f;
        }

        for (int k = 0; k < segCount; k++)
        {
            Vector2 p0 = points[k].position;
            Vector2 p1 = points[k + 1].position;
            float speed = Mathf.Max(0.0001f, speeds[k]);

            float startCut = cutBuffer[k];
            float endCut = cutBuffer[k + 1];

            Vector2 lineStart = Vector2.MoveTowards(p0, p1, startCut);
            Vector2 lineEnd = Vector2.MoveTowards(p1, p0, endCut);

            if (startCut > 0f)
            {
                Vector2 arcStart = Vector2.MoveTowards(p0, points[k - 1].position, startCut);
                float arcSpeed = Mathf.Max(0.0001f, (speeds[k - 1] + speeds[k]) * 0.5f);
                pieceCache.Add(new Piece
                {
                    isCurve = true,
                    a = arcStart,
                    b = p0,
                    c = lineStart,
                    duration = (Vector2.Distance(arcStart, p0) + Vector2.Distance(p0, lineStart)) * 0.5f / arcSpeed,
                    speedValue = arcSpeed
                });
            }

            pieceCache.Add(new Piece
            {
                isCurve = false,
                a = lineStart,
                b = lineEnd,
                duration = Vector2.Distance(lineStart, lineEnd) / speed,
                speedValue = speed
            });
        }
    }

    private static Vector2 QuadraticBezier(Vector2 a, Vector2 b, Vector2 c, float t)
    {
        Vector2 ab = Vector2.Lerp(a, b, t);
        Vector2 bc = Vector2.Lerp(b, c, t);
        return Vector2.Lerp(ab, bc, t);
    }
}
