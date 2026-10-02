using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Save slots for the session, as JSON in persistentDataPath/Saves/slotN.json: the game as it was.
/// - Player: pose, velocity, what it stands on (Organism's IWorldState), stores, head ring, genes + the loaded one.
/// - World: WorldStreamer.Capture (every visited sector's contents as they are now, with each object's IWorldStates:
///   chunks' yield, cells' insides, alarm / converted (CellSignal), tendrils, creatures' footing; the seed for the rest;
///   the vessel's clock, alerts and frame).
/// - Hand-placed scene objects (Rigidbodies, Surfaces, IWorldStates outside the streamed world): pose, velocity, states.
/// - Antibodies (ImmuneSystem: state, prey, slot, grip), ropes (VirusRope: shape, anchors, held, settings, blood /
///   seal), the command board (groups, links, chains).
/// Cross references (a rope's anchor, an antibody's prey, a group's members) go by SaveRef, resolved once what they
/// point at is back, so the load runs in order: player out of trouble, world, scene objects, player state, antibodies,
/// ropes, board, cameras.
///
/// Not kept: things on their way out (a cell bursting, a virus being swallowed: gone), white cells' hunt (they look
/// round again), a gene's effect still waiting on its delay, an extraction or injection in progress, the camera's
/// angle, UI. A hand-placed object destroyed since the scene loaded can't come back. Add a component's state by
/// implementing IWorldState on it; a manager's by a Capture / Restore pair called here.
/// </summary>
public static class SaveGame
{
    public const int Slots = 3;
    const int Version = 2;

    [Serializable]
    class SaveFile
    {
        public int version = Version;
        public string scene;
        public string savedAt;
        public PlayerData player = new PlayerData();
        public WorldStreamer.WorldSave world;
        public List<SceneObject> sceneObjects;
        public ImmuneSystem.Save immune;
        public VirusRope.Save ropes;
        public CommandBoard.SavedPlan board;
    }

    [Serializable]
    class PlayerData
    {
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 velocity;
        public List<string> stateKeys, states;
        public List<StoreSlot> slots = new List<StoreSlot>();
        public List<VirusInventory.Mount> ring = new List<VirusInventory.Mount>();
        public List<Genome.Gene> genes = new List<Genome.Gene>();
        public int selected = -1;
    }

    [Serializable]
    class SceneObject
    {
        public string at; // SaveRef "s:..."
        public Vector3 position, scale, velocity, spin;
        public Quaternion rotation = Quaternion.identity;
        public List<string> stateKeys, states;
    }

    public static string Folder => Path.Combine(Application.persistentDataPath, "Saves");
    public static string PathOf(int slot) => Path.Combine(Folder, $"slot{slot + 1}.json");
    public static bool Exists(int slot) => File.Exists(PathOf(slot));

    /// <summary>"2026-09-25  14:02" for a used slot, "EMPTY" for a free one.</summary>
    public static string Describe(int slot) =>
        Exists(slot) ? File.GetLastWriteTime(PathOf(slot)).ToString("yyyy-MM-dd  HH:mm") : "EMPTY";

    static VirusMovement Player() => UnityEngine.Object.FindAnyObjectByType<VirusMovement>();

