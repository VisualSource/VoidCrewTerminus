using System;
using Photon.Pun;
using Photon.Realtime;
using VoidCrewTerminus.Escalation;
using VoidCrewTerminus.Forge;
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

    // True when this client owns the authoritative state. Solo (not in a room)
    // counts as authority so single-player behaves exactly as before.
    internal static bool IsAuthority => !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;

    // Only actually put a message on the wire when we're the authority AND there's
    // someone else in the room to receive it.
    private static bool ShouldBroadcast =>
        PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient && PhotonNetwork.CurrentRoom.PlayerCount > 1;

    internal static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        PhotonNetwork.AddCallbackTarget(_callbacks);
    }

    internal static void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;
        PhotonNetwork.RemoveCallbackTarget(_callbacks);
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

    // ---- IInRoomCallbacks -------------------------------------------------

    public void OnPlayerEnteredRoom(Player newPlayer) => SendStateTo(newPlayer);

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
