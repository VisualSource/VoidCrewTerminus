using CG.Client.Player.Interactions.Build;
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
// Visual: rotates VisualHandle on Z while held, spring-back on early release. This
// component sits on DeconstructTrigger (the collider), not the lever mesh, so
// VisualHandle may be null — deconstruct still works, just without the animation.
public class ForgeDeconstructInteractable : HoldClickerInteractable
{
    private const float RotateSpeedDegPerSec = 90f;
    private const float MaxAngle = 80f;

    public Transform VisualHandle;

    private CellModule _module;
    private bool _holding;
    private float _angle;

    public override void Awake()
    {
        base.Awake();
        // Sits on the "Handle" child, not the module root — walk up to find it.
        _module = GetComponentInParent<CellModule>();
        HoldCompleted += OnDeconstruct;
    }

    public override void StartClick()
    {
        base.StartClick();
        _holding = true;
        BepinPlugin.Log.LogDebug($"[Forge] Deconstruct StartClick on {(_module != null ? _module.name : "?")} (GetInstanceID={GetInstanceID()}).");
    }

    public override void EndClick()
    {
        base.EndClick();
        _holding = false;
        BepinPlugin.Log.LogDebug($"[Forge] Deconstruct EndClick on {(_module != null ? _module.name : "?")} (GetInstanceID={GetInstanceID()}).");
    }

    // StartClick subscribes onto a single GLOBAL InputAction shared by every
    // Hold-driven interactable, unsubscribing only in EndClick or on completion — a
    // leak risk if this is destroyed mid-hold. (NOT the cause of the "deconstructing
    // any module hit the Forge" bug — see Highlighted — but cheap insurance.)
    // EndClick unconditionally unsubscribes, so calling it here is always safe.
    public override void OnDestroy()
    {
        base.OnDestroy();
        EndClick();
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
        if (VisualHandle == null) return;
        float target = _holding ? MaxAngle : 0f;
        _angle = Mathf.MoveTowards(_angle, target, RotateSpeedDegPerSec * Time.deltaTime);
        VisualHandle.localRotation = Quaternion.Euler(0f, 0f, _angle);
    }

    // _holding only goes true between this component's own StartClick/EndClick, so a
    // stale subscription firing from the shared Hold action becomes a no-op rather
    // than deconstructing the wrong module.
    private void OnDeconstruct()
    {
        BepinPlugin.Log.LogInfo(
            $"[Forge] OnDeconstruct fired on {(_module != null ? _module.name : "?")} (GetInstanceID={GetInstanceID()}), _holding={_holding}.");
        if (!_holding || _module == null) return;
        var result = Deconstruct.CanRemoveModule(_module);
        if (Deconstruct.CanStartDeconstruct(_module) == ConstructResult.Valid)
            BuildProcessController.Instance.TryDeconstructModule(_module);
        else
            WarningHelper.ShowResultWarning(result);
    }
}
