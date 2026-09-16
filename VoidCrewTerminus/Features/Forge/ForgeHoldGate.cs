using CG;
using CG.Client;
using CG.Client.Ship.Interactions;
using CG.Input;
using HarmonyLib;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// A hold-to-confirm timer with a duration we choose. The global HoldAction fires on the Input
// System's single shared Hold interaction, the short generic "hold F", which is far too quick
// for an irreversible action. Vanilla's own deconstruct doesn't use it either: Lever
// accumulates to a per-prefab threshold, which this reproduces, HUD ring publish included.
internal sealed class ForgeHoldGate
{
    private float _elapsed;
    private float _duration;
    private bool _active;

    // 0..1, so a lever animation tracks the real hold rather than a fixed speed.
    internal float Progress => _active && _duration > 0f ? Mathf.Clamp01(_elapsed / _duration) : 0f;
    internal bool IsHolding => _active;

    internal void Begin(float duration)
    {
        _elapsed = 0f;
        _duration = Mathf.Max(duration, 0.01f);
        _active = true;
        PublishRingStart(_duration);
    }

    // Returns true exactly once, on the frame the hold completes. Goes inactive at
    // that point so a player who keeps the button down doesn't re-fire.
    internal bool Tick(float deltaTime)
    {
        if (!_active) return false;

        _elapsed += deltaTime;
        if (_elapsed < _duration) return false;

        _active = false;

        var action = HoldAction();
        if (action != null) ViewEventBus.Instance?.OnHoldInteractionEnded?.Publish(action);
        return true;
    }

    // Released early (or the raycast moved off). No-op if the hold already completed.
    internal void Cancel()
    {
        if (!_active) return;
        _active = false;

        var action = HoldAction();
        if (action != null) ViewEventBus.Instance?.OnHoldInteractionCancelled?.Publish(action);
    }

    private static void PublishRingStart(float duration)
    {
        var action = HoldAction();
        if (action == null) return;
        // keepDisplayed: false, matching Lever, so the prompt clears once the hold resolves.
        ViewEventBus.Instance?.OnCustomHoldInteractionStarted?.Publish(action, duration, false);
    }

    // Resolved per call rather than cached: InputService is a ServiceBase whose
    // instance does not survive a session teardown, and a stale reference here would
    // publish onto a dead action.
    private static UnityEngine.InputSystem.InputAction HoldAction()
    {
        var service = ServiceBase<InputService>.Instance;
        return service?.InputActionReferences?.HoldAction?.action;
    }

    // Measured off a real ExtruderLever rather than hardcoded: pullSpeed and triggerThreshold
    // are private [SerializeField]s authored per prefab, so the number exists only in asset
    // data. Measuring keeps the Forge matched to vanilla across a retune.
    private static float _vanillaDeconstructSeconds;
    private static bool _loggedFallback;

    internal const float FallbackDeconstructSeconds = 2.5f;

    internal static float VanillaDeconstructSeconds
    {
        get
        {
            if (_vanillaDeconstructSeconds > 0f) return _vanillaDeconstructSeconds;

            var lever = Object.FindObjectOfType<ExtruderLever>();
            if (lever != null && TryMeasure(lever, out float seconds))
            {
                _vanillaDeconstructSeconds = seconds;
                BepinPlugin.Log.LogDebug(
                    $"[Forge] Vanilla deconstruct hold measured at {seconds:0.00}s from {lever.name} — matching it.");
                return _vanillaDeconstructSeconds;
            }

            if (!_loggedFallback)
            {
                _loggedFallback = true;
                BepinPlugin.Log.LogWarning(
                    $"[Forge] No vanilla ExtruderLever found to measure — using {FallbackDeconstructSeconds:0.00}s " +
                    "for the Forge's deconstruct hold. Will keep retrying.");
            }
            return FallbackDeconstructSeconds;
        }
    }

    // Time to TRIGGER, which is what the player experiences: Lever climbs from startPosition
    // at pullSpeed and fires past triggerThreshold. Lever's own HUD publish uses
    // (1 - position)/pullSpeed, so vanilla's ring runs slightly past the moment it fires.
    private static bool TryMeasure(ExtruderLever lever, out float seconds)
    {
        seconds = 0f;

        var pullSpeed = AccessTools.Field(typeof(Lever), "pullSpeed")?.GetValue(lever) as float?;
        var threshold = AccessTools.Field(typeof(Lever), "triggerThreshold")?.GetValue(lever) as float?;
        var start = AccessTools.Field(typeof(Lever), "startPosition")?.GetValue(lever) as float?;

        if (pullSpeed is not > 0f) return false;

        float travel = (threshold ?? 0.9f) - (start ?? 0f);
        if (travel <= 0f) return false;

        seconds = travel / pullSpeed.Value;
        return seconds > 0f;
    }
}
