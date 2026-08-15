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
// on the prefab. None of those can be authored in the modding SDK (see
// ForgeCommitInteractable's doc comment — com.hutlihut.void_crew_common exposes no
// gameplay component types) and none can be runtime-grafted the way flat-data
// components are, because ExtruderLever needs real geometry and a configured
// AnimationCurve. But AbstractModuleMediator.OnClickedDeconstructButton turns out to
// be three plain calls with zero dependency on the Mediator instance itself —
// Deconstruct.CanRemoveModule / Deconstruct.CanStartDeconstruct (both static) and
// BuildProcessController.Instance.TryDeconstructModule (singleton, and already fully
// networked — it RPCs to the master client itself) — so replicating them directly
// here is simpler AND lower-risk than grafting three more vanilla component types
// this mod has never exercised.
//
// Visual: rotates VisualHandle (a separate, purely cosmetic mesh part — this
// component itself sits on DeconstructTrigger, the hand-authored click
// collider, not on the visual lever) on the Z axis while held, spring-back to
// 0 on early release. Approximates a lever pull without ExtruderLever's
// AnimationCurve machinery — good enough for "shows progress, cleanly reverts
// if you let go." VisualHandle may be null (prefab has no Handle mesh) — then
// deconstruct still works, just with no visible animation.
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
    // Hold-driven interactable in the game, vanilla included, and only
    // unsubscribes in EndClick() or once the hold completes. This is a real leak
    // risk on its own (component destroyed mid-hold before EndClick runs), even
    // though it turned out NOT to be the cause of the "deconstructing any other
    // module deconstructs the Forge too" report — see Highlighted's doc comment
    // for the actual root cause. Kept as cheap, harmless belt-and-braces: EndClick()
    // unconditionally unsubscribes regardless of isClickable state, so calling it
    // here is always safe, even if already unsubscribed.
    public override void OnDestroy()
    {
        base.OnDestroy();
        EndClick();
    }

    // ClickerInteractable.Highlighted (what this would otherwise inherit)
    // iterates a private outlineObjects[] that only the Unity Inspector ever
    // populates — null on every Forge interactable, since all of them are
    // AddComponent'd at runtime with no Inspector data. Calling it threw a
    // NullReferenceException from RaycastHandler.RaycastInteractables() every
    // time the player's raycast target changed to or away from this trigger
    // (confirmed via log: a burst of these NREs appeared immediately after
    // every StartClick/EndClick). Uncaught, that exception aborts the REST of
    // RaycastInteractables() for that frame — including whatever line reassigns
    // RaycastHandler.Current to the player's new target — so looking away from
    // this trigger left RaycastHandler.Current permanently stuck pointing at it.
    // Every later Hold-to-deconstruct attempt anywhere then read that stuck
    // Current back in EnvironmentInteract.TryStartInteract and fired on the
    // Forge instead, regardless of what the player was actually aiming at —
    // this IS the actual "deconstructing any other module deconstructs the
    // Forge too" bug (confirmed by the log: OnDeconstruct fired with
    // _holding=True and the SAME GetInstanceID as the StartClick moments
    // before — a real, correctly-scoped hold, just aimed at a stale target).
    // Uses ForgeOutline instead: the real outline-shader highlight every vanilla
    // module/BuildBox hover uses. Scoped to VisualHandle specifically (the same
    // "Handle" mesh the rotation animation below drives) rather than the whole
    // module — mirrors ForgeCommitInteractable's LeverBox scoping. Falls back to
    // the whole module only if the prefab has no Handle mesh.
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
