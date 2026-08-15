using CG.Game.Player;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// The Forge's Commit button, held rather than clicked — committing consumes relics
// irreversibly, so an accidental tap on the wrong target shouldn't be able to fire
// it the way an accidental click could. Uses the same vanilla mechanism as module
// deconstruction: HoldClickerInteractable, driven by EnvironmentInteract (the F/Hold
// action) rather than CarryableInteract (fire1Action — the click every other Forge
// interactable still uses). HoldCompleted only fires once the Input System's
// configured Hold interaction on InputActionReferences.HoldAction actually
// completes; releasing early never reaches it at all.
//
// Deliberately NOT a ForgeInteractable — ClickerInteractable is a SIBLING branch off
// AbstractInteractable (see ForgeInteractable's own doc comment), not a base either
// shares — so this can't reuse that class. Built at runtime with no prefab-authored
// Inspector data, same as every other Forge interactable, which matters for two
// ClickerInteractable fields specifically: UpgradeForgeBehavior must set
// DontSelfSetInteractionInfo before ClickerInteractable.Start() runs SetClickable(),
// or Start() overwrites the InteractionInfo assignment with the null it captured
// back in Awake(); and Highlighted() is overridden here to skip
// ClickerInteractable's own implementation entirely, which iterates an
// `outlineObjects` array nothing ever populates outside the Unity Inspector.
public class ForgeCommitInteractable : HoldClickerInteractable
{
    public UpgradeForgeBehavior Forge;
    public Transform Anchor;

    // The lever's own visual mesh (UpgradeForgeBehavior.CommitLeverBoxName —
    // "LeverBox" — buried in the FBX hierarchy like Handle). Scopes the outline
    // highlight to just the lever instead of the whole module (see
    // Highlighted/ForgeOutline). Null if the prefab has no LeverBox — falls back
    // to outlining the whole module rather than showing nothing.
    public Transform OutlineTarget;

    private bool _holding;

    public override void Awake()
    {
        base.Awake();
        HoldCompleted += OnCommit;
    }

    // Subscribed once here, in Awake — guaranteed to run exactly once for this
    // GameObject's lifetime — rather than by whoever builds this component.
    // UpgradeForgeBehavior.BuildInteractables() is not a run-once initializer: it
    // re-runs (reusing this same component via GetComponent) on every hot-reload
    // attach, so subscribing there would stack a new handler on every reload.
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

    // HoldClickerInteractable.StartClick subscribes onto a single GLOBAL
    // InputAction shared by every Hold-driven interactable in the game — see
    // ForgeDeconstructInteractable's OnDestroy/OnDeconstruct doc comments for
    // the full leak mechanism (same base class, same risk here: a leaked
    // subscription would fire an unwanted Commit whenever ANY unrelated Hold
    // completes anywhere). Same two-part defense: force-unsubscribe on destroy,
    // and gate the action on our own locally-tracked hold state so a stray
    // callback is a no-op rather than an unwanted commit.
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

    // Uses ForgeOutline instead of calling base.Highlighted — see ForgeOutline's
    // doc comment for why base.Highlighted (ClickerInteractable's own, iterating
    // an outlineObjects[] array only the Unity Inspector ever populates) can't
    // be used on a runtime-built interactable like this one. Scoped to just
    // OutlineTarget (the lever mesh) rather than the whole module, unlike the
    // other Forge interactables — Commit is a distinct, separately-modeled part
    // the player should see highlighted on its own.
    public override void Highlighted(bool isHighlighted)
    {
        var target = OutlineTarget != null ? OutlineTarget : (Forge != null ? Forge.transform : null);
        ForgeOutline.SetHighlighted(target, isHighlighted);
    }
}
