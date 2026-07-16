using CG.Ship.Modules;
using UnityEngine;

namespace VoidCrewTerminus.Forge.Burdens;

// Base MonoBehaviour for all Maintenance Burden types (Phase 7-C). Each burden
// type has its own subclass; one instance per burden type is attached to the
// module GameObject when the snapshot lists that burden.
//
// Design constraint: burdens are OPERATIONAL, not statistical. A burden makes
// the module annoying to operate (random shutoffs, heat ticks, manual resets)
// without ever changing its damage/defense/etc. numbers. Stat mods live on
// ForgeModuleState; MonoBehaviours live here.
public abstract class MaintenanceBurdenBehavior : MonoBehaviour
{
    // Which burden type this component represents. Used by ForgeModuleState.
    // SyncBurdenBehaviors to reconcile attached components against snapshot state.
    public abstract BurdenType BurdenType { get; }

    protected CellModule Module { get; private set; }

    protected virtual void Awake()
    {
        Module = GetComponent<CellModule>();
        if (Module == null)
            BepinPlugin.Log?.LogWarning($"[Burden] {GetType().Name} attached to non-CellModule GameObject '{name}' — will be inert.");
    }
}
