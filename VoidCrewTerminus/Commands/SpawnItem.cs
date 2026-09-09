using System;
using System.Collections.Generic;
using System.Linq;
using CG;
using CG.Game.Player;
using CG.Game.Scenarios;
using CG.Objects;
using CG.Space;
using Photon.Pun;
using ResourceAssets;
using UnityEngine;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

internal class SpawnItemCommand : PublicCommand
{
    internal sealed class SpawnableCarryable
    {
        public readonly string Name;
        public readonly GUIDUnion Guid;
        public readonly bool IsLocked;

        public SpawnableCarryable(string name, GUIDUnion guid, bool isLocked)
        {
            Name = name;
            Guid = guid;
            IsLocked = isLocked;
        }
    }

    public override string[] CommandAliases() => new[] { "spawn" };

    public override string Description() => "Spawn a carryable item at your position for testing";

    public override List<Argument> Arguments() =>
    [
        new("%item_name")
    ];

    public override string[] UsageExamples() =>
    [
        "!spawn Power Fuse",
        "!spawn oxygen"
    ];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(arguments))
        {
            Messaging.Notification("Usage: !spawn <item name>  |  browse the full list in the Terminus settings menu's Spawn tab.");
            return;
        }

        var player = LocalPlayer.Instance;
        if (player == null)
        {
            Messaging.Notification("Cannot spawn: not in an active game session");
            return;
        }

        var carryables = GetCarryables();
        var item = carryables.FirstOrDefault(s =>
                s.Name.Equals(arguments, StringComparison.OrdinalIgnoreCase))
            ?? carryables.FirstOrDefault(s =>
                s.Name.IndexOf(arguments, StringComparison.OrdinalIgnoreCase) >= 0);

        if (item == null)
        {
            Messaging.Notification($"No carryable found matching '{arguments}'. Browse the Spawn tab in the Terminus settings menu.");
            return;
        }

        var spawnPos = player.transform.position + player.transform.forward * 2f;

        if (PhotonNetwork.IsMasterClient)
        {
            TrySpawn(item, spawnPos, out var message);
            Messaging.Notification(message);
        }
    }

    // Repopulates DebugSpawnObjects' Carryable-only list on every call rather than
    // trusting its cached state: that list is shared with the vanilla debug menu's
    // own Object Type toolbar, so if a player ever switches it to WeaponBuildBox/
    // SpaceObject, our filter on _objectType silently returns nothing until it's
    // switched back. Clearing first avoids appending duplicates on repeat calls.
    internal static List<SpawnableCarryable> GetCarryables()
    {
        DebugSpawnObjects.SpawnablesList.Clear();
        DebugSpawnObjects.PopulateCarryablesList();

        return DebugSpawnObjects.SpawnablesList
            .Where(s => s._objectType == typeof(CarryableObject))
            .Select(s => new SpawnableCarryable(s.name, s._guidUnion, s.IsLocked))
            .ToList();
    }

    internal static bool TrySpawnAtPlayer(SpawnableCarryable item, out string message)
    {
        var player = LocalPlayer.Instance;
        if (player == null)
        {
            message = "Cannot spawn: not in an active game session";
            return false;
        }
        if (!PhotonNetwork.IsMasterClient)
        {
            message = "Only the host can spawn items";
            return false;
        }

        var spawnPos = player.transform.position + player.transform.forward * 2f;
        return TrySpawn(item, spawnPos, out message);
    }

    private static bool TrySpawn(SpawnableCarryable item, Vector3 position, out string message) =>
        TrySpawnGuarded(item.Guid, item.Name, position, out message, out _);

    // SpawnUtils.SpawnCarryable throws rather than returning null when the game
    // isn't in a state to take a spawn — it dereferences both the guid's
    // CloneStarObjectDef and GameSessionManager.ActiveSector unconditionally, and
    // the latter is null across a void jump. Seen live as a run of
    // NullReferenceExceptions from !spawn, and worse from the settings menu's Spawn
    // tab, where the throw escapes into the OnGUI draw loop.
    //
    // Reported as a failed spawn, which is what it is. Logged at Warning, not Debug:
    // a swallowed exception is not routine diagnostics, and this is the only place
    // it's recorded at all.
    //
    // Shared with !forgespawn (ForgeCommitCommand), which spawns by raw guid and hit
    // the identical crash from the same helper.
    internal static bool TrySpawnGuarded(
        GUIDUnion guid, string label, Vector3 position, out string message, out OrbitObject spawned)
    {
        spawned = null;
        try
        {
            spawned = SpawnUtils.SpawnCarryable(guid, position, Quaternion.identity);
            message = spawned != null ? $"Spawned: {label}" : $"Failed to spawn: {label}";
            return spawned != null;
        }
        catch (Exception e)
        {
            BepinPlugin.Log.LogWarning($"[Spawn] {label} ({guid.AsHex()}) failed: {e.GetType().Name}: {e.Message}");
            message = $"Failed to spawn: {label} — the game is not accepting spawns right now (mid-jump?).";
            return false;
        }
    }
}
