namespace VoidCrewTerminus.Forge;

// What the player is carrying when they click a Forge interactable.
public enum ForgePayload
{
    None,       // empty-handed
    Relic,
    ModuleBox,
    Other,      // carrying something the Forge does not accept
}

// What the Forge should DO about a click. Every arm of the matrix resolves to
// exactly one of these plus at most one message.
public enum ForgeAction
{
    None,           // message only — a refusal, or an empty-handed status readout
    LoadModule,     // take the carried box into the module socket and dock it
    InsertRelic,    // take the carried relic into the clicked tube and dock it
    Commit,         // resolve the upgrade here (we are the authority)
    RequestCommit,  // ask the host to resolve it
    FeedAlloy,      // spend alloys into the Forge Meter
}

// The Forge as the rules need to see it: facts, with Unity stripped out.
public readonly struct ForgeView
{
    public bool HasModule { get; }
    public int SocketedBoxLevel { get; }        // Module Level of the box in the socket; 0 when empty
    public bool SocketedBoxHasViewId { get; }   // false = no network identity, so no commit can name it
    public int RelicCount { get; }
    public int Capacity { get; }                // Forge Capacity — the Forge Meter's level
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

// The click itself.
public readonly struct ForgeClick
{
    public ForgePayload Payload { get; }
    public int CarriedBoxLevel { get; }         // Module Level of the carried box; meaningless unless Payload is ModuleBox
    public ForgeInteractableKind Target { get; }

    // The clicked anchor cannot take anything. A MISSING anchor reports as
    // occupied too: both mean "not this tube", and they share one refusal.
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

    // A refusal or a readout: nothing happens, the player is told why.
    public static ForgeDecision Say(string message) => new(ForgeAction.None, message);

    public static ForgeDecision Nothing => new(ForgeAction.None, null);
}

// Every rule about what a click on the Upgrade Forge means, in one place and free
// of Unity.
//
// These rules previously lived inline in UpgradeForgeBehavior.HandleInteraction,
// where nothing could reach them: a MonoBehaviour method body cannot even be
// JIT-compiled in the test host, so the entire payload x target matrix — which
// target each payload is legal on, which refusal wins when two apply, what the
// counts read after an insert, whether a commit runs here or goes to the host —
// was verifiable only by standing in front of a Forge in-game and clicking.
//
// The split is: everything decidable BEFORE touching the world is decided here;
// everything that depends on the outcome of touching it (the commit result, the
// alloy spend) is reported by the caller afterwards. So this owns the wording of
// every refusal and every readout, and none of the wording of a result.
//
// Commit refusals deliberately route through ForgeLabels.DescribeCommit rather
// than carrying their own strings. The client path used to keep private copies of
// them, and answered a box with no network identity with "load a module box",
// which was simply wrong — both paths now refuse identically, which is the drift
// ForgeLabels exists to prevent.
public static class ForgeInteractionPolicy
{
    public static ForgeDecision Decide(in ForgeView forge, in ForgeClick click)
    {
        // Carrying something: the payload decides which target is legal, and a
        // mismatch names the right target instead of just refusing.
        switch (click.Payload)
        {
            case ForgePayload.ModuleBox:
                return DecideModuleBox(forge, click);
            case ForgePayload.Relic:
                return DecideRelic(forge, click);
            case ForgePayload.Other:
                return ForgeDecision.Say("The Forge only accepts relics and module boxes.");
        }

        // Empty-handed. Commit lives on its own button; docked relics and the
        // docked box are retrieved by grabbing them directly — the module socket's
        // ForgeInteractable.IsInteractive steps aside while it holds a box, so an
        // empty-hand click only reaches the socket when the socket is empty.
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

    private static ForgeDecision DecideModuleBox(in ForgeView forge, in ForgeClick click)
    {
        if (click.Target != ForgeInteractableKind.ModuleSocket)
            return ForgeDecision.Say("Place module boxes on the Forge's module socket.");
        if (forge.HasModule)
            return ForgeDecision.Say("The Forge already holds a module box.");

        // The level quoted is the CARRIED box's, which is what the socket is about
        // to hold — the socket's own level is 0 until this lands.
        return new ForgeDecision(ForgeAction.LoadModule,
            $"Module loaded (L{click.CarriedBoxLevel}). Insert relics and commit to upgrade.");
    }

    private static ForgeDecision DecideRelic(in ForgeView forge, in ForgeClick click)
    {
        if (click.Target != ForgeInteractableKind.RelicTube)
            return ForgeDecision.Say("Insert relics into the relic tubes.");

        // Tube before capacity: clicking a full tube while the Forge still has room
        // is a mis-aim, and "the Forge is full" would send the player away from a
        // Forge that would happily take the relic one tube over.
        if (click.TargetOccupied)
            return ForgeDecision.Say("That tube is occupied — pick an empty one.");
        if (forge.RelicCount >= forge.Capacity)
            return ForgeDecision.Say($"The Forge is full ({forge.RelicCount}/{forge.Capacity} relics).");

        // Counts and projection are stated as they will read AFTER the insert — the
        // message describes the Forge the player is about to be looking at.
        int after = forge.RelicCount + 1;
        return new ForgeDecision(ForgeAction.InsertRelic,
            $"Relic inserted ({after}/{forge.Capacity}). Projected level: L{Projected(forge, after)}.");
    }

    private static ForgeDecision DecideCommit(in ForgeView forge)
    {
        // Checked here rather than once per path, so the host and a client refuse
        // on the same facts with the same words. On the host these are the same
        // three guards ForgeCommit.Execute would have hit a moment later.
        if (!forge.HasModule) return Refuse(CommitStatus.NoModule, forge);
        if (!forge.SocketedBoxHasViewId) return Refuse(CommitStatus.MissingViewId, forge);
        if (forge.RelicCount == 0) return Refuse(CommitStatus.NoRelics, forge);

        // The roll is host-authoritative — cursed markers and RNG live there
        // (Phase 8-C). Solo counts as authority, so single-player runs inline.
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

    // With no module socketed this reports the curve's floor (L3) rather than
    // nothing, because MaxReachable clamps its from-level. Kept as it was: relics
    // are legitimately staged in the tubes before the box arrives.
    private static int Projected(in ForgeView forge, int relicCount) =>
        ForgeCostCurve.MaxReachable(forge.SocketedBoxLevel, relicCount);
}
