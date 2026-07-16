using System.Collections.Generic;
using CG.Game;
using Gameplay.Loot;
using Gameplay.Quests;
using HarmonyLib;
using ResourceAssets;
using VC.Common.CoreData;
using VoidCrewTerminus.Forge;
using VoidCrewTerminus.Escalation;

namespace VoidCrewTerminus.Patches;

// Phase 6 — hooks LootManager's per-sector list build. Runs once per sector on
// every client (setup is deterministic across the network — the mutation is
// seeded from quest+sector so all clients converge on the same reshaped lists).
// Only the host actually consumes the lists to spawn drops.
[HarmonyPatch(typeof(LootManager), "SetupCurrentSectorLootLists")]
internal static class LootTableEscalationPatch
{
    private static readonly AccessTools.FieldRef<LootManager, Dictionary<LootRarities, List<CraftableItemRef>>> LootListsRef =
        AccessTools.FieldRefAccess<LootManager, Dictionary<LootRarities, List<CraftableItemRef>>>("CurrentSectorLootLists");

    // Human-readable summary of the most recent reshape (before→after tier counts
    // per rarity bucket, plus the ceiling). Surfaced by !lootdump so the effect of
    // the boss-count ceiling is visible even when it's a deliberate no-op (e.g. at
    // 2 bosses the ceiling is Legendary, so nothing downgrades — the summary says so).
    public static string LastReshapeSummary { get; private set; } = "(no sector reshaped yet)";

    static void Postfix(LootManager __instance)
    {
        try
        {
            var lists = LootListsRef(__instance);
            if (lists == null) return;

            // Endless-only: the relic ceiling is driven by boss count, and bosses
            // are only defeatable in EndlessQuest (see BossDefeatHook). In other
            // quest types BossesDefeated is permanently 0, which would crush every
            // sector's loot to Common forever — so we don't touch non-Endless runs.
            if (!(GameSessionManager.ActiveSession?.ActiveQuest is EndlessQuest))
            {
                LastReshapeSummary = "(non-Endless quest — loot biasing skipped)";
                return;
            }

            // NOTE: loot tier biasing is deliberately NOT behind the escalation
            // warm-up gate (unlike density/HP/damage). The relic ceiling is driven
            // by boss count from the very first sector: 0 bosses → Common only,
            // 1 → Rare unlocked, 2 → Legendary. Gating this on IsScalingActive
            // (>= 2 bosses) would suppress it exactly until the boss ceiling is
            // already Legendary, making the downgrade a permanent no-op. During
            // warm-up DifficultyScalar stays 0, so the boss ceiling alone drives
            // the tier — which is the intended behaviour.
            int scalar = ForgeMeterController.DifficultyScalar;
            int bosses = SectorEscalation.BossesDefeated;
            int seed = ResolveSeed();

            int rareUnlock = TerminusConfig.EscalationRareUnlockScalar?.Value ?? 3;
            int legendaryUnlock = TerminusConfig.EscalationLegendaryUnlockScalar?.Value ?? 6;
            var ceiling = SectorEscalation.MaxAllowedTier(scalar, bosses, rareUnlock, legendaryUnlock);

            var bucketSummaries = new List<string>();
            int totalDowngraded = 0, totalDropped = 0;

            foreach (var kv in lists)
            {
                var before = Histogram(kv.Value);

                // Seed per-rarity so a change in one list's contents doesn't
                // shift picks in the others — keeps tuning intuitive.
                SectorEscalation.DowngradeRelics(
                    kv.Value,
                    r => r?.Filename,
                    scalar,
                    bosses,
                    unchecked(seed * 397 ^ (int)kv.Key));

                var after = Histogram(kv.Value);
                if (before.Relics == 0) continue; // no relics in this bucket — skip

                int dropped = before.Relics - after.Relics;      // removed (no candidate)
                int overCeiling = before.AboveCeiling(ceiling);  // how many started over the ceiling
                totalDowngraded += overCeiling - dropped;        // swapped-in-place, not dropped
                totalDropped += dropped;

                bucketSummaries.Add(
                    $"[{kv.Key}] {before} → {after}" + (overCeiling > 0 ? $" ({overCeiling} over ceiling)" : ""));
            }

            LastReshapeSummary =
                bucketSummaries.Count == 0
                    ? $"ceiling={ceiling} (scalar {scalar}, bosses {bosses}) — no relics in any bucket"
                    : $"ceiling={ceiling} (scalar {scalar}, bosses {bosses}); downgraded {totalDowngraded}, dropped {totalDropped}. " +
                      string.Join("  ", bucketSummaries);

            BepinPlugin.Log.LogDebug($"[Forge] Loot reshaped: {LastReshapeSummary} (seed {seed}).");
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Forge] LootTableEscalationPatch failed: {e}");
        }
    }

    // Per-tier relic counts of a loot bucket. Non-relic entries are ignored for
    // the tier view but tracked so the summary can distinguish "reshaped nothing
    // because no relics" from "reshaped nothing because ceiling allows all".
    private readonly struct TierHistogram
    {
        public readonly int Common, Rare, Legendary;
        public TierHistogram(int c, int r, int l) { Common = c; Rare = r; Legendary = l; }
        public int Relics => Common + Rare + Legendary;
        public int AboveCeiling(Loot.RelicTier ceiling) =>
            (ceiling < Loot.RelicTier.Rare ? Rare : 0) + (ceiling < Loot.RelicTier.Legendary ? Legendary : 0);
        public override string ToString() => $"C{Common}/R{Rare}/L{Legendary}";
    }

    private static TierHistogram Histogram(List<CraftableItemRef> list)
    {
        int c = 0, r = 0, l = 0;
        foreach (var item in list)
        {
            var name = item?.Filename;
            if (string.IsNullOrEmpty(name) || !Loot.RelicTierData.TryGet(name, out var e)) continue;
            if (e.Tier == Loot.RelicTier.Common) c++;
            else if (e.Tier == Loot.RelicTier.Rare) r++;
            else l++;
        }
        return new TierHistogram(c, r, l);
    }

    // Mirror the seed the vanilla shuffle uses (quest.Seed + sector.Id), so our
    // reshape is deterministic across clients and stable within a run.
    private static int ResolveSeed()
    {
        int seed = 0;
        var quest = GameSessionManager.ActiveSession?.ActiveQuest;
        if (quest != null) seed += quest.Seed;
        if (GameSessionManager.ActiveSector != null) seed += GameSessionManager.ActiveSector.Id;
        return seed;
    }
}
