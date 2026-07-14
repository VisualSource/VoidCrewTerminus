using System.Collections.Generic;
using CG.Game;
using CG.Game.Scenarios;
using Gameplay.Quests;
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

            if (!_awardedBossObjectives.Add(objective))
            {
                BepinPlugin.Log.LogDebug($"[Forge] Boss objective {objective.Asset} already awarded — skipping.");
                return;
            }

            // Compute the unlock message BEFORE the increment so the player sees
            // which tier just became available.
            string unlockMessage = DescribeUnlock(SectorEscalation.BossesDefeated);

            SectorEscalation.IncrementBossesDefeated();
            int bump = System.Math.Max(1, TerminusConfig.EscalationBossScalarBonus?.Value ?? 1);
            ForgeMeterController.IncrementDifficultyScalarBy(bump);

            if (unlockMessage != null)
                Messaging.Notification(unlockMessage);
            BepinPlugin.Log.LogInfo(
                $"[Escalation] Boss defeated ({objective.Asset}) — scalar +{bump}, bosses → {SectorEscalation.BossesDefeated}.");
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Forge] BossDefeatHook failed: {e}");
        }
    }

    private static string DescribeUnlock(int bossesBeforeThisOne)
    {
        // First boss unlocks Rare (was 0 → now 1).
        // Second boss unlocks Legendary (was 1 → now 2).
        // Third+ bosses have no further tier to unlock.
        if (bossesBeforeThisOne == 0)
            return "Boss defeated — the Forge unlocks Rare-tier relics.";
        if (bossesBeforeThisOne == 1)
            return "Boss defeated — the Forge unlocks Legendary-tier relics.";
        return null;
    }
}
