namespace VoidCrewTerminus.Forge;

public enum ForgePayload
{
    None,
    Relic,
    ModuleBox,
    Other,      // carrying something the Forge does not accept
}

// Every arm of the matrix resolves to exactly one of these plus at most one message.
public enum ForgeAction
{
    None,           // message only: a refusal, or an empty-handed status readout
    LoadModule,
    InsertRelic,
    RetrieveItem,
    Commit,         // resolve the upgrade here (we are the authority)
    RequestCommit,  // ask the host to resolve it
    FeedAlloy,
}

// The Forge as the rules need to see it: facts, with Unity stripped out.
public readonly struct ForgeView
{
    public bool HasModule { get; }
    public int SocketedBoxLevel { get; }        // Module Level of the box in the socket; 0 when empty
    public bool SocketedBoxHasViewId { get; }   // false = no network identity, so no commit can name it
    public int RelicCount { get; }
    public int Capacity { get; }                // Forge Capacity: the Forge Meter's level
    public bool IsAuthority { get; }            // true solo; decides commit-here vs ask-the-host

    public ForgeView(bool hasModule, int socketedBoxLevel, bool socketedBoxHasViewId,
                     int relicCount, int capacity, bool isAuthority)
    {
        HasModule = hasModule;
        SocketedBoxLevel = socketedBoxLevel;
        SocketedBoxHasViewId = socketedBoxHasViewId;
        RelicCount = relicCount;
        Capacity = capacity;
        IsAuthority = isAuthority;
    }
}

public readonly struct ForgeClick
{
    public ForgePayload Payload { get; }
    public int CarriedBoxLevel { get; }         // Module Level of the carried box; meaningless unless Payload is ModuleBox
    public ForgeInteractableKind Target { get; }

    // Physically holding an item (AnchorDock's answer). Two arms read this in opposite
    // directions: an insert is refused, an empty-handed click retrieves. A MISSING anchor is
    // therefore not reported here, or it would decide RetrieveItem with nothing to retrieve.
    public bool TargetOccupied { get; }

    public ForgeClick(ForgePayload payload, int carriedBoxLevel,
                      ForgeInteractableKind target, bool targetOccupied)
    {
        Payload = payload;
        CarriedBoxLevel = carriedBoxLevel;
        Target = target;
        TargetOccupied = targetOccupied;
    }
}

public readonly struct ForgeDecision
{
    public ForgeAction Action { get; }
    public string Message { get; }   // null = say nothing; the action speaks for itself

    public ForgeDecision(ForgeAction action, string message)
    {
        Action = action;
        Message = message;
    }

    public static ForgeDecision Say(string message) => new(ForgeAction.None, message);

    public static ForgeDecision Nothing => new(ForgeAction.None, null);
}

// Every rule about a Forge click, in one place and free of Unity: a MonoBehaviour method
// body can't be JIT-compiled in the test host. Everything decidable before touching the
// world is decided here; outcomes (commit result, alloy spend) are reported by the caller.
// Commit refusals route through ForgeLabels.DescribeCommit so host and client can't drift.
public static class ForgeInteractionPolicy
{
    public static ForgeDecision Decide(in ForgeView forge, in ForgeClick click)
    {
        // A mismatch names the right target instead of just refusing.
        switch (click.Payload)
        {
            case ForgePayload.ModuleBox:
                return DecideModuleBox(forge, click);
            case ForgePayload.Relic:
                return DecideRelic(forge, click);
            case ForgePayload.Other:
                return ForgeDecision.Say("The Forge only accepts relics and module boxes.");
        }

        // Retrieval goes through the anchor's own interactable rather than a ray into the
        // machine: the hull sits on a layer RaycastHandler's mask includes and would block it.
        // Stated ahead of the matrix because ForgeInteractable reads the same rule for its HUD
        // prompt. Keyed on TargetOccupied (physically pinned), not HasModule (bookkeeping).
        if (click.TargetOccupied && RetrievesWhenOccupied(click.Target))
            return new ForgeDecision(ForgeAction.RetrieveItem, null);

        switch (click.Target)
        {
            case ForgeInteractableKind.CommitButton:
                return DecideCommit(forge);

            case ForgeInteractableKind.AlloyTerminal:
                // Whether the spend succeeds depends on the ship's supplies and on
                // who owns them, so the terminal reports its own result.
                return new ForgeDecision(ForgeAction.FeedAlloy, null);

            case ForgeInteractableKind.ModuleSocket:
                return ForgeDecision.Say("Deconstruct a module and place its build box here to upgrade it.");

            case ForgeInteractableKind.RelicTube:
                return DescribeStatus(forge);

            default:
                return ForgeDecision.Nothing;
        }
    }

