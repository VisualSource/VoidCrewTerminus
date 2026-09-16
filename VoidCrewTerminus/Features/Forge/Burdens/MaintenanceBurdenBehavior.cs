using CG.Ship.Modules;
using UnityEngine;

namespace VoidCrewTerminus.Forge.Burdens;

// One subclass per burden type, one instance attached to the module when the snapshot lists
// that burden. Burdens are OPERATIONAL, not statistical: they make a module annoying to
// operate without ever changing its numbers. Stat mods live on ForgeModuleState.
public abstract class MaintenanceBurdenBehavior : MonoBehaviour
{
    // Reconciled against snapshot state by ForgeModuleState.SyncBurdenBehaviors.
    public abstract BurdenType BurdenType { get; }

    protected CellModule Module { get; private set; }

    protected virtual void Awake()
    {
        Module = GetComponent<CellModule>();
        if (Module == null)
            BepinPlugin.Log?.LogWarning($"[Burden] {GetType().Name} attached to non-CellModule GameObject '{name}' — will be inert.");
    }
}
