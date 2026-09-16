using System.Collections.Generic;
using CG.Game.Scenarios;
using VoidCrewTerminus.Escalation;
using VoidCrewTerminus.Forge;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Patches;

// Passive Forge Meter fill on sector exit, landing during warp so the crew has transit time
// to react to a level-up. Not a Harmony patch: OnSectorExited is a public static event.
// New runs are detected by GameSession identity and each sector pays out at most once per
// run, so bouncing between two sectors can't farm the meter. Only a Completed objective pays.
internal static class ForgeSectorHook
{
    private static bool _initialized;
    private static GameSession _lastSession;
    private static readonly HashSet<int> _awardedSectorIds = new();

    internal static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        GameSessionSectorManager.OnSectorExited += OnSectorExited;
        GameSessionSectorManager.OnSectorEntered += OnSectorEnteredDiagnostic;
        BepinPlugin.Log.LogInfo("[Forge] Sector hook armed (award on sector exit).");
    }

    // Hot-reload teardown: the game events are static, so a leaked subscription
    // from the old assembly would double-award the meter after F6.
    internal static void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;
        GameSessionSectorManager.OnSectorExited -= OnSectorExited;
        GameSessionSectorManager.OnSectorEntered -= OnSectorEnteredDiagnostic;
    }

    // Diagnostic only: confirms enter events flow while awards happen on exit.
    private static void OnSectorEnteredDiagnostic(GameSessionSector sector) =>
        BepinPlugin.Log.LogDebug($"[Forge] SectorEntered: id={(sector == null ? "null" : sector.Id.ToString())}");

    private static void OnSectorExited(GameSessionSector departed)
    {
        try
        {
            var session = GameSessionManager.ActiveSession;
            var destination = GameSessionSectorManager.Instance?.DestinationSector;
            BepinPlugin.Log.LogInfo(
                $"[Forge] SectorExited: id={(departed == null ? "null" : departed.Id.ToString())}, " +
                $"objective={(departed == null ? "-" : departed.ObjectiveState.ToString())}, " +
                $"session={(session == null ? "null" : session.IsHub ? "hub" : "run")}, " +
                $"dest={(destination == null ? "null" : destination.Id.ToString())}");

            if (departed == null || departed.Id < 0) return; // sentinel sectors

            if (session == null || session.IsHub) return;

            // This event fires on every client, but only the master awards and increments.
            if (!Net.ForgeNetSync.IsAuthority)
            {
                BepinPlugin.Log.LogDebug($"[Forge] Sector {departed.Id} exit — client defers meter/scalar to host.");
                return;
            }

            if (!ReferenceEquals(session, _lastSession))
            {
                // The first exit of a run leaves the empty starting zone, with nothing
                // accomplished yet, so it earns no award.
                _lastSession = session;
                _awardedSectorIds.Clear();
                _awardedSectorIds.Add(departed.Id);
                BepinPlugin.Log.LogInfo($"[Forge] Leaving the run's starting sector {departed.Id} — no meter award.");
                return;
            }

            // Deliberately no destination check: Endless jumps leave DestinationSector
            // legitimately unset at spin-up, so gating on it would eat every award.
            if (!_awardedSectorIds.Add(departed.Id))
            {
                BepinPlugin.Log.LogInfo($"[Forge] Sector {departed.Id} already paid out — no meter award.");
                return;
            }

            // The sector's payout is burned either way — installing a Forge later
            // doesn't back-pay a sector already exited without one.
            if (UnityEngine.Object.FindObjectOfType<UpgradeForgeBehavior>() == null)
            {
                Messaging.Notification("The Forge Meter is idle — no Upgrade Forge is installed.");
                BepinPlugin.Log.LogInfo($"[Forge] Sector {departed.Id} exited with no Forge installed — meter award withheld.");
                return;
            }

            if (departed.ObjectiveState != ObjectiveState.Completed)
            {
                Messaging.Notification("The Forge gains nothing from an unfinished sector.");
                BepinPlugin.Log.LogInfo(
                    $"[Forge] Sector {departed.Id} objective {departed.ObjectiveState} (not Completed) — meter award withheld.");
                return;
            }

            ForgeMeterController.AddMeter(
                TerminusConfig.MeterPerSectorJump, "sector jump");

            // Scalar bumps only count after the activation threshold; meter fill is Forge
            // state, not escalation state, and is unaffected. Unlike the meter award above,
            // this is NOT gated on Forge presence, so one installed mid-run picks up the
            // scalar already accumulated.
            if (SectorEscalation.IsScalingActive)
                ForgeMeterController.IncrementDifficultyScalar();

            Net.ForgeNetSync.BroadcastState();
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Forge] Sector hook failed: {e}");
        }
    }
}
