using Gameplay.Utilities;

namespace VoidCrewTerminus.Leech.Dynamics;

// Drives a FloatModifier from the live attached-Leech count, so any StatMod can
// scale continuously with how infested the ship is.
//
// One instance per owning StatMod, never shared and never reused. Init binds
// RuleOwner permanently and is guarded by IsInitialized, so a second Init on a
// different StatMod is a silent no-op; Destroy does not reset the flag, so a torn
// down instance is permanently unusable.
internal sealed class LeechAttachedCountValue : ModDynamicValue<FloatModifier, float>
{
    private readonly float _perLeech;
    private readonly float _baseValue;

    internal LeechAttachedCountValue(float perLeech, float baseValue = 0f)
    {
        _perLeech = perLeech;
        _baseValue = baseValue;
    }

    public override float DynamicValueBase => _baseValue;

    public override float GetValue() => _baseValue + LeechEncounterController.AttachedCount * _perLeech;

    public override void OnInitialize()
    {
        LeechEncounterController.AttachedCountChanged += OnAttachedCountChanged;
        RecalculateValue();
    }

    // Reachable without a successful Init, so this must stay safe to call twice and
    // safe to call cold. Unsubscribing an absent handler is a no-op.
    public override void OnDestroy()
        => LeechEncounterController.AttachedCountChanged -= OnAttachedCountChanged;

    // RecalculateValue, not InformValueChange. The latter raises OnValueChange
    // without writing Mod.Amount, so every consumer would be notified and then read
    // a stale number.
    private void OnAttachedCountChanged(int count) => RecalculateValue();

    // The base implementation indexes a localization DataTable by this type, which
    // a mod-defined type is never in — it throws KeyNotFoundException straight down
    // the live tooltip path. Overriding is mandatory, not cosmetic.
    public override string Description() => $"{_perLeech:+0.##;-0.##} per attached Leech";
}
