using System;
using System.Collections.Generic;
using CG.Ship.Modules;
using CG.Ship.Object;
using Client.Player.Interactions;
using HarmonyLib;
using UnityEngine;
using VoidCrewTerminus.Forge;
using VoidCrewTerminus.Loot;

namespace VoidCrewTerminus.Patches;

// One patch point covers relics, build boxes and installed modules: all three resolve their
// tooltip through this getter, which allocates a fresh ContextInfoViewModel per call, so
// mutating __result can't accumulate. Not a ContextInfoModifier (the game's own extension
// point): registering one needs a replay of the Awake-time Init() that already-awake objects
// would miss, and everything shown here is fixed at commit time anyway.
[HarmonyPatch(typeof(ContextInfoProvider), nameof(ContextInfoProvider.ContextInfo), MethodType.Getter)]
internal static class ForgeHoverUiPatch
{
    // This getter runs per hover frame. A throwing postfix would spam the log and
    // could break tooltips wholesale, so failures are swallowed after one report.
    private static bool _loggedFailure;

    // Vanilla header formats aren't inspectable offline, so RewriteMark has to guess whether
    // a mark is already embedded. Each distinct header is logged once to settle that.
    private static readonly HashSet<string> _loggedHeaders = new();

    static void Postfix(ContextInfoProvider __instance, ref ContextInfoViewModel __result)
    {
        if (__result == null || __instance == null) return;

        try
        {
            var go = __instance.gameObject;
            if (go == null) return;

            // A given object is only ever one of these.
            var module = go.GetComponent<CellModule>();
            if (module != null) { ApplyModule(module, __result); return; }

            var box = go.GetComponent<BuildBox>();
            if (box != null) { ApplyBox(box, __result); return; }

            ApplyRelic(go, __result);
        }
        catch (Exception ex)
        {
            if (_loggedFailure) return;
            _loggedFailure = true;
            BepinPlugin.Log.LogWarning($"[Forge/UI] hover tooltip postfix failed (suppressing further): {ex}");
        }
    }

    private static void ApplyModule(CellModule module, ContextInfoViewModel vm)
    {
        if (!ForgeStateStore.TryGet(module, out var state)) return;
        Decorate(vm, state.Level, state.PerkSlots, state.Burdens);
    }

    private static void ApplyBox(BuildBox box, ContextInfoViewModel vm)
    {
        if (box.photonView == null) return;

        // Read the snapshot directly rather than through LevelOfBox, which walks the whole
        // UpgradableAssetDataTable: far too expensive for a getter that runs every hover
        // frame. A missing snapshot already means "nothing forged here".
        if (!ForgeStateStore.TryPeekSnapshot(box.photonView.ViewID, out var snap)) return;
        Decorate(vm, snap.Level, snap.PerkSlots, snap.Burdens);
    }

    private static void Decorate(ContextInfoViewModel vm, int level,
        IReadOnlyList<string> perks, IReadOnlyList<BurdenType> burdens)
    {
        // Untouched modules render byte-identical to vanilla. This is also the path a client
        // takes before forge state has synced, so it shows vanilla rather than a wrong level.
        if (!ForgeLabels.HasOverlay(level, perks, burdens)) return;

        LogHeaderOnce(vm.Header);
        vm.Header = ForgeLabels.RewriteMark(vm.Header, level);
        vm.Body += ForgeLabels.BuildOverlayBody(level, perks, burdens);
    }

    // Cursed marker on the instance, tier keyed by prefab name.
    private static void ApplyRelic(GameObject go, ContextInfoViewModel vm)
    {
        if (!RelicTierData.TryGet(go.name, out var entry)) return;
        vm.Body += ForgeLabels.BuildRelicBody(entry.Tier, CursedRelicMarker.GetBurden(go));
        // vm.Rarity is vanilla's own, authored separately from the mod's forge tier.
    }

    private static void LogHeaderOnce(string header)
    {
        if (string.IsNullOrEmpty(header) || !_loggedHeaders.Add(header)) return;
        BepinPlugin.Log.LogDebug($"[Forge/UI] raw vanilla header: \"{header}\"");
    }
}
