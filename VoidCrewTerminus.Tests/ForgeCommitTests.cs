using System;
using VoidCrewTerminus.Forge;
using VoidCrewTerminus.Loot;
using Xunit;

namespace VoidCrewTerminus.Tests;

// The two halves of a commit that are not the calculator: the projection of relic
// facts into a request, and the fold of the outcome back into the module's
// overlay. Both used to sit inside a static method on a MonoBehaviour, between a
// heavily-tested calculator and a playtested game, reachable from neither.
//
// Defaults come from the shipped config values (curve 1,1,2,2,3,3,4; burden
// chance 0.75) since TerminusConfig is not initialised in the test host — the
// same assumption UpgradeCommitCalculatorTests documents.
//
// No [Collection]: nothing here writes a static. Resolve is pure, and
// ForgeCommit.Execute — which reads the scene, saves and broadcasts — is never
// called from a test.
public class ForgeCommitTests
{
    private const string ExistingPerk = "weapon_overclocked_coils";

    private static RelicFacts Relic(RelicTier tier = RelicTier.Common,
        string name = "Relic_Test", BurdenType curse = BurdenType.None) =>
        new(tier, name, curse);

    // The calculator draws in a fixed order: the perk gate first, then — only when
    // a CONSUMED relic is cursed — the burden gate. Anything past the end repeats
    // the last value.
    private static Func<float> Draws(params float[] values)
    {
        int i = 0;
        return () => values[Math.Min(i++, values.Length - 1)];
    }

    private const float GateMisses = 1f;   // >= any chance
    private const float GateLands = 0f;    // < any chance

    // ---- the outcome reaches the snapshot ----------------------------------

    [Fact]
    public void Resolve_saves_the_level_the_outcome_reports()
    {
        var resolution = ForgeCommit.Resolve(
            ForgeSnapshot.Empty, currentLevel: 3, ForgeCategory.Unknown,
            new[] { Relic(), Relic() }, Draws(GateMisses));

        Assert.Equal(CommitStatus.Ok, resolution.Outcome.Status);
        Assert.Equal(5, resolution.Outcome.NewLevel);   // L3 + 2 relics on the default curve
        Assert.Equal(5, resolution.Updated.Level);
    }

    // Nothing is half-applied. A refused commit hands back the snapshot it was
    // given, so a caller that saves unconditionally still cannot corrupt state.
    [Fact]
    public void Resolve_leaves_the_snapshot_untouched_when_the_commit_fails()
    {
        var current = ForgeSnapshot.Create(5, new[] { ExistingPerk, null, null });

        var resolution = ForgeCommit.Resolve(
            current, currentLevel: 5, ForgeCategory.Unknown,
            Array.Empty<RelicFacts>(), Draws(GateMisses));

        Assert.Equal(CommitStatus.NoRelics, resolution.Outcome.Status);
        Assert.Same(current, resolution.Updated);
    }

    // A commit edits the overlay; it does not replace it. What the module earned
    // in earlier commits has to survive the fold.
    [Fact]
    public void Resolve_keeps_an_existing_perk_across_a_level_up()
    {
        var current = ForgeSnapshot.Create(3, new[] { ExistingPerk, null, null });

        var resolution = ForgeCommit.Resolve(
            current, currentLevel: 3, ForgeCategory.Unknown,
            new[] { Relic() }, Draws(GateMisses));

        Assert.Equal(4, resolution.Updated.Level);
        Assert.Equal(ExistingPerk, resolution.Updated.PerkSlots[0]);
    }

    // The third fold — writing a rolled perk into outcome.TargetSlot — has no test
    // here and cannot have one yet. Reaching it needs a non-null RolledPerk, and
    // there are two independent walls: PerkPool's pools need StatType, whose
    // class-based Enumeration cctor requires the game runtime (see the skips in
    // UpgradeCommitCalculatorTests), and PerkDefinition's own constructor names
    // StatType in its params signature, so the test project cannot even build one
    // by hand without a compile reference to Assembly-CSharp. ForgeSnapshot.WithPerk
    // is covered directly in ForgeSnapshotTests; what stays unproven is that this
    // path passes it the right slot, and only when a perk actually landed.

