using System.Collections.Generic;
using UnityEngine;
using PurrNet;
using PurrNet.Prediction;

/// <summary>
/// Registry of players. The player root is spawned by PurrDiction, not by PurrNet's hierarchy,
/// so ownership is mirrored from <see cref="PlayerMovement"/> instead of PurrNet's spawn callbacks.
/// </summary>
[RequireComponent(typeof(PlayerMovement))]
public class PlayerManager : MonoBehaviour
{
    static readonly Dictionary<PlayerID, PlayerManager> players = new();

    public static IReadOnlyDictionary<PlayerID, PlayerManager> allPlayers => players;

    public static PlayerManager local { get; private set; }

    public PlayerMovement playerMovement;

    public PlayerID? owner => playerMovement ? playerMovement.owner : null;

    public bool isOwner => playerMovement && playerMovement.isOwner;

    PlayerID? registeredOwner;
    bool registeredAsLocal;

    public static bool TryGetLocal(out PlayerManager player)
    {
        player = local;
        return player;
    }

    public static bool TryGetPlayer(PlayerID id, out PlayerManager player)
    {
        return players.TryGetValue(id, out player) && player;
    }

    void Reset()
    {
        playerMovement = GetComponent<PlayerMovement>();
    }

    void Awake()
    {
        if (!playerMovement)
            playerMovement = GetComponent<PlayerMovement>();
    }

    // PurrDiction assigns owner after Awake, and clears it again when the object returns to the pool.
    void Update()
    {
        PlayerID? currentOwner = owner;
        bool currentlyLocal = isOwner;

        if (currentOwner == registeredOwner && currentlyLocal == registeredAsLocal)
            return;

        Unregister();

        registeredOwner = currentOwner;
        registeredAsLocal = currentlyLocal;

        if (currentOwner.HasValue)
            players[currentOwner.Value] = this;

        if (currentlyLocal)
            local = this;
    }

    void OnDestroy()
    {
        Unregister();
        registeredOwner = null;
        registeredAsLocal = false;
    }

    void Unregister()
    {
        if (registeredOwner.HasValue &&
            players.TryGetValue(registeredOwner.Value, out var existing) &&
            existing == this)
        {
            players.Remove(registeredOwner.Value);
        }

        if (local == this)
            local = null;
    }
}
