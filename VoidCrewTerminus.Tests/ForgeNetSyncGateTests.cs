using System;
using System.Linq;
using VoidCrewTerminus.Forge;
using VoidCrewTerminus.Net;
using Xunit;

namespace VoidCrewTerminus.Tests;

// The four gate rules in ForgeNetSync, which had no coverage at all before the
// transport seam existed — verifying them used to mean launching the game twice
// and reading two log files side by side.
//
// These drive the REAL entry points (BroadcastState, BroadcastDock, …) through a
// recording transport, so the gates are exercised exactly as production does.
// The interface is the test surface.
//
// Not covered here, and honestly so: every handler that resolves a PhotonView or
// calls FindObjectsOfType still needs a live scene (ApplyIncomingCursed,
// ApplyIncomingDock, HandleCommitRequest's box lookup, SendCursedSnapshotTo's
// marker scan). The transport seam does not reach those; only a second port over
// scene lookup would, and one adapter for that is a hypothetical seam.
[Collection(SharedStaticStateCollection.Name)]
public class ForgeNetSyncGateTests : IDisposable
{
    private readonly IForgeTransport _original = ForgeNetSync.Transport;

    public void Dispose() => ForgeNetSync.Transport = _original;

    private static RecordingTransport Install(RecordingTransport t)
    {
        ForgeNetSync.Transport = t;
        return t;
    }

    // ---- gate 1: IsAuthority ---------------------------------------------

    // Solo has to read as authority or single-player would sit waiting for a host
    // that does not exist.
    [Fact]
    public void IsAuthority_TrueSolo()
    {
        Install(RecordingTransport.Solo());
        Assert.True(ForgeNetSync.IsAuthority);
    }

    [Fact]
    public void IsAuthority_FalseOnAClient()
    {
        Install(RecordingTransport.Client());
        Assert.False(ForgeNetSync.IsAuthority);
    }

    // Nulling the transport must not leave the mod unable to answer "am I the
    // authority?" — falling back to offline keeps solo working.
    [Fact]
    public void Transport_NullFallsBackToOffline()
    {
        ForgeNetSync.Transport = null;

        Assert.NotNull(ForgeNetSync.Transport);
        Assert.True(ForgeNetSync.IsAuthority);
    }

    // ---- gate 2: ShouldBroadcast (authority AND peers) -------------------

    [Fact]
    public void Broadcast_SendsToOthers_AsHostWithPeers()
    {
        var t = Install(RecordingTransport.Host());

        ForgeNetSync.BroadcastState();

        var sent = t.Only();
        Assert.Equal(RecordingTransport.Target.Others, sent.Target);
        Assert.Equal(typeof(ForgeStateSyncMessage), sent.Message);
    }

    // Solo must be wire-silent: the whole design leans on "you are the authority
    // and BroadcastState simply no-ops".
    [Fact]
    public void Broadcast_SilentSolo()
    {
        var t = Install(RecordingTransport.Solo());

        ForgeNetSync.BroadcastState();

        Assert.Equal(0, t.Count);
    }

