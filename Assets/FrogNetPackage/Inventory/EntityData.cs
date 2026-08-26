using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Frognet/Entity", fileName = "Entity")]
public class EntityData : ScriptableObject
{
    private static EntityData[] cachedAll;

    public string entityName;
    [TextArea] public string description;

    public string[] states;

    [SerializeReference]
    public Stat[] stats = new Stat[0];

    public static IReadOnlyList<EntityData> All
    {
        get
        {
            if (cachedAll == null)
                cachedAll = LoadAll();
            return cachedAll;
        }
    }

    public static EntityData Find(string entityName)
    {
        if (string.IsNullOrEmpty(entityName))
            return null;

        IReadOnlyList<EntityData> all = All;

        for (int i = 0; i < all.Count; i++)
        {
            EntityData entity = all[i];
            if (entity != null && entity.entityName == entityName)
                return entity;
        }

        return null;
    }

    public T GetStat<T>() where T : Stat
    {
        if (stats == null)
            return null;

        for (int i = 0; i < stats.Length; i++)
        {
            if (stats[i] is T match)
                return match;
        }

        return null;
    }

    private static EntityData[] LoadAll()
    {
#if UNITY_EDITOR
        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:EntityData");
        var items = new List<EntityData>(guids.Length);

        for (int i = 0; i < guids.Length; i++)
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            EntityData entity = UnityEditor.AssetDatabase.LoadAssetAtPath<EntityData>(path);
            if (entity != null)
                items.Add(entity);
        }

        return items.ToArray();
#else
        return Resources.LoadAll<EntityData>(string.Empty);
#endif
    }
}
