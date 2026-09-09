using Gameplay.Utilities;

namespace VoidCrewTerminus.Leech.Dynamics;

// Gates a StatMod on the attached-Leech count crossing a threshold.
//
// Same one-instance-per-StatMod rule as LeechAttachedCountValue, with an extra
// edge: ModDynamicCondition is itself an IModifierSource and Init assigns itself
// as the owning mod's source. Sharing an instance would collapse several modifiers
// onto one source, and removing any one of them would strip them all.
internal sealed class LeechAttachedCountCondition : ModDynamicCondition
{
    private readonly int _threshold;

    internal LeechAttachedCountCondition(int threshold) => _threshold = threshold;

    // Inversion is applied for us by WillApply via InvertApplication — doing it
    // here as well would cancel out.
    public override bool ShouldApply() => LeechEncounterController.AttachedCount >= _threshold;

    public override void OnInitialize()
    {
        LeechEncounterController.AttachedCountChanged += OnAttachedCountChanged;
        CheckIfActive();
    }

    public override void OnDestroy()
        => LeechEncounterController.AttachedCountChanged -= OnAttachedCountChanged;

    // SetActive only fires on a genuine edge, so re-checking on every count change
    // is cheap and self-debouncing.
    private void OnAttachedCountChanged(int count) => CheckIfActive();

    // See LeechAttachedCountValue.Description — the base throws for mod-defined types.
    public override string Description()
        => $"While {_threshold}+ Leeches are attached";
}
