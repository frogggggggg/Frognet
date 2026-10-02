using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A component with state of its own worth keeping when its object is streamed out or saved (a chunk's
/// remaining yield...). The transform, scale, velocity and mass are kept for everything already.
/// </summary>
public interface IWorldState
{
    /// <summary>Its state as a string (kept as is, handed back to <see cref="LoadState"/>).</summary>
    string SaveState();
    /// <summary>Called right after it's spawned from a record (or a save is loaded), with what <see cref="SaveState"/>
    /// gave. Never called with an empty string (empty = nothing worth keeping, not stored).</summary>
    void LoadState(string state);
    /// <summary>Busy (a white cell swallowing something...): not streamed out while set.</summary>
    bool Pinned { get; }
}

/// <summary>
/// Marks an object the WorldStreamer owns: which catalog prefab it is and its seed (so it comes back looking the
/// same). Added by the streamer on spawn; while enabled it's in the streamer's live list. Destroyed by the game
/// (a chunk drained, a virus eaten), it's simply gone for good: its sector was generated once and never again.
/// </summary>
[DisallowMultipleComponent]
public sealed class WorldEntity : MonoBehaviour
{
    public string key;
    public int seed;
    /// <summary>Its id for as long as it exists, through streaming and saves (WorldStreamer hands them out; SaveRef
    /// "e:uid" names it). 0 until spawned.</summary>
    public long uid;

    /// <summary>Rendering layer bit on a streamed object's renderers: shaders that draw one renderer per object
    /// (the cells) dissolve only those at the streaming edge (StreamFade.hlsl), never hand-placed scenery.</summary>
    public const uint StreamedLayer = 1u << 30;
    static readonly List<Renderer> s_renderers = new List<Renderer>();

    internal int index = -1;          // in WorldStreamer's live list
    // WorldStreamer's Template.farLook (FarField's stand-in), set on spawn. Serialized (hidden) so a play-mode script
    // reload keeps it (the live list is rebuilt from OnEnable then).
    [SerializeField, HideInInspector] internal int farLook = -1;
    IWorldState[] _states;
    Rigidbody _body;
    Transform _t;

    public Transform T => _t ? _t : _t = transform;
    public Rigidbody Body => _body;
    /// <summary>A loose body the Vessel drags along with the blood (a Rigidbody that isn't an Organism: those
    /// fly relative to the blood themselves).</summary>
    public bool Drifts { get; private set; }

    void OnEnable() => WorldStreamer.Track(this);
    void OnDisable() => WorldStreamer.Untrack(this);

    internal void Bind()
    {
        _t = transform;
        TryGetComponent(out _body); // Try*: a failed GetComponent allocates an error object in the editor
        _states = GetComponents<IWorldState>();
        Drifts = _body && !TryGetComponent(out Organism _);
        // Drifting cells move by physics steps; off the calm middle they move relative to the camera, and
        // uninterpolated they stepped at the physics rate against the frame rate (judder). Nothing writes their
        // transform directly, so interpolation is safe. Only near the camera, though: the Vessel's drag loop turns it
        // on and off by distance (Vessel.interpolateWithin).
        if (Drifts && !_body.isKinematic)
            _body.interpolation = Vessel.Interpolates(T.position) ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
        GetComponentsInChildren(true, s_renderers);
        foreach (Renderer r in s_renderers) r.renderingLayerMask |= StreamedLayer;
        s_renderers.Clear();
    }

    /// <summary>Held by something (reparented off the world root: swallowed, carried) or busy: stays loaded.</summary>
    public bool Pinned
    {
        get
        {
            if (T.parent != WorldStreamer.Root) return true;
            if (_states != null)
                foreach (IWorldState s in _states)
                    if (s != null && s.Pinned) return true;
            return false;
        }
    }

    public WorldStreamer.EntityRecord Capture()
    {
        var r = new WorldStreamer.EntityRecord
        {
            key = key, seed = seed, uid = uid,
            position = T.position, rotation = T.rotation, scale = T.localScale,
        };
        if (_body)
        {
            r.mass = _body.mass;
            // Relative to the blood: the world frame turns with the blood round the player, so a world-space velocity
            // kept while stored belongs to whatever frame was in force then (stashed while the player was at the
            // centre, a body by the wall came back ~30 m/s off once they'd moved out there, and slid sideways).
            if (!_body.isKinematic) { r.velocity = _body.linearVelocity - Vessel.FlowAt(T.position); r.spin = _body.angularVelocity; }
        }
        WorldStates.Capture(gameObject, ref r.stateKeys, ref r.states);
        return r;
    }

    internal void Apply(WorldStreamer.EntityRecord r)
    {
        if (_body)
        {
            if (r.mass > 0f) _body.mass = r.mass;
            // Stored relative to the blood (Capture); new records (0) start drifting with it.
            if (!_body.isKinematic) { _body.linearVelocity = r.velocity + Vessel.FlowAt(r.position); _body.angularVelocity = r.spin; }
        }
        if (WorldStates.Apply(gameObject, r.stateKeys, r.states)) _states = GetComponents<IWorldState>(); // one may have been added
    }

    /// <summary>On its way out (bursting, being swallowed): not worth saving, it's gone in a moment.</summary>
    public bool Dying => CellBurst.Bursting(T) || (TryGetComponent(out Organism o) && WhiteBloodCells.Captured(o));
}

/// <summary>
/// Every <see cref="IWorldState"/> on an object as (type name, state) pairs: what streaming and saves keep. Keyed by
/// type, so a component added on demand (CellSignal, once anything alarms the cell) is kept and comes back (added
/// again on apply). Empty states aren't stored. Cost: a GetComponents and the states' strings per capture.
/// </summary>
public static class WorldStates
{
    static readonly List<IWorldState> s_states = new List<IWorldState>();
    static readonly Dictionary<Type, string> s_names = new Dictionary<Type, string>();
    static readonly Dictionary<string, Type> s_types = new Dictionary<string, Type>();

    /// <summary>The object's states into keys / states (left null when there are none).</summary>
    public static void Capture(GameObject go, ref List<string> keys, ref List<string> states)
    {
        keys = states = null;
        go.GetComponents(s_states);
        foreach (IWorldState s in s_states)
        {
            if (s == null || (s is Behaviour b && !b)) continue;
            string state = s.SaveState();
            if (string.IsNullOrEmpty(state)) continue;
            Type t = s.GetType();
            if (!s_names.TryGetValue(t, out string name)) s_names[t] = name = t.Name;
            (keys ??= new List<string>(2)).Add(name);
            (states ??= new List<string>(2)).Add(state);
        }
        s_states.Clear();
    }

    /// <summary>Hands each state to its component (adding one that's missing). No keys: a record from before they
    /// were keyed, handed out by position. True if a component was added.</summary>
    public static bool Apply(GameObject go, List<string> keys, List<string> states)
    {
        if (states == null || states.Count == 0) return false;
        go.GetComponents(s_states);
        bool added = false;
        if (keys == null)
        {
            // Only the kinds that had states then count for the positions (Surface, CellSignal, Organism came later).
            s_states.RemoveAll(s => s == null || s is Surface || s is CellSignal || s is Organism);
            for (int i = 0; i < s_states.Count && i < states.Count; i++)
                if (!string.IsNullOrEmpty(states[i])) Load(s_states[i], states[i], go);
            s_states.Clear();
            return false;
        }
        for (int i = 0; i < keys.Count && i < states.Count; i++)
        {
            if (string.IsNullOrEmpty(states[i])) continue;
            IWorldState into = null;
            foreach (IWorldState s in s_states)
                if (s != null && s.GetType().Name == keys[i]) { into = s; break; }
            if (into == null)
            {
                Type t = TypeOf(keys[i]);
                if (t == null) continue;
                into = go.AddComponent(t) as IWorldState;
                added = true;
            }
            if (into != null) Load(into, states[i], go);
        }
        s_states.Clear();
        return added;
    }

    // One bad state (an edited save, a format that changed) mustn't stop the object, or the rest, coming back.
    static void Load(IWorldState into, string state, GameObject go)
    {
        try { into.LoadState(state); }
        catch (Exception e) { Debug.LogException(e, go); }
    }

    static Type TypeOf(string name)
    {
        if (s_types.TryGetValue(name, out Type t)) return t;
        t = typeof(WorldStates).Assembly.GetType(name);
        if (t != null && (!typeof(MonoBehaviour).IsAssignableFrom(t) || !typeof(IWorldState).IsAssignableFrom(t) || t.IsAbstract)) t = null;
        s_types[name] = t;
        return t;
    }
}
