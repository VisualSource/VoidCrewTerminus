using UI.Fabricator;
using HarmonyLib;
using ResourceAssets;
using UnityEngine.UIElements;

namespace VoidCrewTerminus.Patches;

[HarmonyPatch(typeof(ShopPanelController), nameof(ShopPanelController.SetupShop))]
internal class RelicReplicationPatch
{
    static void Postfix(ShopPanelController __instance)
    {
        if (TerminusConfig.AllowRelicReplication.Value) return;

        if (__instance.shopCategories.TryGetValue(PurchasableItemSubCategory.Mods_Relic, out var relicList))
        {
            relicList.style.display = DisplayStyle.None;
        }
    }
}

