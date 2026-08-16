using System.Collections.Generic;
using CG;
using CG.Client.Player.Interactions;
using CG.Client.Ship.Interactions;
using CG.Game.Player;
using CG.Input;
using Client.Player.Interactions;
using HarmonyLib;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

public enum ForgeInteractableKind
{
    RelicTube,     // insert a held relic / status when empty-handed
    ModuleSocket,  // load a held BuildBox / commit when empty-handed
    CommitButton,  // commit when empty-handed, held not clicked (CommitTarget anchor — see ForgeCommitInteractable)
    AlloyTerminal, // feed alloys into the Forge Meter (AlloyTarget anchor)
}

// Raycast target for the player's interaction system. AbstractInteractable instances
// are picked up by RaycastHandler via their collider (layer "InteractiveObjects"),
// and clicks reach us through the CarryableInteract.StartInteraction prefix in
// ForgeInteractionPatch. Created at runtime by UpgradeForgeBehavior.BuildInteractables —
// the shipped prefab carries only named anchor transforms, no game components.
//
// Covers every Forge interactable EXCEPT the Commit button, which is a
// ForgeCommitInteractable instead (a hold-to-confirm gate on an irreversible
// action needs a different vanilla base class entirely). ForgeInteractableKind.
// CommitButton still exists as a value here because ForgeInteractionPolicy's
// click matrix is keyed by it regardless of which component originates the interaction.
public class ForgeInteractable : AbstractInteractable
{
    public UpgradeForgeBehavior Forge;
    public ForgeInteractableKind Kind;
    public Transform Anchor;

    // Click colliders surround items docked on their anchors and would swallow
    // every click aimed at them. When an anchor is occupied and the player's
    // hands are empty, step aside: RaycastHandler then skips this trigger and
    // the ray reaches the docked item's own Grabbable for retrieval.
    public override bool IsInteractive
    {
        get
        {
            if (!base.IsInteractive) return false;
            if (Forge == null) return true;

            bool occupied = Kind switch
            {
                ForgeInteractableKind.ModuleSocket => Forge.HasModule,
                ForgeInteractableKind.RelicTube => Forge.IsAnchorOccupied(Anchor),
                _ => false,
            };
            if (occupied)
            {
                var player = LocalPlayer.Instance;
                if (player != null && player.Payload == null) return false;
            }
            return true;
        }
        set => base.IsInteractive = value;
    }

    // No outline yet, deliberately: this covers RelicTube/ModuleSocket/AlloyTerminal,
    // and which mesh each should scope its outline to hasn't been decided/modeled
    // yet. base.Highlighted (empty, harmless) is still called to keep the override
    // chain intact for whenever this is revisited.
    public override void Highlighted(bool isHighlighted)
    {
        base.Highlighted(isHighlighted);
    }

    // HUD prompt assets are serialized private fields on vanilla components, so we
    // borrow them from whatever CarryablesSocketActor the ship already has (every
    // ship has sockets — fabricator, void drive). Falls back to an empty
    // InteractionInfo, which the HUD renders as no prompt rather than crashing.
    private static InteractionInfo _insertInfo;
    private static InteractionInfo _defaultInfo;
    private static InteractionInfo _commitInfo;
    private static InteractionInfo _alloyInfo;
    private static InteractionInfo _deconstructInfo;
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

    // Not a ForgeInteractableKind — ForgeDeconstructInteractable isn't part of the
    // click-decision matrix ForgeInteractionPolicy routes (it's its own hold gesture,
    // not something a player's carried payload can target), so it doesn't need a
    // policy-routed kind, just a HUD prompt built the same way.
    public static InteractionInfo DeconstructInfo()
    {
        EnsureInfos();
        return _deconstructInfo;
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
        // Hold, not Press — ForgeCommitInteractable requires a completed hold
        // (see its doc comment), so the HUD prompt should say so.
        _commitInfo = ActionInfo("Commit", InteractionDescription.EInteractionType.Hold);
        _alloyInfo = ActionInfo("Feed Alloy");
        _deconstructInfo = ActionInfo("Deconstruct", InteractionDescription.EInteractionType.Hold);
    }

    private static InteractionInfo EmptyInfo()
    {
        var info = ScriptableObject.CreateInstance<InteractionInfo>();
        info.Interactions = new List<InteractionDescription>();
        return info;
    }

    // CommitButton/AlloyTerminal have no vanilla InteractionInfo asset to borrow, so
    // build one from _insertInfo's key binding with our own label swapped in.
    // interactionType lets a caller override the borrowed Press default — Commit
    // needs Hold (see ForgeCommitInteractable).
    //
    // For Hold-type prompts specifically, the Key must NOT be borrowed from
    // _insertInfo: KeyBindVE.Init resolves its icon by taking Key.FallBackString and
    // looking it up BY NAME as an InputAction (InputService.FindActionKey), and
    // _insertInfo's Key names the regular click/interact action — not
    // HoldClickerInteractable's separate InputActionReferences.HoldAction that Commit
    // and Deconstruct actually listen on. Borrowing it made the HUD show the click
    // action's icon even with InteractionType correctly set to Hold. Built fresh
    // instead from the Hold action's own live name so a rebind can't desync it.
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
