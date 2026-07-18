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

// Surfaces mod state (forge level, perks, burdens, relic curse) in the game's
// hover tooltips.
//
// ONE patch point covers all three surfaces. Relics, build boxes and installed
// modules all descend from AbstractCloneStarObject and all resolve their tooltip
// through ContextInfoProvider.ContextInfo — which is also the getter
// ContextInfoDisplay calls for BOTH the world-hover path and the held-carryable
// path. Patching here means carried and hovered items agree for free.
//
// The getter allocates a fresh ContextInfoViewModel per call (see
// AbstractCloneStarObject.ContextInfo), so mutating __result cannot accumulate
// across calls.
//
// We deliberately do NOT register a ContextInfoModifier (the game's own extension
// point) even though vanilla ships two of them. A modifier would contribute a
// ContinuousRefreshInterval and a DataChanged channel, but everything we display
// is fixed at commit time, and injecting one needs reflection into a private
// [SerializeReference] list plus a replay of the Awake-time Init() lifecycle —
// which would miss objects that already awoke (late joiners, pre-spawned relics).
[HarmonyPatch(typeof(ContextInfoProvider), nameof(ContextInfoProvider.ContextInfo), MethodType.Getter)]
internal static class ForgeHoverUiPatch
{
    // This getter runs per hover frame. A throwing postfix would spam the log and
    // could break tooltips wholesale, so failures are swallowed after one report.
    private static bool _loggedFailure;

    // Raw vanilla header formats aren't inspectable offline (display names live in
    // asset bundles), so ForgeLabels.RewriteMark has to guess whether a mark is
    // already embedded. Log each distinct header once to settle it from a playtest.
    private static readonly HashSet<string> _loggedHeaders = new();

    static void Postfix(ContextInfoProvider __instance, ref ContextInfoViewModel __result)
    {
        if (__result == null || __instance == null) return;

        try
        {
            var go = __instance.gameObject;
            if (go == null) return;

            // Installed module first, then loose box, then relic. A given object is
            // only ever one of these.
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

    // --- installed module: state lives in the per-CellModule table ---

    private static void ApplyModule(CellModule module, ContextInfoViewModel vm)
    {
        if (!ForgeStateStore.TryGet(module, out var state)) return;
        Decorate(vm, state.Level, state.PerkSlots, state.Burdens);
    }

    // --- loose build box: state lives in the ViewID-keyed snapshot store ---

    private static void ApplyBox(BuildBox box, ContextInfoViewModel vm)
    {
        if (box.photonView == null) return;

        // Read the snapshot directly rather than going through
        // UpgradeForgeBehavior.LevelOfBox: that walks the entire
        // UpgradableAssetDataTable to resolve a vanilla mark, which is far too
        // expensive for a getter that runs every hover frame. A missing snapshot
        // already means "nothing forged here", which is exactly the no-op case.
        if (!ForgeStateStore.TryPeekSnapshot(box.photonView.ViewID, out var snap)) return;
        Decorate(vm, snap.Level, snap.PerkSlots, snap.Burdens);
    }

    private static void Decorate(ContextInfoViewModel vm, int level,
        IReadOnlyList<string> perks, IReadOnlyList<BurdenType> burdens)
    {
        // Untouched modules render byte-identical to vanilla. This is also the
        // path a client takes before forge state has synced, so a desynced client
        // shows vanilla rather than a wrong level.
        if (!ForgeLabels.HasOverlay(level, perks, burdens)) return;

        LogHeaderOnce(vm.Header);
        vm.Header = ForgeLabels.RewriteMark(vm.Header, level);
        vm.Body += ForgeLabels.BuildOverlayBody(level, perks, burdens);
    }

    // --- relic: cursed marker on the instance, tier keyed by prefab name ---

    private static void ApplyRelic(GameObject go, ContextInfoViewModel vm)
    {
        if (!RelicTierData.TryGet(go.name, out var entry)) return;
        vm.Body += ForgeLabels.BuildRelicBody(entry.Tier, CursedRelicMarker.GetBurden(go));
        // vm.Rarity is left alone on purpose — that's vanilla's own rarity, which
        // is authored separately from the mod's forge tier. See ForgeLabels.
    }

    private static void LogHeaderOnce(string header)
    {
        if (string.IsNullOrEmpty(header) || !_loggedHeaders.Add(header)) return;
        BepinPlugin.Log.LogDebug($"[Forge/UI] raw vanilla header: \"{header}\"");
    }
}
