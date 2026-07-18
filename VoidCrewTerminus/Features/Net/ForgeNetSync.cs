using System;
using System.Collections.Generic;
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
        BepinPlugin.Log.LogDebug($"[Net] → sent commit result box={boxViewId} L{snap.Level} (consumed {relicsConsumed}) to all.");
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
        BepinPlugin.Log.LogDebug($"[Net] ← applied commit result box={boxViewId} L{level} (consumed {relicsConsumed}).");

        UpgradeForgeBehavior.FindByBoxViewId(boxViewId)?.OnNetworkCommitResult(relicsConsumed);
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

    // ---- IInRoomCallbacks -------------------------------------------------

    public void OnPlayerEnteredRoom(Player newPlayer)
    {
        SendStateTo(newPlayer);
        SendCursedSnapshotTo(newPlayer);
        SendOverlaySnapshotTo(newPlayer);
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
