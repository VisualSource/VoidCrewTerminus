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

// Raycast target for the player's interaction system: RaycastHandler picks these up
// by collider (layer "InteractiveObjects") and clicks arrive through the
// CarryableInteract prefix in ForgeInteractionPatch. Built at runtime by
// BuildInteractables — the shipped prefab carries only anchor transforms.
//
// Covers every Forge interactable EXCEPT the Commit button, which needs a different
// vanilla base class for its hold gate. ForgeInteractableKind.CommitButton still
// exists here because the policy's click matrix is keyed by it regardless of which
// component originates the interaction.
public class ForgeInteractable : AbstractInteractable
{
    public UpgradeForgeBehavior Forge;
    public ForgeInteractableKind Kind;
    public Transform Anchor;

    // Click colliders surround the items docked on their anchors and would swallow
    // every click aimed at them. When occupied and the player's hands are empty, step
    // aside so the ray reaches the docked item's own Grabbable for retrieval.
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

    // No outline yet, deliberately — which mesh RelicTube/ModuleSocket/AlloyTerminal
    // should each scope to hasn't been modeled. base.Highlighted is empty and
    // harmless; called to keep the override chain intact for when this is revisited.
    public override void Highlighted(bool isHighlighted)
    {
        base.Highlighted(isHighlighted);
    }

    // HUD prompt assets are private serialized fields, so borrow them from whatever
    // CarryablesSocketActor the ship already has (every ship has sockets). Falls back
    // to an empty InteractionInfo, which the HUD renders as no prompt rather than
    // crashing.
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

    // CommitButton/AlloyTerminal have no vanilla InteractionInfo to borrow, so build
    // one from _insertInfo's key binding with our own label swapped in.
    //
    // For Hold prompts the Key must NOT be borrowed: KeyBindVE.Init resolves its icon
    // by looking up Key.FallBackString BY NAME as an InputAction, and _insertInfo's
    // Key names the click action, not the separate HoldAction that Commit and
    // Deconstruct listen on — so the HUD showed the click icon even with
    // InteractionType set to Hold. Built fresh from the Hold action's own live name,
    // so a rebind can't desync it.
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
