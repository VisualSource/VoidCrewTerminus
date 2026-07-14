using System;
using VoidCrewTerminus.Forge;
using VoidCrewTerminus.Loot;
using Xunit;

namespace VoidCrewTerminus.Tests;

// Tests for the pure commit algorithm. All defaults come from the shipped config
// values (curve 1,1,2,2,3,3,4; roll chances 0.25/0.40/0.75) since TerminusConfig
// is not initialised in the test host and the ??-fallbacks kick in.
public class UpgradeCommitCalculatorTests
{
    private static readonly string[] EmptySlots = new string[PerkPool.SlotCount];

    private static CommitRequest Request(
        int currentLevel,
        RelicTier[] relicTiers,
        ForgeCategory category = ForgeCategory.Weapon,
        string[] perkSlots = null) =>
        new(currentLevel, relicTiers, category, perkSlots ?? EmptySlots);

    // Rig: force the perk-roll gate to always fail so tests that only care about
    // level/consumption aren't sensitive to which perk lands.
    private static Func<float> NeverRolls => () => 1f;

    // ---- cost curve walk ----------------------------------------------------

    [Theory]
    [InlineData(3, 1, 4, 1)]   // L3 + 1 relic (cost 1) → L4
    [InlineData(3, 2, 5, 2)]   // L3 + 2 relics (1+1) → L5
    [InlineData(3, 4, 6, 4)]   // L3 + 4 relics (1+1+2) → L6, leftover 0
    [InlineData(3, 16, 10, 16)] // full curve
    [InlineData(5, 3, 6, 2)]   // L5 + 3 relics: L5→L6 costs 2, then L6→L7 costs 2 (need 2, have 1) → stops at L6, 1 leftover
    public void Commit_WalksTheCostCurve(int fromLevel, int relicCount, int expectedLevel, int expectedConsumed)
    {
        var relics = new RelicTier[relicCount];
        for (int i = 0; i < relicCount; i++) relics[i] = RelicTier.Common;

        var outcome = UpgradeCommitCalculator.Calculate(Request(fromLevel, relics), NeverRolls);

        Assert.Equal(CommitStatus.Ok, outcome.Status);
        Assert.Equal(expectedLevel, outcome.NewLevel);
        Assert.Equal(expectedConsumed, outcome.RelicsConsumed);
    }

    // ---- guards -------------------------------------------------------------

    [Fact]
    public void Commit_AtMaxLevel_ReturnsAlreadyAtMax()
    {
        var outcome = UpgradeCommitCalculator.Calculate(
            Request(10, new[] { RelicTier.Legendary }), NeverRolls);

        Assert.Equal(CommitStatus.AlreadyAtMax, outcome.Status);
        Assert.Equal(0, outcome.NewLevel);
        Assert.Equal(0, outcome.RelicsConsumed);
    }

    [Fact]
    public void Commit_BelowMinLevel_ReturnsInvalidModuleLevel()
    {
        var outcome = UpgradeCommitCalculator.Calculate(
            Request(2, new[] { RelicTier.Common }), NeverRolls);

        Assert.Equal(CommitStatus.InvalidModuleLevel, outcome.Status);
    }

    [Fact]
    public void Commit_NoRelics_ReturnsNoRelics()
    {
        var outcome = UpgradeCommitCalculator.Calculate(
            Request(3, Array.Empty<RelicTier>()), NeverRolls);

        Assert.Equal(CommitStatus.NoRelics, outcome.Status);
    }

    [Fact]
    public void Commit_InsufficientRelicsForNextStep_ReturnsInsufficientRelics()
    {
        // L5→L6 costs 2 on the default curve; one relic is not enough.
        var outcome = UpgradeCommitCalculator.Calculate(
            Request(5, new[] { RelicTier.Common }), NeverRolls);

        Assert.Equal(CommitStatus.InsufficientRelics, outcome.Status);
    }

    // ---- best-tier tie-break ------------------------------------------------

    [Fact]
    public void Commit_UsesBestTierAmongConsumedRelics()
    {
        // L5 + 2 relics: consumes both (cost 2). Rare should win over Common.
        var outcome = UpgradeCommitCalculator.Calculate(
            Request(5, new[] { RelicTier.Common, RelicTier.Rare }), NeverRolls);

        Assert.Equal(CommitStatus.Ok, outcome.Status);
        Assert.Equal(RelicTier.Rare, outcome.BestTier);
    }

    [Fact]
    public void Commit_IgnoresTiersOfUnconsumedLeftoverRelics()
    {
        // L6 + 3 relics: greedy walk consumes 2 (L6→L7 costs 2), then L7→L8 costs 3
        // which exceeds the 1 leftover — stops at L7. The Legendary at position 2
        // stays in the Forge and must NOT affect the tier used for the roll.
        var outcome = UpgradeCommitCalculator.Calculate(
            Request(6, new[] { RelicTier.Common, RelicTier.Common, RelicTier.Legendary }), NeverRolls);

        Assert.Equal(2, outcome.RelicsConsumed);
        Assert.Equal(7, outcome.NewLevel);
        Assert.Equal(RelicTier.Common, outcome.BestTier);
    }

