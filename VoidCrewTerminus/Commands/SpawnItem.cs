using System;
using System.Collections.Generic;
using System.Linq;
using CG;
using CG.Game.Player;
using CG.Game.Scenarios;
using CG.Objects;
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
            var spawned = SpawnUtils.SpawnCarryable(item.Guid, spawnPos, Quaternion.identity);
            Messaging.Notification(spawned != null
             ? $"Spawned: {item.Name}"
             : $"Failed to spawn: {item.Name}");
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
        var spawned = SpawnUtils.SpawnCarryable(item.Guid, spawnPos, Quaternion.identity);
        message = spawned != null ? $"Spawned: {item.Name}" : $"Failed to spawn: {item.Name}";
        return spawned != null;
    }
}
