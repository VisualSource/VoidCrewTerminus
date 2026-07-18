using System.Reflection;
using CG.Game;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Object;
using Gameplay.Factory;
using HarmonyLib;
using UI.LoadoutTerminal;
using UnityEngine;
using UnityEngine.UIElements;
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
        if (ForgeStateStore.TryGet(module, out var state))
            ForgeStateStore.SaveSnapshot(__result.photonView.ViewID, state.Snapshot());
    }
}

// --- Reconstruction: restore forge level onto the newly spawned CellModule ---

[HarmonyPatch(typeof(BuildBox), nameof(BuildBox.BuildModule))]
internal static class BuildBoxBuildModulePatch
{
    static void Postfix(BuildBox __instance, CellModule __result) =>
        ForgePersistPatchHelper.RestoreSnapshot(__instance, __result);
}

[HarmonyPatch(typeof(CompositeWeaponBuildBox), nameof(BuildBox.BuildModule))]
internal static class CompositeWeaponBuildBoxBuildModulePatch
{
    static void Postfix(CompositeWeaponBuildBox __instance, CellModule __result) =>
        ForgePersistPatchHelper.RestoreSnapshot(__instance, __result);
}

// --- Recycle: scale alloy payout by forge level ---

[HarmonyPatch(typeof(CarryableFactoryLogic), nameof(CarryableFactoryLogic.Recycle))]
internal static class FabricatorRecyclePatch
{
    static void Prefix(CarryableFactoryLogic __instance, ref int amount)
    {
        var box = __instance.RecycleSocket?.Payload as BuildBox;
        if (box == null) return;
        if (!ForgeStateStore.TryPeekSnapshot(box.photonView.ViewID, out var snap)) return;
        float multiplier = 1f + (snap.Level - 3) * ForgeConstants.RecycleAlloysPerLevel;
        amount = Mathf.RoundToInt(amount * multiplier);
    }
}

// --- Recycle: show the SCALED payout in the fabricator panel ---
//
// FabricatorRecyclePatch above scales what a forged box actually pays out, but
// the panel keeps rendering the unscaled CraftableItemDef value — so a Mk X box
// displayed base alloys and then paid +105%. This closes that gap.
//
// RecycleTab has no direct handle on the box instance (PurchasableItem is
// GUID-keyed, not per-instance), so we resolve it the same way the payout patch
// does: through the fabricator logic's recycle socket. Both hops are non-public,
// hence the reflection.

[HarmonyPatch(typeof(RecycleTab), "SocketChanged")]
internal static class RecycleTabDisplayPatch
{
    private static readonly FieldInfo _dataField =
        AccessTools.Field(typeof(FabricatorActionTab), "data");
    private static readonly FieldInfo _logicField =
        AccessTools.Field(typeof(FabricatorData), "logic");
    private static readonly FieldInfo _alloyCostField =
        AccessTools.Field(typeof(RecycleTab), "_alloyCost");

    private static bool _loggedFailure;

    static void Postfix(RecycleTab __instance, PurchasableItem item)
    {
        if (item == null || !item.CraftingRules.CanBeRecycled) return;

        try
        {
            if (_dataField?.GetValue(__instance) is not FabricatorData data) return;
            if (_logicField?.GetValue(data) is not CarryableFactoryLogic logic) return;
            if (logic.RecycleSocket?.Payload is not BuildBox box || box.photonView == null) return;
            if (!ForgeStateStore.TryPeekSnapshot(box.photonView.ViewID, out var snap)) return;

            // Unforged boxes keep vanilla's number untouched.
            float multiplier = 1f + (snap.Level - 3) * ForgeConstants.RecycleAlloysPerLevel;
            if (multiplier <= 1f) return;

            int scaled = Mathf.RoundToInt(item.CraftingRules.Recycle.Amount * multiplier);
            if (_alloyCostField?.GetValue(__instance) is Label label)
                label.text = " +" + scaled;
        }
        catch (System.Exception ex)
        {
            if (_loggedFailure) return;
            _loggedFailure = true;
            BepinPlugin.Log.LogWarning($"[Forge/UI] recycle value display patch failed (suppressing further): {ex}");
        }
    }
}

// --- Shared helper ---

internal static class ForgePersistPatchHelper
{
    internal static void RestoreSnapshot(BuildBox box, CellModule module)
    {
        if (module == null) return;
        if (!ForgeStateStore.TryTakeSnapshot(box.photonView.ViewID, out var snap)) return;
        ForgeStateStore.GetOrCreate(module).ApplySnapshot(snap);

        // Only THIS machine ran BuildModule (it ends in PhotonNetwork.Instantiate),
        // so nobody else can make the box→module connection. Announce it, keyed by
        // the module's ViewID, or every remote client shows a vanilla module.
        if (module.photonView != null)
            Net.ForgeNetSync.BroadcastModuleOverlay(module.photonView.ViewID, snap);
    }
}
