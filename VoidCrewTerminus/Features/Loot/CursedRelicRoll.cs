using System;

namespace VoidCrewTerminus.Loot;

// Pure math for the spawn-time cursed roll. Kept Unity-free so the chance
// formula is testable without the game runtime.
//
// Total chance = clamp(baseChance + perRelicModifier + scalarBonus, 0, maxChance).
// Roll happens against a provided nextRandom (0..1) from the caller — patches
// use UnityEngine.Random; tests pass a stub.
//
// Curses are deliberately NOT gated on SectorEscalation.IsScalingActive: a
// cursed relic is a property of the relic, so the risk exists from the first
// sector. DifficultyScalar only starts climbing after escalation activates,
// which means this naturally yields a flat baseline during warm-up and rising
// risk with depth — without a hard on/off switch.
//
// maxChance is the load-bearing part: DifficultyScalar is uncapped and climbs
// ~1/sector, so an unbounded formula reaches 100% cursed in a long run (every
// relic cursed forever). The ceiling keeps curses a risk rather than a
// certainty.
public static class CursedRelicRoll
{
    // Compute the final curse chance for a relic. Adds the per-relic modifier
    // and the scalar-driven bonus to the base, then clamps to [0, maxChance].
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

    // Given a chance and a random draw (0..1), decide cursed / not.
    public static bool ShouldBeCursed(float chance, float nextRandom) =>
        chance > 0f && nextRandom < chance;
}
