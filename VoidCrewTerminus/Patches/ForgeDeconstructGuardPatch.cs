using CG.Game;
using CG.Ship.Hull;
using CG.Ship.Modules;
using HarmonyLib;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Patches;

// Refuse to deconstruct an Upgrade Forge that still holds relics or a module box,
// the same way vanilla refuses a module with an ammo box still loaded in it.
//
// Vanilla's rule, in Deconstruct.CanStartDeconstruct:
//
//     foreach (CarryablesSocket s in module.ConnectedSockets)
//         if (s.IsFull) return ConstructResult.BlockedByFullSockets;
//
// which WarningHelper renders as the "clear loaded components" quick warning. The
// Forge never tripped it because its relic tubes and module socket are NOT
// CarryablesSockets — they're plain named anchors held by AnchorDock (game
// components can't be serialized into the metem bundle, so the shipped prefab
// carries only transforms). module.ConnectedSockets is empty for a Forge, the loop
// finds nothing, and the check falls straight through.
//
// What that left was an accident that looked like a feature: a loaded Forge usually
// still refused, because the docked items sit inside the build socket's volume and
// tripped ConstructUtil.AnyObjectObstructsVolume instead. That's the wrong refusal
// for the wrong reason — it reports "clear the area of objects" rather than "clear
// the loaded components", and it only holds while the items happen to intersect
// that volume, which the relic tubes are under no obligation to do.
//
// Patched at Deconstruct.CanStartDeconstruct rather than at our own
// ForgeDeconstructInteractable, because that one static method is the single gate
// every path goes through: the deconstruct handle, vanilla's own
// AbstractModuleMediator button, the upgrade paths via CanUpgradeModule, and —
// importantly — DeconstructionProcess.RunWaiting, which re-runs it every tick and
// only advances to Working while it reads Valid. So a relic docked AFTER a
// deconstruct has already begun stalls the process rather than being eaten by it,
// which is exactly how vanilla behaves when a socket is filled mid-deconstruct.
[HarmonyPatch(typeof(Deconstruct), nameof(Deconstruct.CanStartDeconstruct))]
internal static class ForgeDeconstructGuardPatch
{
    static void Postfix(CellModule module, ref ConstructResult __result)
    {
        // Only ever tighten a Valid result — every other value is a vanilla refusal
        // that is already more specific than ours, and overwriting one would
        // replace an accurate message with a vaguer one.
        if (__result != ConstructResult.Valid || module == null) return;

        var forge = module.GetComponent<UpgradeForgeBehavior>();
        if (forge == null || !forge.IsLoaded) return;

        __result = ConstructResult.BlockedByFullSockets;
        BepinPlugin.Log.LogDebug(
            $"[Forge] Deconstruct of {module.name} blocked — {forge.RelicCount} relic(s)" +
            $"{(forge.HasModule ? " and a module box" : "")} still docked.");
    }
}
