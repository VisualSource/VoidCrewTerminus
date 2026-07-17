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

        if (!_triggerNextImmediately && Time.time < _nextShutoffAt) return;
        _triggerNextImmediately = false;
        ScheduleNextShutoff();

        // Only cut power when the module is actually running. If it's already off,
        // there's nothing to do — and we never turn it back on.
        if (!IsPowered())
        {
            BepinPlugin.Log?.LogDebug($"[Burden] {ModuleName()} already off — nothing to shut off this cycle.");
            return;
        }

        RequestPowerOff();
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
