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
// Host-only: the marker is a local component and DifficultyScalar isn't networked
// yet, so the host is the authority. Clients therefore don't see cursed state — a
// known gap that lands with the rest of the commit-path sync in Phase 8. The
// commit path logs loudly when it runs off-host, so the divergence is visible
// rather than silent.
[HarmonyPatch(typeof(OrbitObject), nameof(OrbitObject.OnPhotonInstantiate))]
internal static class CursedRelicSpawnPatch
{
    static void Postfix(OrbitObject __instance)
    {
        try
        {
            if (__instance == null) return;
            if (!PhotonNetwork.IsMasterClient) return;

            var go = __instance.gameObject;
            if (go == null) return;

            // Is this a relic we have tier data for? The runtime GameObject name
            // carries Unity's "(Clone)" suffix; NormalizeName strips it, leaving
            // the prefab base name that RelicTierData is keyed by.
            var name = RelicTierData.NormalizeName(go.name);
            if (!RelicTierData.TryGet(name, out var entry)) return;

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
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log?.LogError($"[Escalation] CursedRelicSpawnPatch failed: {e}");
        }
    }
}
