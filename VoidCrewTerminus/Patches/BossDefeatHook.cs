using System.Collections.Generic;
using CG.Game;
using CG.Game.Scenarios;
using CG.Ship.Modules.Weapons;
using Gameplay.Quests;
using ResourceAssets;
using UnityEngine;
using VoidCrewTerminus.Commands;
using VoidCrewTerminus.Escalation;
using VoidCrewTerminus.Forge;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Patches;

// Phase 6 — boss-defeat side of Sector Escalation. Subscribes to the game's
// ModelEventBus.OnObjectiveStateChanged. When an objective completes AND the
// active quest is an EndlessQuest AND the objective is a registered boss, we
// bump DifficultyScalar (by config amount) and BossesDefeated (+1).
//
// Not a Harmony patch — pure event subscription, same pattern as ForgeSectorHook.
// Per-run de-dup by Objective reference (Objectives are recreated each session
// so a HashSet<Objective> works and resets naturally on session start).
internal static class BossDefeatHook
{
    private static readonly HashSet<Objective> _awardedBossObjectives = new();
    private static bool _subscribed;

    // Called from Plugins.cs on HostStartSession — clears the awarded set and
    // re-subscribes to the fresh ClientGame's ModelEventBus.
    internal static void OnSessionStart()
    {
        _awardedBossObjectives.Clear();

        var bus = ClientGame.Current?.ModelEventBus;
        if (bus == null)
        {
            BepinPlugin.Log.LogWarning("[Forge] BossDefeatHook: no ModelEventBus at session start — boss detection disabled this run.");
            return;
        }

        // Safe re-subscribe: unsubscribe first in case a stale handle survived
        // (hot-reload, unusual session lifecycle).
        bus.OnObjectiveStateChanged.Unsubscribe(OnObjectiveStateChanged);
        bus.OnObjectiveStateChanged.Subscribe(OnObjectiveStateChanged);
        _subscribed = true;
        BepinPlugin.Log.LogInfo("[Forge] BossDefeatHook armed for this session.");
    }

    // Hot-reload teardown (ScriptEngine) — a leaked subscription from the old
    // assembly would double-count boss defeats after F6.
    internal static void Shutdown()
    {
        if (!_subscribed) return;
        _subscribed = false;
        var bus = ClientGame.Current?.ModelEventBus;
        bus?.OnObjectiveStateChanged.Unsubscribe(OnObjectiveStateChanged);
        _awardedBossObjectives.Clear();
    }

