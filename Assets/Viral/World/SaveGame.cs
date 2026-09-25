using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Save slots for the session: the player (pose, velocity, stores, head ring, genes) and the whole streamed world
/// (WorldStreamer.Capture: every visited sector's contents as they are now, and the seed for the rest), as JSON in
/// persistentDataPath/Saves/slotN.json. Loading frees the player from anything holding it, clears ropes, moves it,
/// and has the streamer rebuild the world round it.
///
/// Not kept yet: ropes, antibodies, cell signals / converted cells, command-mode groups, hand-placed scene objects
/// (they stay as the scene has them). Add a component's state to the world by implementing IWorldState on it.
/// </summary>
public static class SaveGame
{
    public const int Slots = 3;
    const int Version = 1;

    [Serializable]
    class SaveFile
    {
        public int version = Version;
        public string scene;
        public string savedAt;
        public PlayerData player = new PlayerData();
        public WorldStreamer.WorldSave world;
    }

    [Serializable]
    class PlayerData
    {
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 velocity;
        public List<VirusInventory.Slot> slots = new List<VirusInventory.Slot>();
        public List<VirusInventory.Mount> ring = new List<VirusInventory.Mount>();
        public List<Genome.Gene> genes = new List<Genome.Gene>();
        public int selected = -1;
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
            world = WorldStreamer.Instance ? WorldStreamer.Instance.Capture() : null,
        };
        PlayerData p = file.player;
        p.position = o.transform.position;
        p.rotation = o.transform.rotation;
        if (o.Rb && !o.Rb.isKinematic) p.velocity = o.Rb.linearVelocity;
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

        if (WorldStreamer.Instance && file.world != null) WorldStreamer.Instance.Restore(file.world);

        player.Inventory.Restore(p.slots, p.ring);
        Genome genome = Genome.Of(player);
        genome.Select(-1);
        genome.genes = new List<Genome.Gene>(p.genes);
        genome.Select(p.selected);

        foreach (UniversalCamera cam in UnityEngine.Object.FindObjectsByType<UniversalCamera>(FindObjectsSortMode.None)) cam.Teleport();
        message = $"LOADED SLOT {slot + 1}";
        return true;
    }
}
