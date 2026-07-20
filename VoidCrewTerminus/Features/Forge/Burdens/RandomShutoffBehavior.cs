using UnityEngine;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Forge.Burdens;

// RandomShutoff burden — periodically cuts power to the module. It ONLY ever
// turns the module OFF; restoring it is the crew's job. That's the whole tax:
// a burdened module keeps going dark at inconvenient moments and someone has to
// walk over and switch it back on. The burden never turns it back on itself.
//
// Cadence: at each randomized interval, if the module is currently running we
// shut it off; if it's already off (crew hasn't restored it yet, or shut it down
// themselves) we do nothing that cycle. We never touch the module except to turn
// it off, so we can't fight a crew decision.
//
// Authority: OWNER-ONLY. CellModule.TurnOff() is PowerDrain.IsOn.RequestChange(),
// a local ChangeResponsive request that can be vetoed by ChangeValidators, and
// whose value is owner-authoritative and replicated by the game's PowerDrain
// sync. Ticking on every client would mutate local state the owner's sync then
// overwrites (thrash), so only the owner drives the schedule; clients receive
// the power state normally.
//
// Verification: RequestChange exposes onSuccess/onFail. A vetoed request is
// otherwise a silent no-op — the same failure mode that left Phase 6's density
// scaling dead for a whole phase — so declines are logged loudly. The
// "powered down" notification is driven by the module's REAL power state
// (PowerDrain.IsOn.OnChange), never by the timer, so a declined request can
// never announce a shutoff that didn't happen.
public sealed class RandomShutoffBehavior : MaintenanceBurdenBehavior
{
    public override BurdenType BurdenType => BurdenType.RandomShutoff;

    private System.Random _rng;
    private float _nextShutoffAt;
    private bool _triggerNextImmediately;

    // Earliest Time.time at which a shutoff may land. Set when the crew restores
    // power, so a shutoff can't fire the instant someone walks over and switches
    // the module back on — that reads as the burden trolling the player rather
    // than as a maintenance tax.
    private float _graceUntil;

    // True only for the instant we apply our own IsOn change, so the OnChange
    // handler can tell "we shut it off" from "the crew changed power".
    private bool _applyingOwnChange;

    private bool IsOwner => Module != null && Module.photonView != null && Module.photonView.AmOwner;

    protected override void Awake()
    {
        base.Awake();
        _rng = new System.Random(System.Environment.TickCount ^ GetInstanceID());
        ScheduleNextShutoff();

        if (Module != null && Module.PowerDrain != null)
            Module.PowerDrain.IsOn.OnChange += OnPowerStateChanged;
    }

    private void OnDestroy()
    {
        if (Module != null && Module.PowerDrain != null)
            Module.PowerDrain.IsOn.OnChange -= OnPowerStateChanged;
    }

    // Announce only OUR shutoff, and only once the real value has actually flipped
    // (so a vetoed request stays silent). We never announce power-on — restoring
    // is the crew's action, not the burden's.
    private void OnPowerStateChanged(bool isOn)
    {
        if (_applyingOwnChange && !isOn)
            Messaging.Notification($"{ModuleName()} powered down — switch it back on manually.");

        // Crew (or anything that isn't us) restored power: start the grace window
        // and re-roll the interval from now, so the countdown measures UPTIME
        // rather than having elapsed invisibly while the module sat dark.
        if (isOn && !_applyingOwnChange)
        {
            _graceUntil = Time.time + (TerminusConfig.BurdenRestoreGraceSeconds?.Value ?? 20f);
            ScheduleNextShutoff();
        }
    }

    // Called by !triggerburden — forces the next shutoff on the next Update tick.
    public void TriggerImmediately() => _triggerNextImmediately = true;

    // !listburdens helpers. IsShutOff reflects the module's real power state — the
    // burden doesn't "hold" a shutoff, so this is just "is it currently off".
    public float SecondsUntilNextShutoff => Mathf.Max(0f, _nextShutoffAt - Time.time);
    public bool IsShutOff => !IsPowered();

    private void Update()
    {
        if (Module == null) return;
        if (!IsOwner) return; // owner drives; PowerDrain sync carries it to clients

        LogOwnershipOnce();

        // Nothing to do while the module is dark, and crucially we do NOT run the
        // countdown here. The old code rescheduled every cycle against an
        // already-off module, which logged "already off" every 30-90s forever
        // (dozens of lines in the 26-07-19 session) and meant the interval elapsed
        // invisibly while the module sat dark. The schedule now restarts from
        // OnPowerStateChanged when power actually comes back.
        if (!IsPowered())
        {
            LogIdleOnce();
            return;
        }
        _loggedIdle = false;

        bool forced = _triggerNextImmediately;
        if (!forced)
        {
            // Grace window after a crew restore — see _graceUntil.
            if (Time.time < _graceUntil) return;
            if (Time.time < _nextShutoffAt) return;
        }

        _triggerNextImmediately = false;
        ScheduleNextShutoff();
        RequestPowerOff();
    }

    private bool _loggedIdle;
    private bool _loggedOwnership;

    private void LogIdleOnce()
    {
        if (_loggedIdle) return;
        _loggedIdle = true;
        BepinPlugin.Log?.LogDebug(
            $"[Burden] {ModuleName()} is off — burden idle until the crew restores power.");
    }

    // Both machines logged a shutoff for the same module in the 26-07-19 session,
    // which would mean two owners driving one schedule. Ownership is the gate, so
    // record it once per instance: the next 2-client run either shows exactly one
    // machine claiming ownership, or names the desync.
    private void LogOwnershipOnce()
    {
        if (_loggedOwnership) return;
        _loggedOwnership = true;
        BepinPlugin.Log?.LogDebug(
            $"[Burden] {ModuleName()} shutoff schedule OWNED here (viewID={Module.photonView?.ViewID}, " +
            $"owner=#{Module.photonView?.OwnerActorNr}, next in {SecondsUntilNextShutoff:0}s).");
    }

    private bool IsPowered() => Module != null && Module.PowerDrain != null && Module.PowerDrain.IsOn.Value;

    // Requests the shutoff and surfaces the result. A declined request would
    // otherwise be invisible; log it so a validator veto can't quietly neuter
    // the burden.
    private void RequestPowerOff()
    {
        if (Module == null || Module.PowerDrain == null) return;

        _applyingOwnChange = true;
        try
        {
            Module.PowerDrain.IsOn.RequestChange(
                false,
                onSuccess: () => BepinPlugin.Log?.LogDebug($"[Burden] {ModuleName()} shutoff applied (IsOn->False)."),
                onFail: () => BepinPlugin.Log?.LogWarning(
                    $"[Burden] {ModuleName()} shutoff DECLINED by a ChangeValidator — burden had no effect this cycle."));
        }
        finally
        {
            _applyingOwnChange = false;
        }
    }

    private void ScheduleNextShutoff()
    {
        _nextShutoffAt = Time.time + RandRange(
            TerminusConfig.BurdenIntervalMinSeconds?.Value ?? 30f,
            TerminusConfig.BurdenIntervalMaxSeconds?.Value ?? 90f);
    }

    private float RandRange(float min, float max)
    {
        if (max <= min) return min;
        return min + (float)_rng.NextDouble() * (max - min);
    }

    private string ModuleName() => Module == null ? name : Module.name;
}
