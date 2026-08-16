using CG.Space;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using VoidCrewTerminus.Loot;

namespace VoidCrewTerminus.Patches;

// Hooks OrbitObject.OnPhotonInstantiate rather than LootOnDeathDropper: every
// relic reaches the world through Photon instantiation (enemy loot, POI loot,
// shrines, !spawn) and fires exactly once per instance, so no idempotency
// sentinel is needed. Walking the scene with FindObjectsOfType per drop (the
// earlier design) was O(scene) on every enemy death and missed non-drop sources.
//
// Host rolls + marks + broadcasts; clients drain any buffered cursed flag that
// arrived before this relic instantiated and only mirror the roll for awareness
// (!cursedstatus / hover UI) — the host is authoritative.
[HarmonyPatch(typeof(OrbitObject), nameof(OrbitObject.OnPhotonInstantiate))]
internal static class CursedRelicSpawnPatch
{
    static void Postfix(OrbitObject __instance)
    {
        try
        {
            if (__instance == null) return;
            var go = __instance.gameObject;
            if (go == null) return;

            // The runtime GameObject name carries Unity's "(Clone)" suffix;
            // NormalizeName strips it to the prefab base name RelicTierData is keyed by.
            var name = RelicTierData.NormalizeName(go.name);
            if (!RelicTierData.TryGet(name, out var entry)) return;

            if (!PhotonNetwork.IsMasterClient)
            {
                Net.ForgeNetSync.TryApplyPendingCursed(__instance.photonView, go);
                return;
            }

            float chance = CursedRelicRoll.ChanceFor(
                entry,
                Forge.ForgeMeterController.DifficultyScalar,
                TerminusConfig.BaseCurseChance,
                TerminusConfig.CurseChancePerScalar,
                TerminusConfig.MaxCurseChance);

            if (!CursedRelicRoll.ShouldBeCursed(chance, Random.value)) return;

            // Curse identity is fixed for the lifetime of this relic instance.
            var affinity = entry.BurdenAffinity;
            if (affinity == null || affinity.Count == 0) return;
            var burden = affinity[affinity.Count == 1 ? 0 : Random.Range(0, affinity.Count)];

            CursedRelicMarker.MarkCursed(go, burden);

            BepinPlugin.Log?.LogDebug(
                $"[Escalation] Relic {name} spawned CURSED with {burden} (chance {chance:P1})");

            Net.ForgeNetSync.BroadcastCursed(__instance.photonView, burden);
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log?.LogError($"[Escalation] CursedRelicSpawnPatch failed: {e}");
        }
    }
}
