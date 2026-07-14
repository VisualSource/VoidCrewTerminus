using System.Collections.Generic;
using CG.Objects;
using CG.Space;
using Gameplay.Loot;
using HarmonyLib;
using UnityEngine;
using VoidCrewTerminus.Escalation;
using VoidCrewTerminus.Loot;

namespace VoidCrewTerminus.Patches;

// Phase 7-B — per-instance cursed roll on relic spawn. Host-only patch.
//
// Hook: LootOnDeathDropper.SpawnLootInCurrentPosition postfix. Runs after the
// enemy's loot has been spawned into orbit space. We iterate active orbit
// objects near the dropper and mark any that are relics with the cursed flag,
// based on a per-relic chance computed by CursedRelicRoll.
//
// Multiplayer note: this is scaffold — the marker is added on the host only.
// Client-side cursed state will need proper sync when Phase 7-C wires the flag
// into gameplay (cursed-augmented perk pool). Until then, the flag has no
// gameplay effect, so host-only is acceptable.
[HarmonyPatch(typeof(LootOnDeathDropper), nameof(LootOnDeathDropper.SpawnLootInCurrentPosition))]
internal static class CursedRelicSpawnPatch
{
    static void Postfix(List<ResourceAssets.CraftableItemRef> lootToSpawn)
    {
        try
        {
            if (lootToSpawn == null || lootToSpawn.Count == 0) return;
            if (!Photon.Pun.PhotonNetwork.IsMasterClient) return;

            // Freshly-spawned relics take at least a frame to register in the
            // sector's active orbit list. Delay by one frame via a coroutine on
            // the LootManager (which has a stable lifetime for the session)?
            // Simpler: run the scan immediately — SpawnLootInCurrentPosition
            // instantiates via ObjectFactory synchronously, and the objects are
            // in the scene by the time this postfix runs.
            ScanAndMarkNearbyRelics();
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log?.LogError($"[Escalation] CursedRelicSpawnPatch failed: {e}");
        }
    }

    // Walk all currently-spawned CarryableObjects in the active sector; for each
    // one that looks like a relic, roll cursed if it hasn't been marker-tagged
    // yet. The "not yet marked" check makes this idempotent — repeat calls
    // (from multiple droppers in a single frame) don't re-roll already-processed
    // relics.
    private static void ScanAndMarkNearbyRelics()
    {
        float baseChance = TerminusConfig.RelicBaseCurseChance?.Value ?? 0.15f;
        float scalarBonus = TerminusConfig.EscalationCurseChancePerScalar?.Value ?? 0.03f;
        int scalar = Forge.ForgeMeterController.DifficultyScalar;
        bool active = SectorEscalation.IsScalingActive;

        // If chance would always be 0 (dormant escalation + no base curse), skip
        // the scene walk entirely.
        if (!active) return;

        foreach (var carryable in Object.FindObjectsOfType<CarryableObject>())
        {
            if (carryable == null) continue;
            var go = carryable.gameObject;
            if (go == null) continue;

            // Already processed?
            if (go.GetComponent<CursedRelicMarker>() != null) continue;
            if (go.GetComponent<CursedProcessedMarker>() != null) continue;

            // Is this a relic we know about?
            var name = RelicTierData.NormalizeName(go.name);
            if (!RelicTierData.TryGet(name, out var entry)) continue;

            // Roll — deterministic-ish per (viewId, quest.seed) but simple: use
            // UnityEngine.Random on host, which is host-authoritative.
            float chance = CursedRelicRoll.ChanceFor(entry, scalar, active, baseChance, scalarBonus);
            if (CursedRelicRoll.ShouldBeCursed(chance, Random.value))
            {
                // Pick the specific burden type NOW, at spawn — the curse is
                // baked into the relic. Uniform pick from the relic's
                // BurdenAffinity list. If empty, no burden can attach — skip
                // marking cursed at all (defensive; RelicTierEntry defaults to
                // [RandomShutoff] so this branch is rare).
                var affinity = entry.BurdenAffinity;
                if (affinity == null || affinity.Count == 0) continue;
                int idx = affinity.Count == 1 ? 0 : Random.Range(0, affinity.Count);
                var burden = affinity[idx];

                CursedRelicMarker.MarkCursed(go, burden);
                BepinPlugin.Log?.LogDebug(
                    $"[Escalation] Relic {name} spawned CURSED with {burden} (chance {chance:P1})");
            }

            // Idempotency marker so we don't re-roll on subsequent scans.
            go.AddComponent<CursedProcessedMarker>();
        }
    }
}

// Sentinel component — indicates this relic has already been through the
// cursed-roll pipeline (whether or not it was cursed). Prevents subsequent
// spawn events (multiple droppers in one frame, re-entrance) from re-rolling.
internal sealed class CursedProcessedMarker : MonoBehaviour { }
