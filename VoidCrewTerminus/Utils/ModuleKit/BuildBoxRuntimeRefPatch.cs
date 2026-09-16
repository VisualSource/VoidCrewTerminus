using CG.Ship.Object;
using Client.Ship.Actor;
using HarmonyLib;
using ResourceAssets;

namespace VoidCrewTerminus.ModuleKit;

// moduleRef.IsRuntime is [NonSerialized], so the prefab-to-instance clone doesn't carry it:
// the template has it true, but every spawned instance resets to false. BuildBoxActor.Awake
// then falls through to the vanilla Resources.Load path, gets null for a non-vanilla guid
// and NREs inside GetMeshSetup, leaving the box invisible and non-interactable.
//
// Re-stamped immediately before Awake reads it, gated to guids RuntimeAssetsRegister knows
// about so real vanilla BuildBox instances are left untouched.
[HarmonyPatch(typeof(BuildBoxActor), nameof(BuildBoxActor.Awake))]
internal static class BuildBoxRuntimeRefPatch
{
    static void Prefix(BuildBoxActor __instance)
    {
        var box = __instance.GetComponent<BuildBox>();
        var moduleRef = box != null ? box.moduleRef : null;
        if (moduleRef == null || moduleRef.IsNull) return;

        if (RuntimeAssetsRegister.Instance.HasAsset(moduleRef.AssetGuid))
            moduleRef.IsRuntime = true;
    }
}
