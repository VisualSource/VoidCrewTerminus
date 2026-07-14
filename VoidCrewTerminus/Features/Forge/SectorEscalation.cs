using System;
using System.Collections.Generic;

namespace VoidCrewTerminus.Forge;

// Phase 6 — Sector Escalation loot half. Reshapes relic entries in the sector's
// vanilla loot pool so early sectors flood Common relics and later sectors admit
// Rare/Legendary. Non-relic entries pass through untouched.
//
// Rule (Z-shape: "soft downgrade filter" — no upgrades):
//   scalar < RareUnlock       → max allowed tier = Common
//   scalar < LegendaryUnlock  → max allowed tier = Rare
//   scalar ≥ LegendaryUnlock  → max allowed tier = Legendary (nothing changes)
//
// When a relic's tier exceeds the max, we swap its reference for a random
// same-list relic of the max tier (seeded from quest+sector for MP determinism).
// If the list has no candidate at the target tier, the entry is dropped.
public static class SectorEscalation
{
    // Reshape `entries` in place. Generic over the item ref type so tests can
    // pass plain strings; production callers pass CraftableItemRef and a name
    // extractor that reads .Filename.
    public static void DowngradeRelics<T>(
        List<T> entries,
        Func<T, string> getName,
        int scalar,
        int seed)
    {
        if (entries == null || entries.Count == 0) return;

        int rareUnlock = TerminusConfig.EscalationRareUnlockScalar?.Value ?? 3;
        int legendaryUnlock = TerminusConfig.EscalationLegendaryUnlockScalar?.Value ?? 6;
        var maxAllowed = MaxAllowedTier(scalar, rareUnlock, legendaryUnlock);

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

    public static RelicTier MaxAllowedTier(int scalar, int rareUnlockScalar, int legendaryUnlockScalar)
    {
        if (scalar < rareUnlockScalar) return RelicTier.Common;
        if (scalar < legendaryUnlockScalar) return RelicTier.Rare;
        return RelicTier.Legendary;
    }
}
