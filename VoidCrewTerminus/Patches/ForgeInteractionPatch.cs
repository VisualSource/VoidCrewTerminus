using CG.Ship.Modules;
using CG.Ship.Object;
using HarmonyLib;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Patches;

// Binds the runtime UpgradeForgeBehavior to Forge module instances as they build.
//
// The prefab (Assets/voidcrewterminus.metem → UpgradeForgeModuleCell.prefab) is loaded
// through the RuntimeAssets pipeline. When the crew constructs it via a BuildBox, this
// postfix ensures a live UpgradeForgeBehavior is attached to the resulting CellModule.
//
// Physical socket wiring — trigger volumes / drop points for relics and BuildBoxes
// that call UpgradeForgeBehavior.TryInsertRelic / TryTakeModule — is deferred to a
// pre-flight-gated follow-up. Signatures for the game's socket / interactable
// components (InteractiveCarryable, CarryableSocket, etc.) need confirmation via a
// live decompile before the physical hookup can be finalised. Until then Phase 3
// tests exercise the behavior via ForgeDevCommands.
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

internal static class ForgeAttachHelper
{
    internal static void TryAttach(CellModule module)
    {
        if (module == null) return;
        if (!IsForgeModule(module)) return;
        if (module.GetComponent<UpgradeForgeBehavior>() != null) return;
        module.gameObject.AddComponent<UpgradeForgeBehavior>();
        BepinPlugin.Log.LogInfo($"[Forge] Attached UpgradeForgeBehavior to {module.name}");
    }

    // Match the runtime CellModule against the Forge prefab by name. The vanilla
    // "(Clone)" suffix is stripped so we accept both "UpgradeForgeModuleCell" and
    // "UpgradeForgeModuleCell(Clone)".
    private static bool IsForgeModule(CellModule module)
    {
        if (module == null) return false;
        var name = module.name ?? "";
        if (name.EndsWith("(Clone)")) name = name[..^7].TrimEnd();
        return name == UpgradeForgeBehavior.PrefabName;
    }
}
