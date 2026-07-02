using CG.Client.Player.Interactions;
using CG.Game.Player;
using CG.Ship.Modules;
using CG.Ship.Object;
using HarmonyLib;
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

        // Stamp the mod's Forge tag so this instance (and anything else that wants
        // to identify Forges) can tag-check instead of name-matching from here on.
        if (!HasForgeTag(module))
            module.AddTag(CsTagRegistry.ForgeModule);

        var behavior = module.GetComponent<UpgradeForgeBehavior>();
        if (behavior == null)
        {
            behavior = module.gameObject.AddComponent<UpgradeForgeBehavior>();
            BepinPlugin.Log.LogInfo($"[Forge] Attached UpgradeForgeBehavior to {module.name}");
        }
        behavior.BuildInteractables();
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
