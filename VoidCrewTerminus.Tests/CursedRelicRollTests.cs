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
    public void ChanceFor_DormantEscalation_ReturnsZero()
    {
        // No cursed rolls during warm-up regardless of base chance.
        var chance = CursedRelicRoll.ChanceFor(
            Entry(0.10f), difficultyScalar: 5, isScalingActive: false,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f);

        Assert.Equal(0f, chance);
    }

    [Fact]
    public void ChanceFor_ActiveEscalation_SumsBaseAndModifiers()
    {
        // 0.15 base + 0.10 relic modifier + (3 * 0.03) scalar bonus = 0.34
        var chance = CursedRelicRoll.ChanceFor(
            Entry(0.10f), difficultyScalar: 3, isScalingActive: true,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f);

        Assert.Equal(0.34f, chance, precision: 5);
    }

    [Fact]
    public void ChanceFor_NegativeRelicModifier_ReducesChance()
    {
        // 0.15 base + (-0.05) modifier + 0 scalar = 0.10
        var chance = CursedRelicRoll.ChanceFor(
            Entry(-0.05f), difficultyScalar: 0, isScalingActive: true,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f);

        Assert.Equal(0.10f, chance, precision: 5);
    }

    [Fact]
    public void ChanceFor_ClampedTo_Zero_WhenNegativeSum()
    {
        // 0.05 base + (-0.10) modifier = -0.05 → clamped to 0
        var chance = CursedRelicRoll.ChanceFor(
            Entry(-0.10f), difficultyScalar: 0, isScalingActive: true,
            baseChance: 0.05f, scalarBonusPerScalar: 0.03f);

        Assert.Equal(0f, chance);
    }

    [Fact]
    public void ChanceFor_ClampedTo_One_WhenSumExceedsOne()
    {
        // 0.50 base + 0.30 modifier + (10 * 0.10) = 1.80 → clamped to 1
        var chance = CursedRelicRoll.ChanceFor(
            Entry(0.30f), difficultyScalar: 10, isScalingActive: true,
            baseChance: 0.50f, scalarBonusPerScalar: 0.10f);

        Assert.Equal(1f, chance);
    }

    [Fact]
    public void ChanceFor_NegativeScalar_TreatedAsZero()
    {
        var chance = CursedRelicRoll.ChanceFor(
            Entry(0f), difficultyScalar: -5, isScalingActive: true,
            baseChance: 0.15f, scalarBonusPerScalar: 0.03f);

        Assert.Equal(0.15f, chance, precision: 5);
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
