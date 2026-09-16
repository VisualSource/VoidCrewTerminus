using System.Collections.Generic;
using CG.Game;
using Gameplay.Loot;
using Gameplay.Quests;
using HarmonyLib;
using ResourceAssets;
using VC.Common.CoreData;
using VoidCrewTerminus.Escalation;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Patches;

// Runs once per sector on every client, seeded from quest+sector so all clients converge on
// the same reshaped lists. Only the host consumes them to spawn drops.
[HarmonyPatch(typeof(LootManager), "SetupCurrentSectorLootLists")]
internal static class LootTableEscalationPatch
{
    private static readonly AccessTools.FieldRef<LootManager, Dictionary<LootRarities, List<CraftableItemRef>>> LootListsRef =
        AccessTools.FieldRefAccess<LootManager, Dictionary<LootRarities, List<CraftableItemRef>>>("CurrentSectorLootLists");

    // Surfaced by !lootdump so the ceiling's effect is visible even when it is a deliberate
    // no-op, as at 2 bosses where the ceiling is already Legendary.
    public static string LastReshapeSummary { get; private set; } = "(no sector reshaped yet)";

    static void Postfix(LootManager __instance)
    {
        try
        {
            var lists = LootListsRef(__instance);
            if (lists == null) return;

            // Bosses are only defeatable in EndlessQuest, so elsewhere BossesDefeated stays
            // 0 and would crush every sector's loot to Common forever.
            if (!(GameSessionManager.ActiveSession?.ActiveQuest is EndlessQuest))
            {
                LastReshapeSummary = "(non-Endless quest — loot biasing skipped)";
                return;
            }

            // Deliberately not gated on the escalation warm-up, unlike density and HP: the
            // relic ceiling is driven by boss count from the first sector, and gating on
            // IsScalingActive would suppress this until the ceiling is already Legendary.
            int scalar = ForgeMeterController.DifficultyScalar;
            int bosses = SectorEscalation.BossesDefeated;
            int seed = ResolveSeed();

            var ceiling = SectorEscalation.MaxAllowedTier(scalar, bosses);

            var bucketSummaries = new List<string>();
            int totalDowngraded = 0, totalDropped = 0;

            foreach (var kv in lists)
            {
                var before = Histogram(kv.Value);

                // Seed per-rarity so a change in one list's contents doesn't shift picks in the others.
                SectorEscalation.DowngradeRelics(
                    kv.Value,
                    r => r?.Filename,
                    scalar,
                    bosses,
                    unchecked(seed * 397 ^ (int)kv.Key));

                var after = Histogram(kv.Value);
                if (before.Relics == 0) continue;

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

    // Non-relic entries are ignored, so the summary can distinguish "no relics" from
    // "ceiling allows all".
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

    // Mirrors the vanilla shuffle's seed so the reshape is deterministic across clients and
    // stable within a run.
    private static int ResolveSeed()
    {
        int seed = 0;
        var quest = GameSessionManager.ActiveSession?.ActiveQuest;
        if (quest != null) seed += quest.Seed;
        if (GameSessionManager.ActiveSector != null) seed += GameSessionManager.ActiveSector.Id;
        return seed;
    }
}
