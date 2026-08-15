using System;
using System.Collections.Generic;
using CG.Ship.Modules;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using VoidCrewTerminus.Escalation;
using VoidCrewTerminus.Forge;
using VoidCrewTerminus.Loot;
using VoidManager.ModMessages;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Net;

// Phase 8-A — host-authoritative sync of the meter / escalation state
// (DifficultyScalar, BossesDefeated, Meter, Level) plus the client→host alloy
// spend hop.
//
// Authority == Photon master client, and ALSO true in solo/offline play, so
// single-player is unchanged: you are the authority, and BroadcastState simply
// no-ops with no one to send to. Which of those you are is the transport's
// business (see IForgeTransport) — this class only composes gates out of it.
//
// The escalation increment hooks (ForgeSectorHook, BossDefeatHook), the alloy
// spend, the per-run reset, and the dev setters all run ONLY on the authority,
// which then calls BroadcastState(); every other client is a pure receiver that
// applies whatever the host sends. A late joiner gets a targeted snapshot on
// join. On host migration authority is re-derived live from IsMasterClient (no
// stored role to flip), so the new master's hooks just start acting — we only
// re-assert current state so nobody is briefly stale.
internal sealed class ForgeNetSync : IInRoomCallbacks
{
    private static readonly ForgeNetSync _callbacks = new();
    private static bool _initialized;

    // Whether our IInRoomCallbacks target is currently attached to PUN.
    private static bool _registered;

    // Stored so Shutdown can unsubscribe — a bare lambda can't be removed, and a
    // leaked handler would survive ScriptEngine hot-reload into the new assembly.
    private static EventHandler _onJoinedRoom;
    private static EventHandler _onLeftRoom;

    // The network underneath us. Starts OFFLINE and is swapped to the PUN adapter
    // only once we are genuinely in a room — see OfflineTransport for why that
    // ordering is load-bearing rather than cosmetic. Tests install a fake.
    private static IForgeTransport _transport = OfflineTransport.Instance;

    internal static IForgeTransport Transport
    {
        get => _transport;
        set => _transport = value ?? OfflineTransport.Instance;
    }

    // ---- gates ------------------------------------------------------------
    //
    // Four distinct rules, stated together so the differences are visible. Each is
    // a composition of the two facts the transport reports:
    //
    //   IsAuthority     — we own the state. Read by callers OUTSIDE this class
    //                     (DoCommit, ForgeSectorHook) to decide whether to act
    //                     locally or ask the host. True solo.
    //   ShouldBroadcast — an authority-originated push. Silent solo (no peers) and
    //                     silent on clients (not authority).
    //   ShouldRelay     — a push whose originator need NOT be the authority: the
    //                     player who PLACED a module announces its overlay, and
    //                     that player may be a client. Relaying already-
    //                     authoritative state, not deciding anything new.
    //   Targeted sends  — the late-joiner catch-up. Authority-only; the recipient
    //                     is named, so peer count is irrelevant.
    internal static bool IsAuthority => _transport.IsAuthority;

    private static bool ShouldBroadcast => _transport.IsAuthority && _transport.HasPeers;

    private static bool ShouldRelay => _transport.HasPeers;

    // Init runs from BepInEx plugin Awake, which is FAR earlier than the game's
    // own Photon setup — the chainloader finishes before "Starting photon
    // connect". Calling PhotonNetwork.AddCallbackTarget here forces PUN's static
    // initializer to construct the LoadBalancingClient before the game has
    // applied its ServerSettings, which leaves matchmaking unable to create a
    // lobby (region list renders as raw codes, status hangs on "connecting").
    //
    // So this method must touch NOTHING in Photon. We subscribe to VoidManager's
    // room events instead — Events.Instance is safe at Awake, it was already
    // being used there before any of this net code existed — and only attach the
    // PUN callback target once we're genuinely in a room, long after the game has
    // configured Photon itself.
    internal static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        _onJoinedRoom = (_, _) => RegisterCallbacks();
        _onLeftRoom = (_, _) => UnregisterCallbacks();

