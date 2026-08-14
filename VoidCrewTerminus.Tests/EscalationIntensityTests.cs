using VoidCrewTerminus.Escalation;
using Xunit;

namespace VoidCrewTerminus.Tests;

// EscalationIntensity replaced a four-line preamble that five separate patch
// sites each carried their own copy of. These tests pin the combined gate/cap/
// multiplier behaviour so the copies can't drift back in.
//
// Everything goes through the explicit constructor. EscalationIntensity.Current
// reads two static singletons plus the config and is the ambient convenience for
// production; the constructor is the seam that makes the logic testable at all.
public class EscalationIntensityTests
{
    private static EscalationIntensity Active(int rawScalar, int cap = 0,
        float statRate = 0.05f, float densityRate = 0.12f) =>
        new(active: true, rawScalar, cap, statRate, densityRate);

    // ---- the cap ---------------------------------------------------------

    // Enemy pressure has to plateau: the raw scalar climbs all run, and an
    // uncapped density multiplier would spawn unbounded networked ships.
    [Theory]
    [InlineData(5, 10, 5)]    // below the cap, untouched
    [InlineData(10, 10, 10)]  // at the cap
    [InlineData(25, 10, 10)]  // above the cap, clamped
    public void Scalar_IsCapped(int raw, int cap, int expected) =>
        Assert.Equal(expected, Active(raw, cap).Scalar);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCap_MeansUncapped(int cap) =>
        Assert.Equal(99, Active(99, cap).Scalar);

    // Loot tier biasing and the dev readout need the UNCAPPED value, so capping
    // must not destroy it.
    [Fact]
    public void RawScalar_SurvivesCapping()
    {
        var e = Active(25, cap: 10);

        Assert.Equal(25, e.RawScalar);
        Assert.Equal(10, e.Scalar);
    }

    // ---- the shared gate -------------------------------------------------

    [Fact]
    public void AffectsEnemies_FalseWhileDormant() =>
        Assert.False(new EscalationIntensity(active: false, 8, 10, 0.05f, 0.12f).AffectsEnemies);

    // A zero scalar means nothing has accumulated yet — vanilla is left alone.
    [Fact]
    public void AffectsEnemies_FalseAtZeroScalar() =>
        Assert.False(Active(0).AffectsEnemies);

    [Fact]
    public void AffectsEnemies_TrueWhenActiveAndAccumulated() =>
        Assert.True(Active(1).AffectsEnemies);

    // Dormant-but-accumulated is the warm-up state the whole design hinges on:
    // scalar keeps climbing so that scaling switches on at full accumulated
    // intensity the moment the boss threshold is crossed.
    [Fact]
    public void Dormant_StillTracksScalar()
    {
        var e = new EscalationIntensity(active: false, 7, 10, 0.05f, 0.12f);

        Assert.False(e.AffectsEnemies);
        Assert.Equal(7, e.Scalar);
    }

    // ---- multipliers -----------------------------------------------------

    [Fact]
    public void StatBonus_IsCappedScalarTimesRate() =>
        Assert.Equal(0.5f, Active(10, cap: 10, statRate: 0.05f).StatBonus, precision: 5);

    [Fact]
    public void StatBonus_UsesTheCappedScalar_NotTheRaw() =>
        Assert.Equal(0.5f, Active(100, cap: 10, statRate: 0.05f).StatBonus, precision: 5);

    [Fact]
    public void StatMultiplier_IsOnePlusBonus() =>
        Assert.Equal(1.5f, Active(10, cap: 10, statRate: 0.05f).StatMultiplier, precision: 5);

    // The documented default plateau: cap 10 × rate 0.12 → 2.2x density.
    [Fact]
    public void DensityMultiplier_PlateausAtTheDocumentedDefault() =>
        Assert.Equal(2.2f, Active(50, cap: 10, densityRate: 0.12f).DensityMultiplier, precision: 5);

    // A zero or negative RATE is a distinct no-op from a zero scalar, which is
    // why the HP patch checks StatBonus separately from AffectsEnemies —
    // registering a zero-value StatMod would attach a modifier for nothing.
    [Theory]
    [InlineData(0f)]
    [InlineData(-0.1f)]
    public void StatBonus_NonPositive_WhenRateIsNonPositive(float rate)
    {
        var e = Active(10, cap: 10, statRate: rate);

        Assert.True(e.AffectsEnemies);   // gate passes...
        Assert.True(e.StatBonus <= 0f);  // ...but there is nothing to apply
    }

    // ---- density scaling -------------------------------------------------

    // Callers apply this unconditionally, so a dormant run must pass the
    // scenario's own value straight through.
    [Fact]
    public void ScaleDensity_DormantIsIdentity() =>
        Assert.Equal(8, new EscalationIntensity(active: false, 10, 10, 0.05f, 0.12f).ScaleDensity(8));

    [Fact]
    public void ScaleDensity_ZeroScalarIsIdentity() =>
        Assert.Equal(8, Active(0).ScaleDensity(8));

    // 8 × (1 + 5×0.12) = 12.8 → rounds UP, so escalation never quietly loses an
    // enemy to truncation.
    [Fact]
    public void ScaleDensity_RoundsUp() =>
        Assert.Equal(13, Active(5, cap: 10, densityRate: 0.12f).ScaleDensity(8));

    // Negative deltas are scenarios REDUCING intensity; amplifying them would
    // invert the scenario's intent.
    [Fact]
    public void ScaleDensity_DoesNotAmplifyNegativeDeltas() =>
        Assert.Equal(-5, Active(10, cap: 10).ScaleDensity(-5));
}
