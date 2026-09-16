using CG.Client.Player.Interactions.Build;
using CG.Client.Ship.Interactions;
using CG.Game;
using CG.Ship.Hull;
using CG.Ship.Modules;
using Client.Utils;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// The Forge's deconstruct handle, using the same hold gate as ForgeCommitInteractable.
// Vanilla wires deconstruct through a Mediator + ModuleDeconstructButton + ExtruderLever
// chain, none of it authorable in the SDK or runtime-graftable, but
// OnClickedDeconstructButton is three plain calls with no dependency on the Mediator, so
// replicating them is lower-risk than grafting three component types. The hold duration is
// measured off a live ExtruderLever (ForgeHoldGate). VisualHandle may be null.
public class ForgeDeconstructInteractable : ClickerInteractable
{
    private const float MaxAngle = 80f;

    // Only the spring-back after an early release; the pull itself tracks hold progress.
    private const float SpringBackDegPerSec = 180f;

    public Transform VisualHandle;

    private CellModule _module;
    private readonly ForgeHoldGate _gate = new();
    private float _angle;

    public override void Awake()
    {
        base.Awake();
        // Sits on the DeconstructTrigger child, not the module root.
        _module = GetComponentInParent<CellModule>();
    }

    public override void StartClick()
    {
        base.StartClick();
        if (!isClickable) return;
        float seconds = ForgeHoldGate.VanillaDeconstructSeconds;
        _gate.Begin(seconds);
        BepinPlugin.Log.LogDebug(
            $"[Forge] Deconstruct hold started on {(_module != null ? _module.name : "?")} ({seconds:0.00}s required).");
    }

    public override void EndClick()
    {
        base.EndClick();
        if (_gate.IsHolding)
            BepinPlugin.Log.LogDebug($"[Forge] Deconstruct hold released early at {_gate.Progress:P0}.");
        _gate.Cancel();
    }

    // Releasing the hold is what cancels it, so a component destroyed mid-hold would
    // otherwise leave the HUD ring spinning on a lever that no longer exists.
    public override void OnDestroy()
    {
        base.OnDestroy();
        _gate.Cancel();
    }

    // base.Highlighted iterates a private outlineObjects[] only the Inspector populates, so
    // it NREs on a runtime-built interactable and aborts the rest of the raycast sweep for
    // that frame, leaving RaycastHandler.Current stuck on this trigger.
    public override void Highlighted(bool isHighlighted)
    {
        var target = VisualHandle != null ? VisualHandle : (_module != null ? _module.transform : null);
        ForgeOutline.SetHighlighted(target, isHighlighted);
    }

    private void Update()
    {
        bool fired = _gate.Tick(Time.deltaTime);

        // Tracks progress rather than easing at a fixed speed, so the lever bottoms out
        // exactly when the deconstruct fires rather than early, which reads as stuck.
        if (VisualHandle != null)
        {
            _angle = _gate.IsHolding
                ? MaxAngle * _gate.Progress
                : Mathf.MoveTowards(_angle, 0f, SpringBackDegPerSec * Time.deltaTime);
            VisualHandle.localRotation = Quaternion.Euler(0f, 0f, _angle);
        }

        if (fired) OnDeconstruct();
    }

    private void OnDeconstruct()
    {
        BepinPlugin.Log.LogDebug(
            $"[Forge] Deconstruct hold completed on {(_module != null ? _module.name : "?")} (GetInstanceID={GetInstanceID()}).");
        if (_module == null) return;
        var result = Deconstruct.CanRemoveModule(_module);
        if (Deconstruct.CanStartDeconstruct(_module) == ConstructResult.Valid)
            BuildProcessController.Instance.TryDeconstructModule(_module);
        else
            WarningHelper.ShowResultWarning(result);
    }
}
