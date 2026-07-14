using System;
using System.Collections.Generic;

namespace VoidCrewTerminus.Forge;

// Phase 6 — Sector Escalation loot half. Reshapes relic entries in the sector's
// vanilla loot pool so early sectors flood Common relics and later sectors admit
// Rare/Legendary. Non-relic entries pass through untouched.
//
// The max allowed tier is the higher of two independent signals:
//   * Scalar-based ceiling (natural progression through sector jumps):
//       scalar < RareUnlock       → Common ceiling
//       scalar < LegendaryUnlock  → Rare ceiling
//       scalar ≥ LegendaryUnlock  → Legendary ceiling
//   * Boss-based ceiling (rare event unlock, guaranteed):
//       bossesDefeated ≥ 1        → at least Rare
//       bossesDefeated ≥ 2        → at least Legendary
//
// When a relic's tier exceeds the max, we swap its reference for a random
// same-list relic of the max tier (seeded from quest+sector for MP determinism).
// If the list has no candidate at the target tier, the entry is dropped.
//
// Escalation state (BossesDefeated) lives here rather than on the Forge —
// bosses don't affect the Forge module itself (level, capacity, meter);
// they affect what tier of relics the loot table can drop.
//
// Activation gate: all escalation systems (density, HP, damage, loot bias)
// stay dormant until BossesDefeated reaches EscalationBossActivationThreshold
// (default 2). The player has to demonstrate combat capability with the
// vanilla difficulty before the mod starts turning up the pressure. Scalar
// and boss count still accumulate during the warm-up period, so the moment
// the threshold is crossed, scaling activates at whatever intensity has
// piled up.
public static class SectorEscalation
{
    // Independent of DifficultyScalar. Each boss defeat unlocks the next relic
    // tier ceiling regardless of scalar progression. Reset per-run from
    // Plugins.cs; incremented by BossDefeatHook.
    public static int BossesDefeated { get; private set; }

    // Whether any escalation should apply right now. Read by every escalation
    // patch/hook before it does anything.
    public static bool IsScalingActive =>
        BossesDefeated >= (TerminusConfig.EscalationBossActivationThreshold?.Value ?? 2);

    public static void ResetForRun() => BossesDefeated = 0;

    public static void IncrementBossesDefeated()
    {
        BossesDefeated++;
        BepinPlugin.Log?.LogDebug($"[Escalation] BossesDefeated → {BossesDefeated}");
    }

    public static void SetBossesDefeated(int value)
    {
        BossesDefeated = System.Math.Max(0, value);
        BepinPlugin.Log?.LogDebug($"[Escalation] BossesDefeated set to {BossesDefeated} (dev)");
    }

    // Reshape `entries` in place. Generic over the item ref type so tests can
    // pass plain strings; production callers pass CraftableItemRef and a name
    // extractor that reads .Filename.
    public static void DowngradeRelics<T>(
        List<T> entries,
        Func<T, string> getName,
        int scalar,
        int bossesDefeated,
        int seed)
    {
        if (entries == null || entries.Count == 0) return;

        int rareUnlock = TerminusConfig.EscalationRareUnlockScalar?.Value ?? 3;
        int legendaryUnlock = TerminusConfig.EscalationLegendaryUnlockScalar?.Value ?? 6;
        var maxAllowed = MaxAllowedTier(scalar, bossesDefeated, rareUnlock, legendaryUnlock);

        // Optimisation and correctness: at max scalar nothing is ever downgraded,
        // so skip the walk (and skip triggering any RelicTierData lookups) entirely.
        if (maxAllowed == RelicTier.Legendary) return;

        // Cache tier per entry so we don't hit RelicTierData twice per relic.
        // Null = non-relic (name not in the tier data map).
        var tiers = new RelicTier?[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var name = getName(entries[i]);
            if (!string.IsNullOrEmpty(name) && RelicTierData.TryGet(name, out var entry))
                tiers[i] = entry.Tier;
        }

        var rng = new Random(seed);
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var tier = tiers[i];
            if (tier == null || tier <= maxAllowed) continue;

            // Downgrade: swap for a random same-tier candidate already in the list.
            // Rebuild the candidate index on demand — usually small (single-digit).
            var candidates = new List<int>();
            for (int j = 0; j < entries.Count; j++)
                if (tiers[j] == maxAllowed) candidates.Add(j);

            if (candidates.Count == 0)
            {
                entries.RemoveAt(i);
                // tiers[] parallel array: no update needed since we iterate in
                // reverse and don't re-read tiers[i] again.
                continue;
            }

            int pickIdx = candidates[rng.Next(candidates.Count)];
            entries[i] = entries[pickIdx];
            tiers[i] = maxAllowed;
        }
    }

    public static RelicTier MaxAllowedTier(int scalar, int bossesDefeated, int rareUnlockScalar, int legendaryUnlockScalar)
    {
        var fromScalar = TierFromScalar(scalar, rareUnlockScalar, legendaryUnlockScalar);
        var fromBosses = TierFromBossCount(bossesDefeated);
        return fromScalar > fromBosses ? fromScalar : fromBosses;
    }

    private static RelicTier TierFromScalar(int scalar, int rareUnlockScalar, int legendaryUnlockScalar)
    {
        if (scalar < rareUnlockScalar) return RelicTier.Common;
        if (scalar < legendaryUnlockScalar) return RelicTier.Rare;
        return RelicTier.Legendary;
    }

    // First boss unlocks Rare; second boss unlocks Legendary. Third+ bosses have
    // no further tier to unlock (Legendary is the ceiling).
    private static RelicTier TierFromBossCount(int bossesDefeated)
    {
        if (bossesDefeated <= 0) return RelicTier.Common;
        if (bossesDefeated == 1) return RelicTier.Rare;
        return RelicTier.Legendary;
    }
}
