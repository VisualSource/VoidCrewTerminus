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

// Phase 8-C — Client → host (MasterClient): "I'm committing this box with these
// relics." Host re-derives tier/cursed itself and rolls authoritatively.
// arguments: [int boxViewID, int[] relicViewIDs]
public class CommitRequestMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.HandleCommitRequest(arguments, sender);
}

// Phase 8-C — Host → all: authoritative commit result as a full box snapshot.
// Every client overwrites its snapshot for the box; the operator (the client
// holding the relics) also consumes them. Also reused for the late-joiner overlay
// push (relicsConsumed = 0).
// arguments: [int boxViewID, int level, string[] perkSlots, int[] burdens, int relicsConsumed]
public class CommitResultMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.ApplyCommitResult(arguments);
}
