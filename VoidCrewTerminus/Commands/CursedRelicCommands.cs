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

internal class CursedStatusCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "cursedstatus" };
    public override string Description() => "[DevMode] Is the relic I'm holding cursed? Reports the held relic first, then nearby ones (tier, cursed?, baked burden, computed chance)";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!cursedstatus"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode) return;

        var player = LocalPlayer.Instance;
        if (player == null) { Messaging.Notification("No local player."); return; }
        var origin = player.transform.position;

        // Cursed state is mirrored host→client, so this reads correctly on a client
        // too (commit outcomes remain host-authoritative).
        var held = player.Carrier != null ? player.Payload : null;
        bool heldIsRelic = held != null
            && RelicTierData.TryGet(RelicTierData.NormalizeName(held.gameObject.name), out _);

        if (heldIsRelic)
            Messaging.Notification("[HELD] " + Describe(held));
        else
            Messaging.Notification(held == null
                ? "[HELD] nothing — pick a relic up to check it directly."
                : $"[HELD] {held.gameObject.name} — not a relic.");

        var nearby = Object.FindObjectsOfType<CarryableObject>()
            .Where(co => co != null && co != held
                      && RelicTierData.TryGet(RelicTierData.NormalizeName(co.gameObject.name), out _))
            .OrderBy(co => (co.transform.position - origin).sqrMagnitude)
            .Take(5)
            .ToList();

        if (nearby.Count == 0)
        {
            if (!heldIsRelic) Messaging.Notification("No other relics found nearby.");
            return;
        }

        foreach (var co in nearby)
        {
            float dist = Vector3.Distance(co.transform.position, origin);
            Messaging.Notification($"[{dist:0.#}m] " + Describe(co));
        }
    }

    private static string Describe(CarryableObject co)
    {
        float baseChance = TerminusConfig.BaseCurseChance;
        float scalarBonus = TerminusConfig.CurseChancePerScalar;
        float maxChance = TerminusConfig.MaxCurseChance;
        int scalar = Forge.ForgeMeterController.DifficultyScalar;

        var name = RelicTierData.NormalizeName(co.gameObject.name);
        RelicTierData.TryGet(name, out var entry);
        var burden = CursedRelicMarker.GetBurden(co.gameObject);
        bool cursed = burden != Forge.BurdenType.None;

        float chance = CursedRelicRoll.ChanceFor(entry, scalar, baseChance, scalarBonus, maxChance);
        float uncapped = baseChance + entry.BaseCurseChanceModifier + scalar * scalarBonus;
        string affinity = entry.BurdenAffinity != null && entry.BurdenAffinity.Count > 0
            ? string.Join("/", entry.BurdenAffinity)
            : "none";

        return $"{name}: {entry.Tier} — {(cursed ? $"CURSED ({burden})" : "clean")} " +
               $"| spawn chance was {chance:P1}{(uncapped > maxChance ? $" (capped from {uncapped:P1})" : "")} " +
               $"(base {baseChance:P0}, relic {entry.BaseCurseChanceModifier:+0.00;-0.00}, scalar +{scalar * scalarBonus:P1}, ceiling {maxChance:P0}); affinity: {affinity}";
    }
}

internal class ForceCursedCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forcecursed" };
    public override string Description() => "[DevMode] Force the HELD relic (or nearest if not holding one) cursed: !forcecursed <on|off> [burdenType]. Defaults to the relic's first affinity when omitted.";
    public override List<Argument> Arguments() => [new("%on_or_off"), new("%burden_type?")];
    public override string[] UsageExamples() => ["!forcecursed on", "!forcecursed on RandomShutoff", "!forcecursed off"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode) return;

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

        var held = player.Carrier != null ? player.Payload : null;
        bool heldIsRelic = held != null
            && RelicTierData.TryGet(RelicTierData.NormalizeName(held.gameObject.name), out _);

        var nearest = heldIsRelic
            ? held
            : Object.FindObjectsOfType<CarryableObject>()
                .Where(co => co != null && RelicTierData.TryGet(RelicTierData.NormalizeName(co.gameObject.name), out _))
                .OrderBy(co => (co.transform.position - origin).sqrMagnitude)
                .FirstOrDefault();

        if (nearest == null)
        {
            Messaging.Notification("No relic held or found nearby.");
            return;
        }

        // !forcecursed is a local dev override, NOT synced client→host — forcing it
        // on a client changes only that client's local marker; the host, which
        // decides commit outcomes, won't see it.
        if (!Photon.Pun.PhotonNetwork.IsMasterClient)
            Messaging.Notification("NOTE: !forcecursed is local-only on a client — the host won't see it, so it won't affect commit outcomes. Force it on the host.");

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
