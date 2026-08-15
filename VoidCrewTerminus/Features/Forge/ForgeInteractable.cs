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
// action needs a different vanilla base class entirely — see its doc comment).
// ForgeInteractableKind.CommitButton still exists as a value here because
// ForgeInteractionPolicy's click matrix is keyed by it regardless of which
// component originates the interaction.
public class ForgeInteractable : AbstractInteractable
{
    public UpgradeForgeBehavior Forge;
    public ForgeInteractableKind Kind;
    public Transform Anchor;

    // Click colliders surround the items docked on their anchors, so they would
    // swallow every click aimed at those items. When an anchor is occupied and the
    // player's hands are empty, step aside: RaycastHandler then skips this trigger
    // and the ray reaches the docked item's own Grabbable — retrieval of the
    // BuildBox (before or after committing) and of relics is the vanilla grab.
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

    // No outline yet, deliberately: unlike Commit (LeverBox) and Deconstruct
    // (Handle), this covers RelicTube/ModuleSocket/AlloyTerminal, and which mesh
    // each of those should scope its outline to hasn't been decided/modeled yet.
    // Whole-module outlining for these was an assumption-of-convenience, not a
    // requested design — pulled until that's actually settled, rather than
    // outlining the whole Forge on every tube/socket/terminal hover in the
    // meantime. base.Highlighted is still called (AbstractInteractable's; empty,
    // harmless) to keep the override chain intact for whenever this is revisited.
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

    // CommitButton/AlloyTerminal have no vanilla InteractionInfo asset to borrow, so build
    // one: same interact key binding as _insertInfo (the same F prompt already shown on the
    // relic tubes and module socket — vanilla's generic "action key", e.g. toggling a
    // module's power) with our own label swapped in, instead of the default's empty
    // Interactions list, which the HUD renders as no prompt at all. interactionType lets a
    // caller override the borrowed Press default — Commit needs Hold (see ForgeCommitInteractable).
    //
    // KeyBindVE.Init resolves its icon by taking InteractionDescription.Key.FallBackString,
    // stripping "<keybind>...</keybind>" tags, and looking THAT UP BY NAME as an InputAction
    // (InputService.FindActionKey — a live search through InputActionAsset.actionMaps, not
    // just a display string). _insertInfo's own Key names vanilla's regular click/interact
    // action — correct for Press-type prompts (RelicTube/ModuleSocket/AlloyTerminal all
    // borrow it verbatim), but Commit and Deconstruct are actually driven by
    // HoldClickerInteractable's SEPARATE InputActionReferences.HoldAction (see that class),
    // a different bound key/mouse-button than the click action. Borrowing the click action's
    // Key for a Hold-type prompt made KeyBindVE resolve and display the CLICK action's icon
    // (in practice a mouse-click glyph) instead of the Hold action's — even though
    // InteractionType was already correctly set to Hold. Built fresh from the Hold action's
    // OWN live name (read off ServiceBase<InputService>.Instance.InputActionReferences.HoldAction
    // rather than hardcoded, so a rebind/rename in the Input Actions asset can't silently
    // desync this from what HoldClickerInteractable itself actually listens on) instead of
    // reusing source.Key for Hold-type prompts specifically.
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
