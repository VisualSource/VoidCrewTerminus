using CG.Space;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using VoidCrewTerminus.Loot;

namespace VoidCrewTerminus.Patches;

// Phase 7-B — per-instance cursed roll, one roll per relic at its own spawn.
//
// Hook: OrbitObject.OnPhotonInstantiate postfix. Every relic reaches the world
// through Photon instantiation, so this catches every source — enemy death
// loot, predefined/POI loot, relic shrines, and !spawn — and fires exactly once
// per instance. That makes the old CursedProcessedMarker idempotency sentinel
// unnecessary: an object is only instantiated once.
//
// This replaces an earlier design that hooked LootOnDeathDropper and then walked
// the whole scene with FindObjectsOfType<CarryableObject>() per drop. That was
// O(scene) on every enemy death (and density escalation multiplies deaths), only
// rolled relics when some enemy happened to drop loot, and missed relics from
// non-drop sources entirely.
//
// Host rolls + marks + broadcasts (Phase 8-B); clients drain any buffered cursed
// flag that arrived before this relic instantiated. The host is authoritative for
// the roll; clients only mirror it for awareness (!cursedstatus / future hover UI).
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

            // Is this a relic we have tier data for? The runtime GameObject name
            // carries Unity's "(Clone)" suffix; NormalizeName strips it, leaving
            // the prefab base name that RelicTierData is keyed by.
            var name = RelicTierData.NormalizeName(go.name);
            if (!RelicTierData.TryGet(name, out var entry)) return;

            if (!PhotonNetwork.IsMasterClient)
            {
                // Client: the host owns the roll. If its cursed message already
                // arrived for this relic, apply the buffered flag now.
                Net.ForgeNetSync.TryApplyPendingCursed(__instance.photonView, go);
                return;
            }

            // Host: roll cursed authoritatively.
            float chance = CursedRelicRoll.ChanceFor(
                entry,
                Forge.ForgeMeterController.DifficultyScalar,
                TerminusConfig.RelicBaseCurseChance?.Value ?? 0.15f,
                TerminusConfig.EscalationCurseChancePerScalar?.Value ?? 0.03f,
                TerminusConfig.RelicMaxCurseChance?.Value ?? 0.50f);

            if (!CursedRelicRoll.ShouldBeCursed(chance, Random.value)) return;

            // Bake the specific burden now — curse identity is fixed for the
            // lifetime of this relic instance. Uniform pick from the relic's
            // BurdenAffinity (every relic defaults to [RandomShutoff] today).
            var affinity = entry.BurdenAffinity;
            if (affinity == null || affinity.Count == 0) return;
            var burden = affinity[affinity.Count == 1 ? 0 : Random.Range(0, affinity.Count)];

            CursedRelicMarker.MarkCursed(go, burden);

            // Causal log (7-B): proves the roll fired and what it decided.
            BepinPlugin.Log?.LogDebug(
                $"[Escalation] Relic {name} spawned CURSED with {burden} (chance {chance:P1})");

            // Mirror it to clients (Phase 8-B). No-ops in solo / with no peers.
            Net.ForgeNetSync.BroadcastCursed(__instance.photonView, burden);
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log?.LogError($"[Escalation] CursedRelicSpawnPatch failed: {e}");
        }
    }
}
