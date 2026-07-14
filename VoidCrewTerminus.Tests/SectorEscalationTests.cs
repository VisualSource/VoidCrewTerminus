using System.Collections.Generic;
using System.Linq;
using VoidCrewTerminus.Forge;
using Xunit;

namespace VoidCrewTerminus.Tests;

// Tests for the pure downgrade algorithm. Uses real relic prefab names from
// RelicTierData so tier lookups resolve without stubbing.
public class SectorEscalationTests
{
    // Known-tier names lifted from RelicTierData._map so tier lookups are real.
    private const string CommonRelic1  = "Relic_00_Solo";
    private const string CommonRelic2  = "Relic_01_A_StarboardPower";
    private const string RareRelic1    = "Relic_02_PowerForBreakers";
    private const string RareRelic2    = "Relic_03_VulnerabilityDuringVoidCharge";
    private const string LegendaryRelic1 = "Relic_15_BiomassForThrustersAndDamage";
    private const string LegendaryRelic2 = "Relic_28_PayloadRecharge";
    private const string NonRelic      = "SomeModule_Cell";

    private static List<string> MakeEntries(params string[] names) => new(names);

    // ---- max-allowed tier -------------------------------------------------

    [Theory]
    [InlineData(0, RelicTier.Common)]
    [InlineData(2, RelicTier.Common)]
    [InlineData(3, RelicTier.Rare)]
    [InlineData(5, RelicTier.Rare)]
    [InlineData(6, RelicTier.Legendary)]
    [InlineData(100, RelicTier.Legendary)]
    public void MaxAllowedTier_MapsScalarBands(int scalar, RelicTier expected)
    {
        Assert.Equal(expected, SectorEscalation.MaxAllowedTier(scalar, rareUnlockScalar: 3, legendaryUnlockScalar: 6));
    }

    // ---- downgrade behaviour ----------------------------------------------

    [Fact]
    public void Downgrade_AtScalarZero_RareBecomesCommon()
    {
        var entries = MakeEntries(CommonRelic1, RareRelic1);
        SectorEscalation.DowngradeRelics(entries, s => s, scalar: 0, seed: 42);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(RelicTier.Common, RelicTierData.Get(e).Tier));
    }

    [Fact]
    public void Downgrade_AtScalarZero_LegendaryBecomesCommon()
    {
        var entries = MakeEntries(CommonRelic1, LegendaryRelic1);
        SectorEscalation.DowngradeRelics(entries, s => s, scalar: 0, seed: 42);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(RelicTier.Common, RelicTierData.Get(e).Tier));
    }

    [Fact]
    public void Downgrade_AtScalarThree_LegendaryBecomesRare()
    {
        var entries = MakeEntries(RareRelic1, LegendaryRelic1);
        SectorEscalation.DowngradeRelics(entries, s => s, scalar: 3, seed: 42);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(RelicTier.Rare, RelicTierData.Get(e).Tier));
    }

    [Fact]
    public void Downgrade_AtScalarThree_RareUnchanged()
    {
        var entries = MakeEntries(RareRelic1, RareRelic2);
        SectorEscalation.DowngradeRelics(entries, s => s, scalar: 3, seed: 42);

        Assert.Equal(new[] { RareRelic1, RareRelic2 }, entries);
    }

    [Fact]
    public void Downgrade_AtScalarSix_NothingChanges()
    {
        var entries = MakeEntries(CommonRelic1, RareRelic1, LegendaryRelic1);
        var expected = entries.ToList();

        SectorEscalation.DowngradeRelics(entries, s => s, scalar: 6, seed: 42);

        Assert.Equal(expected, entries);
    }

    [Fact]
    public void Downgrade_ReplacementPickedFromPoolOfTargetTier()
    {
        // Rare + two candidate Commons — the Rare must be replaced by one of them.
        var entries = MakeEntries(CommonRelic1, CommonRelic2, RareRelic1);
        SectorEscalation.DowngradeRelics(entries, s => s, scalar: 0, seed: 42);

        // Third slot (originally Rare) is now one of the two Commons.
        Assert.Contains(entries[2], new[] { CommonRelic1, CommonRelic2 });
    }

    [Fact]
    public void Downgrade_NoReplacementCandidate_EntryDropped()
    {
        // Only Rare + Legendary at scalar 0 — no Common candidate. Both drop.
        var entries = MakeEntries(RareRelic1, LegendaryRelic1);
        SectorEscalation.DowngradeRelics(entries, s => s, scalar: 0, seed: 42);

        Assert.Empty(entries);
    }

    [Fact]
    public void Downgrade_LeavesNonRelicEntriesUntouched()
    {
        var entries = MakeEntries(NonRelic, LegendaryRelic1, NonRelic);
        SectorEscalation.DowngradeRelics(entries, s => s, scalar: 0, seed: 42);

        // Non-relic entries stay in place, Legendary got dropped (no Common candidate).
        Assert.Equal(new[] { NonRelic, NonRelic }, entries);
    }

    [Fact]
    public void Downgrade_UnknownRelic_TreatedAsCommon()
    {
        // "Relic_XX_Unknown" fall through to the Common fallback in RelicTierData,
        // so it counts as Common — unchanged at any scalar.
        var entries = MakeEntries("Relic_XX_Unknown");
        SectorEscalation.DowngradeRelics(entries, s => s, scalar: 6, seed: 42);

        Assert.Equal(new[] { "Relic_XX_Unknown" }, entries);
    }

    [Fact]
    public void Downgrade_Deterministic_SameSeedProducesSameResult()
    {
        // Multiple runs with same input + seed must produce identical outputs.
        var a = MakeEntries(CommonRelic1, CommonRelic2, RareRelic1, RareRelic2, LegendaryRelic1);
        var b = MakeEntries(CommonRelic1, CommonRelic2, RareRelic1, RareRelic2, LegendaryRelic1);

        SectorEscalation.DowngradeRelics(a, s => s, scalar: 0, seed: 12345);
        SectorEscalation.DowngradeRelics(b, s => s, scalar: 0, seed: 12345);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Downgrade_EmptyList_NoOp()
    {
        var entries = new List<string>();
        SectorEscalation.DowngradeRelics(entries, s => s, scalar: 0, seed: 42);
        Assert.Empty(entries);
    }

    [Fact]
    public void Downgrade_NullList_NoOp()
    {
        // Should not throw.
        SectorEscalation.DowngradeRelics<string>(null, s => s, scalar: 0, seed: 42);
    }
}
