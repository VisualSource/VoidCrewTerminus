using CG.Ship.Modules;
using CG.Space;
using HarmonyLib;

namespace VoidCrewTerminus.Patches;

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
            AssetLoader.EnsureBuildBoxTemplateReady();

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
