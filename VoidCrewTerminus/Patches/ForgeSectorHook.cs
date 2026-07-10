using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Patches;

// Passive Forge Meter fill: +ForgeMeterPerSectorJump for every NEW sector the crew
// jumps into. Not a Harmony patch — GameSessionSectorManager.OnSectorEntered is a
// public static event.
//
// De-dupe rules (verified in the Phase 5 pre-flight, see the design doc):
//   - The event fires once per sector-index change, BEFORE PostEnter() marks the
//     sector visited — so `SectorVisited` is false exactly on first entry.
//     Re-entries (interdiction resumption, backtracking) are filtered by it.
//   - The run's starting sector also arrives unvisited → consumed via
//     ForgeMeterController.ConsumeInitialSectorSkip (reset on HostStartSession).
//   - Sentinel sectors (off-world −2, end-run −3, preserve −4) and hub sessions
//     never award.
// An interdiction therefore awards exactly once on entry, and the resumed
// destination awards on its own first entry (two warp charges → two awards).
internal static class ForgeSectorHook
{
    private static bool _initialized;

    internal static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        GameSessionSectorManager.OnSectorEntered += OnSectorEntered;
    }

    private static void OnSectorEntered(GameSessionSector sector)
    {
        if (sector == null || sector.Id < 0) return;
        if (sector.SectorVisited) return;

        var session = GameSessionManager.ActiveSession;
        if (session == null || session.IsHub) return;

        if (ForgeMeterController.ConsumeInitialSectorSkip()) return;

        ForgeMeterController.AddMeter(
            TerminusConfig.ForgeMeterPerSectorJump?.Value ?? 20f, "sector jump");
    }
}
