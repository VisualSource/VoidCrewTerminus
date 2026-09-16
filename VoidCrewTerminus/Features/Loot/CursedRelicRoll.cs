using System;

namespace VoidCrewTerminus.Loot;

// Pure and Unity-free so the chance formula is testable without the game runtime.
// Total chance = clamp(baseChance + perRelicModifier + scalarBonus, 0, maxChance), rolled
// against a caller-supplied nextRandom in [0,1).
//
// Deliberately NOT gated on IsScalingActive: a curse is a property of the relic, so the risk
// exists from the first sector. DifficultyScalar only climbs once escalation activates, which
// yields a flat warm-up baseline and rising risk with depth without an on/off switch.
//
// maxChance is the load-bearing part: DifficultyScalar is uncapped and climbs ~1/sector, so
// an unbounded formula reaches 100% cursed in a long run.
public static class CursedRelicRoll
{
    public static float ChanceFor(
        RelicTierEntry entry,
        int difficultyScalar,
        float baseChance,
        float scalarBonusPerScalar,
        float maxChance)
    {
        float total = baseChance + entry.BaseCurseChanceModifier
                    + Math.Max(0, difficultyScalar) * scalarBonusPerScalar;
        if (total < 0f) return 0f;
        float ceiling = maxChance < 0f ? 0f : (maxChance > 1f ? 1f : maxChance);
        return total > ceiling ? ceiling : total;
    }

    public static bool ShouldBeCursed(float chance, float nextRandom) =>
        chance > 0f && nextRandom < chance;
}
