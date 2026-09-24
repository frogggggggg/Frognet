using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scatters prefabs through a ball around this object: spread through the whole volume
/// (not just its surface), picked by weight, with no two overlapping each other or
/// anything already in the scene.
/// </summary>
public class SpawnManager : MonoBehaviour
{
    [Serializable]
    public class SpawnItem
    {
        public GameObject spawnObject;
        public float weight = 1f;
    }

    [Min(0f), Tooltip("Radius of the ball, around this object, that spawns fill.")]
    public float spawnRadius = 100;
    [Min(0)] public int amount = 100;
    public List<SpawnItem> spawnItems = new List<SpawnItem>();

    [Header("Placement")]
    [Min(0f), Tooltip("Extra gap kept between spawns and anything already there, in world units.")]
    public float spacing = 5f;
    [Tooltip("Random scale multiplier per spawn (min, max).")]
    public Vector2 scaleRange = new Vector2(1f, 1f);
    public bool randomRotation = true;
    [Tooltip("Layers that block a spawn spot (the player, scenery, other cells).")]
    public LayerMask blockingLayers = ~0;
    [Min(1), Tooltip("Tries per spawn before giving up on it (the ball is too full).")]
    public int attempts = 30;
    [Tooltip("Use a fixed seed so the layout repeats. 0 = different every time.")]
    public int seed;

    class Template
    {
        public GameObject prefab;
        public float radius; // bounding radius at scale 1
        public float weight;
    }

    void Start()
    {
        if (seed != 0) UnityEngine.Random.InitState(seed);

        var templates = new List<Template>();
        float totalWeight = 0f;
        foreach (SpawnItem item in spawnItems)
        {
            if (!item.spawnObject || item.weight <= 0f) continue;
            templates.Add(new Template { prefab = item.spawnObject, radius = BoundingRadius(item.spawnObject), weight = item.weight });
            totalWeight += item.weight;
        }
        if (totalWeight > 0f) Spawn(templates, totalWeight);
    }

    void Spawn(List<Template> templates, float totalWeight)
    {
        var placed = new List<Vector4>(amount); // xyz = centre, w = radius
        Vector3 centre = transform.position;
        int skipped = 0;

        for (int n = 0; n < amount; n++)
        {
            Template t = Pick(templates, totalWeight);
            float scale = UnityEngine.Random.Range(scaleRange.x, Mathf.Max(scaleRange.x, scaleRange.y));
            float radius = t.radius * scale;

            if (!FindSpot(centre, radius, placed, out Vector3 position))
            {
                skipped++;
                continue;
            }
            placed.Add(new Vector4(position.x, position.y, position.z, radius));

            Quaternion rotation = randomRotation ? UnityEngine.Random.rotation : t.prefab.transform.rotation;
            GameObject spawn = Instantiate(t.prefab, position, rotation);
            spawn.transform.localScale = t.prefab.transform.localScale * scale;
        }

        if (skipped > 0)
            Debug.LogWarning($"SpawnManager: no room for {skipped} of {amount} spawns; raise spawnRadius or lower spacing.", this);
    }

    Template Pick(List<Template> templates, float totalWeight)
    {
        float r = UnityEngine.Random.value * totalWeight;
        foreach (Template t in templates)
        {
            r -= t.weight;
            if (r <= 0f) return t;
        }
        return templates[templates.Count - 1];
    }

    // A random point spread evenly through the ball, clear of earlier spawns and of anything
    // already in the scene.
    bool FindSpot(Vector3 centre, float radius, List<Vector4> placed, out Vector3 position)
    {
        float reach = Mathf.Max(0f, spawnRadius - radius);
        for (int a = 0; a < attempts; a++)
        {
            position = centre + UnityEngine.Random.insideUnitSphere * reach;
            if (Clear(position, radius, placed)) return true;
        }
        position = default;
        return false;
    }

    bool Clear(Vector3 position, float radius, List<Vector4> placed)
    {
        for (int i = 0; i < placed.Count; i++)
        {
            Vector4 p = placed[i];
            float gap = radius + p.w + spacing;
            if (((Vector3)p - position).sqrMagnitude < gap * gap) return false;
        }
        return !Physics.CheckSphere(position, radius + spacing, blockingLayers, QueryTriggerInteraction.Ignore);
    }

    // Radius around the object's origin that its meshes reach: each mesh's centre plus its
    // longest half-axis (not the box corner, which oversizes round things like cells by 1.7x).
    // Worked out from mesh bounds because a prefab asset's renderers report no bounds.
    static float BoundingRadius(GameObject root)
    {
        float radius = 0f;
        Vector3 rootScale = root.transform.localScale;
        Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!filter.sharedMesh) continue;
            Bounds b = filter.sharedMesh.bounds;
            // Mesh space -> root space, including the root's own scale.
            Matrix4x4 m = Matrix4x4.Scale(rootScale) * toRoot * filter.transform.localToWorldMatrix;
            float half = Mathf.Max(m.MultiplyVector(new Vector3(b.extents.x, 0f, 0f)).magnitude,
                                   m.MultiplyVector(new Vector3(0f, b.extents.y, 0f)).magnitude,
                                   m.MultiplyVector(new Vector3(0f, 0f, b.extents.z)).magnitude);
            radius = Mathf.Max(radius, m.MultiplyPoint3x4(b.center).magnitude + half);
        }
        return radius;
    }
}
