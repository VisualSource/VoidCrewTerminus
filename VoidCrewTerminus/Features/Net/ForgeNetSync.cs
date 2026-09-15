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

// Host-authoritative sync of the meter/escalation state plus the client→host alloy hop.
//
// Authority is the Photon master client and ALSO true solo, so single-player is unchanged:
// BroadcastState no-ops with no peers. It is re-derived live from IsMasterClient rather than
// stored, so host migration needs no role flip, only a re-assert so nobody stays stale.
internal sealed class ForgeNetSync : IInRoomCallbacks
{
    private static readonly ForgeNetSync _callbacks = new();
    private static bool _initialized;

    private static bool _registered;

    // Stored so Shutdown can unsubscribe: a bare lambda can't be removed, and a leaked
    // handler would survive hot-reload into the new assembly.
    private static EventHandler _onJoinedRoom;
    private static EventHandler _onLeftRoom;

    // Starts OFFLINE and swaps to the PUN adapter only once genuinely in a room; see
    // OfflineTransport for why that ordering is load-bearing. Tests install a fake.
    private static IForgeTransport _transport = OfflineTransport.Instance;

    internal static IForgeTransport Transport
    {
        get => _transport;
        set => _transport = value ?? OfflineTransport.Instance;
    }

    // The gates differ only in who may originate: IsAuthority says we own the state (true
    // solo); ShouldBroadcast is an authority-originated push; ShouldRelay is a push whose
    // originator need not be the authority, for state that is already authoritative.
    internal static bool IsAuthority => _transport.IsAuthority;

    private static bool ShouldBroadcast => _transport.IsAuthority && _transport.HasPeers;

    private static bool ShouldRelay => _transport.HasPeers;

    // This method must touch NOTHING in Photon: Awake runs long before the game's own Photon
    // setup, and any PhotonNetwork reference here constructs the LoadBalancingClient before
    // the game applies its ServerSettings, silently breaking matchmaking. VoidManager's room
    // events are safe at Awake, so the PUN callback target is attached only once in a room.
    internal static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        _onJoinedRoom = (_, _) => RegisterCallbacks();
        _onLeftRoom = (_, _) => UnregisterCallbacks();

