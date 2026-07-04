using System.Collections.Generic;
using CG.Client.Player.Interactions;
using CG.Game.Player;
using CG.Network;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Object;
using HarmonyLib;
using ResourceAssets;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Patches;

// Binds the runtime UpgradeForgeBehavior to Forge module instances as they build,
// and routes in-world clicks on the Forge's interactables into it.
//
// The prefab (Assets/voidcrewterminus.metem → UpgradeForgeModuleCell.prefab) is loaded
// through the RuntimeAssets pipeline and carries only named anchor transforms
// (RelicTubeTarget ×6, InputTarget, optional CommitTarget) — no game components.
// When the crew constructs it via a BuildBox, the postfixes below attach a live
// UpgradeForgeBehavior and spawn ForgeInteractable click targets on the anchors.
[HarmonyPatch(typeof(BuildBox), nameof(BuildBox.BuildModule))]
internal static class ForgeBuildBoxAttachBehavior
{
    static void Postfix(CellModule __result) => ForgeAttachHelper.TryAttach(__result);
}

// Vanilla BuildBox.BuildModule resolves moduleRef through the CloneStarObjectContainer
// and dereferences the resulting def — which is null for runtime-registered assets
// like the Forge, so it would NRE right after instantiating. Runtime assets have
// their own factory path (RuntimeAssetsRegister-backed); take it when the box's
// moduleRef is runtime. The vanilla flow is untouched for every normal module, and
// the BuildModule postfixes (level restore, behavior attach) still run.
[HarmonyPatch(typeof(BuildBox), nameof(BuildBox.BuildModule))]
internal static class ForgeBuildBoxRuntimeModulePatch
{
    static bool Prefix(BuildBox __instance, BuildSocket targetLocation, ref CellModule __result)
    {
        var moduleRef = __instance.moduleRef;
        if (moduleRef == null || !moduleRef.IsRuntime) return true;

        if (!RuntimeAssetsRegister.Instance.HasAsset(moduleRef.AssetGuid))
        {
            BepinPlugin.Log.LogError(
                $"[Forge] BuildBox moduleRef is runtime but GUID {moduleRef.AssetGuid.AsHex()} is not in RuntimeAssetsRegister — falling through to vanilla (will likely fail).");
            return true;
        }

        var instantiationData = new Dictionary<byte, object> { { 1, targetLocation.photonView.ViewID } };
        __result = (CellModule)ObjectFactory.InstantiateRuntimeObject(
            moduleRef.AssetGuid, targetLocation.WorldPosition, targetLocation.WorldRotation, instantiationData);
        return false;
    }
}

[HarmonyPatch(typeof(CompositeWeaponBuildBox), nameof(BuildBox.BuildModule))]
internal static class ForgeCompositeBuildBoxAttachBehavior
{
    static void Postfix(CellModule __result) => ForgeAttachHelper.TryAttach(__result);
}

// CarryableInteract.StartInteraction is the game's single dispatch point for the
// player's primary interact input: it receives whatever AbstractInteractable the
// RaycastHandler currently targets, both empty-handed and while carrying. When the
// target is one of our ForgeInteractables we take over completely (insert relic /
// load box / commit) and skip the vanilla flow. Everything else — including grabbing
// docked relics or the docked BuildBox straight back out of the Forge — stays
// vanilla; UpgradeForgeBehavior.Update reconciles state afterwards.
[HarmonyPatch(typeof(CarryableInteract), nameof(CarryableInteract.StartInteraction))]
internal static class ForgeCarryableInteractPatch
{
    private static readonly AccessTools.FieldRef<CarryableInteract, bool> LockInteractionRef =
        AccessTools.FieldRefAccess<CarryableInteract, bool>("lockInteraction");

    static bool Prefix(CarryableInteract __instance, AbstractInteractable interactable)
    {
        if (interactable is not ForgeInteractable forgeTarget || forgeTarget.Forge == null)
            return true;

        var player = LocalPlayer.Instance;
        if (player == null || player.IsBusy || LockInteractionRef(__instance))
            return false;

        forgeTarget.Forge.HandleInteraction(forgeTarget, player);
        return false;
    }
}

internal static class ForgeAttachHelper
{
    internal static void TryAttach(CellModule module)
    {
        if (module == null) return;
        if (!IsForgeModule(module)) return;

        // Stamp the mod's Forge identity tag (so Forges are tag-checkable instead
        // of name-matched) and the vanilla Utility category tag (the Forge presents
        // as a utility module to anything filtering by module category). The stat
        // collection snapshots CsTags during Awake — before this postfix runs — so
        // resync it through the game's own OverrideInitTags after stamping.
        bool tagsChanged = EnsureTag(module, CsTagRegistry.ForgeModule);
        tagsChanged |= EnsureTag(module, CsTagRegistry.Utility);
        if (tagsChanged)
            module.Stats.OverrideInitTags(module.CsTags);

        var behavior = module.GetComponent<UpgradeForgeBehavior>();
        if (behavior == null)
        {
            behavior = module.gameObject.AddComponent<UpgradeForgeBehavior>();
            BepinPlugin.Log.LogInfo($"[Forge] Attached UpgradeForgeBehavior to {module.name}");
        }
        behavior.BuildInteractables();
    }

    private static bool EnsureTag(CellModule module, Gameplay.Tags.CsTag tag)
    {
        if (tag == null) return false;
        if (module.CsTags != null && System.Array.IndexOf(module.CsTags, tag) >= 0) return false;
        module.AddTag(tag);
        return true;
    }

    private static bool HasForgeTag(CellModule module) =>
        module.CsTags != null &&
        System.Array.IndexOf(module.CsTags, CsTagRegistry.ForgeModule) >= 0;

    // Identify the Forge module. Tag check first (any instance stamped above);
    // otherwise fall back to matching the prefab name. The name bootstrap cannot be
    // replaced by a tag: the metem prefab carries only the VoidCrewAsset marker and
    // anchor transforms — game CsTag assets can't be serialized into it, so a fresh
    // build's CellModule arrives untagged. The vanilla "(Clone)" suffix is stripped
    // so we accept both "UpgradeForgeModuleCell" and "UpgradeForgeModuleCell(Clone)".
    private static bool IsForgeModule(CellModule module)
    {
        if (module == null) return false;
        if (HasForgeTag(module)) return true;
        var name = module.name ?? "";
        if (name.EndsWith("(Clone)")) name = name[..^7].TrimEnd();
        return name == UpgradeForgeBehavior.PrefabName;
    }
}
