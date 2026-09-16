using Gameplay.Utilities;

namespace VoidCrewTerminus.Leech.Dynamics;

// Drives a FloatModifier from the live attached-Leech count.
//
// One instance per owning StatMod, never shared and never reused: Init binds RuleOwner
// permanently behind an IsInitialized guard, so a second Init is a silent no-op, and Destroy
// does not reset the flag, so a torn-down instance is permanently unusable.
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

    // Reachable without a successful Init, so it must stay safe to call cold and twice.
    public override void OnDestroy()
        => LeechEncounterController.AttachedCountChanged -= OnAttachedCountChanged;

    // RecalculateValue, not InformValueChange: the latter raises OnValueChange without
    // writing Mod.Amount, so every consumer is notified and then reads a stale number.
    private void OnAttachedCountChanged(int count) => RecalculateValue();

    // The base indexes a localization DataTable by this type, which a mod-defined type is
    // never in, throwing KeyNotFoundException down the live tooltip path. Mandatory.
    public override string Description() => $"{_perLeech:+0.##;-0.##} per attached Leech";
}
