using System.Collections.Generic;
using CG;
using CG.Client.Player.Interactions;
using CG.Client.Ship.Interactions;
using CG.Input;
using Client.Player.Interactions;
using HarmonyLib;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

public enum ForgeInteractableKind
{
    RelicTube,
    ModuleSocket,
    CommitButton,  // held, not clicked; see ForgeCommitInteractable
    AlloyTerminal,
}

// Raycast target on layer "InteractiveObjects"; clicks arrive through the CarryableInteract
// prefix. Covers every Forge interactable except the Commit button, which needs a different
// base for its hold gate; that kind still lives here because the policy's matrix is keyed by it.
public class ForgeInteractable : AbstractInteractable
{
    public UpgradeForgeBehavior Forge;
    public ForgeInteractableKind Kind;
    public Transform Anchor;

    // Tracked so the assignment below happens only on a flip: the setter fires
    // InteractionInfoUpdated, which the HUD listens to.
    private bool _promptShowsRetrieve;

    // The prompt must say which of the two things a click will do, since this interactable
    // stays targetable while it holds something. Polled rather than pushed: AnchorDock signals
    // only into the anchor's "Filled" helper, and the poll is a compare against Capacity entries.
    private void Update()
    {
        if (Forge == null) return;

        bool retrieve = ForgeInteractionPolicy.RetrievesWhenOccupied(Kind)
                        && Forge.IsAnchorOccupied(Anchor);
        if (retrieve == _promptShowsRetrieve) return;

        _promptShowsRetrieve = retrieve;
        InteractionInfo = retrieve ? RetrieveInfo() : InfoFor(Kind);
    }

    // Deliberately no outline yet: which mesh each kind should scope to isn't modeled. The
    // override is kept so the chain is intact when it is.
    public override void Highlighted(bool isHighlighted)
    {
        base.Highlighted(isHighlighted);
    }

    // HUD prompt assets are private serialized fields, so they're borrowed from any
    // CarryablesSocketActor on the ship; an empty InteractionInfo renders as no prompt.
    private static InteractionInfo _insertInfo;
    private static InteractionInfo _defaultInfo;
    private static InteractionInfo _commitInfo;
    private static InteractionInfo _alloyInfo;
    private static InteractionInfo _deconstructInfo;
    private static InteractionInfo _retrieveInfo;
    private static bool _infosResolved;

    public static InteractionInfo InfoFor(ForgeInteractableKind kind)
    {
        EnsureInfos();
        return kind switch
        {
            ForgeInteractableKind.RelicTube or ForgeInteractableKind.ModuleSocket => _insertInfo,
            ForgeInteractableKind.CommitButton => _commitInfo,
            ForgeInteractableKind.AlloyTerminal => _alloyInfo,
            _ => _defaultInfo,
        };
    }

    // Not a ForgeInteractableKind: deconstruct is its own hold gesture, not something a
    // carried payload can target, so it needs a HUD prompt but no policy-routed kind.
    public static InteractionInfo DeconstructInfo()
    {
        EnsureInfos();
        return _deconstructInfo;
    }

    // Not a ForgeInteractableKind: retrieval is decided by occupancy, not by which anchor it is.
    private static InteractionInfo RetrieveInfo()
    {
        EnsureInfos();
        return _retrieveInfo;
    }

    private static void EnsureInfos()
    {
        if (_infosResolved && _insertInfo != null && _defaultInfo != null) return;
        _infosResolved = true;

        var actor = Object.FindObjectOfType<CarryablesSocketActor>();
        if (actor != null)
        {
            _insertInfo = AccessTools.Field(typeof(CarryablesSocketActor), "insertInteractionInfo")
                ?.GetValue(actor) as InteractionInfo;
            _defaultInfo = AccessTools.Field(typeof(CarryablesSocketActor), "defaultInteractionInfo")
                ?.GetValue(actor) as InteractionInfo;
        }

        _insertInfo ??= EmptyInfo();
        _defaultInfo ??= EmptyInfo();
        // Hold, not Press: ForgeCommitInteractable requires a completed hold.
        _commitInfo = ActionInfo("Commit", InteractionDescription.EInteractionType.Hold);
        _alloyInfo = ActionInfo("Feed Alloy");
        _deconstructInfo = ActionInfo("Deconstruct", InteractionDescription.EInteractionType.Hold);
        _retrieveInfo = ActionInfo("Retrieve");
    }

    private static InteractionInfo EmptyInfo()
    {
        var info = ScriptableObject.CreateInstance<InteractionInfo>();
        info.Interactions = new List<InteractionDescription>();
        return info;
    }

    // For Hold prompts the Key must NOT be borrowed: KeyBindVE.Init resolves its icon by
    // looking up Key.FallBackString by name as an InputAction, and _insertInfo's Key names the
    // click action, not the HoldAction. Built from the Hold action's live name so a rebind
    // can't desync it.
    private static InteractionInfo ActionInfo(string label, InteractionDescription.EInteractionType? interactionType = null)
    {
        var info = ScriptableObject.CreateInstance<InteractionInfo>();
        var source = _insertInfo.Interactions is { Count: > 0 } ? _insertInfo.Interactions[0] : null;

        DefaultableLocalizedString key;
        if (interactionType == InteractionDescription.EInteractionType.Hold)
        {
            var holdActionName = ServiceBase<InputService>.Instance.InputActionReferences.HoldAction.action.name;
            BepinPlugin.Log.LogDebug($"[Forge] Hold prompt \"{label}\" bound to action name \"{holdActionName}\".");
            key = new DefaultableLocalizedString { FallBackString = $"<keybind>{holdActionName}</keybind>" };
        }
        else
        {
            key = source?.Key ?? new DefaultableLocalizedString { FallBackString = "Interact" };
        }

        info.Interactions = new List<InteractionDescription>
        {
            new()
            {
                InteractionType = interactionType ?? source?.InteractionType ?? InteractionDescription.EInteractionType.Press,
                Key = key,
                Description = new DefaultableLocalizedString { FallBackString = label },
            },
        };
        return info;
    }
}
