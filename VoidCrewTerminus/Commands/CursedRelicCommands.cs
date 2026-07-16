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
        float maxChance = TerminusConfig.RelicMaxCurseChance?.Value ?? 0.50f;
        int scalar = Forge.ForgeMeterController.DifficultyScalar;

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
            var burden = CursedRelicMarker.GetBurden(co.gameObject);
            bool cursed = burden != Forge.BurdenType.None;
            float chance = CursedRelicRoll.ChanceFor(entry, scalar, baseChance, scalarBonus, maxChance);
            float uncapped = baseChance + entry.BaseCurseChanceModifier + scalar * scalarBonus;
            string affinity = entry.BurdenAffinity != null && entry.BurdenAffinity.Count > 0
                ? string.Join("/", entry.BurdenAffinity)
                : "none";
            Messaging.Notification(
                $"{name}: {entry.Tier}{(cursed ? $" CURSED({burden})" : "")} — chance would be {chance:P1}" +
                $"{(uncapped > maxChance ? $" (capped from {uncapped:P1})" : "")} " +
                $"(base {baseChance:P0}, relic {entry.BaseCurseChanceModifier:+0.00;-0.00}, scalar +{scalar * scalarBonus:P1}, ceiling {maxChance:P0}); affinity: {affinity}");
        }
    }
}

internal class ForceCursedCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forcecursed" };
    public override string Description() => "[DevMode] Force the nearest relic cursed with a specific burden type: !forcecursed <on|off> [burdenType]. Defaults to the relic's first affinity when omitted.";
    public override List<Argument> Arguments() => [new("%on_or_off"), new("%burden_type?")];
    public override string[] UsageExamples() => ["!forcecursed on", "!forcecursed on RandomShutoff", "!forcecursed off"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var parts = (arguments ?? "").Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
        {
            Messaging.Notification("Usage: !forcecursed <on|off> [burdenType]");
            return;
        }
        var arg = parts[0].ToLowerInvariant();
        bool? target = arg switch
        {
            "on" or "true" or "1" or "yes" => true,
            "off" or "false" or "0" or "no" => false,
            _ => (bool?)null,
        };
        if (target == null)
        {
            Messaging.Notification("Usage: !forcecursed <on|off> [burdenType]");
            return;
        }

        Forge.BurdenType? explicitBurden = null;
        if (target.Value && parts.Length >= 2)
        {
            if (!System.Enum.TryParse<Forge.BurdenType>(parts[1], ignoreCase: true, out var parsed) || parsed == Forge.BurdenType.None)
            {
                Messaging.Notification($"Unknown burden type '{parts[1]}'. Valid: RandomShutoff.");
                return;
            }
            explicitBurden = parsed;
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
            var name = RelicTierData.NormalizeName(nearest.gameObject.name);
            RelicTierData.TryGet(name, out var entry);
            Forge.BurdenType chosen = explicitBurden
                ?? (entry.BurdenAffinity != null && entry.BurdenAffinity.Count > 0
                    ? entry.BurdenAffinity[0]
                    : Forge.BurdenType.RandomShutoff);

            // Uncurse first so re-cursing with a different burden type works.
            CursedRelicMarker.Uncurse(nearest.gameObject);
            CursedRelicMarker.MarkCursed(nearest.gameObject, chosen);
            Messaging.Notification($"{nearest.gameObject.name} is now CURSED with {chosen}.");
        }
        else
        {
            CursedRelicMarker.Uncurse(nearest.gameObject);
            Messaging.Notification($"{nearest.gameObject.name} is now clean.");
        }
    }
}
