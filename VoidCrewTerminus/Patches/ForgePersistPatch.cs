using CG.Game;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Object;
using Gameplay.Factory;
using HarmonyLib;
using UnityEngine;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Patches;

// Per level above 3, the alloy recycle payout scales by this fraction.
// e.g. 0.15 → L7 box (4 levels above 3) gives 1.60× base alloys.
internal static class ForgeConstants
{
    public const float RecycleAlloysPerLevel = 0.15f;
}

// --- Deconstruction: save forge level keyed by the new BuildBox's ViewID ---

[HarmonyPatch(typeof(Deconstruct), nameof(Deconstruct.CreateBuildBox))]
internal static class DeconstructCreateBuildBoxPatch
{
    static void Postfix(CellModule module, BuildBox __result)
    {
        if (__result == null) return;
        if (ForgeOverlayTable.TryGet(module, out var state) && state.Level > 3)
            ForgeOverlayTable.SavePendingLevel(__result.photonView.ViewID, state.Level);
    }
}

// --- Reconstruction: restore forge level onto the newly spawned CellModule ---

[HarmonyPatch(typeof(BuildBox), nameof(BuildBox.BuildModule))]
internal static class BuildBoxBuildModulePatch
{
    static void Postfix(BuildBox __instance, CellModule __result) =>
        ForgePersistPatchHelper.RestoreLevel(__instance, __result);
}

[HarmonyPatch(typeof(CompositeWeaponBuildBox), nameof(BuildBox.BuildModule))]
internal static class CompositeWeaponBuildBoxBuildModulePatch
{
    static void Postfix(CompositeWeaponBuildBox __instance, CellModule __result) =>
        ForgePersistPatchHelper.RestoreLevel(__instance, __result);
}

// --- Recycle: scale alloy payout by forge level ---

[HarmonyPatch(typeof(CarryableFactoryLogic), nameof(CarryableFactoryLogic.Recycle))]
internal static class FabricatorRecyclePatch
{
    static void Prefix(CarryableFactoryLogic __instance, ref int amount)
    {
        var box = __instance.RecycleSocket?.Payload as BuildBox;
        if (box == null) return;
        if (!ForgeOverlayTable.TryPeekPendingLevel(box.photonView.ViewID, out int level)) return;
        float multiplier = 1f + (level - 3) * ForgeConstants.RecycleAlloysPerLevel;
        amount = Mathf.RoundToInt(amount * multiplier);
    }
}

// --- Shared helper ---

internal static class ForgePersistPatchHelper
{
    internal static void RestoreLevel(BuildBox box, CellModule module)
    {
        if (module == null) return;
        if (!ForgeOverlayTable.TryRestoreLevel(box.photonView.ViewID, out int level)) return;
        ForgeOverlayTable.GetOrCreate(module).SetLevel(level);
    }
}
