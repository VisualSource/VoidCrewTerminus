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

    // ---- burdens (Phase 7-C) --------------------------------------------

    [Fact]
    public void Empty_HasNoBurdens()
    {
        Assert.Empty(ForgeSnapshot.Empty.Burdens);
    }

    [Fact]
    public void WithBurdenAdded_AddsToBurdenSet()
    {
        var updated = ForgeSnapshot.Empty.WithBurdenAdded(BurdenType.RandomShutoff);

        Assert.Contains(BurdenType.RandomShutoff, updated.Burdens);
        Assert.Empty(ForgeSnapshot.Empty.Burdens);   // original unchanged
    }

    [Fact]
    public void WithBurdenAdded_None_IsNoOp()
    {
        var snap = ForgeSnapshot.Empty;
        Assert.Same(snap, snap.WithBurdenAdded(BurdenType.None));
    }

    [Fact]
    public void WithBurdenAdded_SameType_Idempotent_ReturnsSameInstance()
    {
        var once = ForgeSnapshot.Empty.WithBurdenAdded(BurdenType.RandomShutoff);
        var twice = once.WithBurdenAdded(BurdenType.RandomShutoff);

        Assert.Same(once, twice);
        Assert.Single(twice.Burdens);
    }

    [Fact]
    public void WithLevel_PreservesBurdens()
    {
        var withBurden = ForgeSnapshot.Empty
            .WithLevel(5)
            .WithBurdenAdded(BurdenType.RandomShutoff);

        var relevelled = withBurden.WithLevel(7);

        Assert.Equal(7, relevelled.Level);
        Assert.Contains(BurdenType.RandomShutoff, relevelled.Burdens);
    }

    [Fact]
    public void WithPerk_PreservesBurdens()
    {
        var withBurden = ForgeSnapshot.Empty.WithBurdenAdded(BurdenType.RandomShutoff);
        var withPerk = withBurden.WithPerk(0, "some_perk");

        Assert.Contains(BurdenType.RandomShutoff, withPerk.Burdens);
        Assert.Equal("some_perk", withPerk.PerkSlots[0]);
    }

    [Fact]
    public void Create_DedupsBurdens_DropsNone()
    {
        var snap = ForgeSnapshot.Create(5, null, new[]
        {
            BurdenType.RandomShutoff,
            BurdenType.None,             // dropped
            BurdenType.RandomShutoff,    // deduped
        });

        Assert.Single(snap.Burdens);
        Assert.Equal(BurdenType.RandomShutoff, snap.Burdens[0]);
    }

    // ---- wire form -------------------------------------------------------
    //
    // These are the tests the net layer never had. The payload layout used to be
    // hand-rolled in three places inside ForgeNetSync, so adding a field here
    // compiled fine and silently dropped it on the wire — producing a client
    // rendering a stale overlay, the hardest class of bug in this mod to
    // diagnose.
    //
    // The round-trip test alone would NOT catch that: it asserts the fields it
    // already knows about, which is exactly how the hand-rolled copies stayed
    // green while dropping state. ToPayload_CarriesEveryPublicSnapshotField is
    // the actual guard — it fails on a field nobody taught the codec about.

    // Fails the moment ForgeSnapshot gains (or loses) a public instance property.
    // Fixing it means three things in lock-step: carry the field in ToPayload,
    // restore it in TryFromPayload, and assert it in the round-trip test below.
    [Fact]
    public void ToPayload_CarriesEveryPublicSnapshotField()
    {
        var carriedByCodec = new[]
        {
            nameof(ForgeSnapshot.Level),
            nameof(ForgeSnapshot.PerkSlots),
            nameof(ForgeSnapshot.Burdens),
        };

        var declared = typeof(ForgeSnapshot)
            .GetProperties(System.Reflection.BindingFlags.Public |
                           System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name);

        Assert.Equal(carriedByCodec.OrderBy(n => n), declared.OrderBy(n => n));
    }

    [Fact]
    public void ToPayload_RoundTrips_EveryField()
    {
        var original = ForgeSnapshot.Create(7, new[] { "perk_a", null, "perk_c" },
            new[] { BurdenType.RandomShutoff });

        var ok = ForgeSnapshot.TryFromPayload(
            original.ToPayload(viewId: 4242, relicsConsumed: 3),
            out int viewId, out var decoded, out int relicsConsumed);

        Assert.True(ok);
        Assert.Equal(4242, viewId);
        Assert.Equal(3, relicsConsumed);
        Assert.Equal(original.Level, decoded.Level);
        Assert.Equal(original.PerkSlots, decoded.PerkSlots);
        Assert.Equal(original.Burdens, decoded.Burdens);
    }

    // Empty slots cross the wire as "" and must come back as null. Both spellings
    // read as empty elsewhere in the mod, but only one may cross — the two decode
    // sites used to disagree about this (the commit-result path normalised, the
    // module-overlay path did not).
    [Fact]
    public void TryFromPayload_NormalisesEmptyStringSlotsBackToNull()
    {
        var payload = ForgeSnapshot.Empty.WithPerk(1, "only_middle").ToPayload(1, 0);

        Assert.All((string[])payload[2], s => Assert.NotNull(s)); // no nulls on the wire

        Assert.True(ForgeSnapshot.TryFromPayload(payload, out _, out var decoded, out _));
        Assert.Null(decoded.PerkSlots[0]);
        Assert.Equal("only_middle", decoded.PerkSlots[1]);
        Assert.Null(decoded.PerkSlots[2]);
    }

    [Fact]
    public void ToPayload_HasDeclaredLength()
    {
        Assert.Equal(ForgeSnapshot.PayloadLength, ForgeSnapshot.Empty.ToPayload(1, 0).Length);
    }

    // An arity check is the only validation the wire has ever had. A short or
    // absent payload must leave every out parameter safe rather than throw into
    // a Photon callback.
    [Fact]
    public void TryFromPayload_RejectsNullOrShortPayload_WithSafeDefaults()
    {
        Assert.False(ForgeSnapshot.TryFromPayload(null, out int viewId, out var snap, out int consumed));
        Assert.Equal(0, viewId);
        Assert.Equal(0, consumed);
        Assert.Same(ForgeSnapshot.Empty, snap);

        Assert.False(ForgeSnapshot.TryFromPayload(
            new object[] { 1, 5, new[] { "a" } }, out _, out _, out _));
    }

    // Decode routes through Create, so wire values that violate the snapshot's
    // own invariants are corrected rather than trusted — a desynced or tampered
    // peer can't push a module to L99.
    [Fact]
    public void TryFromPayload_ClampsLevelAndDedupsBurdens()
    {
        var payload = new object[]
        {
            7, 99,
            new[] { "", "", "" },
            new[] { (int)BurdenType.RandomShutoff, (int)BurdenType.RandomShutoff, (int)BurdenType.None },
            0,
        };

        Assert.True(ForgeSnapshot.TryFromPayload(payload, out _, out var snap, out _));
        Assert.Equal(ForgeCostCurve.MaxLevel, snap.Level);
        Assert.Single(snap.Burdens);
    }

    // Photon hands back boxed primitives that need not be the exact CLR type the
    // sender wrote, which is why decode goes through Convert rather than a cast.
    [Fact]
    public void TryFromPayload_AcceptsWidenedNumericSlots()
    {
        var payload = new object[]
        {
            (short)11, (long)6, new[] { "", "", "" }, new int[0], (byte)2,
        };

        Assert.True(ForgeSnapshot.TryFromPayload(payload, out int viewId, out var snap, out int consumed));
        Assert.Equal(11, viewId);
        Assert.Equal(6, snap.Level);
        Assert.Equal(2, consumed);
    }
}
