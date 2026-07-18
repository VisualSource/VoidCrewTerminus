using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CG.Game;
using Gameplay.Loot;
using Gameplay.NPC.AI;
using ResourceAssets;
using ToolClasses;
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
        int bosses = Escalation.SectorEscalation.BossesDefeated;
        int rareUnlock = TerminusConfig.EscalationRareUnlockScalar?.Value ?? 3;
        int legendaryUnlock = TerminusConfig.EscalationLegendaryUnlockScalar?.Value ?? 6;
        int threshold = TerminusConfig.EscalationBossActivationThreshold?.Value ?? 2;
        var maxTier = Escalation.SectorEscalation.MaxAllowedTier(scalar, bosses, rareUnlock, legendaryUnlock);
        string status = Escalation.SectorEscalation.IsScalingActive
            ? "ACTIVE"
            : $"DORMANT (needs {threshold - bosses} more boss defeat{(threshold - bosses == 1 ? "" : "s")})";

        Messaging.Notification(
            $"Escalation {status} — DifficultyScalar={scalar}, BossesDefeated={bosses}/{threshold}, " +
            $"max relic tier: {maxTier} (rare@scalar{rareUnlock}, legendary@scalar{legendaryUnlock}; boss1→Rare, boss2→Legendary)");
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
        Net.ForgeNetSync.BroadcastState(); // host dev-set → propagate (no-ops off-authority)
        Messaging.Notification($"DifficultyScalar set to {value}. Next sector's loot table will reshape on entry.");
    }
}

internal class SetBossesCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "setbosses" };
    public override string Description() => "[DevMode] Force BossesDefeated to a value (0=no unlock, 1=Rare unlock, 2+=Legendary unlock)";
    public override List<Argument> Arguments() => [new("%value")];
    public override string[] UsageExamples() => ["!setbosses 0", "!setbosses 2"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;
        if (!int.TryParse((arguments ?? "").Trim(), out int value) || value < 0)
        {
            Messaging.Notification("Usage: !setbosses <n>  (n >= 0)");
            return;
        }
        Escalation.SectorEscalation.SetBossesDefeated(value);
        Net.ForgeNetSync.BroadcastState(); // host dev-set → propagate (no-ops off-authority)
        Messaging.Notification($"BossesDefeated set to {value}. Next sector's loot table will reshape on entry.");
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

        // Show what the last reshape actually did (before→after tier counts + ceiling).
        // This is the direct "is loot gating working" signal: at 0 bosses you'll see
        // Legendaries/Rares collapse to Common; at 2 bosses the ceiling is Legendary
        // so it reports no downgrades — which is correct, not a bug.
        Messaging.Notification($"Reshape: {Patches.LootTableEscalationPatch.LastReshapeSummary}");

        int recognizedTotal = 0;
        var unrecognizedSamples = new List<string>();

        foreach (var kv in lists)
        {
            if (kv.Value.Count == 0) continue;

            int total = kv.Value.Count;
            var tierCounts = new Dictionary<Loot.RelicTier, int>();
            int nonRelic = 0;
            foreach (var itemRef in kv.Value)
            {
                var name = itemRef?.Filename;
                if (!string.IsNullOrEmpty(name) && Loot.RelicTierData.TryGet(name, out var entry))
                {
                    tierCounts.TryGetValue(entry.Tier, out int c);
                    tierCounts[entry.Tier] = c + 1;
                    recognizedTotal++;
                }
                else
                {
                    nonRelic++;
                    if (!string.IsNullOrEmpty(name) && !unrecognizedSamples.Contains(name) && unrecognizedSamples.Count < 12)
                        unrecognizedSamples.Add(name);
                }
            }

            var parts = new List<string> { $"total={total}" };
            foreach (Loot.RelicTier t in System.Enum.GetValues(typeof(Loot.RelicTier)))
                if (tierCounts.TryGetValue(t, out int c)) parts.Add($"{t}={c}");
            if (nonRelic > 0) parts.Add($"non-relic={nonRelic}");

            Messaging.Notification($"[{kv.Key}] {string.Join(", ", parts)}");
        }

        // Name-match diagnostic: if RelicTierData recognizes ZERO entries, the key
        // format almost certainly doesn't match CraftableItemRef.Filename and loot
        // gating is a silent no-op. Print the raw filenames so the mismatch (and the
        // real names to fix the map with) is visible in one command.
        if (recognizedTotal == 0)
            Messaging.Notification("WARNING: 0 relics recognized by RelicTierData — loot gating is likely a NO-OP (name mismatch). Raw sample names below:");
        if (unrecognizedSamples.Count > 0)
            Messaging.Notification($"raw filenames: {string.Join(", ", unrecognizedSamples)}");
    }
}

// Dump every live AIDirector spawner's intensity so density escalation can be
// verified directly (Target/Max are what SpawnerInitIntensityScalingPatch and the
// AIDirector prefixes inflate; Current is how much has actually spawned). If the
// escalation is working you'll see Max/Target above the vanilla profile values;
// compare the same encounter at !setdifficulty 0 vs a high value.
internal class SpawnersDumpCommand : PublicCommand
{
    private static readonly FieldInfo SpawnersField =
        typeof(AIDirector).GetField("spawners", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo DefaultSpawnerField =
        typeof(AIDirector).GetField("defaultSpawner", BindingFlags.Instance | BindingFlags.NonPublic);

    public override string[] CommandAliases() => new[] { "spawners" };
    public override string Description() => "[DevMode] Dump live AIDirector spawner intensities (Target/Current/Max) to verify density scaling";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!spawners"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var director = Singleton<AIDirector>.I;
        if (director == null) { Messaging.Notification("No AIDirector in the active session."); return; }
        if (SpawnersField == null) { Messaging.Notification("Spawners: reflection lookup failed."); return; }

        int scalar = ForgeMeterController.DifficultyScalar;
        int capped = Escalation.EnemyScalingHelpers.CapScalar(scalar, TerminusConfig.EscalationScalarCap?.Value ?? 10);
        float rate = TerminusConfig.EscalationDensityScalarPerJump?.Value ?? 0.12f;
        Messaging.Notification(
            $"Spawners — escalation {(Escalation.SectorEscalation.IsScalingActive ? "ACTIVE" : "DORMANT")}, " +
            $"scalar={scalar} (capped {capped}), density x{1f + capped * rate:0.00}");

        var all = new List<Spawner>();
        if (DefaultSpawnerField?.GetValue(director) is Spawner def && def != null) all.Add(def);
        if (SpawnersField.GetValue(director) is List<Spawner> list)
            all.AddRange(list.Where(s => s != null && !all.Contains(s)));

        if (all.Count == 0) { Messaging.Notification("No active spawners right now (none created for this sector yet)."); return; }

        foreach (var s in all)
            Messaging.Notification(
                $"[{s.Identifier?.Asset?.name ?? "Default"}] Target={s.TargetIntensity}, Current={s.CurrentIntensity:0.#}, Max={s.MaxIntensity:0.#}");
    }
}
