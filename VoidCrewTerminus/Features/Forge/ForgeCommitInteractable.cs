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

    private Transform _highlight;
    private bool _highlightResolved;

    protected override void Awake()
    {
        base.Awake();
        HoldCompleted += OnCommit;
    }

    // Subscribed once here, in Awake — guaranteed to run exactly once for this
    // GameObject's lifetime — rather than by whoever builds this component.
    // UpgradeForgeBehavior.BuildInteractables() is not a run-once initializer: it
    // re-runs (reusing this same component via GetComponent) on every hot-reload
    // attach, so subscribing there would stack a new handler on every reload.
    private void OnCommit()
    {
        if (Forge == null) return;
        var player = LocalPlayer.Instance;
        if (player == null) return;
        Forge.HandleInteraction(ForgeInteractableKind.CommitButton, Anchor, player);
    }

    // Mirrors ForgeInteractable.Highlighted's own convention — a disabled
    // "Highlight" child toggled on hover — instead of calling base.Highlighted.
    protected override void Highlighted(bool isHighlighted)
    {
        if (!_highlightResolved && Anchor != null)
        {
            _highlight = ForgeAnchors.FindDeep(Anchor, ForgeAnchors.HighlightName);
            _highlightResolved = true;
        }
        if (_highlight != null) _highlight.gameObject.SetActive(isHighlighted);
    }
}
