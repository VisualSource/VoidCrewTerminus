using UnityEngine;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Forge.Burdens;

// Only ever turns the module OFF; restoring is the crew's job, and it never touches an
// already-off module, so it can't fight a crew decision.
//
// Owner-only: TurnOff is a RequestChange whose value is owner-authoritative and replicated by
// the game's PowerDrain sync, so ticking on every client would thrash. The "powered down"
// notification comes from PowerDrain.IsOn.OnChange, never the timer, so a request vetoed by a
// ChangeValidator can't announce a shutoff that didn't happen.
public sealed class RandomShutoffBehavior : MaintenanceBurdenBehavior
{
    public override BurdenType BurdenType => BurdenType.RandomShutoff;

    private System.Random _rng;
    private float _nextShutoffAt;
    private bool _triggerNextImmediately;

    // Earliest Time.time a shutoff may land, set when the crew restores power so one can't
    // fire the instant someone switches the module back on.
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

    // Announces only our own shutoff, and only once the real value has flipped, so a vetoed
    // request stays silent. Power-on is never announced: restoring is the crew's action.
    private void OnPowerStateChanged(bool isOn)
    {
        if (_applyingOwnChange && !isOn)
            Messaging.Notification($"{ModuleName()} powered down — switch it back on manually.");

        // Someone other than us restored power: start the grace window and re-roll from now,
        // so the countdown measures uptime rather than elapsing while the module sat dark.
        if (isOn && !_applyingOwnChange)
        {
            _graceUntil = Time.time + TerminusConfig.BurdenRestoreGrace;
            ScheduleNextShutoff();
            _loggedDecline = false; // a fresh power-on deserves a fresh warning if the next attempt is also vetoed
        }
    }

    // Called by !triggerburden; forces the next shutoff on the next Update tick.
    public void TriggerImmediately() => _triggerNextImmediately = true;

    // IsShutOff reflects real power state: the burden doesn't "hold" a shutoff.
    public float SecondsUntilNextShutoff => Mathf.Max(0f, _nextShutoffAt - Time.time);
    public bool IsShutOff => !IsPowered();

    private void Update()
    {
        if (Module == null) return;
        if (!IsOwner) return; // owner drives; PowerDrain sync carries it to clients

        LogOwnershipOnce();

        // The countdown deliberately does NOT run while the module is dark; it restarts from
        // OnPowerStateChanged when power comes back, so the interval measures uptime.
        if (!IsPowered())
        {
            LogIdleOnce();
            return;
        }
        _loggedIdle = false;

        bool forced = _triggerNextImmediately;
        if (!forced)
        {
            // Grace window after a crew restore; see _graceUntil.
            if (Time.time < _graceUntil) return;
            if (Time.time < _nextShutoffAt) return;
        }

        _triggerNextImmediately = false;
        ScheduleNextShutoff();
        RequestPowerOff();
    }

    private bool _loggedIdle;
    private bool _loggedOwnership;
    private bool _loggedDecline;

    private void LogIdleOnce()
    {
        if (_loggedIdle) return;
        _loggedIdle = true;
        BepinPlugin.Log?.LogDebug(
            $"[Burden] {ModuleName()} is off — burden idle until the crew restores power.");
    }

    // Recorded once per instance so a multi-client run can confirm exactly one machine
    // claims the schedule.
    private void LogOwnershipOnce()
    {
        if (_loggedOwnership) return;
        _loggedOwnership = true;
        BepinPlugin.Log?.LogDebug(
            $"[Burden] {ModuleName()} shutoff schedule OWNED here (viewID={Module.photonView?.ViewID}, " +
            $"owner=#{Module.photonView?.OwnerActorNr}, next in {SecondsUntilNextShutoff:0}s).");
    }

    private bool IsPowered() => Module != null && Module.PowerDrain != null && Module.PowerDrain.IsOn.Value;

    // CanCarry already keeps this burden off AutoPowerOn modules, so a decline here means some
    // other ChangeValidator is blocking it. Logged once per power-cycle: a veto is otherwise
    // invisible, but not worth a warning every interval for the rest of the run.
    private void RequestPowerOff()
    {
        if (Module == null || Module.PowerDrain == null) return;

        _applyingOwnChange = true;
        try
        {
            Module.PowerDrain.IsOn.RequestChange(
                false,
                onSuccess: () => BepinPlugin.Log?.LogDebug($"[Burden] {ModuleName()} shutoff applied (IsOn->False)."),
                onFail: () =>
                {
                    if (_loggedDecline) return;
                    _loggedDecline = true;
                    BepinPlugin.Log?.LogWarning(
                        $"[Burden] {ModuleName()} shutoff DECLINED by a ChangeValidator — burden had no effect this cycle.");
                });
        }
        finally
        {
            _applyingOwnChange = false;
        }
    }

    private void ScheduleNextShutoff()
    {
        _nextShutoffAt = Time.time + RandRange(
            TerminusConfig.BurdenMinInterval,
            TerminusConfig.BurdenMaxInterval);
    }

    private float RandRange(float min, float max)
    {
        if (max <= min) return min;
        return min + (float)_rng.NextDouble() * (max - min);
    }

    private string ModuleName() => Module == null ? name : Module.name;
}
