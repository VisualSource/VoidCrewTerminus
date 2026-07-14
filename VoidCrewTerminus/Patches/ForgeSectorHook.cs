using System.Collections.Generic;
using CG.Game.Scenarios;
using VoidCrewTerminus.Forge;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Patches;

// Passive Forge Meter fill: +ForgeMeterPerSectorJump when the ship ENTERS THE VOID
// leaving a sector — payment for surviving it, landing during warp so the crew has
// transit time to react to a Forge level-up (feed alloys, fresh tube revealed).
// Not a Harmony patch — GameSessionSectorManager.OnSectorExited is a public static
// event, fired with the sector being left.
//
// Bookkeeping is self-contained (no reliance on VoidManager event timing or the
// game's SectorVisited flag):
//   - New runs are detected by GameSession identity; the awarded-id set resets.
//   - Each sector pays out its departure at most once per run, so bouncing between
//     two sectors can't farm the meter. The arrival sector of the run simply pays
//     on ITS departure — the first real jump.
//   - A sector whose objective FAILED pays nothing, and its payout is burned.
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

    // Hot-reload teardown (ScriptEngine): the game events are static, so a leaked
    // subscription from the old assembly would double-award the meter after F6.
    internal static void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;
        GameSessionSectorManager.OnSectorExited -= OnSectorExited;
        GameSessionSectorManager.OnSectorEntered -= OnSectorEnteredDiagnostic;
    }

    // Diagnostic only — confirms enter events flow while we award on exits.
    private static void OnSectorEnteredDiagnostic(GameSessionSector sector) =>
        BepinPlugin.Log.LogInfo($"[Forge] SectorEntered: id={(sector == null ? "null" : sector.Id.ToString())}");

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

            if (!ReferenceEquals(session, _lastSession))
            {
                // First exit of a new run = leaving the empty starting zone to go
                // pick the first mission. Nothing accomplished yet — no award.
                _lastSession = session;
                _awardedSectorIds.Clear();
                _awardedSectorIds.Add(departed.Id);
                BepinPlugin.Log.LogInfo($"[Forge] Leaving the run's starting sector {departed.Id} — no meter award.");
                return;
            }

            // NOTE: no destination check. Endless jumps go through the exit gate
            // without plotting a map destination, so the manager's DestinationSector
            // is legitimately unset (-1) at spin-up — gating on it ate every award.
            // A final exit at run end awarding a moot +20 is harmless: the meter
            // resets with the session.
            if (!_awardedSectorIds.Add(departed.Id))
            {
                BepinPlugin.Log.LogInfo($"[Forge] Sector {departed.Id} already paid out — no meter award.");
                return;
            }

            // The meter only fills while an Upgrade Forge is actually installed on
            // the ship — no forge, no passive progression. The sector's payout is
            // burned either way (installing a forge later doesn't back-pay).
            if (UnityEngine.Object.FindObjectOfType<UpgradeForgeBehavior>() == null)
            {
                Messaging.Notification("The Forge Meter is idle — no Upgrade Forge is installed.");
                BepinPlugin.Log.LogInfo($"[Forge] Sector {departed.Id} exited with no Forge installed — meter award withheld.");
                return;
            }

            if (departed.ObjectiveState == ObjectiveState.Failed)
            {
                Messaging.Notification("The Forge gains nothing from a failed sector.");
                BepinPlugin.Log.LogInfo($"[Forge] Sector {departed.Id} objective failed — meter award withheld.");
                return;
            }

            ForgeMeterController.AddMeter(
                TerminusConfig.ForgeMeterPerSectorJump?.Value ?? 20f, "sector jump");

            // Phase 6: sector-jump scalar bumps only start counting AFTER the
            // escalation activation threshold has been crossed. Sector exits
            // during the warm-up don't accumulate scalar — the mod stays
            // completely silent until enough bosses have fallen. Meter fill
            // (Forge Level progression) is unaffected — that's Forge state,
            // not escalation state.
            if (SectorEscalation.IsScalingActive)
                ForgeMeterController.IncrementDifficultyScalar();
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Forge] Sector hook failed: {e}");
        }
    }
}
