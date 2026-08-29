using CG.Client.Ship.Interactions;
using CG.Game.Player;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// The Forge's Commit button, held rather than clicked — committing consumes relics
// irreversibly, so an accidental tap shouldn't be able to fire it.
//
// Extends ClickerInteractable directly, NOT HoldClickerInteractable, and times the
// hold itself via ForgeHoldGate. HoldClickerInteractable fires on the global
// HoldAction's own duration — the short generic "hold F" — which is far too quick to
// gate an irreversible action, and it cannot be lengthened because that duration is
// shared with every other prompt in the game. Vanilla's own levers don't use it
// either (see ForgeHoldGate). EnvironmentInteract drives StartClick/EndClick off any
// ClickerInteractable, so dropping down a level costs nothing.
//
// Dropping HoldClickerInteractable also removes this component from the global
// HoldAction subscription entirely, which is what made a leaked subscription able to
// fire an unwanted Commit whenever ANY unrelated hold completed. The gate is local
// state; there is nothing left to leak.
//
// Deliberately NOT a ForgeInteractable — ClickerInteractable is a SIBLING branch off
// AbstractInteractable, not a shared base. Being built at runtime with no Inspector
// data matters for two of its fields: DontSelfSetInteractionInfo must be set before
// Start() (see UpgradeForgeBehavior.CreateCommitInteractable), and Highlighted() is
// overridden below to skip an outlineObjects array nothing populates outside the
// Unity Inspector.
public class ForgeCommitInteractable : ClickerInteractable
{
    // Mirrors ForgeDeconstructInteractable.VisualHandle's pull animation — same
    // hold-progress-driven angle, same early-release spring-back — just on the X
    // axis instead of Z, matching Level's authored pivot.
    private const float MaxAngle = 80f;
    private const float SpringBackDegPerSec = 180f;

    public UpgradeForgeBehavior Forge;
    public Transform Anchor;

    // The lever's own visual mesh (UpgradeForgeBehavior.CommitLeverBoxName —
    // "LeverBox" — buried in the FBX hierarchy). Scopes the outline highlight to
    // just the lever instead of the whole module. Null if the prefab has no
    // LeverBox — falls back to outlining the whole module.
    public Transform OutlineTarget;

    // The lever's cosmetic moving part (UpgradeForgeBehavior.CommitLevelName —
    // "Level" — buried in the FBX hierarchy same as OutlineTarget). Rotated on -X
    // around its own pivot, driven by hold progress. Optional — Commit still works
    // with no lever animation if absent.
    public Transform VisualLevel;

    private float _angle;

    private readonly ForgeHoldGate _gate = new();

    public override void StartClick()
    {
        base.StartClick();
        // base.StartClick is a no-op unless clickable; don't start timing what it
        // ignored.
        if (!isClickable) return;
        _gate.Begin(TerminusConfig.CommitHoldSeconds);
        BepinPlugin.Log.LogDebug(
            $"[Forge] Commit hold started ({TerminusConfig.CommitHoldSeconds:0.00}s required).");
    }

    public override void EndClick()
    {
        base.EndClick();
        if (_gate.IsHolding)
            BepinPlugin.Log.LogDebug($"[Forge] Commit hold released early at {_gate.Progress:P0}.");
        _gate.Cancel();
    }

    // Releasing the hold is what cancels it, so a component destroyed mid-hold would
    // otherwise leave the HUD ring spinning on a lever that no longer exists.
    public override void OnDestroy()
    {
        base.OnDestroy();
        _gate.Cancel();
    }

    private void Update()
    {
        bool fired = _gate.Tick(Time.deltaTime);

        // Tracking progress rather than easing toward a fixed target at a fixed
        // speed — see ForgeDeconstructInteractable.Update for why.
        if (VisualLevel != null)
        {
            _angle = _gate.IsHolding
                ? MaxAngle * _gate.Progress
                : Mathf.MoveTowards(_angle, 0f, SpringBackDegPerSec * Time.deltaTime);
            VisualLevel.localRotation = Quaternion.Euler(-_angle, 0f, 0f);
        }

        if (fired) OnCommit();
    }

    private void OnCommit()
    {
        BepinPlugin.Log.LogInfo($"[Forge] Commit hold completed (GetInstanceID={GetInstanceID()}).");
        if (Forge == null) return;
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
