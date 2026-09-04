using System.Collections.Generic;
using CG.Game.Scenarios;
using VoidCrewTerminus.Escalation;
using VoidCrewTerminus.Forge;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Patches;

// Passive Forge Meter fill: +ForgeMeterPerSectorJump when the ship leaves a
// sector for the void, landing during warp so the crew has transit time to react
// to a level-up. Not a Harmony patch — GameSessionSectorManager.OnSectorExited is
// a public static event, fired with the sector being left.
//
// Bookkeeping is self-contained (no reliance on VoidManager event timing or the
// game's SectorVisited flag): new runs are detected by GameSession identity, and
// each sector pays out its departure at most once per run so bouncing between
// two sectors can't farm the meter. Only a Completed objective pays out; leaving
// a sector Started (abandoned), Failed, or with NoObjective burns the payout.
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
        BepinPlugin.Log.LogDebug("[Forge] Sector hook armed (award on sector exit).");
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

    // Diagnostic only — confirms enter events flow while we award on exits.
    private static void OnSectorEnteredDiagnostic(GameSessionSector sector) =>
        BepinPlugin.Log.LogDebug($"[Forge] SectorEntered: id={(sector == null ? "null" : sector.Id.ToString())}");

    private static void OnSectorExited(GameSessionSector departed)
    {
        try
        {
            var session = GameSessionManager.ActiveSession;
            var destination = GameSessionSectorManager.Instance?.DestinationSector;
            BepinPlugin.Log.LogDebug(
                $"[Forge] SectorExited: id={(departed == null ? "null" : departed.Id.ToString())}, " +
                $"objective={(departed == null ? "-" : departed.ObjectiveState.ToString())}, " +
                $"session={(session == null ? "null" : session.IsHub ? "hub" : "run")}, " +
                $"dest={(destination == null ? "null" : destination.Id.ToString())}");

            if (departed == null || departed.Id < 0) return; // sentinel sectors

            if (session == null || session.IsHub) return;

            // Host-authoritative: this event fires on every client, but only the
            // master awards and increments; others receive it via the broadcast.
            if (!Net.ForgeNetSync.IsAuthority)
            {
                BepinPlugin.Log.LogDebug($"[Forge] Sector {departed.Id} exit — client defers meter/scalar to host.");
                return;
            }

            if (!ReferenceEquals(session, _lastSession))
            {
                // First exit of a new run = leaving the empty starting zone to go
                // pick the first mission. Nothing accomplished yet — no award.
                _lastSession = session;
                _awardedSectorIds.Clear();
                _awardedSectorIds.Add(departed.Id);
                BepinPlugin.Log.LogDebug($"[Forge] Leaving the run's starting sector {departed.Id} — no meter award.");
                return;
            }

            // Deliberately no destination check: Endless jumps go through the exit
            // gate without plotting a map destination, so DestinationSector is
            // legitimately unset (-1) at spin-up — gating on it ate every award.
            if (!_awardedSectorIds.Add(departed.Id))
            {
                BepinPlugin.Log.LogDebug($"[Forge] Sector {departed.Id} already paid out — no meter award.");
                return;
            }

            // The sector's payout is burned either way — installing a Forge later
            // doesn't back-pay a sector already exited without one.
            if (UnityEngine.Object.FindObjectOfType<UpgradeForgeBehavior>() == null)
            {
                Messaging.Notification("The Forge Meter is idle — no Upgrade Forge is installed.");
                BepinPlugin.Log.LogDebug($"[Forge] Sector {departed.Id} exited with no Forge installed — meter award withheld.");
                return;
            }

            if (departed.ObjectiveState != ObjectiveState.Completed)
            {
                Messaging.Notification("The Forge gains nothing from an unfinished sector.");
                BepinPlugin.Log.LogDebug(
                    $"[Forge] Sector {departed.Id} objective {departed.ObjectiveState} (not Completed) — meter award withheld.");
                return;
            }

            ForgeMeterController.AddMeter(
                TerminusConfig.MeterPerSectorJump, "sector jump");

            // Sector-jump scalar bumps only count after the escalation activation
            // threshold has been crossed; meter fill (Forge Level) is unaffected —
            // that's Forge state, not escalation state. Unlike the meter award
            // above, this is NOT gated on Forge presence: escalation accumulates
            // in the background so a Forge installed mid-run picks up whatever
            // scalar has already built up.
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
