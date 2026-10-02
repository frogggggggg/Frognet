using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// One object in the world as the command line sees it: the root of a creature, cell, chunk, antibody... with its
/// kinds ("tags": redbloodcell, cell, virus, glucose...). Made once per object and kept (so == works across commands).
/// </summary>
public sealed class Thing
{
    public readonly GameObject go;
    public readonly Transform t;
    public string kind;
    public readonly HashSet<string> kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public Organism organism;
    public VirusMovement player;
    public Surface surface;
    public ResourceChunk chunk;
    public WhiteBloodCell white;
    public Antibody antibody;
    public Rigidbody body;

    public Thing(GameObject go)
    {
        this.go = go;
        t = go.transform;
    }

    public bool Alive => go;
    public Vector3 Pos => chunk ? chunk.Centre : t.position;
    public override string ToString() => kind;
}

/// <summary>A substance and how much (50*glucose): what the inventory takes and gives.</summary>
public sealed class Amount
{
    public string substance;
    public float amount;
    public override string ToString() => Cmd.F(amount) + " " + substance.ToLowerInvariant();
}

/// <summary>A DNA strand (dna.kill): a crafting recipe's gene, or a gene a virus carries.</summary>
public sealed class GeneItem
{
    public Genome.Gene gene;
    public override string ToString() => $"dna {gene.code} {gene.name.ToLowerInvariant()}";
}

/// <summary>A spawnable prefab (spawn(cell, aim)).</summary>
public sealed class Prefab
{
    public string key;
    public override string ToString() => "prefab " + key;
}

