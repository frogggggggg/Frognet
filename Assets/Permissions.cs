using System;
using System.Collections.Generic;
using UnityEngine;
using PurrNet;
using PurrNet.Transports;
using PurrNet.Steam;

[Serializable]
public struct PermissionLevel
{
    public string name;

    [Tooltip("Skips the list and allows every command.")]
    public bool all;

    public string[] commands;
}

[Serializable]
public struct PermissionGrant
{
    [Tooltip("Steam ID. The Steam transport inspector has a button to copy yours while in play mode.")]
    public string steamId;

    public string level;
}

/// <summary>
/// Two checks over one shared table. The local check exists so the console can refuse instantly and
/// print a reason; the server check is the only one that decides anything.
/// </summary>
public class Permissions : NetworkBehaviour
{
    public static Permissions Instance { get; private set; }

    [SerializeField] private PermissionLevel[] levels =
    {
        new PermissionLevel { name = "admin", all = true },
        new PermissionLevel { name = "player", commands = Array.Empty<string>() }
    };

    [SerializeField] private string serverLevel = "admin";
    [SerializeField] private string clientLevel = "player";

    [SerializeField, Tooltip("Levels that apply to specific Steam accounts, whatever session they join in.")]
    private PermissionGrant[] grants = Array.Empty<PermissionGrant>();

    readonly Dictionary<PlayerID, string> granted = new Dictionary<PlayerID, string>();
    readonly Dictionary<ulong, string> grantedBySteamId = new Dictionary<ulong, string>();
    string localLevel;

    void Awake()
    {
        Instance = this;
    }

    protected override void OnSpawned()
    {
        if(isServer)
            localLevel = serverLevel;
        else
            RequestLevel();
    }

    [ServerRpc(requireOwnership: false)]
    void RequestLevel(RPCInfo info = default)
    {
        string level = ResolveLevel(info.sender);
        granted[info.sender] = level;
        GrantLevel(info.sender, level);
    }

    [TargetRpc]
    void GrantLevel(PlayerID player, string level)
    {
        localLevel = level;
    }

    /// <summary>Server only. Sticks to the Steam account, so it survives a reconnect within the session.</summary>
    public void SetLevel(PlayerID player, string level)
    {
        if(!isServer)
            return;

        granted[player] = level;

        if(PurrSteamUtils.TryGetSteamID(player, out ulong steamId))
            grantedBySteamId[steamId] = level;

        GrantLevel(player, level);
    }

    string ResolveLevel(PlayerID player)
    {
        if(networkManager && networkManager.localPlayer.Equals(player))
            return serverLevel;

        // Read off the transport connection, so it is not something the client can claim.
        if(!PurrSteamUtils.TryGetSteamID(player, out ulong steamId))
            return clientLevel;

        if(grantedBySteamId.TryGetValue(steamId, out string runtimeLevel))
            return runtimeLevel;

        for(int i = 0; i < grants.Length; i++)
        {
            if(ulong.TryParse(grants[i].steamId, out ulong id) && id == steamId)
                return grants[i].level;
        }

        return clientLevel;
    }

    /// <summary>Advisory. Offline play is unrestricted; anything networked without a Permissions object is denied.</summary>
    public static bool LocalAllows(string command)
    {
        var manager = NetworkManager.main;

        if(!manager || (!manager.isClient && !manager.isServer))
            return true;

        return Instance && Instance.LevelAllows(Instance.localLevel, command);
    }

    /// <summary>Server side. The check that actually decides.</summary>
    public bool Allows(PlayerID player, string command)
    {
        return LevelAllows(granted.TryGetValue(player, out var level) ? level : ResolveLevel(player), command);
    }

    bool LevelAllows(string levelName, string command)
    {
        if(string.IsNullOrEmpty(levelName))
            return false;

        for(int i = 0; i < levels.Length; i++)
        {
            if(!string.Equals(levels[i].name, levelName, StringComparison.OrdinalIgnoreCase))
                continue;

            if(levels[i].all)
                return true;

            string[] allowed = levels[i].commands;

            if(allowed == null)
                return false;

            for(int c = 0; c < allowed.Length; c++)
            {
                if(string.Equals(allowed[c], command, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        return false;
    }
}
