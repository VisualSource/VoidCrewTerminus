using HarmonyLib;
using UnityEngine.UIElements;

namespace VoidCrewTerminus.Patches;

[HarmonyPatch(typeof(FabricationTab), "OnShopUpdated")]
internal class RelicReplicationPatch
{
    static void Postfix(VisualElement ___root)
    {
        if (TerminusConfig.AllowRelicReplication.Value) return;

        if (___root?.Q("RelicsTab") is { } tab)
            tab.style.display = DisplayStyle.None;
    }
}

