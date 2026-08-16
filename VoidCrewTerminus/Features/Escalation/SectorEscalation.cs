using System;
using System.Collections.Generic;

namespace VoidCrewTerminus.Escalation;

// Sector Escalation loot half. Reshapes relic entries in the sector's vanilla
// loot pool so early sectors flood Common relics and later sectors admit
// Rare/Legendary. Non-relic entries pass through untouched. The max allowed
// tier is the higher of a scalar-based ceiling (natural progression) and a
// boss-based ceiling (guaranteed unlock on boss kills) — see TierFromScalar
// and TierFromBossCount. Relics over the max are swapped for a random
// same-list relic at the max tier (seeded from quest+sector for MP
// determinism); dropped if the list has no candidate at that tier.
//
// BossesDefeated lives here rather than on the Forge because bosses affect
// loot table tier, not the Forge module itself.
//
// Activation gate: all escalation systems (density, HP, damage, loot bias)
// stay dormant until BossesDefeated reaches the configured threshold — the
// player has to prove out vanilla difficulty first. Scalar and boss count
// still accumulate during the warm-up period, so scaling activates at
// whatever intensity has already piled up the moment the threshold is crossed.
public static class SectorEscalation
{
    public static int BossesDefeated { get; private set; }

    public static bool IsScalingActive =>
        BossesDefeated >= TerminusConfig.BossActivationThreshold;

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

    // Client-side apply of the host-authoritative boss count. Silent (no
    // notification) — the host owns the count and drives the unlock messages.
    internal static void ApplyNetworkBosses(int bosses) => BossesDefeated = System.Math.Max(0, bosses);

    // Generic over the item ref type so tests can pass plain strings; production
    // callers pass CraftableItemRef and a name extractor reading .Filename.
    public static void DowngradeRelics<T>(
        List<T> entries,
        Func<T, string> getName,
        int scalar,
        int bossesDefeated,
        int seed)
    {
        if (entries == null || entries.Count == 0) return;

        var maxAllowed = MaxAllowedTier(scalar, bossesDefeated);

        // At max scalar nothing is ever downgraded, so skip the walk entirely.
        if (maxAllowed == Loot.RelicTier.Legendary) return;

        // Null = non-relic (name not in the tier data map).
        var tiers = new Loot.RelicTier?[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var name = getName(entries[i]);
            if (!string.IsNullOrEmpty(name) && Loot.RelicTierData.TryGet(name, out var entry))
                tiers[i] = entry.Tier;
        }

        var rng = new Random(seed);
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var tier = tiers[i];
            if (tier == null || tier <= maxAllowed) continue;

            var candidates = new List<int>();
            for (int j = 0; j < entries.Count; j++)
                if (tiers[j] == maxAllowed) candidates.Add(j);

            if (candidates.Count == 0)
            {
                // tiers[] stays valid: iterating in reverse means tiers[i] is never re-read.
                entries.RemoveAt(i);
                continue;
            }

            int pickIdx = candidates[rng.Next(candidates.Count)];
            entries[i] = entries[pickIdx];
            tiers[i] = maxAllowed;
        }
    }

    // The four-argument overload below takes thresholds explicitly so the tier
    // table can be tested without config bound; production callers use this one.
    public static Loot.RelicTier MaxAllowedTier(int scalar, int bossesDefeated) =>
        MaxAllowedTier(scalar, bossesDefeated,
            TerminusConfig.RareUnlockScalar, TerminusConfig.LegendaryUnlockScalar);

    public static Loot.RelicTier MaxAllowedTier(int scalar, int bossesDefeated, int rareUnlockScalar, int legendaryUnlockScalar)
    {
        var fromScalar = TierFromScalar(scalar, rareUnlockScalar, legendaryUnlockScalar);
        var fromBosses = TierFromBossCount(bossesDefeated);
        return fromScalar > fromBosses ? fromScalar : fromBosses;
    }

    private static Loot.RelicTier TierFromScalar(int scalar, int rareUnlockScalar, int legendaryUnlockScalar)
    {
        if (scalar < rareUnlockScalar) return Loot.RelicTier.Common;
        if (scalar < legendaryUnlockScalar) return Loot.RelicTier.Rare;
        return Loot.RelicTier.Legendary;
    }

    private static Loot.RelicTier TierFromBossCount(int bossesDefeated)
    {
        if (bossesDefeated <= 0) return Loot.RelicTier.Common;
        if (bossesDefeated == 1) return Loot.RelicTier.Rare;
        return Loot.RelicTier.Legendary;
    }
}