    public static bool Save(int slot, out string message)
    {
        VirusMovement player = Player();
        if (!player) { message = "NO PLAYER"; return false; }
        Organism o = player.Organism;
        if (WhiteBloodCells.Captured(o)) { message = "CAN'T SAVE WHILE BEING DIGESTED"; return false; }

        var file = new SaveFile
        {
            scene = SceneManager.GetActiveScene().name,
            savedAt = DateTime.Now.ToString("o"),
            immune = ImmuneSystem.Capture(), // first: it settles the antibody order SaveRef counts in
            board = CommandBoard.Capture(),
            ropes = player.rope ? player.rope.Capture() : null,
            world = WorldStreamer.Instance ? WorldStreamer.Instance.Capture() : null,
            sceneObjects = CaptureScene(),
        };
        PlayerData p = file.player;
        p.position = o.transform.position;
        p.rotation = o.transform.rotation;
        if (o.Rb && !o.Rb.isKinematic) p.velocity = o.Rb.linearVelocity;
        WorldStates.Capture(o.gameObject, ref p.stateKeys, ref p.states);
        VirusInventory inv = player.Inventory;
        p.slots.AddRange(inv.Slots);
        p.ring.AddRange(inv.ring);
        Genome genome = Genome.Of(player);
        p.genes.AddRange(genome.genes);
        p.selected = genome.Selected;

        try
        {
            Directory.CreateDirectory(Folder);
            string path = PathOf(slot), temp = path + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(file));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path); // a crash mid-write leaves the old save whole
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            message = "SAVE FAILED: " + e.Message.ToUpperInvariant();
            return false;
        }
        message = $"SAVED TO SLOT {slot + 1}";
        return true;
    }

    public static bool Load(int slot, out string message)
    {
        if (!Exists(slot)) { message = $"SLOT {slot + 1} IS EMPTY"; return false; }
        SaveFile file;
        try { file = JsonUtility.FromJson<SaveFile>(File.ReadAllText(PathOf(slot))); }
        catch (Exception e)
        {
            Debug.LogException(e);
            message = "LOAD FAILED: " + e.Message.ToUpperInvariant();
            return false;
        }
        if (file == null || file.player == null) { message = "SAVE IS DAMAGED"; return false; }
        if (file.scene != SceneManager.GetActiveScene().name) { message = $"SAVE IS FROM {file.scene.ToUpperInvariant()}"; return false; }
        VirusMovement player = Player();
        if (!player) { message = "NO PLAYER"; return false; }

        Organism o = player.Organism;
        PlayerData p = file.player;

        // Let go of the old world first: anything holding the player, its ropes, the surface it stands on.
        WhiteBloodCells.Free(o, p.position);
        if (o.grounded.surface.Attached) o.grounded.surface.Detach(Vector3.zero);
        if (player.rope) player.rope.ClearAllRopes();

        o.transform.SetPositionAndRotation(p.position, p.rotation);
        if (o.Rb)
        {
            o.Rb.position = p.position;
            o.Rb.rotation = p.rotation;
        }
        o.Halt();
        if (o.Rb && !o.Rb.isKinematic) o.Rb.linearVelocity = p.velocity;
        Physics.SyncTransforms();

        // The world round the player, then everything that points into it.
        if (WorldStreamer.Instance && file.world != null) WorldStreamer.Instance.Restore(file.world);
        if (file.sceneObjects != null) RestoreScene(file.sceneObjects);
        Physics.SyncTransforms();

        WorldStates.Apply(o.gameObject, p.stateKeys, p.states); // lands it back on what it stood on
        player.Inventory.Restore(p.slots, p.ring);
        Genome genome = Genome.Of(player);
        genome.Select(-1);
        genome.genes = new List<Genome.Gene>(p.genes);
        genome.Select(p.selected);

        ImmuneSystem.Restore(file.immune);
        if (player.rope && file.ropes != null) player.rope.Restore(file.ropes);
        CommandBoard.Restore(file.board); // an older save has none: the old plan pointed into the old world

        foreach (UniversalCamera cam in UnityEngine.Object.FindObjectsByType<UniversalCamera>(FindObjectsSortMode.None)) cam.Teleport();
        message = $"LOADED SLOT {slot + 1}";
        return true;
    }

    // ---------------- hand-placed scene objects ----------------

    static readonly List<GameObject> s_found = new List<GameObject>();
    static readonly HashSet<GameObject> s_seen = new HashSet<GameObject>();

    // What a save keeps of the scene's own objects: anything with a Rigidbody, a Surface or an IWorldState that isn't
    // streamed, the player, or an antibody (those are saved on their own). One per Rigidbody / Surface owner.
    static List<GameObject> SceneOwners()
    {
        s_found.Clear();
        s_seen.Clear();
        Scene scene = SceneManager.GetActiveScene();
        void Consider(Component c)
        {
            GameObject go = c.gameObject;
            if (go.scene != scene || s_seen.Contains(go)) return;
            if (go.GetComponentInParent<WorldEntity>(true) || go.GetComponentInParent<VirusMovement>(true) ||
                go.GetComponentInParent<Antibody>(true)) return;
            s_seen.Add(go);
            s_found.Add(go);
        }
        foreach (Rigidbody rb in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None)) Consider(rb);
        foreach (Surface s in UnityEngine.Object.FindObjectsByType<Surface>(FindObjectsSortMode.None))
            Consider(s.GetComponentInParent<Rigidbody>() is Rigidbody rb && rb ? rb : s);
        foreach (MonoBehaviour m in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            if (m is IWorldState && !m.GetComponentInParent<Rigidbody>() && !m.GetComponent<Surface>()) Consider(m);
        s_seen.Clear();
        return s_found;
    }

    static List<SceneObject> CaptureScene()
    {
        var list = new List<SceneObject>();
        foreach (GameObject go in SceneOwners())
        {
            if (CellBurst.Bursting(go.transform)) continue; // gone in a moment
            Transform t = go.transform;
            var s = new SceneObject { at = SaveRef.Of(t), position = t.position, rotation = t.rotation, scale = t.localScale };
            if (go.TryGetComponent(out Rigidbody rb) && !rb.isKinematic) { s.velocity = rb.linearVelocity; s.spin = rb.angularVelocity; }
            WorldStates.Capture(go, ref s.stateKeys, ref s.states);
            list.Add(s);
        }
        s_found.Clear();
        return list;
    }

    // Each saved object back where it was; a cell or creature the save doesn't have was destroyed by then, so it goes.
    static void RestoreScene(List<SceneObject> saved)
    {
        var kept = new HashSet<GameObject>();
        foreach (SceneObject s in saved)
        {
            Transform t = SaveRef.Resolve(s.at);
            if (!t) continue;
            kept.Add(t.gameObject);
            t.SetPositionAndRotation(s.position, s.rotation);
            if (s.scale != Vector3.zero) t.localScale = s.scale;
            if (t.TryGetComponent(out Rigidbody rb))
            {
                rb.position = s.position;
                rb.rotation = s.rotation;
                if (!rb.isKinematic) { rb.linearVelocity = s.velocity; rb.angularVelocity = s.spin; }
            }
            WorldStates.Apply(t.gameObject, s.stateKeys, s.states);
        }
        foreach (GameObject go in SceneOwners())
            if (!kept.Contains(go) && (go.GetComponent<Surface>() || go.GetComponent<Organism>())) UnityEngine.Object.Destroy(go);
        s_found.Clear();
    }
}
