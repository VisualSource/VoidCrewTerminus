using VoidCrewTerminus.Forge;
using VoidCrewTerminus.Loot;
using Xunit;

namespace VoidCrewTerminus.Tests;

// Pure formatting coverage for the tooltip vocabulary.
//
// RewriteMark carries the most risk in the hover feature: vanilla display names
// live inside asset bundles and can't be inspected offline, so the regex has to
// stay correct whether or not a mark is already embedded in the name.
// BuildOverlayBody is deliberately NOT covered here — it reaches into PerkPool,
// which needs StatType initialisation (same reason the existing perk pool tests
// are skipped) and is verified by playtest instead.
public class ForgeLabelsTests
{
    // ---- Mk mapping ------------------------------------------------------

    // Mk number maps directly onto module level so the mod continues vanilla's
    // ladder: L3 is vanilla's cap (Mk III) and forging runs it up to Mk X.
    [Theory]
    [InlineData(3, "Mk III")]
    [InlineData(4, "Mk IV")]
    [InlineData(5, "Mk V")]
    [InlineData(7, "Mk VII")]
    [InlineData(9, "Mk IX")]
    [InlineData(10, "Mk X")]
    public void MarkLabel_maps_level_directly(int level, string expected) =>
        Assert.Equal(expected, ForgeLabels.MarkLabel(level));

    // Vanilla marks are 1-3; the same helper has to render those too since
    // LevelOfBox reports them for non-forgeable boxes.
    [Theory]
    [InlineData(1, "I")]
    [InlineData(2, "II")]
    [InlineData(10, "X")]
    public void Roman_covers_vanilla_marks(int n, string expected) =>
        Assert.Equal(expected, ForgeLabels.Roman(n));

    // ---- RewriteMark: name already carries a mark ------------------------

    [Theory]
    [InlineData("Pulse Laser Mk III")]
    [InlineData("Pulse Laser Mk. III")]
    [InlineData("Pulse Laser MK3")]
    [InlineData("Pulse Laser Mk 3")]
    [InlineData("Pulse Laser mk III")]
    public void RewriteMark_replaces_an_existing_trailing_mark(string header) =>
        Assert.Equal("Pulse Laser Mk VII", ForgeLabels.RewriteMark(header, 7));

    // ---- RewriteMark: name carries no mark -------------------------------

    [Fact]
    public void RewriteMark_appends_when_no_mark_present() =>
        Assert.Equal("Pulse Laser Mk VII", ForgeLabels.RewriteMark("Pulse Laser", 7));

    // A localised name won't match the English-centric regex. It must degrade to
    // a plain append — possibly duplicating a mark, but never corrupting the name.
    [Fact]
    public void RewriteMark_falls_back_to_append_on_unmatched_format() =>
        Assert.Equal("Impulslaser Ausf. 3 Mk VII",
            ForgeLabels.RewriteMark("Impulslaser Ausf. 3", 7));

    [Fact]
    public void RewriteMark_handles_null_and_empty_headers()
    {
        Assert.Equal("Mk VII", ForgeLabels.RewriteMark(null, 7));
        Assert.Equal("Mk VII", ForgeLabels.RewriteMark("", 7));
    }

    // Only a TRAILING mark may be stripped — a mark mid-name is part of the name.
    [Fact]
    public void RewriteMark_only_strips_trailing_marks() =>
        Assert.Equal("Mk III Prototype Mk VII",
            ForgeLabels.RewriteMark("Mk III Prototype", 7));

    // ---- HasOverlay: the "is this worth showing" gate --------------------

    // An untouched module must render byte-identical to vanilla. This is also the
    // path a client takes before forge state syncs, so it must stay silent rather
    // than display a wrong level.
    [Fact]
    public void HasOverlay_false_for_untouched_module() =>
        Assert.False(ForgeLabels.HasOverlay(3, new[] { "", "", "" }, new BurdenType[0]));

    [Fact]
    public void HasOverlay_true_above_base_level() =>
        Assert.True(ForgeLabels.HasOverlay(4, new[] { "", "" }, new BurdenType[0]));

    [Fact]
    public void HasOverlay_true_when_a_perk_is_filled() =>
        Assert.True(ForgeLabels.HasOverlay(3, new[] { "", "perk_x" }, new BurdenType[0]));

    // A burden alone counts: a cursed commit can land a burden without raising
    // the level, and that must still be visible to the crew.
    [Fact]
    public void HasOverlay_true_when_a_burden_is_present() =>
        Assert.True(ForgeLabels.HasOverlay(3, new[] { "" },
            new[] { BurdenType.RandomShutoff }));

    [Fact]
    public void HasOverlay_tolerates_nulls() =>
        Assert.False(ForgeLabels.HasOverlay(3, null, null));

    // ---- Display names ---------------------------------------------------

    [Fact]
    public void BurdenName_is_human_readable() =>
        Assert.Equal("Random Shutoff", ForgeLabels.BurdenName(BurdenType.RandomShutoff));

    [Theory]
    [InlineData(RelicTier.Common, "Common")]
    [InlineData(RelicTier.Rare, "Rare")]
    [InlineData(RelicTier.Legendary, "Legendary")]
    public void TierName_covers_every_tier(RelicTier tier, string expected) =>
        Assert.Equal(expected, ForgeLabels.TierName(tier));

    // ---- Relic body ------------------------------------------------------

    [Fact]
    public void BuildRelicBody_labels_forge_tier_not_rarity()
    {
        var body = ForgeLabels.BuildRelicBody(RelicTier.Rare, BurdenType.None);
        Assert.Contains("FORGE TIER: Rare", body);
        // Must not claim to be a rarity — vanilla already shows its own, authored
        // on different criteria and with a different shape (it includes Epic).
        Assert.DoesNotContain("RARITY", body);
    }

