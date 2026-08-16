using CG.Client.Player.Interactions.Build;
using CG.Game;
using CG.Ship.Hull;
using CG.Ship.Modules;
using Client.Utils;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// The Forge's deconstruct handle. Same hold-to-confirm mechanism as
// ForgeCommitInteractable (HoldClickerInteractable, driven by EnvironmentInteract's
// Hold action) — reused here instead of vanilla's Mediator/ExtruderLever chain.
//
// Vanilla wires deconstruct through AbstractModuleMediator<T>.InitializeDeconstructButton,
// which needs BaseModuleMediator + ModuleDeconstructButton + ExtruderLever all present
// on the prefab — none of which can be authored in the modding SDK or runtime-grafted
// (ExtruderLever needs real geometry and a configured AnimationCurve). But
// AbstractModuleMediator.OnClickedDeconstructButton turns out to be three plain calls
// with zero dependency on the Mediator instance itself — Deconstruct.CanRemoveModule /
// Deconstruct.CanStartDeconstruct (both static) and
// BuildProcessController.Instance.TryDeconstructModule (already fully networked, RPCs
// to the master client itself) — so replicating them directly here is simpler and
// lower-risk than grafting three more vanilla component types.
//
// Visual: rotates VisualHandle (a separate cosmetic mesh part — this component sits
// on DeconstructTrigger, the click collider, not the visual lever) on the Z axis
// while held, spring-back to 0 on early release, approximating a lever pull without
// ExtruderLever's AnimationCurve machinery. VisualHandle may be null (no Handle mesh
// on the prefab) — deconstruct still works, just with no visible animation.
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

    // HoldClickerInteractable.StartClick subscribes OnHoldCompleted onto a single
    // GLOBAL InputAction (InputActionReferences.HoldAction) shared by every
    // Hold-driven interactable in the game, and only unsubscribes in EndClick()
    // or once the hold completes — a real leak risk if this is destroyed mid-hold
    // before EndClick runs (turned out NOT to be the cause of the "deconstructing
    // any other module deconstructs the Forge too" bug — see Highlighted below
    // for the actual root cause — but kept as cheap, harmless belt-and-braces).
    // EndClick() unconditionally unsubscribes, so calling it here is always safe.
    public override void OnDestroy()
    {
        base.OnDestroy();
        EndClick();
    }

    // ClickerInteractable.Highlighted (what this would otherwise inherit) iterates
    // a private outlineObjects[] that only the Unity Inspector populates — null on
    // every Forge interactable, since all are AddComponent'd at runtime. Calling it
    // threw an NRE from RaycastHandler.RaycastInteractables() every time the raycast
    // target changed to/away from this trigger. Uncaught, that exception aborted
    // the rest of RaycastInteractables() for the frame — including the line that
    // reassigns RaycastHandler.Current — so looking away left Current permanently
    // stuck pointing at this trigger. Every later Hold-to-deconstruct attempt
    // anywhere then read that stuck Current in EnvironmentInteract.TryStartInteract
    // and fired on the Forge instead: this WAS the "deconstructing any other module
    // deconstructs the Forge too" bug. Uses ForgeOutline instead (the real
    // outline-shader highlight vanilla uses), scoped to VisualHandle rather than
    // the whole module, falling back to the whole module only if there's no Handle mesh.
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

    // _holding is our own locally-tracked "is the player actually holding THIS
    // interactable right now" state — belt-and-braces against the shared-Hold-
    // action leak above: even if a stale subscription does fire (from some path
    // OnDestroy doesn't catch), it becomes a no-op instead of deconstructing the
    // wrong module, since _holding only goes true between this component's own
    // StartClick/EndClick pair.
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
