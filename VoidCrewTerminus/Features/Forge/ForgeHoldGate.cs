using CG;
using CG.Client;
using CG.Client.Ship.Interactions;
using CG.Input;
using HarmonyLib;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// A hold-to-confirm timer with a duration WE choose.
//
// HoldClickerInteractable — what the Forge's commit and deconstruct levers used to
// extend — has no duration of its own: it subscribes to the global
// InputActionReferences.HoldAction and fires whenever the Input System's single
// shared Hold interaction completes. That is the short generic "hold F" used for
// every incidental prompt in the game, and far too quick for two irreversible
// actions.
//
// Vanilla's own module deconstruct doesn't use it either. It runs on Lever, which
// accumulates LeverPosition += pullSpeed * deltaTime and fires at triggerThreshold —
// a per-prefab duration, which is why deconstructing a real module takes noticeably
// longer than the generic hold. This reproduces that: accumulate, fire at the
// threshold, and drive the HUD reticle ring with the same OnCustomHoldInteractionStarted
// publish Lever uses so the ring matches the real wait instead of the generic one.
internal sealed class ForgeHoldGate
{
    private float _elapsed;
    private float _duration;
    private bool _active;

    // Progress 0..1, for driving a lever animation so it tracks the real hold rather
    // than finishing early at some unrelated fixed speed.
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
        // keepDisplayed: false — matches Lever, so the prompt clears once the hold
        // resolves rather than sticking on the reticle.
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

    // How long vanilla's own module deconstruct takes, measured off a real
    // ExtruderLever in the scene rather than hardcoded — pullSpeed and
    // triggerThreshold are private [SerializeField]s authored per prefab, so the
    // number exists only in asset data and cannot be read offline. Measuring keeps
    // the Forge matched to vanilla across a retune, the same way ForgeGhosts borrows
    // the live hologram material instead of shipping a copy.
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
                BepinPlugin.Log.LogInfo(
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

    // Lever fires at LeverPosition > triggerThreshold, climbing from startPosition at
    // pullSpeed per second. Note this is the time to TRIGGER, which is what the player
    // experiences — Lever's own HUD publish uses (1 - position)/pullSpeed instead, so
    // vanilla's ring actually runs slightly past the moment the action fires.
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
