using CG.Client.Player.Interactions.Build;
using CG.Client.Ship.Interactions;
using CG.Game;
using CG.Ship.Hull;
using CG.Ship.Modules;
using Client.Utils;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// The Forge's deconstruct handle. Same hold-to-confirm mechanism as
// ForgeCommitInteractable, reused instead of vanilla's Mediator/ExtruderLever chain.
//
// Vanilla wires deconstruct through AbstractModuleMediator.InitializeDeconstructButton,
// needing BaseModuleMediator + ModuleDeconstructButton + ExtruderLever on the prefab —
// none authorable in the modding SDK or runtime-graftable (ExtruderLever needs real
// geometry and a configured AnimationCurve). But OnClickedDeconstructButton is three
// plain calls with no dependency on the Mediator instance — two static Deconstruct
// checks and BuildProcessController.TryDeconstructModule, already fully networked —
// so replicating them here is lower-risk than grafting three more component types.
//
// The hold DURATION, though, is matched to vanilla exactly: ForgeHoldGate measures a
// live ExtruderLever in the scene and times this lever to the same figure, rather
// than firing on the short global HoldAction the way HoldClickerInteractable does.
//
// Visual: rotates VisualHandle on Z, driven by hold progress so the lever bottoms out
// exactly when the deconstruct fires. This component sits on DeconstructTrigger (the
// collider), not the lever mesh, so VisualHandle may be null — deconstruct still
// works, just without the animation.
public class ForgeDeconstructInteractable : ClickerInteractable
{
    private const float MaxAngle = 80f;

    // Only used for the spring-back after an early release; the pull itself tracks
    // hold progress directly.
    private const float SpringBackDegPerSec = 180f;

    public Transform VisualHandle;

    private CellModule _module;
    private readonly ForgeHoldGate _gate = new();
    private float _angle;

    public override void Awake()
    {
        base.Awake();
        // Sits on the "DeconstructTrigger" child, not the module root — walk up.
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

    // Root cause of the "deconstructing any other module deconstructs the Forge too"
    // bug: ClickerInteractable.Highlighted iterates a private outlineObjects[] that
    // only the Inspector populates — null on every runtime-built Forge interactable —
    // and the NRE aborted the rest of RaycastHandler.RaycastInteractables() for that
    // frame, including the line reassigning Current. Looking away therefore left
    // Current stuck on this trigger, and every later Hold anywhere fired on the Forge.
    //
    // ForgeOutline instead, scoped to VisualHandle, falling back to the whole module.
    public override void Highlighted(bool isHighlighted)
    {
        var target = VisualHandle != null ? VisualHandle : (_module != null ? _module.transform : null);
        ForgeOutline.SetHighlighted(target, isHighlighted);
    }

    private void Update()
    {
        bool fired = _gate.Tick(Time.deltaTime);

        // Tracking progress rather than easing toward a fixed target at a fixed speed:
        // at any hold longer than MaxAngle/speed the old version bottomed the lever out
        // early and left it sitting there, which reads as "it's stuck" rather than
        // "keep holding".
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
