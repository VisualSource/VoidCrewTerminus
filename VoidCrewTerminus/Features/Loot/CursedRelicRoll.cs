using System;

namespace VoidCrewTerminus.Loot;

// Pure math for the spawn-time cursed roll. Kept Unity-free so the chance
// formula is testable without the game runtime.
//
// Total chance = clamp01(baseChance + perRelicModifier + scalarBonus).
// Roll happens against a provided nextRandom (0..1) from the caller — patches
// use UnityEngine.Random; tests pass a stub.
public static class CursedRelicRoll
{
    // Compute the final curse chance for a relic given the current escalation
    // state. Returns 0 if escalation is dormant (no base chance during warm-up).
    // Otherwise adds per-relic modifier and scalar-driven bonus, clamped to [0, 1].
    public static float ChanceFor(
        RelicTierEntry entry,
        int difficultyScalar,
        bool isScalingActive,
        float baseChance,
        float scalarBonusPerScalar)
    {
        if (!isScalingActive) return 0f;
        float total = baseChance + entry.BaseCurseChanceModifier
                    + Math.Max(0, difficultyScalar) * scalarBonusPerScalar;
        if (total < 0f) return 0f;
        if (total > 1f) return 1f;
        return total;
    }

    // Given a chance and a random draw (0..1), decide cursed / not.
    public static bool ShouldBeCursed(float chance, float nextRandom) =>
        chance > 0f && nextRandom < chance;
}