/// <summary>
/// Frognet's words for the command language (Cmd, CmdLang.cs): all / me / cam / aim / target, kinds, things'
/// properties (pos, vel, speed, inventory...), verbs (kill, delete), nearest / farthest, spawn, substances, dna.
/// See Assets/Viral/Console/CLAUDE.md.
/// </summary>
public static class CmdWorld
{
    static readonly Dictionary<GameObject, Thing> s_things = new Dictionary<GameObject, Thing>();
    static readonly Dictionary<Surface, Thing> s_surfaces = new Dictionary<Surface, Thing>();
    static readonly List<GameObject> s_dead = new List<GameObject>();
    static readonly List<Surface> s_deadSurfaces = new List<Surface>();
    static readonly HashSet<Thing> s_seen = new HashSet<Thing>();
    static readonly List<object> s_all = new List<object>();
    static readonly HashSet<string> s_kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "virus", "player", "ai", "cell", "redbloodcell", "rbc", "whitebloodcell", "wbc", "immune", "chunk", "resource",
        "glucose", "protein", "antibody", "surface", "streamed",
    };
    static readonly HashSet<string> s_prefabWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly RaycastHit[] s_hits = new RaycastHit[64];
    static int s_allFrame = -1;
    static bool s_registered;
    static VirusMovement s_player;

    /// <summary>Registers everything once (again after a script reload wiped the registries).</summary>
    public static void Ensure()
    {
        Cmd.EnsureCore();
        if (s_registered && Cmd.Words.ContainsKey("me")) return;
        s_registered = true;
        Hooks();
        Properties();
        Verbs();
        Words();
    }

    /// <summary>Picks up what may have changed since the last command: the player's recipes (dna.*), the streamer's
    /// prefabs (spawn words).</summary>
    public static void Refresh()
    {
        Ensure();
        VirusMovement p = Player();
        var dna = new Cmd.Space { name = "dna" };
        if (p)
            foreach (Crafting.Recipe r in Crafting.Of(p).recipes)
            {
                if (r == null || r.liquid) continue;
                var item = new GeneItem { gene = GeneOf(r) };
                string name = Norm(r.name);
                dna.members[name] = item;
                dna.members[Norm(r.code)] = item;
                Cmd.AddWord("dna-" + name, $"dna {r.code} {r.name.ToLowerInvariant()}: put in with me.inventory += dna-{name}", e => item);
            }
        Cmd.AddWord("dna", "genes by name: dna.kill (me.inventory += dna.kill)", e => dna);

        WorldStreamer ws = WorldStreamer.Instance;
        if (ws)
            foreach (string key in ws.Keys)
            {
                string name = Norm(key);
                var prefab = new Prefab { key = key };
                if (Cmd.Words.ContainsKey(name) && !s_prefabWords.Contains(name)) continue; // never shadow a real word
                s_prefabWords.Add(name);
                Cmd.AddWord(name, $"the {key} prefab: spawn({name}, aim)", e => prefab);
                s_kinds.Add(name);
            }
    }

    // ---------------- things ----------------

    public static VirusMovement Player()
    {
        if (!s_player) s_player = Object.FindAnyObjectByType<VirusMovement>();
        return s_player;
    }

    static Thing Me()
    {
        VirusMovement p = Player();
        if (!p) throw new Cmd.Error("no player in this scene");
        return Get(p.gameObject);
    }

    /// <summary>Everything in the world (a fresh list; built once a frame).</summary>
    public static Cmd.Many All()
    {
        if (s_allFrame != Time.frameCount)
        {
            s_allFrame = Time.frameCount;
            Gather();
        }
        var m = new Cmd.Many(s_all.Count);
        foreach (object x in s_all)
            if (((Thing)x).Alive) m.Add(x);
        return m;
    }

    // From the game's own registries: no scene search. O(things).
    static void Gather()
    {
        s_dead.Clear();
        foreach (var kv in s_things) if (!kv.Key) s_dead.Add(kv.Key);
        foreach (GameObject g in s_dead) s_things.Remove(g);
        s_deadSurfaces.Clear();
        foreach (var kv in s_surfaces) if (!kv.Key || !kv.Value.Alive) s_deadSurfaces.Add(kv.Key);
        foreach (Surface s in s_deadSurfaces) s_surfaces.Remove(s);

        s_all.Clear();
        s_seen.Clear();
        foreach (Organism o in Organism.All) if (o) Add(Get(o.gameObject));
        foreach (WhiteBloodCell w in WhiteBloodCells.All) if (w) Add(Get(w.gameObject));
        foreach (ResourceChunk c in ResourceField.All) if (c) Add(Get(c.gameObject));
        foreach (Antibody a in ImmuneSystem.Antibodies) if (a) Add(Get(a.gameObject));
        foreach (Surface s in Surface.All)
        {
            if (!s) continue;
            if (!s_surfaces.TryGetValue(s, out Thing th)) s_surfaces[s] = th = Get(RootOf(s));
            Add(th);
        }
        foreach (WorldEntity e in WorldStreamer.Live) if (e) Add(Get(e.gameObject));
    }

    static void Add(Thing th)
    {
        if (th != null && s_seen.Add(th)) s_all.Add(th);
    }

    // The object a Surface is part of: its chunk, white cell, creature or streamed root, else itself.
    static GameObject RootOf(Surface s)
    {
        ResourceChunk c = s.GetComponentInParent<ResourceChunk>();
        if (c) return c.gameObject;
        WhiteBloodCell w = s.GetComponentInParent<WhiteBloodCell>();
        if (w) return w.gameObject;
        Organism o = s.GetComponentInParent<Organism>();
        if (o) return o.gameObject;
        WorldEntity e = s.GetComponentInParent<WorldEntity>();
        return e ? e.gameObject : s.gameObject;
    }

    static Thing Get(GameObject go)
    {
        if (!go) return null;
        if (s_things.TryGetValue(go, out Thing th)) return th;
        th = new Thing(go);
        Classify(th);
        s_things[go] = th;
        return th;
    }

    static void Classify(Thing th)
    {
        GameObject go = th.go;
        go.TryGetComponent(out th.body);
        go.TryGetComponent(out th.organism);
        go.TryGetComponent(out th.white);
        go.TryGetComponent(out th.chunk);
        go.TryGetComponent(out th.antibody);
        th.surface = go.GetComponentInChildren<Surface>(true);
        if (th.organism)
        {
            th.body = th.organism.Rb ? th.organism.Rb : th.body;
            th.kind = "virus";
            if (go.TryGetComponent(out th.player)) th.kinds.Add("player");
            if (go.TryGetComponent(out VirusAI _)) th.kinds.Add("ai");
        }
        else if (th.white) { th.kind = "whitebloodcell"; Tag(th, "wbc", "cell", "immune"); }
        else if (th.chunk) { th.kind = "chunk"; Tag(th, "resource", Norm(th.chunk.substance.name)); }
        else if (th.antibody) { th.kind = "antibody"; Tag(th, "immune"); }
        else if (th.surface && th.surface.isCell)
        {
            CellProfile p = th.surface.TryGetComponent(out CellInterior ci) ? ci.Profile
                          : ViralBuildAssets.Instance ? ViralBuildAssets.Instance.defaultCellProfile : null;
            th.kind = p ? Norm(p.displayName) : "cell";
            Tag(th, "cell", p ? Norm(p.code) : null);
        }
        else th.kind = th.surface ? "surface" : Norm(go.name);
        th.kinds.Add(th.kind);
        if (go.TryGetComponent(out WorldEntity entity)) Tag(th, "streamed", Norm(entity.key));
        foreach (string k in th.kinds) s_kinds.Add(k);
    }

    static void Tag(Thing th, params string[] kinds)
    {
        foreach (string k in kinds) if (!string.IsNullOrEmpty(k)) th.kinds.Add(k);
    }

    /// <summary>Lower case, letters and digits only: "RED BLOOD CELL" -> redbloodcell.</summary>
    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // What's under the crosshair (or the mouse, when the cursor is free): the first hit that isn't the player.
    static bool Aim(out RaycastHit hit, out Ray ray)
    {
        hit = default;
        Camera cam = Camera.main;
        ray = default;
        if (!cam) return false;
        bool free = Cursor.lockState != CursorLockMode.Locked && UnityEngine.InputSystem.Mouse.current != null;
        ray = free ? cam.ScreenPointToRay(UnityEngine.InputSystem.Mouse.current.position.ReadValue()) : cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        int n = Physics.RaycastNonAlloc(ray, s_hits, 5000f, ~0, QueryTriggerInteraction.Ignore);
        VirusMovement p = Player();
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (p && s_hits[i].transform.IsChildOf(p.transform)) continue;
            if (s_hits[i].distance < best) { best = s_hits[i].distance; hit = s_hits[i]; }
        }
        return best < float.MaxValue;
    }

    static Thing ThingAt(Transform t)
    {
        All();
        for (; t; t = t.parent)
            if (s_things.TryGetValue(t.gameObject, out Thing th)) return th;
        return null;
    }

    static Vector3 From() => Player() ? Player().transform.position : SimulationTicker.CameraPosition;

    // ---------------- hooks ----------------

    static void Hooks()
    {
        Cmd.Is = (v, kind) => v is Thing th ? th.kinds.Contains(kind)
                              : v is Amount a ? string.Equals(Norm(a.substance), kind, StringComparison.OrdinalIgnoreCase)
                              : v is GeneItem g && (string.Equals(Norm(g.gene.name), kind, StringComparison.OrdinalIgnoreCase) || kind == "dna");
        Cmd.KindsOf = v => v is Thing th ? (IEnumerable<string>)th.kinds : v is Amount a ? new[] { Norm(a.substance) } : v is GeneItem ? new[] { "dna" } : Array.Empty<string>();
        Cmd.IsKind = k => s_kinds.Contains(k);
        Cmd.AllKinds = () => s_kinds;
        Cmd.KindName = v => v is Thing th ? th.kind : v is Amount ? "substance" : v is GeneItem ? "dna" : v is Prefab ? "prefab" : null;
        Cmd.Describe = Describe;
        Cmd.ToPosition = v => v is Thing th ? th.Pos : (Vector3?)null;
        Cmd.Operators.Clear();
        Cmd.Operators.Add((op, a, b) =>
        {
            if (op == "*" && a is Amount x && b is float f) return new Amount { substance = x.substance, amount = x.amount * f };
            if (op == "*" && b is Amount y && a is float g) return new Amount { substance = y.substance, amount = y.amount * g };
            if (op == "/" && a is Amount z && b is float h && h != 0f) return new Amount { substance = z.substance, amount = z.amount / h };
            if (op == "==" && a is Thing ta && b is Thing tb) return ta == tb;
            return null;
        });
    }

    static string Describe(object v)
    {
        if (!(v is Thing th)) return null;
        if (!th.Alive) return th.kind + " (gone)";
        Vector3 p = th.Pos;
        string extra = th.player ? "  (you)"
                     : th.chunk ? $"  {Cmd.F(th.chunk.Remaining)} {th.chunk.substance.name.ToLowerInvariant()}"
                     : "";
        return $"{th.kind,-15} {Cmd.F(p),-24} {Cmd.F((p - From()).magnitude)} m{extra}";
    }

    // ---------------- properties ----------------

    static void Prop(string name, string help, Func<Thing, bool> on, Func<Thing, object> get, Action<Thing, object> set = null,
                     Func<Thing, object, string> add = null, Func<Thing, object, string> remove = null)
    {
        Cmd.Props.Add(new Cmd.Prop
        {
            name = name, help = help,
            on = v => v is Thing th && th.Alive && (on == null || on(th)),
            get = v => get((Thing)v),
            set = set == null ? null : (Action<object, object>)((v, x) => set((Thing)v, x)),
            add = add == null ? null : (Func<object, object, string>)((v, x) => add((Thing)v, x)),
            remove = remove == null ? null : (Func<object, object, string>)((v, x) => remove((Thing)v, x)),
        });
    }

    static void ValueProp<T>(string name, string help, Func<T, object> get)
    {
        Cmd.Props.Add(new Cmd.Prop { name = name, help = help, on = v => v is T, get = v => get((T)v) });
    }

    static void Properties()
    {
        Cmd.Props.Clear();
        Prop("pos", "position", null, th => th.Pos, (th, v) => SetPos(th, Cmd.Vec(v)));
        Prop("vel", "velocity (m/s)", null,
             th => th.body ? th.body.linearVelocity : th.chunk ? th.chunk.Velocity : Vector3.zero,
             (th, v) =>
             {
                 if (!th.body || th.body.isKinematic) throw new Cmd.Error($"{th.kind} isn't moved by physics");
                 th.body.linearVelocity = Cmd.Vec(v);
             });
        Prop("forward", "facing (a virus: where it aims)", null, th => th.organism ? th.organism.aimForward.normalized : th.t.forward);
        Prop("up", "its up", null, th => th.organism ? th.organism.up : th.t.up);
        Prop("right", "its right", null, th => th.organism ? Vector3.Cross(th.organism.up, th.organism.aimForward).normalized : th.t.right);
        Prop("rot", "rotation (degrees x, y, z)", th => !th.organism, th => th.t.eulerAngles, (th, v) =>
        {
            Quaternion q = Quaternion.Euler(Cmd.Vec(v));
            th.t.rotation = q;
            if (th.body) th.body.rotation = q;
        });
        Prop("scale", "size (a chunk: its radius)", null, th => th.chunk ? th.chunk.radius : th.t.localScale.x, (th, v) =>
        {
            if (th.chunk) { th.chunk.Resize(Mathf.Max(0.05f, Cmd.Num(v, "scale"))); return; }
            th.t.localScale = v is Vector3 s ? s : Vector3.one * Mathf.Max(0.001f, Cmd.Num(v, "scale"));
        });
        Prop("dist", "distance from you (m)", null, th => (th.Pos - From()).magnitude);
        Prop("name", "object name", null, th => th.go.name, (th, v) => th.go.name = v is string s ? s : Cmd.Summary(v));
        Prop("kind", "main kind", null, th => th.kind);
        Prop("kinds", "every kind it is", null, th =>
        {
            var m = new Cmd.Many();
            foreach (string k in th.kinds) m.Add(k);
            return m;
        });
        Prop("id", "unique number", null, th => (float)th.go.GetInstanceID());
        Prop("onscreen", "in view (true / false)", null, th => SimulationTicker.OnScreen(th.Pos, 1f));

        Prop("speed", "flying speed (m/s)", th => th.organism, th => th.organism.flying.moving.thrust.speed,
             (th, v) => th.organism.flying.moving.thrust.speed = Mathf.Max(0f, Cmd.Num(v, "speed")));
        Prop("crawlspeed", "crawling speed (m/s)", th => th.organism, th => th.organism.grounded.moving.crawl.speed,
             (th, v) => th.organism.grounded.moving.crawl.speed = Mathf.Max(0f, Cmd.Num(v, "crawlspeed")));
        Prop("dashspeed", "dash (burst) peak speed (m/s)", th => th.organism, th => th.organism.flying.charging.burst.speed,
             (th, v) => th.organism.flying.charging.burst.speed = Mathf.Max(0f, Cmd.Num(v, "dashspeed")));
        Prop("state", "what it's doing", th => th.organism, th => th.organism.Current != null ? th.organism.Current.GetType().Name.ToLowerInvariant() : "none");
        Prop("grounded", "on a surface (true / false)", th => th.organism, th => th.organism.OnSurface);

        Prop("inventory", "what it holds: += 50*glucose, += dna-kill, -= ...", th => th.organism || IsCell(th), Inventory, add: AddTo, remove: TakeFrom);
        Prop("genes", "its DNA strands", th => th.organism, th => Genes(th), add: AddTo, remove: TakeFrom);
        Prop("amount", "what's left to extract", th => th.chunk, th => th.chunk.Remaining,
             (th, v) => th.chunk.Remaining = Mathf.Clamp(Cmd.Num(v, "amount"), 0f, th.chunk.Amount));
        Prop("substance", "what it's made of", th => th.chunk, th => th.chunk.substance.name.ToLowerInvariant());

        ValueProp<Vector3>("x", "x", v => v.x);
        ValueProp<Vector3>("y", "y", v => v.y);
        ValueProp<Vector3>("z", "z", v => v.z);
        ValueProp<Vector3>("len", "length", v => v.magnitude);
        ValueProp<Vector3>("norm", "direction (length 1)", v => v.normalized);
        ValueProp<Amount>("amount", "how much", a => a.amount);
        ValueProp<Amount>("substance", "what", a => a.substance.ToLowerInvariant());
        ValueProp<GeneItem>("code", "its code", g => g.gene.code);
        ValueProp<GeneItem>("name", "what it does", g => g.gene.name.ToLowerInvariant());
    }

    static bool IsCell(Thing th) => th.surface && th.surface.isCell && !th.white;

    // A teleport: a creature is let go (white cell, surface, ropes) first, the player's cameras jump with it.
    static void SetPos(Thing th, Vector3 p)
    {
        if (th.organism)
        {
            Organism o = th.organism;
            WhiteBloodCells.Free(o, p);
            if (o.grounded.surface.Attached) o.grounded.surface.Detach(Vector3.zero);
            if (th.player && th.player.rope) th.player.rope.ClearAllRopes();
            o.transform.position = p;
            if (o.Rb) o.Rb.position = p;
            Physics.SyncTransforms();
            if (th.player)
                foreach (UniversalCamera cam in Object.FindObjectsByType<UniversalCamera>(FindObjectsSortMode.None)) cam.Teleport();
            return;
        }
        if (th.chunk) { th.chunk.Place(p); return; }
        th.t.position = p;
        if (th.body) th.body.position = p;
    }

    // ---------------- inventory ----------------

    static Cmd.Many Inventory(Thing th)
    {
        var m = new Cmd.Many();
        if (th.organism)
        {
            VirusInventory inv = th.go.GetComponentInChildren<VirusInventory>(true);
            if (inv)
                foreach (StoreSlot s in inv.Slots)
                    if (!s.Empty) m.Add(new Amount { substance = s.substance, amount = s.amount });
            m.AddRange(Genes(th));
        }
        else if (th.surface.TryGetComponent(out CellInterior c))
            foreach (StoreSlot s in c.store)
                if (!s.Empty) m.Add(new Amount { substance = s.substance, amount = s.amount });
        return m;
    }

    static Cmd.Many Genes(Thing th)
    {
        var m = new Cmd.Many();
        Genome g = th.go.GetComponentInChildren<Genome>(true);
        if (g) foreach (Genome.Gene gene in g.genes) m.Add(new GeneItem { gene = gene });
        return m;
    }

    static string AddTo(Thing th, object item)
    {
        switch (item)
        {
            case Amount a when th.organism:
            {
                Substance s = SubstanceCatalog.Find(a.substance);
                float got = VirusInventory.Of(th.organism).Add(s, a.amount);
                return $"+{Cmd.F(got)} {s.name.ToLowerInvariant()}" + (got < a.amount - 1e-3f ? " (full)" : "");
            }
            case Amount a:
            {
                CellInterior c = CellInterior.For(th.surface.transform);
                Substance s = SubstanceCatalog.Find(a.substance);
                float got = Stores.Add(c.store, c.Profile ? c.Profile.capacity : 100f, s, a.amount);
                return $"+{Cmd.F(got)} {s.name.ToLowerInvariant()}" + (got < a.amount - 1e-3f ? " (full)" : "");
            }
            case GeneItem g when th.organism:
            {
                VirusInventory inv = VirusInventory.Of(th.organism);
                Genome genome = Genome.Of(th.organism);
                IReadOnlyList<VirusInventory.Mount> ring = inv.Ring(genome.genes.Count);
                int mount = -1;
                for (int i = 0; i < ring.Count; i++)
                    if (ring[i].kind == VirusInventory.Kind.Gene && ring[i].index < 0) { mount = i; break; }
                genome.genes.Add(Copy(g.gene));
                if (mount >= 0) inv.ring[mount].index = genome.genes.Count - 1; // else Ring() gives it a new mount
                return $"+dna {g.gene.code}";
            }
            case GeneItem _:
                throw new Cmd.Error("only a virus carries DNA");
            case Prefab p:
                throw new Cmd.Error($"{p.key} isn't something to carry (spawn({Norm(p.key)}, aim) makes one)");
        }
        throw new Cmd.Error($"can't put {Cmd.TypeName(item)} in an inventory (50*glucose, dna.kill)");
    }

    static string TakeFrom(Thing th, object item)
    {
        switch (item)
        {
            case Amount a when th.organism:
            {
                float got = VirusInventory.Of(th.organism).Take(SubstanceCatalog.Find(a.substance).name, a.amount);
                return $"-{Cmd.F(got)} {a.substance.ToLowerInvariant()}";
            }
            case Amount a:
            {
                if (!th.surface.TryGetComponent(out CellInterior c)) return "nothing to take";
                float got = Stores.Take(c.store, SubstanceCatalog.Find(a.substance).name, a.amount);
                return $"-{Cmd.F(got)} {a.substance.ToLowerInvariant()}";
            }
            case GeneItem g when th.organism:
            {
                Genome genome = Genome.Of(th.organism);
                for (int i = genome.genes.Count - 1; i >= 0; i--)
                {
                    if (genome.genes[i].code != g.gene.code) continue;
                    VirusInventory.Of(th.organism).GeneRemoved(i);
                    genome.Consume(i);
                    return $"-dna {g.gene.code}";
                }
                return $"no dna {g.gene.code} to take";
            }
        }
        throw new Cmd.Error($"can't take {Cmd.TypeName(item)} out of an inventory");
    }

    static Genome.Gene GeneOf(Crafting.Recipe r) =>
        new Genome.Gene { code = r.code, name = r.name, color = r.color, targets = r.targets, effect = r.effect, delay = r.delay };

    static Genome.Gene Copy(Genome.Gene g) =>
        new Genome.Gene { code = g.code, name = g.name, color = g.color, targets = g.targets, effect = g.effect, delay = g.delay };

    // ---------------- verbs ----------------

    static void Verbs()
    {
        Cmd.Verbs.Clear();
        Cmd.Verbs["kill"] = new Cmd.Verb
        {
            name = "kill", done = "killed", help = "cells burst, anything else is removed",
            on = v => v is Thing th && th.Alive && !th.player,
            run = v => Kill((Thing)v),
        };
        Cmd.Verbs["delete"] = new Cmd.Verb
        {
            name = "delete", done = "deleted", help = "removed on the spot",
            on = v => v is Thing th && th.Alive && !th.player,
            run = v => { Object.Destroy(((Thing)v).go); return true; },
        };
    }

    // Quietly (no alarm): a burst from the side facing the player where the body can (cells, white cells).
    static bool Kill(Thing th)
    {
        if (th.surface && !th.chunk || th.white)
        {
            Vector3 c = th.Pos;
            float r = 1f;
            if (th.go.TryGetComponent(out Renderer rend)) r = rend.bounds.extents.magnitude * 0.7f;
            else if (th.surface && th.surface.TryGetComponent(out Renderer sr)) r = sr.bounds.extents.magnitude * 0.7f;
            Vector3 toward = From() - c;
            Vector3 at = c + (toward.sqrMagnitude > 1e-6f ? toward.normalized : Vector3.up) * r;
            if (CellBurst.Kill(th.t, at)) return true;
            if (CellBurst.Bursting(th.t)) return false;
        }
        Object.Destroy(th.go);
        return true;
    }

    // ---------------- words, functions, list ops ----------------

    static void Words()
    {
        Cmd.AddWord("me", "you (the player's virus)", e => Me());
        Cmd.AddWord("all", "everything in the world; shrink it: all.cell, all.(dist < 20), all.mix.(index < 5)", e => All());
        Cmd.AddWord("cam", "the camera", e =>
        {
            Camera c = Camera.main;
            if (!c) throw new Cmd.Error("no camera");
            Thing th = Get(c.gameObject);
            th.kind = "camera";
            return th;
        });
        Cmd.AddWord("aim", "the point under the crosshair (or mouse)", e =>
        {
            if (Aim(out RaycastHit hit, out Ray ray)) return hit.point;
            return ray.direction.sqrMagnitude > 0f ? ray.origin + ray.direction * 50f : From();
        });
        Cmd.AddWord("target", "the thing under the crosshair (or mouse)", e => Aim(out RaycastHit hit, out _) ? ThingAt(hit.transform) : null);
        foreach (string s in new[] { SubstanceCatalog.ATP, SubstanceCatalog.Oxygen, SubstanceCatalog.Glucose, SubstanceCatalog.Protein })
        {
            string name = s;
            Cmd.AddWord(Norm(name), $"1 {name.ToLowerInvariant()} (50*{Norm(name)} is 50)", e => new Amount { substance = name, amount = 1f });
        }

        Cmd.AddOp("nearest", "(n)", "the nearest to you, or the n nearest", (e, m, a) => ByDistance(e, m, a, false));
        Cmd.AddOp("farthest", "(n)", "the farthest from you, or the n farthest", (e, m, a) => ByDistance(e, m, a, true));

        Cmd.AddFn("spawn", "(what, pos, count, size)", "makes new ones: spawn(cell, aim), spawn(glucosechunk, me.pos + me.forward*20, 5)", Spawn);
        Refresh();
    }

    static object ByDistance(Cmd.Env e, Cmd.Many m, Cmd.Node[] a, bool far)
    {
        Vector3 from = From();
        Cmd.Many sorted = Cmd.Sorted(e, m, null, x => (far ? -1f : 1f) * (Cmd.Vec(x) - from).sqrMagnitude);
        if (a.Length == 0) return sorted.Count > 0 ? sorted[0] : null;
        int n = Cmd.Int(Cmd.Each(e, a[0], null, 0), "n");
        return Cmd.Take(sorted, n);
    }

    static object Spawn(Cmd.Env e, object[] a)
    {
        object what = Cmd.Arg(a, 0, "spawn");
        string key = what is Prefab p ? p.key : what is string s ? s : what is Thing th ? KeyOf(th) : null;
        if (key == null) throw new Cmd.Error($"spawn what? a prefab name ({string.Join(", ", Keys())}), not {Cmd.TypeName(what)}");
        WorldStreamer ws = WorldStreamer.Instance;
        if (!ws) throw new Cmd.Error("no WorldStreamer in this scene to spawn with");
        Vector3 at = a.Length > 1 ? Cmd.Vec(a[1]) : Cmd.Vec(Cmd.Words["aim"].get(e));
        int count = a.Length > 2 ? Mathf.Clamp(Cmd.Int(a[2], "count"), 0, 500) : 1;
        float size = a.Length > 3 ? Cmd.Num(a[3], "size") : 0f;
        var made = new Cmd.Many();
        if (e.dry) { e.Say($"spawn {count} {Norm(key)}"); return made; }
        float spread = count > 1 ? 6f * Mathf.Pow(count, 1f / 3f) : 0f;
        for (int i = 0; i < count; i++)
        {
            GameObject go = ws.SpawnNew(key, at + UnityEngine.Random.insideUnitSphere * spread, size);
            if (!go) throw new Cmd.Error($"no prefab '{key}' (try {string.Join(", ", Keys())})");
            made.Add(Get(go));
        }
        s_allFrame = -1;
        e.Say($"spawned {made.Count} {Norm(key)}", Cmd.Tone.Result);
        return made.Count == 1 ? made[0] : made;
    }

    static string KeyOf(Thing th) => th.go.TryGetComponent(out WorldEntity entity) ? entity.key : null;

    static IEnumerable<string> Keys()
    {
        var list = new List<string>();
        if (WorldStreamer.Instance) foreach (string k in WorldStreamer.Instance.Keys) list.Add(Norm(k));
        return list;
    }
}
