using CG.Space;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using VoidCrewTerminus.Loot;

namespace VoidCrewTerminus.Patches;

// Hooks OnPhotonInstantiate rather than LootOnDeathDropper: every relic reaches the world
// through Photon instantiation (enemy loot, POI loot, shrines, !spawn) and fires exactly
// once per instance, so no idempotency sentinel is needed.
//
// The host rolls, marks and broadcasts. Clients drain any cursed flag buffered before this
// relic instantiated and mirror the roll for awareness only.
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

            // The runtime name carries Unity's "(Clone)" suffix; RelicTierData is keyed
            // by the prefab base name.
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
