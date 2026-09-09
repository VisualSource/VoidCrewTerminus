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

// Not a Harmony patch — subscribes to ModelEventBus.OnObjectiveStateChanged
// directly, same pattern as ForgeSectorHook. Objectives are recreated each
// session, so a HashSet<Objective> for per-run de-dup resets naturally on
// session start.
internal static class BossDefeatHook
{
    private static readonly HashSet<Objective> _awardedBossObjectives = new();
    private static bool _subscribed;

    // Called from Plugins.cs on HostStartSession.
    internal static void OnSessionStart()
    {
        _awardedBossObjectives.Clear();

        var bus = ClientGame.Current?.ModelEventBus;
        if (bus == null)
        {
            BepinPlugin.Log.LogWarning("[Forge] BossDefeatHook: no ModelEventBus at session start — boss detection disabled this run.");
            return;
        }

        // Unsubscribe first in case a stale handle survived a hot-reload.
        bus.OnObjectiveStateChanged.Unsubscribe(OnObjectiveStateChanged);
        bus.OnObjectiveStateChanged.Subscribe(OnObjectiveStateChanged);
        _subscribed = true;
        BepinPlugin.Log.LogInfo("[Forge] BossDefeatHook armed for this session.");
    }

    // Hot-reload teardown: a leaked subscription from the old assembly would
    // double-count boss defeats after F6.
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

            // Host-authoritative: this event fires on every client, but only the
            // master counts and broadcasts; others receive the count via the sync.
            if (!Net.ForgeNetSync.IsAuthority) return;

            if (!_awardedBossObjectives.Add(objective))
            {
                BepinPlugin.Log.LogDebug($"[Forge] Boss objective {objective.Asset} already awarded — skipping.");
                return;
            }

            // Read the pre-increment count so the message reflects the tier that
            // just became available, not the one after this defeat.
            string unlockMessage = DescribeUnlock(SectorEscalation.BossesDefeated);

            // Same threshold DescribeUnlock treats as the Legendary-tier unlock;
            // read before the increment so this fires exactly once, on the
            // completion that crosses from 1 to 2 bosses defeated.
            if (SectorEscalation.BossesDefeated == 1)
                AwardForgeBuildBox();

            // Boss defeats during warm-up — including the one that crosses the
            // activation threshold — don't contribute to DifficultyScalar, so the
            // active flag must be captured before the increment below.
            bool wasActive = SectorEscalation.IsScalingActive;

            SectorEscalation.IncrementBossesDefeated();
            int bump = System.Math.Max(1, TerminusConfig.BossScalarBonus);
            // Not gated on Forge presence: escalation state accumulates in the
            // background even without one installed.
            if (wasActive)
                ForgeMeterController.IncrementDifficultyScalarBy(bump);

            if (unlockMessage != null)
                Messaging.Notification(unlockMessage);
            BepinPlugin.Log.LogInfo(
                $"[Escalation] Boss defeated ({objective.Asset}) — scalar {(wasActive ? "+" + bump : "gated")}, " +
                $"bosses → {SectorEscalation.BossesDefeated}.");

            Net.ForgeNetSync.BroadcastState();
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Forge] BossDefeatHook failed: {e}");
        }
    }

    // Dev-only direct trigger for !forgeboxdrop (Commands/ForgeCommitCommand.cs):
    // same delivery path as the real 2nd-boss reward, without needing to defeat
    // two bosses just to test the care-package pipeline.
    internal static void DebugAwardForgeBuildBox() => AwardForgeBuildBox();

    // Delivered via vanilla's SpawnUtils.SpawnCarePackage rather than hooking
    // monster loot drops: there is no boss-exclusive loot list to hook into (loot
    // is one shared per-sector pool, not per-monster), so this reuses vanilla's
    // "reward package flies in near the ship" delivery instead of a new one.
    // Caller already holds the IsAuthority gate.
    private static void AwardForgeBuildBox()
    {
        // Must run before the guid lookup: this registers the box into
        // RuntimeAssetsRegister (what TryFindForgeAssetGuid searches), so a fresh
        // !forgeboxdrop with no prior !forgespawn this session would otherwise
        // find no guid and bail before the template ever gets built.
        ModuleKit.CustomModuleRegistry.EnsureTemplatesReady();

        if (!ForgeSpawnCommand.TryFindForgeAssetGuid(UpgradeForgeBehavior.BuildBoxPrefabName, out var boxGuid))
        {
            BepinPlugin.Log.LogWarning("[Forge] 2nd boss defeated but the Forge BuildBox isn't registered — reward not spawned.");
            return;
        }

        var playerShip = ClientGame.Current?.PlayerShip;
        if (playerShip == null) return;

        var position = ValidPositionNearPlayerShip(playerShip.transform.position);
        SpawnUtils.SpawnCarePackage(boxGuid, 1, position);
        BepinPlugin.Log.LogInfo("[Forge] 2nd boss defeated — Forge BuildBox care package incoming.");
    }

    // Reimplements Objective.GetValidDonutPositionAroundPlayerShip (private, not
    // directly callable). Not a byte-for-byte port: vanilla feeds degrees straight
    // into Mathf.Cos/Sin without a Deg2Rad conversion, which reads like an existing
    // vanilla quirk rather than something worth replicating.
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

        // Below the threshold: accumulate silently. The defeat that crosses it
        // gets a distinct message; defeats after it just report whichever tier
        // ceiling they unlock, if any.
        if (bossesAfterThisOne < threshold)
            return null;

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
