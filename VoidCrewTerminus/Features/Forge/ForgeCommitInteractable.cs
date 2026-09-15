using CG.Client.Ship.Interactions;
using CG.Game.Player;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// Held rather than clicked: committing consumes relics irreversibly, so an accidental tap
// must not fire it. Extends ClickerInteractable rather than HoldClickerInteractable and times
// the hold itself, because HoldClickerInteractable fires on the global HoldAction's short
// generic duration, which is shared with every other prompt and can't be lengthened.
// Not a ForgeInteractable: ClickerInteractable is a sibling branch off AbstractInteractable.
public class ForgeCommitInteractable : ClickerInteractable
{
    // Mirrors ForgeDeconstructInteractable.VisualHandle's pull animation, on the X axis
    // instead of Z to match Level's authored pivot.
    private const float MaxAngle = 80f;
    private const float SpringBackDegPerSec = 180f;

    public UpgradeForgeBehavior Forge;
    public Transform Anchor;

    // Scopes the outline highlight to the lever. Null when the prefab has no LeverBox,
    // which falls back to outlining the whole module.
    public Transform OutlineTarget;

    // Rotated on -X around its own pivot, driven by hold progress. Optional.
    public Transform VisualLevel;

    private float _angle;

    private readonly ForgeHoldGate _gate = new();

    public override void StartClick()
    {
        base.StartClick();
        // base.StartClick is a no-op unless clickable; don't time what it ignored.
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

        // Tracks progress rather than easing at a fixed speed; see
        // ForgeDeconstructInteractable.Update.
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
        BepinPlugin.Log.LogDebug($"[Forge] Commit hold completed (GetInstanceID={GetInstanceID()}).");
        if (Forge == null) return;
        var player = LocalPlayer.Instance;
        if (player == null) return;
        Forge.HandleInteraction(ForgeInteractableKind.CommitButton, Anchor, player);
    }

    // ForgeOutline instead of base.Highlighted, which NREs on a runtime-built component.
    // Scoped to the lever mesh: Commit is a separately-modeled part.
    public override void Highlighted(bool isHighlighted)
    {
        var target = OutlineTarget != null ? OutlineTarget : (Forge != null ? Forge.transform : null);
        ForgeOutline.SetHighlighted(target, isHighlighted);
    }
}
