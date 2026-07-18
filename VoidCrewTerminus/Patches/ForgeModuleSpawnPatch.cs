using CG.Ship.Modules;
using CG.Space;
using HarmonyLib;

namespace VoidCrewTerminus.Patches;

// Phase 8-D — make forge modules work on clients that didn't place them.
//
// BuildBox.BuildModule (and the CompositeWeaponBuildBox override) end in
// ObjectFactory.InstantiateSpaceObjectByGUID → PhotonNetwork.Instantiate, so they
// run ONLY on the machine that placed the box. Remote clients receive the module
// through Photon's own instantiation path and never execute BuildModule.
//
// Every forge hook used to hang off BuildModule, so on a remote client the forge
// was inert geometry: no UpgradeForgeBehavior, no click targets, and no restored
// overlay. Hooking OnPhotonInstantiate instead runs on EVERY client, including
// the placer (PUN raises it locally too).
//
// Same hook the cursed-relic sync already uses successfully — CellModule
// overrides OnPhotonInstantiate but calls base, so the OrbitObject patch reaches
// it. Both entry points are safe to double-run: TryAttach only adds a missing
// component and BuildInteractables is guarded by _interactablesBuilt.
[HarmonyPatch(typeof(OrbitObject), nameof(OrbitObject.OnPhotonInstantiate))]
internal static class ForgeModuleSpawnPatch
{
    static void Postfix(OrbitObject __instance)
    {
        try
        {
            if (__instance is not CellModule module) return;

            // Forge modules: build the interaction targets locally. TryAttach
            // no-ops for every non-forge CellModule.
            ForgeAttachHelper.TryAttach(module);

            // Any module: an overlay broadcast can outrun this instantiation, so
            // drain whatever was buffered against this ViewID.
            Net.ForgeNetSync.TryApplyPendingModuleOverlay(__instance.photonView, module);
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log?.LogError($"[Forge] ForgeModuleSpawnPatch failed: {e}");
        }
    }
}