    // ---- relic facts reach the calculator in order -------------------------

    // Position is meaning: the tier driving the roll comes from the relics the cost
    // curve actually consumed, in order. RelicFacts keeps each relic's three facts
    // together precisely so that alignment cannot drift.
    [Fact]
    public void Resolve_reads_the_best_tier_from_the_consumed_relics_only()
    {
        // L6 + 3 relics: the walk consumes 2 (L6→L7 costs 2) and stops — L7→L8
        // needs 3. The Legendary sits at position 2 and is never spent.
        var resolution = ForgeCommit.Resolve(
            ForgeSnapshot.Empty, currentLevel: 6, ForgeCategory.Unknown,
            new[] { Relic(), Relic(), Relic(RelicTier.Legendary) }, Draws(GateMisses));

        Assert.Equal(2, resolution.Outcome.RelicsConsumed);
        Assert.Equal(RelicTier.Common, resolution.Outcome.BestTier);
    }

    // A relic destroyed between docking and committing — grabbed by another
    // player, despawned by the network — still holds its place in the tube and
    // still pays for the level. Preserved behaviour, recorded here because it is
    // the sort of thing that reads like an oversight when you meet it in the wild.
    [Fact]
    public void Resolve_counts_a_missing_relic_but_reads_nothing_off_it()
    {
        var resolution = ForgeCommit.Resolve(
            ForgeSnapshot.Empty, currentLevel: 3, ForgeCategory.Unknown,
            new[] { RelicFacts.Missing }, Draws(GateMisses));

        Assert.Equal(CommitStatus.Ok, resolution.Outcome.Status);
        Assert.Equal(4, resolution.Updated.Level);
        Assert.Equal(RelicTier.Common, resolution.Outcome.BestTier);
        Assert.Empty(resolution.Updated.Burdens);
    }

    // ---- burdens ------------------------------------------------------------

    [Fact]
    public void Resolve_applies_a_burden_from_a_consumed_cursed_relic()
    {
        var resolution = ForgeCommit.Resolve(
            ForgeSnapshot.Empty, currentLevel: 3, ForgeCategory.Unknown,
            new[] { Relic(curse: BurdenType.RandomShutoff) },
            Draws(GateMisses, GateLands));   // perk misses, burden lands

        Assert.Equal(BurdenType.RandomShutoff, resolution.Outcome.AppliedBurden);
        Assert.Equal(new[] { BurdenType.RandomShutoff }, resolution.Updated.Burdens);
    }

    // The curse rides the relic, not the commit: a cursed relic left over in the
    // tubes was never spent and cannot burden the module.
    [Fact]
    public void Resolve_ignores_a_curse_on_a_relic_the_commit_never_consumed()
    {
        // Same L6 + 3 shape as above — position 2 is the leftover.
        var resolution = ForgeCommit.Resolve(
            ForgeSnapshot.Empty, currentLevel: 6, ForgeCategory.Unknown,
            new[] { Relic(), Relic(), Relic(curse: BurdenType.RandomShutoff) },
            Draws(GateMisses, GateLands));

        Assert.Equal(BurdenType.None, resolution.Outcome.AppliedBurden);
        Assert.Empty(resolution.Updated.Burdens);
    }

    // Burdens stack by type, never by count: a module that shuts off randomly
    // cannot come to shut off randomly twice.
    [Fact]
    public void Resolve_does_not_duplicate_a_burden_the_module_already_carries()
    {
        var current = ForgeSnapshot.Create(3, null, new[] { BurdenType.RandomShutoff });

        var resolution = ForgeCommit.Resolve(
            current, currentLevel: 3, ForgeCategory.Unknown,
            new[] { Relic(curse: BurdenType.RandomShutoff) },
            Draws(GateMisses, GateLands));

        Assert.Equal(BurdenType.RandomShutoff, resolution.Outcome.AppliedBurden);
        Assert.Equal(new[] { BurdenType.RandomShutoff }, resolution.Updated.Burdens);
        Assert.Equal(4, resolution.Updated.Level);
    }
}
