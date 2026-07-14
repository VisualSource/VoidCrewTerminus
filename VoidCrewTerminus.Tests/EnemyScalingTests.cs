using VoidCrewTerminus.Escalation;
using Xunit;

namespace VoidCrewTerminus.Tests;

// Pure math coverage of the density scale. HP/damage StatMod attachment paths
// need a live game (StatType, IModifierSource pipeline, faction resolution) —
// same limitation as the existing skipped tests.
public class EnemyScalingTests
{
    // ---- ScaleIntensity -----------------------------------------------------

    [Theory]
    [InlineData(0, 5, 0.20f, 5)]         // scalar 0 → no change
    [InlineData(3, 0, 0.20f, 0)]         // zero base → zero out
    [InlineData(3, 10, 0.20f, 16)]       // 10 * (1 + 3*0.20) = 16
    [InlineData(6, 10, 0.20f, 22)]       // 10 * (1 + 6*0.20) = 22
    [InlineData(3, 5, 0.20f, 8)]         // 5 * 1.6 = 8 (exact)
    [InlineData(3, 4, 0.20f, 7)]         // 4 * 1.6 = 6.4 → Ceil = 7
    public void ScaleIntensity_ScalesPositiveValues(int scalar, int requested, float rate, int expected)
    {
        Assert.Equal(expected, EnemyScalingHelpers.ScaleIntensity(requested, scalar, rate));
    }

    [Fact]
    public void ScaleIntensity_RoundsUp_NotDown()
    {
        // 4 * (1 + 3*0.20) = 4 * 1.6 = 6.4. Floor would give 6; Ceil gives 7.
        Assert.Equal(7, EnemyScalingHelpers.ScaleIntensity(4, 3, 0.20f));
    }

    [Fact]
    public void ScaleIntensity_ZeroScalar_ReturnsInput()
    {
        Assert.Equal(10, EnemyScalingHelpers.ScaleIntensity(10, 0, 0.20f));
        Assert.Equal(-5, EnemyScalingHelpers.ScaleIntensity(-5, 0, 0.20f));
    }

    [Fact]
    public void ScaleIntensity_ZeroRate_ReturnsInput()
    {
        Assert.Equal(10, EnemyScalingHelpers.ScaleIntensity(10, 5, 0f));
    }

    [Fact]
    public void ScaleIntensity_NegativeDelta_PassesThroughUnamplified()
    {
        // Scenario reducing intensity — we must not amplify the reduction.
        // Rate 0.20, scalar 3 → factor 1.6. Naive: -5 * 1.6 = -8 (bigger reduction).
        // Guarded: keeps the smaller-magnitude value (-5).
        Assert.Equal(-5, EnemyScalingHelpers.ScaleIntensity(-5, 3, 0.20f));
    }

    [Fact]
    public void ScaleIntensity_NeverShrinksRequestedPositiveValue()
    {
        // If scaled math ever came out lower than input (shouldn't happen with
        // positive rate/scalar, but guard exists), we'd keep input.
        Assert.Equal(10, EnemyScalingHelpers.ScaleIntensity(10, 0, 0.20f));
    }

    // ---- faction helpers ---------------------------------------------------

    [Theory]
    [InlineData(0, false)]  // Neutral
    [InlineData(1, false)]  // Metem (player)
    [InlineData(2, true)]   // Remnant — hostile
    [InlineData(3, true)]   // Hollows — hostile
    [InlineData(4, false)]  // Wildlife — untouched
    public void IsEnemyFaction_MatchesHollowsAndRemnantOnly(int faction, bool expected)
    {
        Assert.Equal(expected, EnemyScalingHelpers.IsEnemyFaction(faction));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]   // Metem only
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void IsPlayerFaction_MatchesMetemOnly(int faction, bool expected)
    {
        Assert.Equal(expected, EnemyScalingHelpers.IsPlayerFaction(faction));
    }
}
