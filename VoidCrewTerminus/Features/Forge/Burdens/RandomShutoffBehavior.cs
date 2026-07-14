using UnityEngine;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Forge.Burdens;

// RandomShutoff burden — periodically calls CellModule.TurnOff() for a short
// window, then TurnOn(). Timing (interval + shutoff duration) is randomized
// within config-defined ranges. Player-visible: the module goes dark, forcing
// the crew to notice and react.
//
// Multiplayer strategy: seed the RNG deterministically from
// module.photonView.ViewID (stable across clients) + a static per-session salt
// so all clients converge on the same shutoff schedule without RPC. Actual
// TurnOn/TurnOff propagation is handled by the game's PowerDrain sync.
public sealed class RandomShutoffBehavior : MaintenanceBurdenBehavior
{
    public override BurdenType BurdenType => BurdenType.RandomShutoff;

    private System.Random _rng;
    private float _nextShutoffAt;
    private float _shutoffEndsAt;
    private bool _isShutOff;
    private bool _triggerNextImmediately;

    protected override void Awake()
    {
        base.Awake();
        int seed = Module != null && Module.photonView != null
            ? Module.photonView.ViewID
            : (name.GetHashCode() ^ (int)Time.realtimeSinceStartup);
        _rng = new System.Random(seed);
        ScheduleNextShutoff();
    }

    // Called by !triggerburden dev command — forces the next shutoff to fire
    // on the next Update tick regardless of interval progress.
    public void TriggerImmediately() => _triggerNextImmediately = true;

    // Query helpers for the !listburdens dev dump.
    public float SecondsUntilNextShutoff => _isShutOff ? 0f : Mathf.Max(0f, _nextShutoffAt - Time.time);
    public float SecondsUntilRecovery => _isShutOff ? Mathf.Max(0f, _shutoffEndsAt - Time.time) : 0f;
    public bool IsShutOff => _isShutOff;

    private void Update()
    {
        if (Module == null) return;

        if (_isShutOff)
        {
            if (Time.time >= _shutoffEndsAt)
            {
                Module.TurnOn();
                _isShutOff = false;
                Messaging.Notification($"{ModuleName()} restored.");
                ScheduleNextShutoff();
            }
            return;
        }

        if (_triggerNextImmediately || Time.time >= _nextShutoffAt)
        {
            _triggerNextImmediately = false;
            Module.TurnOff();
            _isShutOff = true;
            float duration = RandRange(
                TerminusConfig.BurdenShutoffMinSeconds?.Value ?? 2f,
                TerminusConfig.BurdenShutoffMaxSeconds?.Value ?? 4f);
            _shutoffEndsAt = Time.time + duration;
            Messaging.Notification($"{ModuleName()} powered down.");
        }
    }

    private void ScheduleNextShutoff()
    {
        float interval = RandRange(
            TerminusConfig.BurdenIntervalMinSeconds?.Value ?? 30f,
            TerminusConfig.BurdenIntervalMaxSeconds?.Value ?? 90f);
        _nextShutoffAt = Time.time + interval;
    }

    private float RandRange(float min, float max)
    {
        if (max <= min) return min;
        return min + (float)_rng.NextDouble() * (max - min);
    }

    // Human-readable module name for notifications. Falls back to Unity's
    // GameObject name when the game's LocalizedName isn't accessible.
    private string ModuleName()
    {
        if (Module == null) return name;
        return Module.name;
    }
}
