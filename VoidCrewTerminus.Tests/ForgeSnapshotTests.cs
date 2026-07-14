using System.Linq;
using VoidCrewTerminus.Forge;
using Xunit;

namespace VoidCrewTerminus.Tests;

public class ForgeSnapshotTests
{
    [Fact]
    public void Empty_HasVanillaLevelAndAllSlotsFree()
    {
        var snap = ForgeSnapshot.Empty;

        Assert.Equal(ForgeCostCurve.MinLevel, snap.Level);
        Assert.Equal(PerkPool.SlotCount, snap.PerkSlots.Count);
        Assert.All(snap.PerkSlots, s => Assert.Null(s));
    }

    [Fact]
    public void WithLevel_ReturnsNewInstance_LeavesOriginalUntouched()
    {
        var original = ForgeSnapshot.Empty;
        var updated = original.WithLevel(7);

        Assert.NotSame(original, updated);
        Assert.Equal(ForgeCostCurve.MinLevel, original.Level);
        Assert.Equal(7, updated.Level);
    }

    [Fact]
    public void WithLevel_SameValue_ReturnsSameInstance()
    {
        var snap = ForgeSnapshot.Empty;

        Assert.Same(snap, snap.WithLevel(snap.Level));
    }

    [Theory]
    [InlineData(0, ForgeCostCurve.MinLevel)]   // below floor clamps up
    [InlineData(99, ForgeCostCurve.MaxLevel)]  // above ceiling clamps down
    public void WithLevel_ClampsToLegalRange(int requested, int expected)
    {
        Assert.Equal(expected, ForgeSnapshot.Empty.WithLevel(requested).Level);
    }

    [Fact]
    public void WithPerk_ReplacesTargetSlot_LeavesOthersFree()
    {
        var updated = ForgeSnapshot.Empty.WithPerk(1, "some_perk_id");

        Assert.Null(updated.PerkSlots[0]);
        Assert.Equal("some_perk_id", updated.PerkSlots[1]);
        Assert.Null(updated.PerkSlots[2]);
    }

    [Fact]
    public void WithPerk_DoesNotMutateOriginal()
    {
        var original = ForgeSnapshot.Empty.WithPerk(0, "first");
        var chained = original.WithPerk(0, "second");

        Assert.Equal("first", original.PerkSlots[0]);
        Assert.Equal("second", chained.PerkSlots[0]);
    }

    [Fact]
    public void Create_CopiesSourceSlots_SourceMutationDoesNotLeak()
    {
        var source = new string[] { "a", "b", "c" };
        var snap = ForgeSnapshot.Create(5, source);

        source[0] = "MUTATED";

        Assert.Equal("a", snap.PerkSlots[0]);
    }

    [Fact]
    public void Create_ShorterSlotSource_PadsWithNulls()
    {
        var snap = ForgeSnapshot.Create(5, new[] { "only_one" });

        Assert.Equal("only_one", snap.PerkSlots[0]);
        Assert.Null(snap.PerkSlots[1]);
        Assert.Null(snap.PerkSlots[2]);
    }

    [Fact]
    public void Create_NullSlots_ProducesEmptySlots()
    {
        var snap = ForgeSnapshot.Create(5, null);

        Assert.Equal(PerkPool.SlotCount, snap.PerkSlots.Count);
        Assert.All(snap.PerkSlots, s => Assert.Null(s));
    }

    [Fact]
    public void WithPerk_InvalidSlot_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => ForgeSnapshot.Empty.WithPerk(-1, "x"));
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => ForgeSnapshot.Empty.WithPerk(PerkPool.SlotCount, "x"));
    }
}
