using CG.Game;
using CG.Ship.Hull;
using CG.Ship.Modules;
using HarmonyLib;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Patches;

// Refuse to deconstruct an Upgrade Forge that still holds relics or a module box,
// the way vanilla refuses a module with an ammo box loaded.
//
// Vanilla's check iterates module.ConnectedSockets, which is always empty for a
// Forge — its tubes and socket are plain anchors, not CarryablesSockets (ADR-0002).
// A loaded Forge only seemed to refuse because the docked items usually tripped
// AnyObjectObstructsVolume instead; when they didn't, deconstructing stranded them.
//
// Patched at Deconstruct.CanStartDeconstruct rather than at our own deconstruct
// handle because every path funnels through it — including
// DeconstructionProcess.RunWaiting, which re-runs it per tick, so a relic docked
// mid-deconstruct stalls the process instead of being eaten by it.
[HarmonyPatch(typeof(Deconstruct), nameof(Deconstruct.CanStartDeconstruct))]
internal static class ForgeDeconstructGuardPatch
{
    static void Postfix(CellModule module, ref ConstructResult __result)
    {
        // Only ever tighten a Valid result: any other value is a vanilla refusal
        // already more specific than ours.
        if (__result != ConstructResult.Valid || module == null) return;

        var forge = module.GetComponent<UpgradeForgeBehavior>();
        if (forge == null || !forge.IsLoaded) return;

        __result = ConstructResult.BlockedByFullSockets;
        BepinPlugin.Log.LogDebug(
            $"[Forge] Deconstruct of {module.name} blocked — {forge.RelicCount} relic(s)" +
            $"{(forge.HasModule ? " and a module box" : "")} still docked.");
    }
}
