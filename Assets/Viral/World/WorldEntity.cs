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
    /// <summary>Called right after it's spawned from a record, with what <see cref="SaveState"/> gave (or what
    /// the generator wrote for a new one). Empty: nothing to restore.</summary>
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

    /// <summary>Rendering layer bit on a streamed object's renderers: shaders that draw one renderer per object
    /// (the cells) dissolve only those at the streaming edge (StreamFade.hlsl), never hand-placed scenery.</summary>
    public const uint StreamedLayer = 1u << 30;
    static readonly List<Renderer> s_renderers = new List<Renderer>();

    internal int index = -1;          // in WorldStreamer's live list
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
        _body = GetComponent<Rigidbody>();
        _states = GetComponents<IWorldState>();
        Drifts = _body && !GetComponent<Organism>();
        // Drifting cells move by physics steps; off the calm middle they move relative to the camera, and
        // uninterpolated they stepped at the physics rate against the frame rate (judder). Nothing writes their
        // transform directly, so interpolation is safe; sleeping bodies cost nothing.
        if (Drifts && !_body.isKinematic) _body.interpolation = RigidbodyInterpolation.Interpolate;
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
            key = key, seed = seed,
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
        if (_states != null && _states.Length > 0)
        {
            r.states = new List<string>(_states.Length);
            foreach (IWorldState s in _states) r.states.Add(s != null ? s.SaveState() ?? "" : "");
        }
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
        if (r.states == null) return;
        for (int i = 0; i < _states.Length && i < r.states.Count; i++)
            if (_states[i] != null && !string.IsNullOrEmpty(r.states[i])) _states[i].LoadState(r.states[i]);
    }
}
