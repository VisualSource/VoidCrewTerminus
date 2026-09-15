using CG.Game;
using CG.Ship.Hull;
using CG.Ship.Modules;
using HarmonyLib;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Patches;

// Refuses to deconstruct a Forge that still holds relics or a module box, as vanilla refuses
// a module with an ammo box loaded. Vanilla's own check iterates ConnectedSockets, which is
// always empty for a Forge: its tubes are plain anchors, not CarryablesSockets (ADR-0002).
//
// Patched at CanStartDeconstruct rather than at our own handle because every path funnels
// through it, including DeconstructionProcess.RunWaiting, which re-runs it per tick, so a
// relic docked mid-deconstruct stalls the process instead of being eaten by it.
[HarmonyPatch(typeof(Deconstruct), nameof(Deconstruct.CanStartDeconstruct))]
internal static class ForgeDeconstructGuardPatch
{
    static void Postfix(CellModule module, ref ConstructResult __result)
    {
        // Only tighten a Valid result: anything else is a vanilla refusal more specific.
        if (__result != ConstructResult.Valid || module == null) return;

        var forge = module.GetComponent<UpgradeForgeBehavior>();
        if (forge == null || !forge.IsLoaded) return;

        __result = ConstructResult.BlockedByFullSockets;
        BepinPlugin.Log.LogDebug(
            $"[Forge] Deconstruct of {module.name} blocked — {forge.RelicCount} relic(s)" +
            $"{(forge.HasModule ? " and a module box" : "")} still docked.");
    }
}
