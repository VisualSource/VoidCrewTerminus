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
}
