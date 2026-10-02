using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// A reference to an object that survives a save and load (or a stream out and back in): what a rope is anchored to,
/// the virus an antibody holds, a command group's members, the cell a creature stands on. A string:
/// "p" the player, "e:uid" a streamed object (WorldEntity.uid), "a:i" the i-th antibody (ImmuneSystem.Antibodies, in
/// save order), "s:path" a hand-placed scene object (names from a scene root, "#k" = k-th sibling of that name); any of
/// them may carry "|path" down to a child. "" = nothing. Resolve them only once the world is back (SaveGame's order).
/// </summary>
public static class SaveRef
{
    static readonly StringBuilder s_sb = new StringBuilder();
    static readonly List<GameObject> s_roots = new List<GameObject>();

    public static string Of(Component c) => c ? Of(c.transform) : "";

    public static string Of(Transform t)
    {
        if (!t) return "";
        Transform root;
        string head;
        WorldEntity e = t.GetComponentInParent<WorldEntity>(true);
        VirusMovement player;
        Antibody antibody;
        if (e && e.uid != 0) { root = e.transform; head = "e:" + e.uid; }
        else if ((player = t.GetComponentInParent<VirusMovement>(true))) { root = player.transform; head = "p"; }
        else if ((antibody = t.GetComponentInParent<Antibody>(true)))
        {
            int i = IndexOf(ImmuneSystem.Antibodies, antibody);
            if (i < 0) return "";
            root = antibody.transform;
            head = "a:" + i;
        }
        else return t.gameObject.scene.IsValid() ? "s:" + Path(null, t) : "";
        return t == root ? head : head + "|" + Path(root, t);
    }

    public static Transform Resolve(string r)
    {
        if (string.IsNullOrEmpty(r)) return null;
        int bar = r.IndexOf('|');
        string head = bar < 0 ? r : r.Substring(0, bar);
        Transform root = null;
        if (head == "p")
        {
            VirusMovement v = Object.FindAnyObjectByType<VirusMovement>();
            root = v ? v.transform : null;
        }
        else if (head.StartsWith("e:") && long.TryParse(head.Substring(2), out long uid))
        {
            WorldEntity e = WorldStreamer.Find(uid);
            root = e ? e.transform : null;
        }
        else if (head.StartsWith("a:") && int.TryParse(head.Substring(2), out int i))
        {
            IReadOnlyList<Antibody> all = ImmuneSystem.Antibodies;
            root = i >= 0 && i < all.Count && all[i] ? all[i].transform : null;
        }
        else if (head.StartsWith("s:")) return Find(null, head.Substring(2));
        if (!root || bar < 0) return root;
        return Find(root, r.Substring(bar + 1));
    }

    public static T Resolve<T>(string r) where T : Component
    {
        Transform t = Resolve(r);
        return t ? t.GetComponentInParent<T>() : null;
    }

    static int IndexOf(IReadOnlyList<Antibody> list, Antibody a)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == a) return i;
        return -1;
    }

    // "name#k/name#k" from 'root' (a scene root when null) down to t.
    static string Path(Transform root, Transform t)
    {
        s_sb.Clear();
        for (Transform c = t; c && c != root; c = c.parent)
        {
            string step = c.name + "#" + SameNameIndex(c);
            s_sb.Insert(0, s_sb.Length > 0 ? step + "/" : step);
        }
        return s_sb.ToString();
    }

    static int SameNameIndex(Transform c)
    {
        int k = 0;
        if (c.parent)
        {
            for (int i = 0; i < c.parent.childCount; i++)
            {
                Transform s = c.parent.GetChild(i);
                if (s == c) return k;
                if (s.name == c.name) k++;
            }
            return k;
        }
        c.gameObject.scene.GetRootGameObjects(s_roots);
        foreach (GameObject g in s_roots)
        {
            if (g.transform == c) break;
            if (g.name == c.name) k++;
        }
        s_roots.Clear();
        return k;
    }

    static Transform Find(Transform root, string path)
    {
        Transform at = root;
        foreach (string step in path.Split('/'))
        {
            int hash = step.LastIndexOf('#');
            string name = hash < 0 ? step : step.Substring(0, hash);
            int k = hash < 0 ? 0 : int.TryParse(step.Substring(hash + 1), out int n) ? n : 0;
            at = Child(at, name, k);
            if (!at) return null;
        }
        return at;
    }

    static Transform Child(Transform parent, string name, int k)
    {
        if (parent)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c.name == name && k-- == 0) return c;
            }
            return null;
        }
        SceneManager.GetActiveScene().GetRootGameObjects(s_roots);
        Transform found = null;
        foreach (GameObject g in s_roots)
            if (g.name == name && k-- == 0) { found = g.transform; break; }
        s_roots.Clear();
        return found;
    }
}
