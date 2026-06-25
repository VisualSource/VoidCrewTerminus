using HarmonyLib;
using UnityEngine.UIElements;
using Client.Player.Interactions;
using Gameplay.Terminals;

namespace VoidCrewTerminus.Patches;

[HarmonyPatch(typeof(FabricationTab), MethodType.Constructor, typeof(VisualElement), typeof(FabricatorData), typeof(ContextInfo), typeof(VisualTreeAsset), typeof(TerminalScreen))]
internal class RelicReplicationPatch
{
    static void Postfix(VisualElement root)
    {
        if (TerminusConfig.AllowRelicReplication.Value) return;

        if (root.Q("RelicsTab") is { } tab)
            tab.style.display = DisplayStyle.None;
    }
}

