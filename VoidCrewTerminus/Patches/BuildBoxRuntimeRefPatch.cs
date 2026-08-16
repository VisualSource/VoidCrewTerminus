using CG.Ship.Object;
using Client.Ship.Actor;
using HarmonyLib;
using ResourceAssets;

namespace VoidCrewTerminus.Patches;

// moduleRef.IsRuntime is [NonSerialized], so Unity's prefab→instance clone
// (PhotonNetwork.Instantiate) doesn't carry it: AssetLoader.LinkForgeBuildBox
// sets it true on the prefab asset, but every spawned instance's copy resets to
// false. BuildBoxActor.Awake() then falls through to the vanilla Resources.Load
// path, gets null for our non-vanilla-registered guid, and NREs inside
// GetMeshSetup — the box spawns but is invisible and non-interactable, and the
// same moduleRef.Asset read later blocks BuildBox.BuildModule/GetBuildSize too.
//
// Re-stamp IsRuntime immediately before Awake reads it, gated to guids
// RuntimeAssetsRegister actually knows about so real vanilla BuildBox instances
// are left untouched.
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
