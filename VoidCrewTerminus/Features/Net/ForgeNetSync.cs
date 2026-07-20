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

namespace VoidCrewTerminus.Net;

// Phase 8-A — host-authoritative sync of the meter / escalation state
// (DifficultyScalar, BossesDefeated, Meter, Level) plus the client→host alloy
// spend hop.
//
// Authority == Photon master client. It is ALSO true in solo/offline play
// (PhotonNetwork.IsMasterClient is true when not in a room), so single-player is
// unchanged: you are the authority, and BroadcastState simply no-ops with no one
// to send to.
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

    // True when this client owns the authoritative state. Solo (not in a room)
    // counts as authority so single-player behaves exactly as before.
    internal static bool IsAuthority => !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;

    // Only actually put a message on the wire when we're the authority AND there's
    // someone else in the room to receive it.
    private static bool ShouldBroadcast =>
        PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient && PhotonNetwork.CurrentRoom.PlayerCount > 1;

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
        PhotonNetwork.AddCallbackTarget(_callbacks);
        BepinPlugin.Log.LogDebug("[Net] PUN callback target attached (in room).");
    }

    private static void UnregisterCallbacks()
    {
        if (!_registered) return;
        _registered = false;
        PhotonNetwork.RemoveCallbackTarget(_callbacks);
        _pendingCursed.Clear();
        BepinPlugin.Log.LogDebug("[Net] PUN callback target detached (left room).");
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
        _pendingCursed.Clear();
    }

    // ---- outbound (authority → clients) -----------------------------------

    internal static void BroadcastState()
    {
        if (!ShouldBroadcast) return;
        var args = StatePayload();
        ModMessage.Send(MyPluginInfo.PLUGIN_GUID,
            ModMessage.GetIdentifier(typeof(ForgeStateSyncMessage)),
            ReceiverGroup.Others, args, reliable: true);
        BepinPlugin.Log.LogDebug($"[Net] → sent forge state {Describe(args)} to all.");
    }

    private static void SendStateTo(Player player)
    {
        if (!PhotonNetwork.IsMasterClient || player == null) return;
        var args = StatePayload();
        ModMessage.Send(MyPluginInfo.PLUGIN_GUID,
            ModMessage.GetIdentifier(typeof(ForgeStateSyncMessage)),
            player, args, reliable: true);
        BepinPlugin.Log.LogDebug($"[Net] → sent forge state {Describe(args)} to joiner #{player.ActorNumber}.");
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
        BepinPlugin.Log.LogDebug(
            $"[Net] ← applied forge state {{scalar={scalar}, bosses={bosses}, meter={meter:0.#}, level={level}}}.");
    }

    // ---- alloy spend hop (client → host) ----------------------------------

    internal static void RequestAlloySpend()
    {
        ModMessage.Send(MyPluginInfo.PLUGIN_GUID,
            ModMessage.GetIdentifier(typeof(AlloySpendRequestMessage)),
            ReceiverGroup.MasterClient, Array.Empty<object>(), reliable: true);
        BepinPlugin.Log.LogDebug("[Net] → sent alloy-spend request to host.");
    }

    internal static void HandleAlloySpendRequest(Player sender)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        bool ok = ForgeMeterController.TrySpendAlloys(out string message);
        BepinPlugin.Log.LogDebug(
            $"[Net] ← alloy-spend request from #{sender?.ActorNumber}: {(ok ? "spent" : message)}");
        if (ok) BroadcastState(); // push the new meter/level to everyone incl. the requester
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
    private static readonly Dictionary<int, BurdenType> _pendingCursed = new();

    // Host: announce one freshly-cursed relic to all clients.
    internal static void BroadcastCursed(PhotonView pv, BurdenType burden)
    {
        if (!ShouldBroadcast) return;
        if (pv == null || pv.ViewID <= 0) return;
        ModMessage.Send(MyPluginInfo.PLUGIN_GUID,
            ModMessage.GetIdentifier(typeof(CursedRelicMessage)),
            ReceiverGroup.Others,
            new object[] { new[] { pv.ViewID }, new[] { (int)burden } }, reliable: true);
        BepinPlugin.Log.LogDebug($"[Net] → sent cursed relic viewID={pv.ViewID} ({burden}) to all.");
    }

    // Host: full cursed set for a joining player.
    private static void SendCursedSnapshotTo(Player player)
    {
        if (!PhotonNetwork.IsMasterClient || player == null) return;

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

        ModMessage.Send(MyPluginInfo.PLUGIN_GUID,
            ModMessage.GetIdentifier(typeof(CursedRelicMessage)),
            player, new object[] { ids.ToArray(), burdens.ToArray() }, reliable: true);
        BepinPlugin.Log.LogDebug($"[Net] → sent cursed snapshot ({ids.Count} relics) to joiner #{player.ActorNumber}.");
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
                BepinPlugin.Log.LogDebug($"[Net] ← applied cursed relic viewID={viewID} ({burden}).");
            }
            else
            {
                _pendingCursed[viewID] = burden;
                BepinPlugin.Log.LogDebug($"[Net] ← buffered cursed relic viewID={viewID} ({burden}) — object not spawned yet.");
            }
        }
    }

    // Client: called from OnPhotonInstantiate to drain a buffered cursed flag for
    // a relic that has now appeared.
    internal static void TryApplyPendingCursed(PhotonView pv, GameObject go)
    {
        if (pv == null || go == null) return;
        if (!_pendingCursed.TryGetValue(pv.ViewID, out var burden)) return;
        _pendingCursed.Remove(pv.ViewID);
        CursedRelicMarker.MarkCursed(go, burden);
        BepinPlugin.Log.LogDebug($"[Net] ← applied buffered cursed relic viewID={pv.ViewID} ({burden}).");
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
        ModMessage.Send(MyPluginInfo.PLUGIN_GUID,
            ModMessage.GetIdentifier(typeof(CommitRequestMessage)),
            ReceiverGroup.MasterClient,
            new object[] { boxViewId, relicViewIds ?? Array.Empty<int>() }, reliable: true);
        BepinPlugin.Log.LogDebug($"[Net] → sent commit request box={boxViewId} ({relicViewIds?.Length ?? 0} relics) to host.");
    }

    // Host resolves + computes. UpgradeForgeBehavior.ComputeAndPersist saves the
    // host snapshot and broadcasts the result; the operator consumes on receipt.
    internal static void HandleCommitRequest(object[] a, Player sender)
    {
        if (!PhotonNetwork.IsMasterClient) return;
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
            BepinPlugin.Log.LogWarning($"[Net] ← commit request from #{sender?.ActorNumber} for box={boxViewId}: box not found — ignored.");
            return;
        }

        var relics = new List<GameObject>();
        foreach (var vid in relicViewIds)
        {
            var pv = PhotonView.Find(vid);
            if (pv != null && pv.gameObject != null) relics.Add(pv.gameObject);
        }
        BepinPlugin.Log.LogDebug($"[Net] ← commit request from #{sender?.ActorNumber} box={boxViewId} ({relics.Count}/{relicViewIds.Length} relics resolved).");

        UpgradeForgeBehavior.ComputeAndPersist(box, relics); // saves host snapshot + broadcasts result
    }

    // Host → all: authoritative box snapshot (also the late-joiner overlay push,
    // with relicsConsumed = 0).
    internal static void BroadcastCommitResult(int boxViewId, ForgeSnapshot snap, int relicsConsumed)
    {
        if (!ShouldBroadcast) return;
        ModMessage.Send(MyPluginInfo.PLUGIN_GUID,
            ModMessage.GetIdentifier(typeof(CommitResultMessage)),
            ReceiverGroup.Others, SnapshotPayload(boxViewId, snap, relicsConsumed), reliable: true);
        BepinPlugin.Log.LogDebug($"[Net] → sent commit result box={boxViewId} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}, consumed {relicsConsumed}) to all.");
    }

    // Client applies the authoritative snapshot + (if operator) consumes.
    internal static void ApplyCommitResult(object[] a)
    {
        if (IsAuthority) return; // host already persisted inline
        if (a == null || a.Length < 5) return;

        int boxViewId = Convert.ToInt32(a[0]);
        int level = Convert.ToInt32(a[1]);
        var perkSlots = a[2] as string[] ?? Array.Empty<string>();
        var burdensInt = a[3] as int[] ?? Array.Empty<int>();
        int relicsConsumed = Convert.ToInt32(a[4]);

        var slots = new string[perkSlots.Length];
        for (int i = 0; i < perkSlots.Length; i++)
            slots[i] = string.IsNullOrEmpty(perkSlots[i]) ? null : perkSlots[i]; // "" ← empty slot

        var burdens = new BurdenType[burdensInt.Length];
        for (int i = 0; i < burdensInt.Length; i++)
            burdens[i] = (BurdenType)burdensInt[i];

        ForgeStateStore.SaveSnapshot(boxViewId, ForgeSnapshot.Create(level, slots, burdens));
        BepinPlugin.Log.LogDebug($"[Net] ← applied commit result box={boxViewId} L{level} " +
            $"({DescribeOverlay(slots, burdens)}, consumed {relicsConsumed}).");

        UpgradeForgeBehavior.FindByBoxViewId(boxViewId)?.OnNetworkCommitResult(relicsConsumed);
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

    private static object[] SnapshotPayload(int boxViewId, ForgeSnapshot snap, int relicsConsumed)
    {
        var perks = new string[snap.PerkSlots.Count];
        for (int i = 0; i < snap.PerkSlots.Count; i++) perks[i] = snap.PerkSlots[i] ?? ""; // null → "" for transport
        var burdens = new int[snap.Burdens.Count];
        for (int i = 0; i < snap.Burdens.Count; i++) burdens[i] = (int)snap.Burdens[i];
        return new object[] { boxViewId, snap.Level, perks, burdens, relicsConsumed };
    }

    // Host → joiner: every upgraded box's overlay so their modules reconstruct
    // with the right level/perks/burdens.
    private static void SendOverlaySnapshotTo(Player player)
    {
        if (!PhotonNetwork.IsMasterClient || player == null) return;
        var all = ForgeStateStore.AllSnapshots();
        if (all.Count == 0) return;
        foreach (var kv in all)
            ModMessage.Send(MyPluginInfo.PLUGIN_GUID,
                ModMessage.GetIdentifier(typeof(CommitResultMessage)),
                player, SnapshotPayload(kv.Key, kv.Value, 0), reliable: true);
        BepinPlugin.Log.LogDebug($"[Net] → sent overlay snapshot ({all.Count} boxes) to joiner #{player.ActorNumber}.");
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
    private static readonly Dictionary<int, ForgeSnapshot> _pendingModuleOverlay = new();

    // Placer → everyone else: "this module ViewID carries this overlay."
    internal static void BroadcastModuleOverlay(int moduleViewId, ForgeSnapshot snap)
    {
        // Deliberately not ShouldBroadcast: the placer may be a client, and this
        // is a relay of already-authoritative state rather than a new decision.
        if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom.PlayerCount <= 1) return;
        if (moduleViewId <= 0 || snap == null) return;

        ModMessage.Send(MyPluginInfo.PLUGIN_GUID,
            ModMessage.GetIdentifier(typeof(ModuleOverlayMessage)),
            ReceiverGroup.Others, SnapshotPayload(moduleViewId, snap, 0), reliable: true);
        BepinPlugin.Log.LogDebug($"[Net] → sent module overlay module={moduleViewId} L{snap.Level} " +
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
    private static void SendModuleOverlaysTo(Player player)
    {
        if (!PhotonNetwork.IsMasterClient || player == null) return;
        var all = ForgeStateStore.AllModuleStates();
        if (all.Count == 0) return;
        foreach (var (viewId, snap) in all)
            ModMessage.Send(MyPluginInfo.PLUGIN_GUID,
                ModMessage.GetIdentifier(typeof(ModuleOverlayMessage)),
                player, SnapshotPayload(viewId, snap, 0), reliable: true);
        BepinPlugin.Log.LogDebug($"[Net] → sent module overlays ({all.Count}) to joiner #{player.ActorNumber}.");
    }

    internal static void ApplyIncomingModuleOverlay(object[] a)
    {
        if (a == null || a.Length < 4) return;

        int moduleViewId = Convert.ToInt32(a[0]);
        var snap = ForgeSnapshot.Create(
            Convert.ToInt32(a[1]),
            a[2] as string[] ?? Array.Empty<string>(),
            ToBurdens(a[3] as int[]));

        var pv = PhotonView.Find(moduleViewId);
        var module = pv != null ? pv.GetComponent<CellModule>() : null;
        if (module == null)
        {
            // The overlay can outrun the module's own instantiation; drained from
            // OnPhotonInstantiate once it appears.
            _pendingModuleOverlay[moduleViewId] = snap;
            BepinPlugin.Log.LogDebug($"[Net] ← buffered module overlay module={moduleViewId} — module not spawned yet.");
            return;
        }

        ForgeStateStore.GetOrCreate(module).ApplySnapshot(snap);
        BepinPlugin.Log.LogDebug($"[Net] ← applied module overlay module={moduleViewId} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}).");
    }

    // Called from OnPhotonInstantiate for a module that has now appeared.
    internal static void TryApplyPendingModuleOverlay(PhotonView pv, CellModule module)
    {
        if (pv == null || module == null) return;
        if (!_pendingModuleOverlay.TryGetValue(pv.ViewID, out var snap)) return;
        _pendingModuleOverlay.Remove(pv.ViewID);
        ForgeStateStore.GetOrCreate(module).ApplySnapshot(snap);
        BepinPlugin.Log.LogDebug($"[Net] ← applied buffered module overlay module={pv.ViewID} L{snap.Level} " +
            $"({DescribeOverlay(snap.PerkSlots, snap.Burdens)}).");
    }

    private static List<BurdenType> ToBurdens(int[] raw)
    {
        var list = new List<BurdenType>();
        if (raw != null)
            foreach (var b in raw) list.Add((BurdenType)b);
        return list;
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
        if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom.PlayerCount <= 1) return;
        if (forgeViewId <= 0 || itemViewId <= 0) return;

        ModMessage.Send(MyPluginInfo.PLUGIN_GUID,
            ModMessage.GetIdentifier(typeof(ForgeDockMessage)),
            ReceiverGroup.Others,
            new object[] { forgeViewId, itemViewId, anchorIndex, docked }, reliable: true);
        BepinPlugin.Log.LogDebug(
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
            BepinPlugin.Log.LogDebug($"[Net] ← dock for forge={forgeViewId} ignored — forge not found here.");
            return;
        }

        if (docked) forge.ApplyRemoteDock(itemViewId, anchorIndex);
        else forge.ApplyRemoteUndock(itemViewId);
    }

    // ---- IInRoomCallbacks -------------------------------------------------

    public void OnPlayerEnteredRoom(Player newPlayer)
    {
        SendStateTo(newPlayer);
        SendCursedSnapshotTo(newPlayer);
        SendOverlaySnapshotTo(newPlayer);
        SendModuleOverlaysTo(newPlayer);
    }

    public void OnMasterClientSwitched(Player newMasterClient)
    {
        // Authority is computed live from IsMasterClient, so the new master's
        // hooks already act on their own — just re-assert so no client stays
        // stale, and so escalation never silently freezes after a host leaves.
        if (PhotonNetwork.IsMasterClient)
        {
            BepinPlugin.Log.LogInfo("[Net] Became master client — asserting forge-state authority.");
            BroadcastState();
        }
    }

    public void OnPlayerLeftRoom(Player otherPlayer) { }
    public void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable propertiesThatChanged) { }
    public void OnPlayerPropertiesUpdate(Player targetPlayer, ExitGames.Client.Photon.Hashtable changedProps) { }
}