    // The commit lever and alloy terminal never hold anything, so they are not retrieval
    // targets. Public so ForgeInteractable labels its prompt from the rule that decides the click.
    public static bool RetrievesWhenOccupied(ForgeInteractableKind kind) =>
        kind is ForgeInteractableKind.ModuleSocket or ForgeInteractableKind.RelicTube;

    private static ForgeDecision DecideModuleBox(in ForgeView forge, in ForgeClick click)
    {
        if (click.Target != ForgeInteractableKind.ModuleSocket)
            return ForgeDecision.Say("Place module boxes on the Forge's module socket.");
        if (forge.HasModule)
            return ForgeDecision.Say("The Forge already holds a module box.");

        // The level quoted is the carried box's: the socket's own level is 0 until this lands.
        return new ForgeDecision(ForgeAction.LoadModule,
            $"Module loaded (L{click.CarriedBoxLevel}). Insert relics and commit to upgrade.");
    }

    private static ForgeDecision DecideRelic(in ForgeView forge, in ForgeClick click)
    {
        if (click.Target != ForgeInteractableKind.RelicTube)
            return ForgeDecision.Say("Insert relics into the relic tubes.");

        // Tube before capacity: "the Forge is full" would send the player away from a Forge
        // that would happily take the relic one tube over.
        if (click.TargetOccupied)
            return ForgeDecision.Say("That tube is occupied — pick an empty one.");
        if (forge.RelicCount >= forge.Capacity)
            return ForgeDecision.Say($"The Forge is full ({forge.RelicCount}/{forge.Capacity} relics).");

        // Counts and projection read as they will after the insert: the message describes the
        // Forge the player is about to be looking at.
        int after = forge.RelicCount + 1;
        return new ForgeDecision(ForgeAction.InsertRelic,
            $"Relic inserted ({after}/{forge.Capacity}). Projected level: L{Projected(forge, after)}.");
    }

    private static ForgeDecision DecideCommit(in ForgeView forge)
    {
        // Checked here rather than once per path, so host and client refuse on the same facts
        // with the same words. These are the guards ForgeCommit.Execute would hit anyway.
        if (!forge.HasModule) return Refuse(CommitStatus.NoModule, forge);
        if (!forge.SocketedBoxHasViewId) return Refuse(CommitStatus.MissingViewId, forge);
        if (forge.RelicCount == 0) return Refuse(CommitStatus.NoRelics, forge);

        // The roll is host-authoritative: cursed markers and RNG live there. Solo counts as
        // authority, so single-player runs inline.
        return forge.IsAuthority
            ? new ForgeDecision(ForgeAction.Commit, null)   // the outcome does the talking
            : new ForgeDecision(ForgeAction.RequestCommit, "Requesting upgrade from the host…");
    }

    private static ForgeDecision Refuse(CommitStatus status, in ForgeView forge)
    {
        var lines = ForgeLabels.DescribeCommit(
            CommitOutcome.Failure(status), forge.SocketedBoxLevel, forge.RelicCount);
        return ForgeDecision.Say(lines.Count > 0 ? lines[0] : null);
    }

    private static ForgeDecision DescribeStatus(in ForgeView forge) =>
        ForgeDecision.Say(forge.HasModule
            ? $"Forge: L{forge.SocketedBoxLevel} module loaded, {forge.RelicCount}/{forge.Capacity} relics, " +
              $"projected L{Projected(forge, forge.RelicCount)}."
            : $"Forge: no module loaded, {forge.RelicCount}/{forge.Capacity} relics.");

    // With no module socketed this reports the curve's floor because MaxReachable clamps its
    // from-level; relics are legitimately staged in the tubes before the box arrives.
    private static int Projected(in ForgeView forge, int relicCount) =>
        ForgeCostCurve.MaxReachable(forge.SocketedBoxLevel, relicCount);
}
