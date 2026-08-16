using UnityEngine;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Forge.Burdens;

// RandomShutoff burden — periodically cuts power to the module. It ONLY ever
// turns the module OFF; restoring it is the crew's job, and the burden never
// touches an already-off module, so it can't fight a crew decision.
//
// Authority: OWNER-ONLY. CellModule.TurnOff() is PowerDrain.IsOn.RequestChange(),
// a local ChangeResponsive request that can be vetoed by ChangeValidators, and
// whose value is owner-authoritative and replicated by the game's PowerDrain
// sync. Ticking on every client would mutate local state the owner's sync then
// overwrites (thrash), so only the owner drives the schedule.
//
// Verification: RequestChange exposes onSuccess/onFail. A vetoed request is
// otherwise a silent no-op, so declines are logged loudly. The "powered down"
// notification is driven by the module's REAL power state
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
            _graceUntil = Time.time + TerminusConfig.BurdenRestoreGrace;
            ScheduleNextShutoff();
            _loggedDecline = false; // a fresh power-on deserves a fresh warning if the next attempt is also vetoed
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
        // countdown here — rescheduling against an already-off module would let
        // the interval elapse invisibly while it sat dark. The schedule instead
        // restarts from OnPowerStateChanged when power actually comes back.
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
    private bool _loggedDecline;

    private void LogIdleOnce()
    {
        if (_loggedIdle) return;
        _loggedIdle = true;
        BepinPlugin.Log?.LogDebug(
            $"[Burden] {ModuleName()} is off — burden idle until the crew restores power.");
    }

    // Ownership is the gate for who drives the schedule; recording it once per
    // instance lets a multi-client run confirm exactly one machine claims it.
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
    // the burden. ForgeModuleState.CanCarry already keeps this burden off
    // AutoPowerOn modules (the one veto we know about), so a decline here means
    // some OTHER ChangeValidator is blocking it — worth knowing about, but not
    // worth a warning every retry-interval for the rest of the run, so this
    // logs only the first decline per power-cycle (reset in OnPowerStateChanged).
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
