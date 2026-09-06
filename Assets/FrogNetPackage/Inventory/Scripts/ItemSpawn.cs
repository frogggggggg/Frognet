using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Turns an <see cref="Item"/> into a pickup in the world. An item may name its own prefab under
/// <c>prefab</c>; anything that does not falls back to the shared one on <see cref="Command"/>.
/// </summary>
public static class ItemSpawn
{
    private static readonly Dictionary<int, GameObject> cache = new Dictionary<int, GameObject>();
    private static uint cachedFor;

    public static GameObject PrefabFor(Item item)
    {
        GameObject prefab = Resolve(item.id);

        if (prefab)
            return prefab;

        return Command.Instance ? Command.Instance.itemPrefab : null;
    }

    private static GameObject Resolve(int id)
    {
        if (id <= 0)
            return null;

        uint hash = ItemRegistry.Hash;

        if (cachedFor != hash)
        {
            cachedFor = hash;
            cache.Clear();
        }

        if (cache.TryGetValue(id, out GameObject cached))
            return cached;

        string path = ItemRegistry.PrefabPath(id);
        GameObject prefab = string.IsNullOrEmpty(path) ? null : Resources.Load<GameObject>(path);

        if (prefab == null && !string.IsNullOrEmpty(path))
            Debug.LogWarning($"Item '{ItemRegistry.Get(id)?.name}' asks for prefab '{path}', which is not under a Resources folder.");

        cache[id] = prefab;
        return prefab;
    }

    /// <summary>Server only. Null when the item has no prefab to spawn.</summary>
    public static Pickup Spawn(Item item, Vector3 position, Vector3 launch = default)
    {
        if (item.IsEmpty)
            return null;

        GameObject prefab = PrefabFor(item);

        if (!prefab)
        {
            Debug.LogWarning($"No pickup prefab for '{item.Name}', and no fallback on Command.");
            return null;
        }

        GameObject spawned = Object.Instantiate(prefab, position, Quaternion.identity);
        var pickup = spawned.GetComponent<Pickup>();

        if (!pickup)
        {
            Debug.LogWarning($"Pickup prefab for '{item.Name}' has no Pickup component.");
            Object.Destroy(spawned);
            return null;
        }

        pickup.Initialize(item, launch);
        return pickup;
    }
}