    // A client must never push authoritative state, even with peers present.
    [Fact]
    public void Broadcast_SilentOnAClient()
    {
        var t = Install(RecordingTransport.Client());

        ForgeNetSync.BroadcastState();

        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void BroadcastCommitResult_FollowsTheBroadcastGate()
    {
        var host = Install(RecordingTransport.Host());
        ForgeNetSync.BroadcastCommitResult(42, ForgeSnapshot.Empty.WithLevel(7), 2);
        Assert.Equal(typeof(CommitResultMessage), host.Only().Message);

        var client = Install(RecordingTransport.Client());
        ForgeNetSync.BroadcastCommitResult(42, ForgeSnapshot.Empty.WithLevel(7), 2);
        Assert.Equal(0, client.Count);
    }

    // ---- gate 3: ShouldRelay (peers only, authority irrelevant) ----------
    //
    // The distinction that is easiest to get wrong. Only the player who PLACED a
    // module knows which ViewID its box became, and that player may be a client —
    // so this relay must fire off-authority. Routing it through the broadcast gate
    // would silently drop every overlay placed by a non-host, which is precisely
    // the Phase 8-D bug.

    [Fact]
    public void ModuleOverlay_RelaysFromAClient()
    {
        var t = Install(RecordingTransport.Client());

        ForgeNetSync.BroadcastModuleOverlay(99, ForgeSnapshot.Empty.WithLevel(5));

        var sent = t.Only();
        Assert.Equal(RecordingTransport.Target.Others, sent.Target);
        Assert.Equal(typeof(ModuleOverlayMessage), sent.Message);
    }

    [Fact]
    public void Dock_RelaysFromAClient()
    {
        var t = Install(RecordingTransport.Client());

        ForgeNetSync.BroadcastDock(forgeViewId: 7, itemViewId: 8, anchorIndex: 2, docked: true);

        Assert.Equal(typeof(ForgeDockMessage), t.Only().Message);
    }

    // Still silent solo — a relay with no peers is nothing.
    [Fact]
    public void Relays_SilentSolo()
    {
        var t = Install(RecordingTransport.Solo());

        ForgeNetSync.BroadcastModuleOverlay(99, ForgeSnapshot.Empty.WithLevel(5));
        ForgeNetSync.BroadcastDock(7, 8, 2, true);

        Assert.Equal(0, t.Count);
    }

    // Guards against a bogus ViewID going out even when the gate passes.
    [Theory]
    [InlineData(0, 8)]
    [InlineData(7, 0)]
    [InlineData(-1, 8)]
    public void Dock_RejectsNonPositiveViewIds(int forgeViewId, int itemViewId)
    {
        var t = Install(RecordingTransport.Client());

        ForgeNetSync.BroadcastDock(forgeViewId, itemViewId, 0, true);

        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void ModuleOverlay_RejectsNullSnapshotAndBadViewId()
    {
        var t = Install(RecordingTransport.Client());

        ForgeNetSync.BroadcastModuleOverlay(0, ForgeSnapshot.Empty);
        ForgeNetSync.BroadcastModuleOverlay(99, null);

        Assert.Equal(0, t.Count);
    }

    // ---- gate 4: targeted catch-up (authority, peer count irrelevant) ----

    // The recipient is NAMED, so this must fire even with HasPeers false — gating
    // the catch-up on peer count would break the two-player case entirely, which is
    // the only case that matters for a late joiner.
    [Fact]
    public void CatchUp_SendsStateToTheNamedJoiner_EvenWithoutPeerCount()
    {
        var t = Install(new RecordingTransport { IsAuthority = true, HasPeers = false });

        ForgeNetSync.SendStateTo(actorNumber: 4);

        var sent = t.Only();
        Assert.Equal(RecordingTransport.Target.Peer, sent.Target);
        Assert.Equal(4, sent.ActorNumber);
        Assert.Equal(typeof(ForgeStateSyncMessage), sent.Message);
    }

    // A client must never answer a joiner — only the authority owns the state.
    [Fact]
    public void CatchUp_SilentOnAClient()
    {
        var t = Install(RecordingTransport.Client());

        ForgeNetSync.SendStateTo(4);
        ForgeNetSync.SendOverlaySnapshotTo(4);

        Assert.Equal(0, t.Count);
    }

    // A joiner with forged boxes already in the store gets one overlay push per
    // box, addressed to them rather than broadcast.
    [Fact]
    public void CatchUp_PushesEveryStoredBoxSnapshotToTheJoiner()
    {
        ForgeStateStore.ClearAll();
        try
        {
            ForgeStateStore.SaveSnapshot(11, ForgeSnapshot.Empty.WithLevel(5));
            ForgeStateStore.SaveSnapshot(12, ForgeSnapshot.Empty.WithLevel(9));
            var t = Install(RecordingTransport.Host());

            ForgeNetSync.SendOverlaySnapshotTo(4);

            var overlays = t.OfType<CommitResultMessage>().ToList();
            Assert.Equal(2, overlays.Count);
            Assert.All(overlays, s =>
            {
                Assert.Equal(RecordingTransport.Target.Peer, s.Target);
                Assert.Equal(4, s.ActorNumber);
            });
        }
        finally { ForgeStateStore.ClearAll(); }
    }

    // Nothing stored means nothing sent, rather than an empty push per joiner.
    [Fact]
    public void CatchUp_SendsNoOverlaysWhenTheStoreIsEmpty()
    {
        ForgeStateStore.ClearAll();
        var t = Install(RecordingTransport.Host());

        ForgeNetSync.SendOverlaySnapshotTo(4);

        Assert.Equal(0, t.Count);
    }

    // ---- request hops (client → host, ungated on purpose) ----------------

    [Fact]
    public void AlloySpendRequest_GoesToTheMaster()
    {
        var t = Install(RecordingTransport.Client());

        ForgeNetSync.RequestAlloySpend();

        var sent = t.Only();
        Assert.Equal(RecordingTransport.Target.Master, sent.Target);
        Assert.Equal(typeof(AlloySpendRequestMessage), sent.Message);
    }

    [Fact]
    public void CommitRequest_GoesToTheMaster_CarryingBoxAndRelicIds()
    {
        var t = Install(RecordingTransport.Client());

        ForgeNetSync.RequestCommit(boxViewId: 55, relicViewIds: new[] { 1, 2, 3 });

        var sent = t.Only();
        Assert.Equal(RecordingTransport.Target.Master, sent.Target);
        Assert.Equal(55, Convert.ToInt32(sent.Payload[0]));
        Assert.Equal(new[] { 1, 2, 3 }, (int[])sent.Payload[1]);
    }

    // A commit with no relics loaded must still produce a well-formed payload
    // rather than a null the host has to defend against.
    [Fact]
    public void CommitRequest_NullRelicIdsBecomeAnEmptyArray()
    {
        var t = Install(RecordingTransport.Client());

        ForgeNetSync.RequestCommit(55, null);

        Assert.Empty((int[])t.Only().Payload[1]);
    }

    // HandleCommitRequest's off-authority guard is deliberately NOT tested here.
    // Its body resolves a PhotonView, and a method containing Unity engine calls
    // throws SecurityException at JIT time in this host — before any early return
    // executes. Only a second port over scene lookup would reach it, and a test
    // adapter for that cannot produce real Components, so there is no second
    // adapter to justify the seam.

    // ---- payload shape ---------------------------------------------------

    // Four slots, in the order NetMessages documents. ApplyIncomingState reads
    // them positionally, so a reordering here would desync silently.
    [Fact]
    public void StatePayload_HasFourSlotsInDocumentedOrder()
    {
        var t = Install(RecordingTransport.Host());

        ForgeNetSync.BroadcastState();

        var payload = t.Only().Payload;
        Assert.Equal(4, payload.Length);
        Assert.Equal(ForgeMeterController.DifficultyScalar, Convert.ToInt32(payload[0]));
        Assert.Equal(Escalation.SectorEscalation.BossesDefeated, Convert.ToInt32(payload[1]));
        Assert.Equal(ForgeMeterController.Meter, Convert.ToSingle(payload[2]));
        Assert.Equal(ForgeMeterController.Level, Convert.ToInt32(payload[3]));
    }

    [Fact]
    public void DockPayload_CarriesForgeItemAnchorAndDirection()
    {
        var t = Install(RecordingTransport.Client());

        ForgeNetSync.BroadcastDock(forgeViewId: 7, itemViewId: 8, anchorIndex: -1, docked: false);

        var p = t.Only().Payload;
        Assert.Equal(7, Convert.ToInt32(p[0]));
        Assert.Equal(8, Convert.ToInt32(p[1]));
        Assert.Equal(-1, Convert.ToInt32(p[2]));   // -1 = the module socket
        Assert.False(Convert.ToBoolean(p[3]));
    }

    // ---- inbound state application ---------------------------------------

    // Broadcasts go to Others so the host should never see its own push, but the
    // guard matters: applying it would overwrite authoritative state with a copy
    // of itself and, on a stale message, roll the run backwards.
    [Fact]
    public void ApplyIncomingState_IgnoredOnTheAuthority()
    {
        Install(RecordingTransport.Host());
        ForgeMeterController.ResetForRun();

        ForgeNetSync.ApplyIncomingState(new object[] { 9, 3, 55f, 4 });

        Assert.Equal(0, ForgeMeterController.DifficultyScalar);
    }

    [Fact]
    public void ApplyIncomingState_AppliedOnAClient()
    {
        Install(RecordingTransport.Client());
        ForgeMeterController.ResetForRun();
        try
        {
            ForgeNetSync.ApplyIncomingState(new object[] { 9, 3, 55f, 4 });

            Assert.Equal(9, ForgeMeterController.DifficultyScalar);
            Assert.Equal(55f, ForgeMeterController.Meter);
            Assert.Equal(4, ForgeMeterController.Level);
            Assert.Equal(3, Escalation.SectorEscalation.BossesDefeated);
        }
        finally
        {
            ForgeMeterController.ResetForRun();
            Escalation.SectorEscalation.ResetForRun();
        }
    }

    // A short or absent payload must leave state untouched rather than throw
    // inside a Photon callback or apply a partially-read state.
    [Fact]
    public void ApplyIncomingState_RejectsNullPayload() =>
        AssertStateUnchangedBy(null);

    [Fact]
    public void ApplyIncomingState_RejectsEmptyPayload() =>
        AssertStateUnchangedBy(Array.Empty<object>());

    [Fact]
    public void ApplyIncomingState_RejectsTruncatedPayload() =>
        AssertStateUnchangedBy(new object[] { 9, 3, 55f });   // level slot missing

    private static void AssertStateUnchangedBy(object[] payload)
    {
        ForgeNetSync.Transport = RecordingTransport.Client();
        ForgeMeterController.ResetForRun();

        ForgeNetSync.ApplyIncomingState(payload);

        Assert.Equal(0, ForgeMeterController.DifficultyScalar);
    }
}
