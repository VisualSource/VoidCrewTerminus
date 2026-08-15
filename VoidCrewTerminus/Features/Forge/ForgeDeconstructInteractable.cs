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
// Visual: rotates its own transform on the Y axis while held, spring-back to 0 on
// early release. Approximates a lever pull without ExtruderLever's AnimationCurve
// machinery — good enough for "shows progress, cleanly reverts if you let go."
public class ForgeDeconstructInteractable : HoldClickerInteractable
{
    private const float RotateSpeedDegPerSec = 90f;
    private const float MaxAngle = 80f;

    private CellModule _module;
    private bool _holding;
    private float _angle;

    protected override void Awake()
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
    }

    public override void EndClick()
    {
        base.EndClick();
        _holding = false;
    }

    private void Update()
    {
        float target = _holding ? MaxAngle : 0f;
        _angle = Mathf.MoveTowards(_angle, target, RotateSpeedDegPerSec * Time.deltaTime);
        transform.localRotation = Quaternion.Euler(0f, _angle, 0f);
    }

    private void OnDeconstruct()
    {
        if (_module == null) return;
        var result = Deconstruct.CanRemoveModule(_module);
        if (Deconstruct.CanStartDeconstruct(_module) == ConstructResult.Valid)
            BuildProcessController.Instance.TryDeconstructModule(_module);
        else
            WarningHelper.ShowResultWarning(result);
    }
}