        // A host and a joining client don't necessarily raise the same event;
        // RegisterCallbacks is idempotent, so overlapping delivery is harmless.
        VoidManager.Events.Instance.JoinedRoom += _onJoinedRoom;
        VoidManager.Events.Instance.HostCreateRoom += _onJoinedRoom;
        VoidManager.Events.Instance.LeftRoom += _onLeftRoom;
    }

    private static void RegisterCallbacks()
    {
        if (_registered) return;
        _registered = true;
        // First touch of PhotonNetwork in the plugin's lifetime, inside a room event, on purpose.
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

    // ViewIDs are scoped to a room, so a buffer left across a room change would eventually be
    // applied to whatever unrelated object inherits that ID.
    private static void ClearPending()
    {
        _pendingCursed.Clear();
        _pendingModuleOverlay.Clear();
    }

    internal static void BroadcastState()
    {
        if (!ShouldBroadcast) return;
        var args = StatePayload();
        _transport.SendToOthers(typeof(ForgeStateSyncMessage), args);
        BepinPlugin.Log?.LogDebug($"[Net] → sent forge state {Describe(args)} to all.");
    }

    // internal, not private: free of Unity calls, so it is one of the only ways to exercise
    // the targeted-send gate from a test. See ForgeNetSyncGateTests.
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

    internal static void ApplyIncomingState(object[] a)
    {
        if (a == null || a.Length < 4) return;
        // Broadcasts go to Others, so this shouldn't fire on the host; guard anyway.
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

        // The requester's local call already returned before the host saw this, so
        // BroadcastState alone leaves them with no explanation on success and nothing on failure.
        SendAlloySpendResultTo(senderActor, ok,
            ok ? $"Alloys spent — {ForgeMeterController.Describe()}" : message);
    }

    // Requester only: SendToOthers would tell every other client about a request that
    // wasn't theirs.
    private static void SendAlloySpendResultTo(int actorNumber, bool ok, string message)
    {
        if (!IsAuthority) return;
        _transport.SendToPeer(actorNumber, typeof(AlloySpendResultMessage), new object[] { ok, message });
        BepinPlugin.Log?.LogDebug(
            $"[Net] → sent alloy-spend result to #{actorNumber}: {(ok ? "ok" : "failed")} ({message}).");
    }

    // The host resolved this locally already, so it never sends itself a result.
    internal static void ApplyIncomingAlloySpendResult(object[] a)
    {
        if (IsAuthority) return;
        if (a == null || a.Length < 2) return;
        bool ok = a[0] is bool flag && flag;
        string message = a[1] as string;
        if (!string.IsNullOrEmpty(message)) Messaging.Notification(message);
        BepinPlugin.Log?.LogDebug($"[Net] ← applied alloy-spend result: {(ok ? "ok" : "failed")} ({message}).");
    }

    // Cursed state is host-authoritative and purely for client awareness: the commit reads the
    // host's own markers, so a client mis-seeing cursed can't change an outcome. A live
    // broadcast can beat the relic's instantiation, so unresolved ViewIDs are buffered.

    // ViewID → burden, for cursed flags that arrived before the object existed.
    private static readonly PendingByViewId<BurdenType> _pendingCursed = new();

    // Parallel arrays so a single live flag and a whole joiner snapshot share one wire shape.
    private static object[] CursedPayload(int[] viewIds, int[] burdens) =>
        new object[] { viewIds, burdens };

    internal static void BroadcastCursed(PhotonView pv, BurdenType burden)
    {
        if (!ShouldBroadcast) return;
        if (pv == null || pv.ViewID <= 0) return;
        _transport.SendToOthers(typeof(CursedRelicMessage),
            CursedPayload(new[] { pv.ViewID }, new[] { (int)burden }));
        BepinPlugin.Log?.LogDebug($"[Net] → sent cursed relic viewID={pv.ViewID} ({burden}) to all.");
    }

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

    // Called from OnPhotonInstantiate once the relic appears.
    internal static void TryApplyPendingCursed(PhotonView pv, GameObject go)
    {
        if (pv == null || go == null) return;
        if (!_pendingCursed.TryTake(pv.ViewID, out var burden)) return;
        CursedRelicMarker.MarkCursed(go, burden);
        BepinPlugin.Log?.LogDebug($"[Net] ← applied buffered cursed relic viewID={pv.ViewID} ({burden}).");
    }

    // The roll is host-authoritative: a client sends {boxViewID, relicViewIDs} and the host
    // re-resolves the relics itself rather than trusting any client-reported tier or cursed flag.
    internal static void RequestCommit(int boxViewId, int[] relicViewIds)
    {
        _transport.SendToMaster(typeof(CommitRequestMessage),
            new object[] { boxViewId, relicViewIds ?? Array.Empty<int>() });
        BepinPlugin.Log?.LogDebug($"[Net] → sent commit request box={boxViewId} ({relicViewIds?.Length ?? 0} relics) to host.");
    }

    // ForgeCommit.Execute saves the host snapshot and broadcasts; the operator consumes on receipt.
    internal static void HandleCommitRequest(object[] a, int senderActor)
    {
        if (!IsAuthority) return;
        if (a == null || a.Length < 2) return;

        int boxViewId = Convert.ToInt32(a[0]);
        var relicViewIds = a[1] as int[] ?? Array.Empty<int>();

        // By ViewID, not from a behaviour: docking is a local interaction, so the host's own
        // forge instance has no _moduleBox when a client docked.
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

    // Also the late-joiner overlay push, with relicsConsumed = 0.
    internal static void BroadcastCommitResult(int boxViewId, ForgeSnapshot snap, int relicsConsumed)
    {
        if (!ShouldBroadcast) return;
        _transport.SendToOthers(typeof(CommitResultMessage), snap.ToPayload(boxViewId, relicsConsumed));
        BepinPlugin.Log?.LogDebug($"[Net] → sent commit result box={boxViewId} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}, consumed {relicsConsumed}) to all.");
    }

    internal static void ApplyCommitResult(object[] a)
    {
        if (IsAuthority) return; // host already persisted inline
        if (!ForgeSnapshot.TryFromPayload(a, out int boxViewId, out var snap, out int relicsConsumed)) return;

        ForgeStateStore.SaveSnapshot(boxViewId, snap);
        BepinPlugin.Log?.LogDebug($"[Net] ← applied commit result box={boxViewId} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}, consumed {relicsConsumed}).");

        UpgradeForgeBehavior.FindByBoxViewId(boxViewId)?.OnNetworkCommitResult(relicsConsumed);
    }

    // Reuses CommitResultMessage: the wire shape is identical to a zero-relics commit result.
    // ShouldRelay, not ShouldBroadcast, because the deconstructing player may be a client. See
    // DeconstructCreateBuildBoxPatch for why the relay is needed.
    internal static void BroadcastBoxOverlay(int boxViewId, ForgeSnapshot snap)
    {
        if (!ShouldRelay) return;
        if (boxViewId <= 0 || snap == null) return;

        _transport.SendToOthers(typeof(CommitResultMessage), snap.ToPayload(boxViewId, 0));
        BepinPlugin.Log?.LogDebug($"[Net] → sent box overlay box={boxViewId} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}) to all.");
    }

    // Both sides format through here so paired →sent / ←applied log lines diff directly.
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

    // Every upgraded box's overlay, so a joiner's modules reconstruct with the right state.
    internal static void SendOverlaySnapshotTo(int actorNumber)
    {
        if (!IsAuthority) return;
        var all = ForgeStateStore.AllSnapshots();
        if (all.Count == 0) return;
        foreach (var kv in all)
            _transport.SendToPeer(actorNumber, typeof(CommitResultMessage), kv.Value.ToPayload(kv.Key, 0));
        BepinPlugin.Log?.LogDebug($"[Net] → sent overlay snapshot ({all.Count} boxes) to joiner #{actorNumber}.");
    }

    // BuildModule ends in PhotonNetwork.Instantiate, so it runs only on the machine that placed
    // the box; remote clients never execute it. Only the placer knows which module ViewID the
    // box became, so the placer announces that mapping; the snapshot itself came from the host.

    // moduleViewID → snapshot, for overlays that arrived before the module spawned.
    private static readonly PendingByViewId<ForgeSnapshot> _pendingModuleOverlay = new();

    internal static void BroadcastModuleOverlay(int moduleViewId, ForgeSnapshot snap)
    {
        // ShouldRelay, not ShouldBroadcast: the placer may be a client.
        if (!ShouldRelay) return;
        if (moduleViewId <= 0 || snap == null) return;

        _transport.SendToOthers(typeof(ModuleOverlayMessage), snap.ToPayload(moduleViewId, 0));
        BepinPlugin.Log?.LogDebug($"[Net] → sent module overlay module={moduleViewId} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}) to all.");
    }

    // For paths that mutate an installed module outside the commit flow (the dev commands).
    // Without it the change lands only on the machine that typed it.
    internal static void BroadcastModuleOverlayFor(CellModule module)
    {
        if (module == null || module.photonView == null) return;
        if (!ForgeStateStore.TryGet(module, out var state)) return;
        BroadcastModuleOverlay(module.photonView.ViewID, state.Snapshot());
    }

    // So a late joiner sees forged modules already welded into the ship.
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

    // Docking is a presentation concern relayed by the operator rather than routed through the
    // host; the commit that consumes these items re-resolves everything from ViewIDs anyway.
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
            // Not buffered, unlike cursed markers: a dock is transient staging state, and
            // replaying a stale one against a Forge that appears later is worse than nothing.
            BepinPlugin.Log?.LogDebug($"[Net] ← dock for forge={forgeViewId} ignored — forge not found here.");
            return;
        }

        if (docked) forge.ApplyRemoteDock(itemViewId, anchorIndex);
        else forge.ApplyRemoteUndock(itemViewId);
    }

    public void OnPlayerEnteredRoom(Player newPlayer)
    {
        if (newPlayer == null) return;
        SendCatchUpTo(newPlayer.ActorNumber);
    }

    // One place so the four pushes can't drift; each is individually authority-gated. Not
    // reachable from tests: a method body containing a Unity call can't be JIT-compiled in the
    // test host, so the two Unity-free pushes are covered individually instead.
    private static void SendCatchUpTo(int actorNumber)
    {
        SendStateTo(actorNumber);
        SendCursedSnapshotTo(actorNumber);
        SendOverlaySnapshotTo(actorNumber);
        SendModuleOverlaysTo(actorNumber);
    }

    public void OnMasterClientSwitched(Player newMasterClient)
    {
        // Authority is derived live, so the new master's hooks already act; this only
        // re-asserts so no client stays stale and escalation never freezes after a host leaves.
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
