using CG.Ship.Modules;
using CG.Space;
using HarmonyLib;

namespace VoidCrewTerminus.Patches;

// BuildBox.BuildModule (and the CompositeWeaponBuildBox override) end in
// ObjectFactory.InstantiateSpaceObjectByGUID -> PhotonNetwork.Instantiate, so it
// runs only on the machine that placed the box; remote clients receive the
// module through Photon's own instantiation path and never execute BuildModule.
// Hooking OnPhotonInstantiate instead runs on every client, including the
// placer (PUN raises it locally too) — same hook the cursed-relic sync already
// uses; CellModule overrides OnPhotonInstantiate but calls base, so the
// OrbitObject patch still reaches it. Both entry points are safe to double-run:
// TryAttach only adds a missing component and BuildInteractables is guarded by
// _interactablesBuilt.
[HarmonyPatch(typeof(OrbitObject), nameof(OrbitObject.OnPhotonInstantiate))]
internal static class ForgeModuleSpawnPatch
{
    static void Postfix(OrbitObject __instance)
    {
        try
        {
            if (__instance is not CellModule module) return;

            // No-ops for every non-forge CellModule.
            ForgeAttachHelper.TryAttach(module);

            // An overlay broadcast can outrun this instantiation; drain whatever
            // was buffered against this ViewID.
            Net.ForgeNetSync.TryApplyPendingModuleOverlay(__instance.photonView, module);
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log?.LogError($"[Forge] ForgeModuleSpawnPatch failed: {e}");
        }
    }
}
