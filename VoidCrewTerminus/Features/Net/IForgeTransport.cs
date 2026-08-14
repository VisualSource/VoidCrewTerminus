using System;
using Photon.Pun;
using Photon.Realtime;
using VoidManager.ModMessages;

namespace VoidCrewTerminus.Net;

// The seam between forge-sync LOGIC and the network underneath it.
//
// Everything above this interface — which gate applies to which message, what
// goes into a payload, who receives it — is ordinary decision-making that was
// impossible to exercise without a running game and two connected clients. The
// gate rules are the subtlest part of the mod (authority, solo-counts-as-host,
// relay-vs-broadcast, host migration) and had zero coverage for exactly that
// reason.
//
// Three adapters, which is what makes this a real seam rather than indirection:
//   * OfflineTransport — solo play, and every moment before we are in a room.
//   * PunTransport     — in a room; the ONLY type here that touches PhotonNetwork.
//   * a recording fake in the tests.
//
// Deliberately expressed as facts about the world (IsAuthority, HasPeers) rather
// than as the gates themselves. The gates are compositions of those facts and
// belong with the logic, where they can be read side by side.
internal interface IForgeTransport
{
    // True when this client owns the authoritative state. Solo counts as
    // authority, so single-player behaves exactly as though it were the host.
    bool IsAuthority { get; }

    // Whether anyone else exists to receive a message. False solo, so a
    // broadcast becomes a no-op without the caller needing to special-case it.
    bool HasPeers { get; }

    void SendToOthers(Type message, object[] payload);
    void SendToMaster(Type message, object[] payload);
    void SendToPeer(int actorNumber, Type message, object[] payload);
}

// Solo, and every moment before we are in a room. References NOTHING from Photon.
//
// That is load-bearing for more than solo play. BepInEx's chainloader finishes
// long before the game runs its own Photon setup, and touching PhotonNetwork
// during plugin Awake forces PUN to construct its LoadBalancingClient before the
// game has applied its ServerSettings — which silently breaks matchmaking (the
// bisect recorded in CLAUDE.md). Because ForgeNetSync holds an OfflineTransport
// until a room event fires, the PunTransport type is never touched that early:
// the hazard is now structural instead of a rule someone has to remember.
internal sealed class OfflineTransport : IForgeTransport
{
    internal static readonly OfflineTransport Instance = new();
    private OfflineTransport() { }

    public bool IsAuthority => true;
    public bool HasPeers => false;

    // Unreachable in practice: every broadcast is gated on HasPeers, and the two
    // ungated request sends only fire off-authority. Solo is neither. These no-op
    // rather than throw so that a future caller which forgets a gate degrades
    // quietly instead of raising inside a Photon callback.
    public void SendToOthers(Type message, object[] payload) { }
    public void SendToMaster(Type message, object[] payload) { }
    public void SendToPeer(int actorNumber, Type message, object[] payload) { }
}

// In a room. The single place PhotonNetwork and ModMessage are spoken to.
internal sealed class PunTransport : IForgeTransport
{
    internal static readonly PunTransport Instance = new();
    private PunTransport() { }

    // Still tolerates being asked outside a room (host migration races, a read
    // landing just after LeftRoom) and answers the same as OfflineTransport would.
    public bool IsAuthority => !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;

    public bool HasPeers => PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.PlayerCount > 1;

    public void SendToOthers(Type message, object[] payload) =>
        Send(message, ReceiverGroup.Others, payload);

    public void SendToMaster(Type message, object[] payload) =>
        Send(message, ReceiverGroup.MasterClient, payload);

    public void SendToPeer(int actorNumber, Type message, object[] payload)
    {
        // findMasterClientInstead: false — matches the game's own GetPlayer usage.
        var player = PhotonNetwork.CurrentRoom?.GetPlayer(actorNumber, false);
        if (player == null) return;
        ModMessage.Send(MyPluginInfo.PLUGIN_GUID, ModMessage.GetIdentifier(message),
            player, payload, reliable: true);
    }

    private static void Send(Type message, ReceiverGroup group, object[] payload) =>
        ModMessage.Send(MyPluginInfo.PLUGIN_GUID, ModMessage.GetIdentifier(message),
            group, payload, reliable: true);
}
