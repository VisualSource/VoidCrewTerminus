using System.Collections.Generic;
using System.Linq;
using VoidCrewTerminus.Forge;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

// Phase 4 dev commands — inspect perk state and statistically verify roll math.
// All gated on TerminusConfig.EnableDevMode.

internal class PerksCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "perks" };
    public override string Description() => "[DevMode] Show perk slots of the nearest module (and the Forge's pending box, if any)";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!perks"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var module = ModuleFinder.NearestToPlayer();
        if (module != null && ForgeStateStore.TryGet(module, out var state))
            Messaging.Notification($"{module.name} L{state.Level} — {DescribeSlots(state.PerkSlots)}");
        else if (module != null)
            Messaging.Notification($"{module.name}: no forge overlay (vanilla).");

        var forge = ForgeCommandHelper.FindNearestForge();
        var box = forge != null ? forge.ModuleBox : null;
        if (box != null && box.photonView != null &&
            ForgeStateStore.TryPeekSnapshot(box.photonView.ViewID, out var snap))
            Messaging.Notification($"[Forge socket] {box.name} L{snap.Level} — {DescribeSlots(snap.PerkSlots)}");
    }

    internal static string DescribeSlots(IReadOnlyList<string> slots)
    {
        var parts = new List<string>();
        for (int i = 0; i < slots.Count; i++)
        {
            if (string.IsNullOrEmpty(slots[i])) { parts.Add($"slot{i + 1}: —"); continue; }
            parts.Add(PerkPool.TryGet(slots[i], out var perk)
                ? $"slot{i + 1}: {perk.Name}"
                : $"slot{i + 1}: {slots[i]} (unknown)");
        }
        return string.Join("  |  ", parts);
    }
}

internal class ForcePerkCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forceperk" };
    public override string Description() => "[DevMode] Force a perk into a slot on the nearest module: !forceperk <slot 1-3> <perkId|list>";
    public override List<Argument> Arguments() => [new("%slot"), new("%perk_id")];
    public override string[] UsageExamples() => ["!forceperk list", "!forceperk 1 weapon_overclocked_coils"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var parts = (arguments ?? "").Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0].Equals("list", System.StringComparison.OrdinalIgnoreCase))
        {
            var lines = PerkPool.AllPerks().Select(p => $"{p.Id} ({p.Description})").ToList();
            for (int i = 0; i < lines.Count; i += 3)
                Messaging.Notification(string.Join(", ", lines.Skip(i).Take(3)));
            return;
        }

        if (parts.Length != 2 || !int.TryParse(parts[0], out int slot) || slot < 1 || slot > PerkPool.SlotCount)
        {
            Messaging.Notification("Usage: !forceperk <slot 1-3> <perkId>  |  !forceperk list");
            return;
        }
        if (!PerkPool.TryGet(parts[1], out var perk))
        {
            Messaging.Notification($"Unknown perk id '{parts[1]}' — try !forceperk list");
            return;
        }

        var module = ModuleFinder.NearestToPlayer();
        if (module == null) { Messaging.Notification("No module found nearby."); return; }

        ForgeStateStore.GetOrCreate(module).SetPerk(slot - 1, perk.Id);
        Net.ForgeNetSync.BroadcastModuleOverlayFor(module); // dev path bypasses commit sync
        Messaging.Notification($"{module.name}: slot {slot} ← {perk.Name} ({perk.Description})");
    }
}

internal class PerkOddsCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "perkodds" };
    public override string Description() => "[DevMode] Simulate perk rolls to verify chances: !perkodds <common|rare|legendary> [n]";
    public override List<Argument> Arguments() => [new("%tier"), new("%rolls?")];
    public override string[] UsageExamples() => ["!perkodds common", "!perkodds legendary 10000"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var parts = (arguments ?? "").Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !System.Enum.TryParse<Loot.RelicTier>(parts[0], true, out var tier))
        {
            Messaging.Notification("Usage: !perkodds <common|rare|legendary> [rolls]");
            return;
        }
        int n = parts.Length > 1 && int.TryParse(parts[1], out int parsed) ? System.Math.Clamp(parsed, 100, 1_000_000) : 10_000;

        float chance = PerkPool.RollChance(tier);
        int hits = 0;
        for (int i = 0; i < n; i++)
            if (UnityEngine.Random.value < chance) hits++;

        Messaging.Notification(
            $"{tier}: configured {chance:P0}, observed {(float)hits / n:P1} over {n} rolls " +
            $"(max slot: {PerkPool.MaxSlotForTier(tier) + 1}).");
    }
}