    private static void OnObjectiveStateChanged(Objective objective, ObjectiveState state)
    {
        try
        {
            if (state != ObjectiveState.Completed) return;
            if (objective == null || objective.Asset == null) return;

            if (!(GameSessionManager.ActiveSession?.ActiveQuest is EndlessQuest endless)) return;
            if (!endless.IsBossObjective(objective.Asset)) return;

            // Phase 8-A: BossesDefeated is host-authoritative. This event fires on
            // every client; only the master counts + drives the unlock messages,
            // and broadcasts the new count. Clients receive it via the broadcast.
            if (!Net.ForgeNetSync.IsAuthority) return;

            if (!_awardedBossObjectives.Add(objective))
            {
                BepinPlugin.Log.LogDebug($"[Forge] Boss objective {objective.Asset} already awarded — skipping.");
                return;
            }

            // Compute the unlock message BEFORE the increment so the player sees
            // which tier just became available.
            string unlockMessage = DescribeUnlock(SectorEscalation.BossesDefeated);

            // The Forge BuildBox reward: awarded on the 2nd boss defeat — same
            // threshold DescribeUnlock already special-cases as the Legendary-tier
            // unlock (bossesBeforeThisOne == 1). Read before the increment below so
            // this fires exactly once, on exactly the completion that crosses from
            // 1 to 2 bosses defeated.
            if (SectorEscalation.BossesDefeated == 1)
                AwardForgeBuildBox();

            // Capture activation state BEFORE the boss-count bump. Boss defeats
            // during warm-up (including the one that CROSSES the threshold) do
            // not contribute to DifficultyScalar — only boss defeats where
            // escalation was already active count. This preserves the
            // "scalar accumulates only after escalation activates" rule.
            bool wasActive = SectorEscalation.IsScalingActive;

            SectorEscalation.IncrementBossesDefeated();
            int bump = System.Math.Max(1, TerminusConfig.BossScalarBonus);
            // Deliberately not gated on Forge presence — escalation state
            // accumulates in the background even without a Forge installed, so
            // installing one mid-run picks up whatever scalar has built up.
            if (wasActive)
                ForgeMeterController.IncrementDifficultyScalarBy(bump);

            if (unlockMessage != null)
                Messaging.Notification(unlockMessage);
            BepinPlugin.Log.LogInfo(
                $"[Escalation] Boss defeated ({objective.Asset}) — scalar {(wasActive ? "+" + bump : "gated")}, " +
                $"bosses → {SectorEscalation.BossesDefeated}.");

            // Push the new boss count (+ any scalar bump) to clients.
            Net.ForgeNetSync.BroadcastState();
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Forge] BossDefeatHook failed: {e}");
        }
    }

    // Delivered via vanilla's own objective-reward pipeline (SpawnUtils.SpawnCarePackage
    // — the same call Objective.ObjectiveCompleted's CompletionDrop uses) rather than
    // hooking monster loot drops: there is no boss-exclusive loot list to hook into
    // (loot content is one shared per-sector pool, not per-monster — see
    // docs/forge-buildbox-research.md), so this reuses vanilla's existing "reward
    // package flies in near the ship" delivery instead of inventing a new one.
    // Caller already holds the IsAuthority gate.
    private static void AwardForgeBuildBox()
    {
        if (!ForgeSpawnCommand.TryFindForgeAssetGuid(UpgradeForgeBehavior.BuildBoxPrefabName, out var boxGuid))
        {
            BepinPlugin.Log.LogWarning("[Forge] 2nd boss defeated but the Forge BuildBox isn't registered — reward not spawned.");
            return;
        }

        AssetLoader.EnsureBuildBoxTemplateReady();

        var playerShip = ClientGame.Current?.PlayerShip;
        if (playerShip == null) return;

        var position = ValidPositionNearPlayerShip(playerShip.transform.position);
        SpawnUtils.SpawnCarePackage(boxGuid, 1, position);
        BepinPlugin.Log.LogInfo("[Forge] 2nd boss defeated — Forge BuildBox care package incoming.");
    }

    // A simplified reimplementation of Objective.GetValidDonutPositionAroundPlayerShip
    // (private, so not directly callable) — a random point in a ring around the ship,
    // retried up to `iterations` times until one has line of sight. Not a byte-for-byte
    // port (vanilla feeds degrees straight into Mathf.Cos/Sin without a Deg2Rad
    // conversion, which reads like an existing vanilla quirk rather than something
    // worth replicating); the practical result — "a plausible point near the ship" —
    // is the same either way.
    private static Vector3 ValidPositionNearPlayerShip(Vector3 shipPosition, float minRange = 250f, float maxRange = 500f, int iterations = 10)
    {
        for (int i = 0; i < iterations; i++)
        {
            float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float range = Random.Range(minRange, maxRange);
            float height = Random.Range(-100f, 100f);
            var candidate = shipPosition + new Vector3(Mathf.Cos(angle) * range, height, Mathf.Sin(angle) * range);
            if (ProjectileUtils.HasLineOfSight(candidate, ClientGame.Current.PlayerShip))
                return candidate;
        }
        return shipPosition;
    }

    private static string DescribeUnlock(int bossesBeforeThisOne)
    {
        int threshold = TerminusConfig.BossActivationThreshold;
        int bossesAfterThisOne = bossesBeforeThisOne + 1;

        // The activation threshold gates all escalation systems. Boss defeats
        // BEFORE the threshold accumulate silently (state ticks up, no player
        // notification). The defeat that CROSSES the threshold gets a distinct
        // "escalation now active" message. Boss defeats AFTER the threshold
        // just report whichever tier ceiling they unlock, if any.
        if (bossesAfterThisOne < threshold)
            return null; // warm-up — silent

        if (bossesAfterThisOne == threshold)
            return "Boss defeated — the Forge stirs to life. Escalation is now active.";

        // Post-activation ceiling unlock messages (matches SectorEscalation.MaxAllowedTier).
        if (bossesBeforeThisOne == 0)
            return "Boss defeated — the Forge unlocks Rare-tier relics.";
        if (bossesBeforeThisOne == 1)
            return "Boss defeated — the Forge unlocks Legendary-tier relics.";
        return null;
    }
}
