using UnityEngine;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Forge.Burdens;

// RandomShutoff burden — periodically powers the module down for a short window,
// then restores it. Timing (interval + shutoff duration) is randomized within
// config-defined ranges. Player-visible: the module goes dark, forcing the crew
// to notice and react.
//
// Authority: OWNER-ONLY. CellModule.TurnOff()/TurnOn() are not setters — they are
// PowerDrain.IsOn.RequestChange(), a local ChangeResponsive request that can be
// vetoed by ChangeValidators, and whose value is owner-authoritative and
// replicated by the game's PowerDrain sync. Ticking on every client would mutate
// local state the owner's sync then overwrites (thrash), so only the owner drives
// the schedule; clients receive the power state normally.
//
// Verification: RequestChange exposes onSuccess/onFail. A vetoed request is
// otherwise a silent no-op — the same failure mode that left Phase 6's density
// scaling dead for a whole phase — so declines are logged loudly.
//
// Notifications are driven by the module's REAL power state
// (PowerDrain.IsOn.OnChange), never by this timer: if a request is declined, the
// crew must not be told "powered down" while the module happily runs.
//
// Player intent wins — we only ever undo our own change. If the module is already
// off when a shutoff is due we skip and reschedule (never force-on a module the
// crew deliberately disabled), and we only restore if nothing else touched IsOn
// while we had it down.
public sealed class RandomShutoffBehavior : MaintenanceBurdenBehavior
{
    public override BurdenType BurdenType => BurdenType.RandomShutoff;

    private System.Random _rng;
    private float _nextShutoffAt;
    private float _shutoffEndsAt;
    private bool _weShutItOff;
    private bool _triggerNextImmediately;

    // True while we are applying our own IsOn change, so the OnChange handler can
    // tell "we did this" from "the crew did this".
    private bool _applyingOwnChange;
    // Cleared if anyone else changes IsOn while we hold the module down.
    private bool _ourShutoffStillIntact;

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

    // Notifications come from here — the real power state — not from the timer.
    private void OnPowerStateChanged(bool isOn)
    {
        if (_applyingOwnChange)
        {
            Messaging.Notification(isOn ? $"{ModuleName()} restored." : $"{ModuleName()} powered down.");
            return;
        }

        // Someone else (crew, game logic) changed power while our shutoff was
        // active — stand down and leave their intent alone.
        if (_weShutItOff && _ourShutoffStillIntact)
        {
            _ourShutoffStillIntact = false;
            BepinPlugin.Log?.LogDebug(
                $"[Burden] {ModuleName()} IsOn changed to {isOn} externally during our shutoff — will not restore.");
        }
    }

    // Called by !triggerburden dev command — forces the next shutoff to fire on
    // the next Update tick regardless of interval progress.
    public void TriggerImmediately() => _triggerNextImmediately = true;

    // Query helpers for the !listburdens dev dump.
    public float SecondsUntilNextShutoff => _weShutItOff ? 0f : Mathf.Max(0f, _nextShutoffAt - Time.time);
    public float SecondsUntilRecovery => _weShutItOff ? Mathf.Max(0f, _shutoffEndsAt - Time.time) : 0f;
    public bool IsShutOff => _weShutItOff;

    private void Update()
    {
        if (Module == null) return;
        if (!IsOwner) return; // owner drives; PowerDrain sync carries it to clients

        if (_weShutItOff)
        {
            if (Time.time < _shutoffEndsAt) return;

            // Only undo our own change, and only if it's still ours to undo.
            if (_ourShutoffStillIntact) RequestPower(true, "restore");

            _weShutItOff = false;
            _ourShutoffStillIntact = false;
            ScheduleNextShutoff();
            return;
        }

        if (!_triggerNextImmediately && Time.time < _nextShutoffAt) return;
        _triggerNextImmediately = false;

        // Already off? The crew disabled it themselves — don't burn the event,
        // and above all never force it back on afterwards.
        if (!IsPowered())
        {
            BepinPlugin.Log?.LogDebug(
                $"[Burden] {ModuleName()} already powered down — skipping shutoff, rescheduling.");
            ScheduleNextShutoff();
            return;
        }

        if (!RequestPower(false, "shutoff")) return; // declined — RequestPower reschedules

        _weShutItOff = true;
        _ourShutoffStillIntact = true;
        _shutoffEndsAt = Time.time + RandRange(
            TerminusConfig.BurdenShutoffMinSeconds?.Value ?? 2f,
            TerminusConfig.BurdenShutoffMaxSeconds?.Value ?? 4f);
    }

    private bool IsPowered() => Module != null && Module.PowerDrain != null && Module.PowerDrain.IsOn.Value;

    // Wraps RequestChange with success/fail reporting. A declined request would
    // otherwise be invisible; surface it so a validator veto can't quietly
    // neuter the whole burden.
    private bool RequestPower(bool on, string what)
    {
        if (Module == null || Module.PowerDrain == null) return false;

        bool ok = false;
        _applyingOwnChange = true;
        try
        {
            Module.PowerDrain.IsOn.RequestChange(
                on,
                onSuccess: () => ok = true,
                onFail: () => BepinPlugin.Log?.LogWarning(
                    $"[Burden] {ModuleName()} {what} DECLINED by a ChangeValidator (IsOn->{on}) — burden had no effect this cycle."));
        }
        finally
        {
            _applyingOwnChange = false;
        }

        if (ok)
            BepinPlugin.Log?.LogDebug($"[Burden] {ModuleName()} {what} applied (IsOn->{on}).");
        else if (!on)
            ScheduleNextShutoff(); // shutoff refused — retry after a fresh interval

        return ok;
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
