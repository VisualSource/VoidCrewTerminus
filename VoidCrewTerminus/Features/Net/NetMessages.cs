using Photon.Realtime;
using VoidManager.ModMessages;

namespace VoidCrewTerminus.Net;

// Phase 8-A ModMessages. VoidManager discovers these by type (same scan that
// finds chat commands) and routes an incoming message to the matching class's
// Handle() by its GetIdentifier() (namespace.name). Payloads are object[] of
// Photon-serializable primitives.

// Host → clients: authoritative meter/escalation snapshot.
// arguments: [int scalar, int bosses, float meter, int level]
public class ForgeStateSyncMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.ApplyIncomingState(arguments);
}

// Client → host (MasterClient): "spend alloys on my behalf." No payload — the
// host runs the spend against its own authoritative supplies and the resulting
// meter/level reaches the requester via the state broadcast.
public class AlloySpendRequestMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.HandleAlloySpendRequest(sender);
}

// Phase 8-B — Host → clients: which relics are cursed, keyed by PhotonView.ViewID.
// Sent length-1 live at each cursed spawn, and as a full batch to a joiner.
// arguments: [int[] viewIDs, int[] burdenTypes]
public class CursedRelicMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.ApplyIncomingCursed(arguments);
}
