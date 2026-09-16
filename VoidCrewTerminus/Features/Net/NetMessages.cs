using Photon.Realtime;
using VoidManager.ModMessages;

namespace VoidCrewTerminus.Net;

// VoidManager routes an incoming message to the matching class by GetIdentifier()
// (namespace.name), so renaming or moving one of these changes its wire identity.

// Host → clients: authoritative meter/escalation snapshot.
// arguments: [int scalar, int bosses, float meter, int level]
public class ForgeStateSyncMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.ApplyIncomingState(arguments);
}

// Placer → everyone else: an installed module's overlay, keyed by the MODULE's ViewID.
// arguments: [int moduleViewID, int level, string[] perkSlots, int[] burdens, int unused]
public class ModuleOverlayMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.ApplyIncomingModuleOverlay(arguments);
}

// Operator → everyone else: a relic or build box was docked into / pulled out of a Forge.
// arguments: [int forgeViewID, int itemViewID, int anchorIndex (-1 = module socket), bool docked]
public class ForgeDockMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.ApplyIncomingDock(arguments);
}

// Client → host: spend alloys on my behalf. No payload.
public class AlloySpendRequestMessage : ModMessage
{
    // Only the actor number crosses into the sync logic; keeping Photon's Player out is
    // what lets the handler be driven from a test.
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.HandleAlloySpendRequest(sender?.ActorNumber ?? 0);
}

// Host → requester: the outcome of an AlloySpendRequestMessage. The state broadcast alone
// says nothing on failure and gives no reason on success.
// arguments: [bool ok, string message]
public class AlloySpendResultMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.ApplyIncomingAlloySpendResult(arguments);
}

// Host → clients: which relics are cursed, keyed by PhotonView.ViewID. Sent
// length-1 live at each cursed spawn, and as a full batch to a joiner.
// arguments: [int[] viewIDs, int[] burdenTypes]
public class CursedRelicMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.ApplyIncomingCursed(arguments);
}

// Client → host (MasterClient): "I'm committing this box with these relics."
// Host re-derives tier/cursed itself and rolls authoritatively.
// arguments: [int boxViewID, int[] relicViewIDs]
public class CommitRequestMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.HandleCommitRequest(arguments, sender?.ActorNumber ?? 0);
}

// Host → all: authoritative commit result as a full box snapshot. Every client overwrites
// its snapshot; the operator also consumes. Reused for the late-joiner push, relicsConsumed=0.
// arguments: [int boxViewID, int level, string[] perkSlots, int[] burdens, int relicsConsumed]
public class CommitResultMessage : ModMessage
{
    public override void Handle(object[] arguments, Player sender)
        => ForgeNetSync.ApplyCommitResult(arguments);
}
