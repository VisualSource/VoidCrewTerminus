using System;
using Photon.Pun;
using Photon.Realtime;
using VoidManager.ModMessages;

namespace VoidCrewTerminus.Net;

// The seam between forge-sync logic and the network underneath. Three adapters make it a real
// seam rather than indirection: OfflineTransport (solo, and before we are in a room),
// PunTransport (the ONLY type here that touches PhotonNetwork), and a recording test fake.
// Expressed as facts (IsAuthority, HasPeers), not as gates; the gates belong with the logic.
internal interface IForgeTransport
{
    // Solo counts as authority, so single-player behaves as though it were the host.
    bool IsAuthority { get; }

    // False solo, so a broadcast becomes a no-op without the caller special-casing it.
    bool HasPeers { get; }

    void SendToOthers(Type message, object[] payload);
    void SendToMaster(Type message, object[] payload);
    void SendToPeer(int actorNumber, Type message, object[] payload);
}

// References NOTHING from Photon, which is load-bearing beyond solo play: touching
// PhotonNetwork during plugin Awake breaks matchmaking (see CLAUDE.md). Holding this until a
// room event fires keeps PunTransport untouched that early, making the hazard structural.
internal sealed class OfflineTransport : IForgeTransport
{
    internal static readonly OfflineTransport Instance = new();
    private OfflineTransport() { }

    public bool IsAuthority => true;
    public bool HasPeers => false;

    // Unreachable in practice. These no-op rather than throw so a caller that forgets a gate
    // degrades quietly instead of raising inside a Photon callback.
    public void SendToOthers(Type message, object[] payload) { }
    public void SendToMaster(Type message, object[] payload) { }
    public void SendToPeer(int actorNumber, Type message, object[] payload) { }
}

// In a room. The single place PhotonNetwork and ModMessage are spoken to.
internal sealed class PunTransport : IForgeTransport
{
    internal static readonly PunTransport Instance = new();
    private PunTransport() { }

    // Tolerates being asked outside a room (host migration races, a read landing just after
    // LeftRoom) and answers the same as OfflineTransport would.
    public bool IsAuthority => !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;

    public bool HasPeers => PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.PlayerCount > 1;

    public void SendToOthers(Type message, object[] payload) =>
        Send(message, ReceiverGroup.Others, payload);

    public void SendToMaster(Type message, object[] payload) =>
        Send(message, ReceiverGroup.MasterClient, payload);

    public void SendToPeer(int actorNumber, Type message, object[] payload)
    {
        // findMasterClientInstead: false, matching the game's own GetPlayer usage.
        var player = PhotonNetwork.CurrentRoom?.GetPlayer(actorNumber, false);
        if (player == null) return;
        ModMessage.Send(MyPluginInfo.PLUGIN_GUID, ModMessage.GetIdentifier(message),
            player, payload, reliable: true);
    }

    private static void Send(Type message, ReceiverGroup group, object[] payload) =>
        ModMessage.Send(MyPluginInfo.PLUGIN_GUID, ModMessage.GetIdentifier(message),
            group, payload, reliable: true);
}
