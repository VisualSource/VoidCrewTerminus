using System.Collections.Generic;
using System.Linq;
using CG.Game.Player;
using CG.Objects;
using UnityEngine;
using VoidCrewTerminus.Escalation;
using VoidCrewTerminus.Loot;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

// Phase 7-B dev commands — inspect / manipulate per-relic cursed state.
// Gated on EnableDevMode.

internal class CursedStatusCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "cursedstatus" };
    public override string Description() => "[DevMode] Show cursed state of relics near the player (name, tier, cursed?, computed chance)";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!cursedstatus"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var player = LocalPlayer.Instance;
        if (player == null) { Messaging.Notification("No local player."); return; }
        var origin = player.transform.position;

        float baseChance = TerminusConfig.RelicBaseCurseChance?.Value ?? 0.15f;
        float scalarBonus = TerminusConfig.EscalationCurseChancePerScalar?.Value ?? 0.03f;
        int scalar = Forge.ForgeMeterController.DifficultyScalar;
        bool active = SectorEscalation.IsScalingActive;

        var relics = Object.FindObjectsOfType<CarryableObject>()
            .Where(co => co != null && RelicTierData.TryGet(RelicTierData.NormalizeName(co.gameObject.name), out _))
            .OrderBy(co => (co.transform.position - origin).sqrMagnitude)
            .Take(5)
            .ToList();

        if (relics.Count == 0)
        {
            Messaging.Notification("No relics found nearby.");
            return;
        }

        foreach (var co in relics)
        {
            var name = RelicTierData.NormalizeName(co.gameObject.name);
            RelicTierData.TryGet(name, out var entry);
            bool cursed = CursedRelicMarker.IsCursed(co.gameObject);
            float chance = CursedRelicRoll.ChanceFor(entry, scalar, active, baseChance, scalarBonus);
            Messaging.Notification(
                $"{name}: {entry.Tier}{(cursed ? " CURSED" : "")} — chance would be {chance:P1} " +
                $"(base {baseChance:P0}, relic {entry.BaseCurseChanceModifier:+0.00;-0.00}, scalar +{scalar * scalarBonus:P1})");
        }
    }
}

internal class ForceCursedCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forcecursed" };
    public override string Description() => "[DevMode] Force the nearest relic cursed / non-cursed: !forcecursed <on|off>";
    public override List<Argument> Arguments() => [new("%on_or_off")];
    public override string[] UsageExamples() => ["!forcecursed on", "!forcecursed off"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var arg = (arguments ?? "").Trim().ToLowerInvariant();
        bool? target = arg switch
        {
            "on" or "true" or "1" or "yes" => true,
            "off" or "false" or "0" or "no" => false,
            _ => (bool?)null,
        };
        if (target == null)
        {
            Messaging.Notification("Usage: !forcecursed <on|off>");
            return;
        }

        var player = LocalPlayer.Instance;
        if (player == null) { Messaging.Notification("No local player."); return; }
        var origin = player.transform.position;

        var nearest = Object.FindObjectsOfType<CarryableObject>()
            .Where(co => co != null && RelicTierData.TryGet(RelicTierData.NormalizeName(co.gameObject.name), out _))
            .OrderBy(co => (co.transform.position - origin).sqrMagnitude)
            .FirstOrDefault();

        if (nearest == null)
        {
            Messaging.Notification("No relic found nearby.");
            return;
        }

        if (target.Value)
        {
            CursedRelicMarker.MarkCursed(nearest.gameObject);
            Messaging.Notification($"{nearest.gameObject.name} is now CURSED.");
        }
        else
        {
            CursedRelicMarker.Uncurse(nearest.gameObject);
            Messaging.Notification($"{nearest.gameObject.name} is now clean.");
        }
    }
}