    // ---- perk roll — the seam -----------------------------------------------

    [Fact]
    public void PerkRoll_SkipsSilently_WhenAllEligibleSlotsFilled()
    {
        // A Common relic can only fill slot 0. Fill it — the roll skips without attempt.
        var filledSlots = new string[PerkPool.SlotCount];
        filledSlots[0] = "weapon_overclocked_coils";

        var outcome = UpgradeCommitCalculator.Calculate(
            Request(3, new[] { RelicTier.Common }, perkSlots: filledSlots),
            nextRandom: () => 0f);   // rng would otherwise always succeed

        Assert.Equal(CommitStatus.Ok, outcome.Status);
        Assert.False(outcome.RollAttempted);
        Assert.Null(outcome.RolledPerk);
        Assert.Equal(-1, outcome.TargetSlot);
    }

    [Fact]
    public void PerkRoll_MissesGate_WhenRngExceedsChance()
    {
        // rng = 1.0f > any chance → attempted but no perk lands.
        var outcome = UpgradeCommitCalculator.Calculate(
            Request(3, new[] { RelicTier.Common }), nextRandom: () => 1f);

        Assert.Equal(CommitStatus.Ok, outcome.Status);
        Assert.True(outcome.RollAttempted);
        Assert.Null(outcome.RolledPerk);
        Assert.Equal(0.25f, outcome.RollChance);   // Common default
    }

    // The seam is asserted in two halves:
    //   * gate branch — covered here without hitting the pool
    //   * pool-pick branch — needs real PerkPool._pools; skipped below (StatType
    //     initialization requires the game's Assembly-CSharp runtime, not the
    //     reference-only stub).
    [Fact(Skip = "PerkPool._pools static init references StatType, whose class-based " +
        "Enumeration cctor requires the game runtime; the reference-only Assembly-CSharp " +
        "stub throws NRE. Enable by copying real game DLLs to the test bin, or by " +
        "refactoring PerkDefinition.Payload to store stat ids as strings.")]
    public void PerkRoll_UsesInjectedRng_ForBothGateAndPoolPick()
    {
        var outcome = UpgradeCommitCalculator.Calculate(
            Request(3, new[] { RelicTier.Common }, category: ForgeCategory.Weapon),
            nextRandom: () => 0f);

        Assert.Equal("weapon_overclocked_coils", outcome.RolledPerk.Id);
    }

    [Fact(Skip = "See PerkRoll_UsesInjectedRng_ForBothGateAndPoolPick — same StatType init limitation.")]
    public void PerkRoll_RareRelic_LandsInSlotOne()
    {
        var slots = new string[PerkPool.SlotCount];
        slots[0] = "weapon_overclocked_coils";

        var outcome = UpgradeCommitCalculator.Calculate(
            Request(5, new[] { RelicTier.Rare, RelicTier.Rare }, perkSlots: slots),
            nextRandom: () => 0f);

        Assert.Equal(1, outcome.TargetSlot);
    }

    [Fact]
    public void PerkRoll_UnknownCategory_SkipsQuietly()
    {
        // Unknown short-circuits before _pools init runs, so this works in the test
        // env. Rng = 0 would otherwise land a perk — proves the pool-is-empty branch
        // returns rollAttempted=true with no perk.
        var outcome = UpgradeCommitCalculator.Calculate(
            Request(3, new[] { RelicTier.Common }, category: ForgeCategory.Unknown),
            nextRandom: () => 0f);

        Assert.Equal(CommitStatus.Ok, outcome.Status);
        Assert.True(outcome.RollAttempted);
        Assert.Null(outcome.RolledPerk);
    }

    // ---- direct PerkPool tests (independent of the calculator) --------------

    [Theory]
    [InlineData(RelicTier.Common, 0)]      // Common → slot 0 only
    [InlineData(RelicTier.Rare, 1)]        // Rare → slot 0 or 1
    [InlineData(RelicTier.Legendary, 2)]   // Legendary → any slot
    public void TargetSlot_ReturnsHighestReachableForTier(RelicTier tier, int expectedMaxSlot)
    {
        Assert.Equal(expectedMaxSlot, PerkPool.MaxSlotForTier(tier));
    }

    [Fact]
    public void TargetSlot_RareTargetsSlotOne_WhenSlotZeroFilled()
    {
        var slots = new string[] { "some_common_perk", null, null };
        Assert.Equal(1, PerkPool.TargetSlot(slots, RelicTier.Rare));
    }

    [Fact]
    public void TargetSlot_ReturnsMinusOne_WhenAllEligibleSlotsFull()
    {
        var slots = new string[] { "common_perk", null, null };
        Assert.Equal(-1, PerkPool.TargetSlot(slots, RelicTier.Common));
    }
}
