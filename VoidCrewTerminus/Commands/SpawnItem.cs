using System;
using System.Collections.Generic;
using System.Linq;
using CG;
using CG.Game.Player;
using CG.Game.Scenarios;
using CG.Objects;
using Photon.Pun;
using UnityEngine;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

internal class SpawnItemCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "spawn" };

    public override string Description() => "Spawn a carryable item at your position for testing";

    public override List<Argument> Arguments() =>
    [
        new("list"),
        new("%item_name")
    ];

    public override string[] UsageExamples() =>
    [
        "!spawn list",
        "!spawn list oxygen",
        "!spawn Power Fuse",
        "!spawn oxygen"
    ];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(arguments))
        {
            Messaging.Notification("Usage: !spawn <item name>  |  !spawn list [filter]");
            return;
        }

        EnsureListPopulated();

        var carryables = DebugSpawnObjects.SpawnablesList
            .Where(s => s._objectType == typeof(CarryableObject))
            .ToList();

        if (arguments.StartsWith("list", StringComparison.OrdinalIgnoreCase))
        {
            var filter = arguments.Length > 4 ? arguments[4..].Trim() : string.Empty;
            var matches = string.IsNullOrEmpty(filter)
                ? carryables
                : carryables.Where(s => s.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (matches.Count == 0)
            {
                Messaging.Notification($"No items match '{filter}'");
                return;
            }

            const int chunkSize = 10;
            for (int i = 0; i < matches.Count; i += chunkSize)
            {
                var chunk = matches.Skip(i).Take(chunkSize).Select(s => s.name);
                Messaging.Notification(string.Join(", ", chunk));
            }
            return;
        }

        var player = LocalPlayer.Instance;
        if (player == null)
        {
            Messaging.Notification("Cannot spawn: not in an active game session");
            return;
        }

        var item = carryables.FirstOrDefault(s =>
                s.name.Equals(arguments, StringComparison.OrdinalIgnoreCase))
            ?? carryables.FirstOrDefault(s =>
                s.name.IndexOf(arguments, StringComparison.OrdinalIgnoreCase) >= 0);

        if (item == null)
        {
            Messaging.Notification($"No carryable found matching '{arguments}'. Use !spawn list to browse.");
            return;
        }

        var spawnPos = player.transform.position + player.transform.forward * 2f;

        if (PhotonNetwork.IsMasterClient)
        {
            var spawned = SpawnUtils.SpawnCarryable(item._guidUnion, spawnPos, Quaternion.identity);
            Messaging.Notification(spawned != null
             ? $"Spawned: {item.name}"
             : $"Failed to spawn: {item.name}");
        }

    }

    private static void EnsureListPopulated()
    {
        if (DebugSpawnObjects.SpawnablesList.Count == 0)
            DebugSpawnObjects.PopulateList();
    }
}
