using VoidCrewTerminus.Loot;
using Xunit;

namespace VoidCrewTerminus.Tests;

// Pure math coverage of the spawn-time cursed roll.
// The Harmony-hosted spawn scan itself (CursedRelicSpawnPatch) is scene-
// integrated and verified by playtest via !cursedstatus.
public class CursedRelicRollTests
{
    private static RelicTierEntry Entry(float modifier = 0f) =>
        new(RelicTier.Common, isCursed: false, baseCurseChanceModifier: modifier);

    // ---- ChanceFor -------------------------------------------------------

    [Fact]
    public void ChanceFor_AppliesDuringWarmUp_NotGatedOnEscalation()
    {
        // Curses are a property of the relic, not of run progress — the roll is
        // live from the first sector. At scalar 0 (warm-up) that's the flat
        // base + relic modifier: 0.15 + 0.10 = 0.25.
        var chance = CursedRelicRoll.ChanceFor(
            Entry(0.10f), difficultyScalar: 0,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f, maxChance: 1f);

        Assert.Equal(0.25f, chance, precision: 5);
    }

    [Fact]
    public void ChanceFor_SumsBaseAndModifiers()
    {
        // 0.15 base + 0.10 relic modifier + (3 * 0.03) scalar bonus = 0.34
        var chance = CursedRelicRoll.ChanceFor(
            Entry(0.10f), difficultyScalar: 3,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f, maxChance: 1f);

        Assert.Equal(0.34f, chance, precision: 5);
    }

    [Fact]
    public void ChanceFor_NegativeRelicModifier_ReducesChance()
    {
        // 0.15 base + (-0.05) modifier + 0 scalar = 0.10
        var chance = CursedRelicRoll.ChanceFor(
            Entry(-0.05f), difficultyScalar: 0,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f, maxChance: 1f);

        Assert.Equal(0.10f, chance, precision: 5);
    }

    [Fact]
    public void ChanceFor_ClampedTo_Zero_WhenNegativeSum()
    {
        // 0.05 base + (-0.10) modifier = -0.05 → clamped to 0
        var chance = CursedRelicRoll.ChanceFor(
            Entry(-0.10f), difficultyScalar: 0,
            baseChance: 0.05f, scalarBonusPerScalar: 0.03f, maxChance: 1f);

        Assert.Equal(0f, chance);
    }

    [Fact]
    public void ChanceFor_NegativeScalar_TreatedAsZero()
    {
        var chance = CursedRelicRoll.ChanceFor(
            Entry(0f), difficultyScalar: -5,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f, maxChance: 1f);

        Assert.Equal(0.15f, chance, precision: 5);
    }

    // ---- the ceiling (guards the uncapped-scalar bug) ---------------------

    [Fact]
    public void ChanceFor_ClampedToMaxChance_NotToOne()
    {
        // 0.15 base + 0 modifier + (30 * 0.03) = 1.05 — uncapped this is a
        // 100%-cursed run. The ceiling holds it at 0.50.
        var chance = CursedRelicRoll.ChanceFor(
            Entry(0f), difficultyScalar: 30,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f, maxChance: 0.50f);

        Assert.Equal(0.50f, chance, precision: 5);
    }

    [Fact]
    public void ChanceFor_PlateausOnceCeilingReached()
    {
        // Past the ceiling, deeper runs stop raising curse chance at all.
        var atCeiling = CursedRelicRoll.ChanceFor(
            Entry(0f), difficultyScalar: 12,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f, maxChance: 0.50f);
        var farBeyond = CursedRelicRoll.ChanceFor(
            Entry(0f), difficultyScalar: 99,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f, maxChance: 0.50f);

        Assert.Equal(atCeiling, farBeyond);
        Assert.Equal(0.50f, farBeyond, precision: 5);
    }

    [Fact]
    public void ChanceFor_BelowCeiling_Unaffected()
    {
        // 0.15 + (2 * 0.03) = 0.21, well under the 0.50 ceiling → untouched.
        var chance = CursedRelicRoll.ChanceFor(
            Entry(0f), difficultyScalar: 2,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f, maxChance: 0.50f);

        Assert.Equal(0.21f, chance, precision: 5);
    }

    [Fact]
    public void ChanceFor_MaxChanceItselfClampedToUnitRange()
    {
        // A misconfigured ceiling > 1 can't push chance above certainty.
        var chance = CursedRelicRoll.ChanceFor(
            Entry(0.9f), difficultyScalar: 10,
            baseChance: 0.9f, scalarBonusPerScalar: 0.1f, maxChance: 5f);

        Assert.Equal(1f, chance);
    }

    // ---- ShouldBeCursed --------------------------------------------------

    [Theory]
    [InlineData(0.5f, 0.4f, true)]     // roll below chance → cursed
    [InlineData(0.5f, 0.5f, false)]    // equal → not cursed (strict <)
    [InlineData(0.5f, 0.6f, false)]    // above → not cursed
    [InlineData(1.0f, 0.99f, true)]    // always-cursed edge
    [InlineData(0.0f, 0.0f, false)]    // zero chance → never cursed
    public void ShouldBeCursed_MatchesThreshold(float chance, float roll, bool expected)
    {
        Assert.Equal(expected, CursedRelicRoll.ShouldBeCursed(chance, roll));
    }
}