    [Fact]
    public void BuildRelicBody_omits_curse_line_when_uncursed() =>
        Assert.DoesNotContain("CURSED",
            ForgeLabels.BuildRelicBody(RelicTier.Common, BurdenType.None));

    // Curse text names the burden with no odds and no hedge: only the FIRST
    // cursed relic in a commit contributes, so a per-relic probability would be
    // wrong whenever it isn't first, and the tooltip can't see the rest of the tube.
    [Fact]
    public void BuildRelicBody_names_the_burden_without_odds()
    {
        var body = ForgeLabels.BuildRelicBody(RelicTier.Legendary, BurdenType.RandomShutoff);
        Assert.Contains("CURSED: Random Shutoff", body);
        Assert.DoesNotContain("%", body);
        Assert.DoesNotContain("chance", body);
    }

    // ---- Plural ----------------------------------------------------------

    [Theory]
    [InlineData(0, "0 relics")]
    [InlineData(1, "1 relic")]
    [InlineData(2, "2 relics")]
    public void Plural_only_singularises_exactly_one(int count, string expected) =>
        Assert.Equal(expected, ForgeLabels.Plural(count, "relic"));

    // ---- DescribeCommit --------------------------------------------------
    //
    // The in-world commit button and the !forgecommit dev command used to hold
    // separate copies of this switch, which had drifted apart on every arm. These
    // tests pin the single vocabulary so the copies can't come back.
    //
    // Perk-bearing outcomes aren't constructed here: CommitOutcome.Success needs a
    // PerkDefinition, whose payload type touches StatType — the same static-init
    // limitation that skips the perk-pool tests. The no-perk and roll-failed
    // branches are covered, which is every branch that doesn't read a perk's name.

    private static CommitOutcome OkOutcome(int newLevel, int consumed) =>
        CommitOutcome.Success(newLevel, consumed, RelicTier.Common,
            rolledPerk: null, targetSlot: -1, rollChance: 0f, rollAttempted: false);

    [Fact]
    public void DescribeCommit_success_reports_level_consumption_and_remainder()
    {
        var lines = ForgeLabels.DescribeCommit(OkOutcome(7, 2), currentLevel: 5, relicsRemaining: 1);

        Assert.Single(lines);
        Assert.Contains("L7", lines[0]);
        Assert.Contains("consumed 2 relics", lines[0]);
        Assert.Contains("1 remaining", lines[0]);
        // The upgrade only lands when the box is rebuilt into a socket; a commit
        // line that omits this reads as though the module is already improved.
        Assert.Contains("Rebuild", lines[0]);
    }

    [Fact]
    public void DescribeCommit_success_singularises_a_one_relic_commit() =>
        Assert.Contains("consumed 1 relic,",
            ForgeLabels.DescribeCommit(OkOutcome(4, 1), 3, 0)[0]);

    // A commit with no eligible slot must not announce a non-event.
    [Fact]
    public void DescribeCommit_success_stays_silent_when_no_roll_was_attempted() =>
        Assert.Single(ForgeLabels.DescribeCommit(OkOutcome(7, 2), 5, 0));

    // A roll that fired and lost IS worth reporting — the crew spent relics on it.
    [Fact]
    public void DescribeCommit_success_reports_a_failed_roll()
    {
        var outcome = CommitOutcome.Success(7, 2, RelicTier.Rare,
            rolledPerk: null, targetSlot: -1, rollChance: 0.4f, rollAttempted: true);

        var lines = ForgeLabels.DescribeCommit(outcome, 5, 0);

        Assert.Equal(2, lines.Count);
        Assert.Contains("No perk this time", lines[1]);
        Assert.Contains("Rare", lines[1]);
    }

    [Fact]
    public void DescribeCommit_insufficient_relics_names_the_cost_and_the_holding()
    {
        // L3→L4 costs 1 on the default curve, so hold 0 to fail it.
        var lines = ForgeLabels.DescribeCommit(
            CommitOutcome.Failure(CommitStatus.InsufficientRelics),
            currentLevel: ForgeCostCurve.MinLevel, relicsRemaining: 0);

        Assert.Single(lines);
        Assert.Contains($"requires {ForgeCostCurve.CostForNextLevel(ForgeCostCurve.MinLevel)} relics", lines[0]);
        Assert.Contains("holds 0", lines[0]);
    }

    // The max level is a const on the cost curve; the old copies of this message
    // hardcoded "L10" in two places instead.
    [Fact]
    public void DescribeCommit_already_at_max_reads_the_cap_from_the_curve() =>
        Assert.Contains($"L{ForgeCostCurve.MaxLevel}",
            ForgeLabels.DescribeCommit(CommitOutcome.Failure(CommitStatus.AlreadyAtMax), 10, 0)[0]);

    // Every status must produce exactly one non-empty line, or a commit can fail
    // in-world with no feedback at all.
    [Theory]
    [InlineData(CommitStatus.NoModule)]
    [InlineData(CommitStatus.NoRelics)]
    [InlineData(CommitStatus.AlreadyAtMax)]
    [InlineData(CommitStatus.InvalidModuleLevel)]
    [InlineData(CommitStatus.InsufficientRelics)]
    [InlineData(CommitStatus.MissingViewId)]
    public void DescribeCommit_every_failure_says_something(CommitStatus status)
    {
        var lines = ForgeLabels.DescribeCommit(CommitOutcome.Failure(status), 5, 2);

        Assert.Single(lines);
        Assert.False(string.IsNullOrWhiteSpace(lines[0]));
    }
}
