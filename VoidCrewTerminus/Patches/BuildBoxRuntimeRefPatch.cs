using CG.Ship.Object;
using Client.Ship.Actor;
using HarmonyLib;
using ResourceAssets;

namespace VoidCrewTerminus.Patches;

// BuildBoxActor.Awake() resolves the module its crate represents via
// moduleRef.Asset, which branches on CloneStarObjectRef.IsRuntime (mod-registered
// lookup via RuntimeAssetsRegister vs. vanilla ResourcePaths/Resources.Load).
// IsRuntime is declared [NonSerialized] on the base ResourceAssetRef though —
// Unity's prefab→instance clone (what PhotonNetwork.Instantiate does under the
// hood) does not carry NonSerialized fields across. AssetLoader.LinkForgeBuildBox
// sets moduleRef.IsRuntime = true once on the prefab ASSET, but every spawned
// INSTANCE's copy resets to false, so Awake() falls through to the vanilla
// Resources.Load path with an empty ResourcePaths entry (our guid isn't
// vanilla-registered), gets null back, and BuildingConstraints NREs on it inside
// GetMeshSetup — confirmed in-game: box spawns (Rigidbody/Collider/PhotonView
// intact) but Awake() dies before wiring the mesh/timeline/diodes, so it's
// invisible and non-interactable. BuildBox.BuildModule/GetBuildSize read the same
// moduleRef.Asset later (when actually installing the box into a ship socket),
// so this one bug blocks both the dev-spawn visuals and the real build flow.
//
// Re-stamp IsRuntime immediately before Awake reads it, gated to only guids
// RuntimeAssetsRegister actually knows about — vanilla BuildBox instances (whose
// moduleRef legitimately resolves via ResourcePaths, IsRuntime correctly false)
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
