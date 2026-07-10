using System.Collections.Generic;
using CG.Client.Player.Interactions;
using CG.Client.Ship.Interactions;
using CG.Game.Player;
using Client.Player.Interactions;
using HarmonyLib;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

public enum ForgeInteractableKind
{
    RelicTube,     // insert a held relic / status when empty-handed
    ModuleSocket,  // load a held BuildBox / commit when empty-handed
    CommitButton,  // commit when empty-handed (CommitTarget anchor)
    AlloyTerminal, // feed alloys into the Forge Meter (AlloyTarget anchor)
}

// Raycast target for the player's interaction system. AbstractInteractable instances
// are picked up by RaycastHandler via their collider (layer "InteractiveObjects"),
// and clicks reach us through the CarryableInteract.StartInteraction prefix in
// ForgeInteractionPatch. Created at runtime by UpgradeForgeBehavior.BuildInteractables —
// the shipped prefab carries only named anchor transforms, no game components.
public class ForgeInteractable : AbstractInteractable
{
    public UpgradeForgeBehavior Forge;
    public ForgeInteractableKind Kind;
    public Transform Anchor;

    private Transform _highlight;
    private bool _highlightResolved;

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

    // Optional prefab-authored hover feedback: a disabled child named "Highlight"
    // under the anchor is shown while the player's raycast targets this interactable.
    // Resolved lazily — Anchor is assigned after AddComponent runs Awake.
    public override void Highlighted(bool isHighlighted)
    {
        base.Highlighted(isHighlighted);
        if (!_highlightResolved && Anchor != null)
        {
            _highlight = UpgradeForgeBehavior.FindDeep(Anchor, "Highlight");
            _highlightResolved = true;
        }
        if (_highlight != null) _highlight.gameObject.SetActive(isHighlighted);
    }

    // HUD prompt assets are serialized private fields on vanilla components, so we
    // borrow them from whatever CarryablesSocketActor the ship already has (every
    // ship has sockets — fabricator, void drive). Falls back to an empty
    // InteractionInfo, which the HUD renders as no prompt rather than crashing.
    private static InteractionInfo _insertInfo;
    private static InteractionInfo _defaultInfo;
    private static bool _infosResolved;

    public static InteractionInfo InfoFor(ForgeInteractableKind kind)
    {
        EnsureInfos();
        return kind == ForgeInteractableKind.RelicTube || kind == ForgeInteractableKind.ModuleSocket
            ? _insertInfo
            : _defaultInfo;
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
    }

    private static InteractionInfo EmptyInfo()
    {
        var info = ScriptableObject.CreateInstance<InteractionInfo>();
        info.Interactions = new List<InteractionDescription>();
        return info;
    }
}
