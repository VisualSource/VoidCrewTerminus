using CG.Game.Player;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// The Forge's Commit button, held rather than clicked — committing consumes relics
// irreversibly, so an accidental tap shouldn't be able to fire it. Same vanilla
// mechanism as module deconstruction: HoldClickerInteractable driven by
// EnvironmentInteract's Hold action, not CarryableInteract's click. HoldCompleted
// only fires once the configured Hold interaction completes; an early release never
// reaches it.
//
// Deliberately NOT a ForgeInteractable — ClickerInteractable is a SIBLING branch off
// AbstractInteractable, not a shared base. Being built at runtime with no Inspector
// data matters for two of its fields: DontSelfSetInteractionInfo must be set before
// Start() (see UpgradeForgeBehavior.CreateCommitInteractable), and Highlighted() is
// overridden below to skip an outlineObjects array nothing populates outside the
// Unity Inspector.
public class ForgeCommitInteractable : HoldClickerInteractable
{
    public UpgradeForgeBehavior Forge;
    public Transform Anchor;

    // The lever's own visual mesh (UpgradeForgeBehavior.CommitLeverBoxName —
    // "LeverBox" — buried in the FBX hierarchy). Scopes the outline highlight to
    // just the lever instead of the whole module. Null if the prefab has no
    // LeverBox — falls back to outlining the whole module.
    public Transform OutlineTarget;

    private bool _holding;

    public override void Awake()
    {
        base.Awake();
        HoldCompleted += OnCommit;
    }

    // HoldCompleted is subscribed in Awake, not by whoever builds this component:
    // BuildInteractables() re-runs on every hot-reload attach and reuses this same
    // component, so subscribing there would stack a handler each time.
    public override void StartClick()
    {
        base.StartClick();
        _holding = true;
        BepinPlugin.Log.LogDebug($"[Forge] Commit StartClick (GetInstanceID={GetInstanceID()}).");
    }

    public override void EndClick()
    {
        base.EndClick();
        _holding = false;
        BepinPlugin.Log.LogDebug($"[Forge] Commit EndClick (GetInstanceID={GetInstanceID()}).");
    }

    // StartClick subscribes onto a single GLOBAL InputAction shared by every
    // Hold-driven interactable, so a leaked subscription would fire an unwanted
    // Commit whenever ANY unrelated Hold completes. Two-part defense, same as
    // ForgeDeconstructInteractable: force-unsubscribe on destroy, and gate on our own
    // _holding flag so a stray callback is a no-op.
    public override void OnDestroy()
    {
        base.OnDestroy();
        EndClick();
    }

    private void OnCommit()
    {
        BepinPlugin.Log.LogInfo($"[Forge] OnCommit fired (GetInstanceID={GetInstanceID()}), _holding={_holding}.");
        if (!_holding || Forge == null) return;
        var player = LocalPlayer.Instance;
        if (player == null) return;
        Forge.HandleInteraction(ForgeInteractableKind.CommitButton, Anchor, player);
    }

    // ForgeOutline instead of base.Highlighted, which NREs on a runtime-built
    // component (see ForgeOutline). Scoped to the lever mesh rather than the whole
    // module — Commit is a separately-modeled part.
    public override void Highlighted(bool isHighlighted)
    {
        var target = OutlineTarget != null ? OutlineTarget : (Forge != null ? Forge.transform : null);
        ForgeOutline.SetHighlighted(target, isHighlighted);
    }
}
