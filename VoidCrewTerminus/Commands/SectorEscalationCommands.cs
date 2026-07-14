using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CG.Game;
using Gameplay.Loot;
using ResourceAssets;
using VC.Common.CoreData;
using VoidCrewTerminus.Forge;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

// Phase 6 dev commands — inspect / manipulate DifficultyScalar and dump the
// current sector's reshaped loot pool. Gated on EnableDevMode.

internal class DifficultyCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "difficulty" };
    public override string Description() => "[DevMode] Show the current DifficultyScalar";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!difficulty"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        int scalar = ForgeMeterController.DifficultyScalar;
        int rareUnlock = TerminusConfig.EscalationRareUnlockScalar?.Value ?? 3;
        int legendaryUnlock = TerminusConfig.EscalationLegendaryUnlockScalar?.Value ?? 6;
        var maxTier = SectorEscalation.MaxAllowedTier(scalar, rareUnlock, legendaryUnlock);

        Messaging.Notification(
            $"DifficultyScalar={scalar} — max relic tier this sector: {maxTier} " +
            $"(rare@{rareUnlock}, legendary@{legendaryUnlock})");
    }
}

internal class SetDifficultyCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "setdifficulty" };
    public override string Description() => "[DevMode] Force DifficultyScalar to a value (takes effect NEXT sector's loot setup)";
    public override List<Argument> Arguments() => [new("%value")];
    public override string[] UsageExamples() => ["!setdifficulty 0", "!setdifficulty 6"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;
        if (!int.TryParse((arguments ?? "").Trim(), out int value) || value < 0)
        {
            Messaging.Notification("Usage: !setdifficulty <n>  (n >= 0)");
            return;
        }
        ForgeMeterController.SetDifficultyScalar(value);
        Messaging.Notification($"DifficultyScalar set to {value}. Next sector's loot table will reshape on entry.");
    }
}

internal class LootDumpCommand : PublicCommand
{
    private static readonly FieldInfo LootListsField =
        typeof(LootManager).GetField("CurrentSectorLootLists",
            BindingFlags.Instance | BindingFlags.NonPublic);

    public override string[] CommandAliases() => new[] { "lootdump" };
    public override string Description() => "[DevMode] Dump the current sector's reshaped loot pool (post-escalation)";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!lootdump"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var lm = LootManager.Instance;
        if (lm == null) { Messaging.Notification("No LootManager in the active session."); return; }
        if (LootListsField == null) { Messaging.Notification("LootDump: reflection lookup failed."); return; }

        var lists = LootListsField.GetValue(lm) as Dictionary<LootRarities, List<CraftableItemRef>>;
        if (lists == null || lists.Count == 0)
        {
            Messaging.Notification("Sector loot pool is empty (not yet set up?).");
            return;
        }

        foreach (var kv in lists)
        {
            if (kv.Value.Count == 0) continue;

            int total = kv.Value.Count;
            var tierCounts = new Dictionary<RelicTier, int>();
            int nonRelic = 0;
            foreach (var itemRef in kv.Value)
            {
                var name = itemRef?.Filename;
                if (!string.IsNullOrEmpty(name) && RelicTierData.TryGet(name, out var entry))
                {
                    tierCounts.TryGetValue(entry.Tier, out int c);
                    tierCounts[entry.Tier] = c + 1;
                }
                else nonRelic++;
            }

            var parts = new List<string> { $"total={total}" };
            foreach (RelicTier t in System.Enum.GetValues(typeof(RelicTier)))
                if (tierCounts.TryGetValue(t, out int c)) parts.Add($"{t}={c}");
            if (nonRelic > 0) parts.Add($"non-relic={nonRelic}");

            Messaging.Notification($"[{kv.Key}] {string.Join(", ", parts)}");
        }
    }
}