        // Both paths are covered because a host and a joining client don't
        // necessarily raise the same event; RegisterCallbacks is idempotent, so
        // overlapping delivery is harmless.
        VoidManager.Events.Instance.JoinedRoom += _onJoinedRoom;
        VoidManager.Events.Instance.HostCreateRoom += _onJoinedRoom;
        VoidManager.Events.Instance.LeftRoom += _onLeftRoom;
    }

    private static void RegisterCallbacks()
    {
        if (_registered) return;
        _registered = true;
        // First touch of PhotonNetwork in the plugin's whole lifetime, and it
        // happens here — inside a room event — on purpose.
        _transport = PunTransport.Instance;
        PhotonNetwork.AddCallbackTarget(_callbacks);
        BepinPlugin.Log?.LogDebug("[Net] PUN callback target attached (in room).");
    }

    private static void UnregisterCallbacks()
    {
        if (!_registered) return;
        _registered = false;
        PhotonNetwork.RemoveCallbackTarget(_callbacks);
        _transport = OfflineTransport.Instance;
        ClearPending();
        BepinPlugin.Log?.LogDebug("[Net] PUN callback target detached (left room).");
    }

    internal static void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;

        if (_onJoinedRoom != null)
        {
            VoidManager.Events.Instance.JoinedRoom -= _onJoinedRoom;
            VoidManager.Events.Instance.HostCreateRoom -= _onJoinedRoom;
            _onJoinedRoom = null;
        }
        if (_onLeftRoom != null)
        {
            VoidManager.Events.Instance.LeftRoom -= _onLeftRoom;
            _onLeftRoom = null;
        }

        UnregisterCallbacks();
        ClearPending();
    }

    // Both buffers, not just the cursed one. ViewIDs are scoped to a room, so a
    // module overlay left buffered across a room change would eventually be
    // applied to whatever unrelated object inherits that ID.
    private static void ClearPending()
    {
        _pendingCursed.Clear();
        _pendingModuleOverlay.Clear();
    }

    // ---- outbound (authority → clients) -----------------------------------

    internal static void BroadcastState()
    {
        if (!ShouldBroadcast) return;
        var args = StatePayload();
        _transport.SendToOthers(typeof(ForgeStateSyncMessage), args);
        BepinPlugin.Log?.LogDebug($"[Net] → sent forge state {Describe(args)} to all.");
    }

    // internal, not private: this and SendOverlaySnapshotTo are the two catch-up
    // pushes free of Unity calls, so they are the only way to exercise the
    // targeted-send gate from a test. See ForgeNetSyncGateTests.
    internal static void SendStateTo(int actorNumber)
    {
        if (!IsAuthority) return;
        var args = StatePayload();
        _transport.SendToPeer(actorNumber, typeof(ForgeStateSyncMessage), args);
        BepinPlugin.Log?.LogDebug($"[Net] → sent forge state {Describe(args)} to joiner #{actorNumber}.");
    }

    private static object[] StatePayload() => new object[]
    {
        ForgeMeterController.DifficultyScalar,
        SectorEscalation.BossesDefeated,
        ForgeMeterController.Meter,
        ForgeMeterController.Level,
    };

    private static string Describe(object[] a) =>
        $"{{scalar={a[0]}, bosses={a[1]}, meter={Convert.ToSingle(a[2]):0.#}, level={a[3]}}}";

    // ---- inbound (client applies host state) ------------------------------

    internal static void ApplyIncomingState(object[] a)
    {
        if (a == null || a.Length < 4) return;
        // The authority never applies pushed state (broadcasts go to Others, so
        // this shouldn't fire on the host — but guard anyway).
        if (IsAuthority) return;

        int scalar = Convert.ToInt32(a[0]);
        int bosses = Convert.ToInt32(a[1]);
        float meter = Convert.ToSingle(a[2]);
        int level = Convert.ToInt32(a[3]);

        ForgeMeterController.ApplyNetworkState(scalar, meter, level);
        SectorEscalation.ApplyNetworkBosses(bosses);
        BepinPlugin.Log?.LogDebug(
            $"[Net] ← applied forge state {{scalar={scalar}, bosses={bosses}, meter={meter:0.#}, level={level}}}.");
    }

    // ---- alloy spend hop (client → host) ----------------------------------

    internal static void RequestAlloySpend()
    {
        _transport.SendToMaster(typeof(AlloySpendRequestMessage), Array.Empty<object>());
        BepinPlugin.Log?.LogDebug("[Net] → sent alloy-spend request to host.");
    }

    internal static void HandleAlloySpendRequest(int senderActor)
    {
        if (!IsAuthority) return;
        bool ok = ForgeMeterController.TrySpendAlloys(out string message);
        BepinPlugin.Log?.LogDebug(
            $"[Net] ← alloy-spend request from #{senderActor}: {(ok ? "spent" : message)}");
        if (ok) BroadcastState(); // push the new meter/level to everyone incl. the requester

        // The requester's own local TrySpendAlloys call already returned before
        // the host ever saw this request (it just fired RequestAlloySpend and gave
        // up), so BroadcastState alone leaves them with no explanation on success
        // and nothing at all on failure. Tell them directly what happened.
        SendAlloySpendResultTo(senderActor, ok,
            ok ? $"Alloys spent — {ForgeMeterController.Describe()}" : message);
    }

    // Host → requester only (not a broadcast — SendToOthers would tell every
    // OTHER client about a request that wasn't theirs).
    private static void SendAlloySpendResultTo(int actorNumber, bool ok, string message)
    {
        if (!IsAuthority) return;
        _transport.SendToPeer(actorNumber, typeof(AlloySpendResultMessage), new object[] { ok, message });
        BepinPlugin.Log?.LogDebug(
            $"[Net] → sent alloy-spend result to #{actorNumber}: {(ok ? "ok" : "failed")} ({message}).");
    }

    // Client: surface the host's outcome. The host resolved this locally too
    // (TrySpendAlloys already ran there), so it never sends itself a result.
    internal static void ApplyIncomingAlloySpendResult(object[] a)
    {
        if (IsAuthority) return;
        if (a == null || a.Length < 2) return;
        bool ok = a[0] is bool flag && flag;
        string message = a[1] as string;
        if (!string.IsNullOrEmpty(message)) Messaging.Notification(message);
        BepinPlugin.Log?.LogDebug($"[Net] ← applied alloy-spend result: {(ok ? "ok" : "failed")} ({message}).");
    }

    // ---- cursed relic sync (Phase 8-B) ------------------------------------
    //
    // Cursed state is host-authoritative (rolled at spawn in CursedRelicSpawnPatch)
    // and purely for client AWARENESS — 8-C's authoritative commit reads the host's
    // own markers, so a client mis-seeing cursed can't change an outcome. Relics
    // are keyed by PhotonView.ViewID. A live broadcast can beat the relic's own
    // instantiation on the client, so unresolved ViewIDs are buffered and drained
    // from the client's OnPhotonInstantiate (see CursedRelicSpawnPatch).

    // ViewID → burden, for cursed flags that arrived before the object existed.
    private static readonly PendingByViewId<BurdenType> _pendingCursed = new();

    // Both cursed messages carry parallel arrays so a single live flag and a whole
    // joiner snapshot share one wire shape.
    private static object[] CursedPayload(int[] viewIds, int[] burdens) =>
        new object[] { viewIds, burdens };

    // Host: announce one freshly-cursed relic to all clients.
    internal static void BroadcastCursed(PhotonView pv, BurdenType burden)
    {
        if (!ShouldBroadcast) return;
        if (pv == null || pv.ViewID <= 0) return;
        _transport.SendToOthers(typeof(CursedRelicMessage),
            CursedPayload(new[] { pv.ViewID }, new[] { (int)burden }));
        BepinPlugin.Log?.LogDebug($"[Net] → sent cursed relic viewID={pv.ViewID} ({burden}) to all.");
    }

    // Host: full cursed set for a joining player.
    private static void SendCursedSnapshotTo(int actorNumber)
    {
        if (!IsAuthority) return;

        var ids = new List<int>();
        var burdens = new List<int>();
        foreach (var marker in UnityEngine.Object.FindObjectsOfType<CursedRelicMarker>())
        {
            var pv = marker != null ? marker.GetComponent<PhotonView>() : null;
            if (pv == null || pv.ViewID <= 0) continue;
            ids.Add(pv.ViewID);
            burdens.Add((int)marker.BakedBurden);
        }
        if (ids.Count == 0) return;

        _transport.SendToPeer(actorNumber, typeof(CursedRelicMessage),
            CursedPayload(ids.ToArray(), burdens.ToArray()));
        BepinPlugin.Log?.LogDebug($"[Net] → sent cursed snapshot ({ids.Count} relics) to joiner #{actorNumber}.");
    }

    // Client: apply (or buffer) cursed flags from host.
    internal static void ApplyIncomingCursed(object[] a)
    {
        if (IsAuthority) return; // host already has its own markers
        if (a == null || a.Length < 2 || a[0] is not int[] ids || a[1] is not int[] burdens) return;

        for (int i = 0; i < ids.Length && i < burdens.Length; i++)
        {
            int viewID = ids[i];
            var burden = (BurdenType)burdens[i];
            var pv = PhotonView.Find(viewID);
            if (pv != null && pv.gameObject != null)
            {
                CursedRelicMarker.MarkCursed(pv.gameObject, burden);
                BepinPlugin.Log?.LogDebug($"[Net] ← applied cursed relic viewID={viewID} ({burden}).");
            }
            else
            {
                _pendingCursed.Buffer(viewID, burden);
                BepinPlugin.Log?.LogDebug($"[Net] ← buffered cursed relic viewID={viewID} ({burden}) — object not spawned yet.");
            }
        }
    }

    // Client: called from OnPhotonInstantiate to drain a buffered cursed flag for
    // a relic that has now appeared.
    internal static void TryApplyPendingCursed(PhotonView pv, GameObject go)
    {
        if (pv == null || go == null) return;
        if (!_pendingCursed.TryTake(pv.ViewID, out var burden)) return;
        CursedRelicMarker.MarkCursed(go, burden);
        BepinPlugin.Log?.LogDebug($"[Net] ← applied buffered cursed relic viewID={pv.ViewID} ({burden}).");
    }

    // ---- authoritative commit (Phase 8-C) ---------------------------------
    //
    // The commit ROLL is host-authoritative (cursed markers + RNG live on the
    // host). A client sends {boxViewID, relicViewIDs}; the host resolves the
    // relics itself (never trusting client-reported tier/cursed), rolls, persists,
    // and broadcasts the full resulting box snapshot. Every client overwrites its
    // snapshot; the operator (the client holding the relics) also consumes them.

    // Client → host.
    internal static void RequestCommit(int boxViewId, int[] relicViewIds)
    {
        _transport.SendToMaster(typeof(CommitRequestMessage),
            new object[] { boxViewId, relicViewIds ?? Array.Empty<int>() });
        BepinPlugin.Log?.LogDebug($"[Net] → sent commit request box={boxViewId} ({relicViewIds?.Length ?? 0} relics) to host.");
    }

    // Host resolves + computes. ForgeCommit.Execute saves the host snapshot and
    // broadcasts the result; the operator consumes on receipt.
    internal static void HandleCommitRequest(object[] a, int senderActor)
    {
        if (!IsAuthority) return;
        if (a == null || a.Length < 2) return;

        int boxViewId = Convert.ToInt32(a[0]);
        var relicViewIds = a[1] as int[] ?? Array.Empty<int>();

        // Resolve the box directly by ViewID — the host's own forge instance has no
        // _moduleBox when a client docked (docking is a local interaction), so we
        // compute from the box object, not from a behaviour.
        var boxPv = PhotonView.Find(boxViewId);
        var box = boxPv != null ? boxPv.GetComponent<CG.Ship.Object.BuildBox>() : null;
        if (box == null)
        {
            BepinPlugin.Log?.LogWarning($"[Net] ← commit request from #{senderActor} for box={boxViewId}: box not found — ignored.");
            return;
        }

        var relics = new List<GameObject>();
        foreach (var vid in relicViewIds)
        {
            var pv = PhotonView.Find(vid);
            if (pv != null && pv.gameObject != null) relics.Add(pv.gameObject);
        }
        BepinPlugin.Log?.LogDebug($"[Net] ← commit request from #{senderActor} box={boxViewId} ({relics.Count}/{relicViewIds.Length} relics resolved).");

        ForgeCommit.Execute(box, relics); // saves host snapshot + broadcasts result
    }

    // Host → all: authoritative box snapshot (also the late-joiner overlay push,
    // with relicsConsumed = 0).
    internal static void BroadcastCommitResult(int boxViewId, ForgeSnapshot snap, int relicsConsumed)
    {
        if (!ShouldBroadcast) return;
        _transport.SendToOthers(typeof(CommitResultMessage), snap.ToPayload(boxViewId, relicsConsumed));
        BepinPlugin.Log?.LogDebug($"[Net] → sent commit result box={boxViewId} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}, consumed {relicsConsumed}) to all.");
    }

    // Client applies the authoritative snapshot + (if operator) consumes.
    internal static void ApplyCommitResult(object[] a)
    {
        if (IsAuthority) return; // host already persisted inline
        if (!ForgeSnapshot.TryFromPayload(a, out int boxViewId, out var snap, out int relicsConsumed)) return;

        ForgeStateStore.SaveSnapshot(boxViewId, snap);
        BepinPlugin.Log?.LogDebug($"[Net] ← applied commit result box={boxViewId} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}, consumed {relicsConsumed}).");

        UpgradeForgeBehavior.FindByBoxViewId(boxViewId)?.OnNetworkCommitResult(relicsConsumed);
    }

    // Deconstructing player → everyone else: "this freshly-created BuildBox
    // carries this forge overlay." Mirrors BroadcastModuleOverlay's reasoning but
    // for the opposite direction — see DeconstructCreateBuildBoxPatch for why the
    // relay is needed at all. Reuses CommitResultMessage/ApplyCommitResult: the
    // wire shape is identical to a zero-relics commit result, which is exactly
    // what SendOverlaySnapshotTo already sends for the late-joiner case below.
    //
    // ShouldRelay, not ShouldBroadcast: the deconstructing player may be a client,
    // not the host, same reasoning as BroadcastModuleOverlay.
    internal static void BroadcastBoxOverlay(int boxViewId, ForgeSnapshot snap)
    {
        if (!ShouldRelay) return;
        if (boxViewId <= 0 || snap == null) return;

        _transport.SendToOthers(typeof(CommitResultMessage), snap.ToPayload(boxViewId, 0));
        BepinPlugin.Log?.LogDebug($"[Net] → sent box overlay box={boxViewId} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}) to all.");
    }

    // Compact perk/burden summary for the paired →sent / ←applied log lines.
    // Both sides format through here so a 2-client verification can diff them
    // directly: the level alone can't prove burdens crossed the wire, which is
    // exactly the gap that left burden sync unverifiable in the 26-07-18 session.
    private static string DescribeOverlay(IReadOnlyList<string> perkSlots, IReadOnlyList<BurdenType> burdens)
    {
        int filled = 0;
        if (perkSlots != null)
            foreach (var id in perkSlots)
                if (!string.IsNullOrEmpty(id)) filled++;

        string burdenText = burdens == null || burdens.Count == 0
            ? "none"
            : string.Join("+", burdens);

        return $"perks={filled}, burdens={burdenText}";
    }

    // Host → joiner: every upgraded box's overlay so their modules reconstruct
    // with the right level/perks/burdens.
    internal static void SendOverlaySnapshotTo(int actorNumber)
    {
        if (!IsAuthority) return;
        var all = ForgeStateStore.AllSnapshots();
        if (all.Count == 0) return;
        foreach (var kv in all)
            _transport.SendToPeer(actorNumber, typeof(CommitResultMessage), kv.Value.ToPayload(kv.Key, 0));
        BepinPlugin.Log?.LogDebug($"[Net] → sent overlay snapshot ({all.Count} boxes) to joiner #{actorNumber}.");
    }

    // ---- installed-module overlay (Phase 8-D) -----------------------------
    //
    // BuildBox.BuildModule ends in PhotonNetwork.Instantiate, so it runs ONLY on
    // the machine that placed the box. Every remote client receives the module
    // through Photon's own instantiation path and never executes BuildModule —
    // which is why the snapshot restore (and the forge's interactables) were
    // missing entirely on the other player's screen.
    //
    // The box snapshot is already replicated everywhere by BroadcastCommitResult,
    // but only the placer knows which module ViewID that box turned into. So the
    // placer announces the mapping. It isn't inventing state: the snapshot it
    // relays originated from the host-authoritative commit.

    // moduleViewID → snapshot, for overlays that arrived before the module spawned.
    private static readonly PendingByViewId<ForgeSnapshot> _pendingModuleOverlay = new();

    // Placer → everyone else: "this module ViewID carries this overlay."
    internal static void BroadcastModuleOverlay(int moduleViewId, ForgeSnapshot snap)
    {
        // ShouldRelay, not ShouldBroadcast: the placer may be a client, and this
        // is a relay of already-authoritative state rather than a new decision.
        if (!ShouldRelay) return;
        if (moduleViewId <= 0 || snap == null) return;

        _transport.SendToOthers(typeof(ModuleOverlayMessage), snap.ToPayload(moduleViewId, 0));
        BepinPlugin.Log?.LogDebug($"[Net] → sent module overlay module={moduleViewId} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}) to all.");
    }

    // Convenience for any path that mutates an installed module's state OUTSIDE
    // the commit flow — the dev commands (!setlevel, !forceperk) in particular.
    // Without this the change lands only on the machine that typed it and every
    // other player keeps rendering the old overlay.
    internal static void BroadcastModuleOverlayFor(CellModule module)
    {
        if (module == null || module.photonView == null) return;
        if (!ForgeStateStore.TryGet(module, out var state)) return;
        BroadcastModuleOverlay(module.photonView.ViewID, state.Snapshot());
    }

    // Host → joiner: every installed module's overlay, so a late joiner sees
    // forged modules already welded into the ship.
    private static void SendModuleOverlaysTo(int actorNumber)
    {
        if (!IsAuthority) return;
        var all = ForgeStateStore.AllModuleStates();
        if (all.Count == 0) return;
        foreach (var (viewId, snap) in all)
            _transport.SendToPeer(actorNumber, typeof(ModuleOverlayMessage), snap.ToPayload(viewId, 0));
        BepinPlugin.Log?.LogDebug($"[Net] → sent module overlays ({all.Count}) to joiner #{actorNumber}.");
    }

    internal static void ApplyIncomingModuleOverlay(object[] a)
    {
        if (!ForgeSnapshot.TryFromPayload(a, out int moduleViewId, out var snap, out _)) return;

        var pv = PhotonView.Find(moduleViewId);
        var module = pv != null ? pv.GetComponent<CellModule>() : null;
        if (module == null)
        {
            // The overlay can outrun the module's own instantiation; drained from
            // OnPhotonInstantiate once it appears.
            _pendingModuleOverlay.Buffer(moduleViewId, snap);
            BepinPlugin.Log?.LogDebug($"[Net] ← buffered module overlay module={moduleViewId} — module not spawned yet.");
            return;
        }

        ForgeStateStore.GetOrCreate(module).ApplySnapshot(snap);
        BepinPlugin.Log?.LogDebug($"[Net] ← applied module overlay module={moduleViewId} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}).");
    }

    // Called from OnPhotonInstantiate for a module that has now appeared.
    internal static void TryApplyPendingModuleOverlay(PhotonView pv, CellModule module)
    {
        if (pv == null || module == null) return;
        if (!_pendingModuleOverlay.TryTake(pv.ViewID, out var snap)) return;
        ForgeStateStore.GetOrCreate(module).ApplySnapshot(snap);
        BepinPlugin.Log?.LogDebug($"[Net] ← applied buffered module overlay module={pv.ViewID} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}).");
    }

    // ---- forge docking (Phase 8-E) ----------------------------------------
    //
    // HandleInteraction only runs for the player who clicked, so docking a relic
    // or a build box was invisible to everyone else — the 26-07-19 session's
    // "forge module placement of relics and buildbox do not sync" report.
    //
    // Relayed from the operator rather than routed through the host: docking is a
    // presentation/staging concern, and the commit that consumes these items is
    // still host-authoritative and re-resolves everything from ViewIDs.
    internal static void BroadcastDock(int forgeViewId, int itemViewId, int anchorIndex, bool docked)
    {
        // ShouldRelay: the operator may be a client, same as the module overlay.
        if (!ShouldRelay) return;
        if (forgeViewId <= 0 || itemViewId <= 0) return;

        _transport.SendToOthers(typeof(ForgeDockMessage),
            new object[] { forgeViewId, itemViewId, anchorIndex, docked });
        BepinPlugin.Log?.LogDebug(
            $"[Net] → sent {(docked ? "dock" : "undock")} item={itemViewId} anchor={anchorIndex} forge={forgeViewId} to all.");
    }

    internal static void ApplyIncomingDock(object[] a)
    {
        if (a == null || a.Length < 4) return;

        int forgeViewId = Convert.ToInt32(a[0]);
        int itemViewId = Convert.ToInt32(a[1]);
        int anchorIndex = Convert.ToInt32(a[2]);
        bool docked = Convert.ToBoolean(a[3]);

        var forge = UpgradeForgeBehavior.FindByViewId(forgeViewId);
        if (forge == null)
        {
            // Unlike cursed markers and overlays this isn't buffered: a dock is a
            // transient staging state, and replaying a stale one against a Forge
            // that appears later would be worse than showing nothing.
            BepinPlugin.Log?.LogDebug($"[Net] ← dock for forge={forgeViewId} ignored — forge not found here.");
            return;
        }

        if (docked) forge.ApplyRemoteDock(itemViewId, anchorIndex);
        else forge.ApplyRemoteUndock(itemViewId);
    }

    // ---- IInRoomCallbacks -------------------------------------------------

    public void OnPlayerEnteredRoom(Player newPlayer)
    {
        if (newPlayer == null) return;
        SendCatchUpTo(newPlayer.ActorNumber);
    }

    // The late-joiner catch-up, in one place so the four pushes can't drift out of
    // step. Each is individually authority-gated.
    //
    // Not reachable from tests: SendCursedSnapshotTo scans the scene for markers,
    // and a method body containing Unity engine calls cannot even be JIT-compiled
    // in the test host — an early return does not help. The two Unity-free pushes
    // are covered individually instead.
    private static void SendCatchUpTo(int actorNumber)
    {
        SendStateTo(actorNumber);
        SendCursedSnapshotTo(actorNumber);
        SendOverlaySnapshotTo(actorNumber);
        SendModuleOverlaysTo(actorNumber);
    }

    public void OnMasterClientSwitched(Player newMasterClient)
    {
        // Authority is derived live, so the new master's hooks already act on their
        // own — just re-assert so no client stays stale, and so escalation never
        // silently freezes after a host leaves.
        if (IsAuthority)
        {
            BepinPlugin.Log?.LogInfo("[Net] Became master client — asserting forge-state authority.");
            BroadcastState();
        }
    }

    public void OnPlayerLeftRoom(Player otherPlayer) { }
    public void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable propertiesThatChanged) { }
    public void OnPlayerPropertiesUpdate(Player targetPlayer, ExitGames.Client.Photon.Hashtable changedProps) { }
}
