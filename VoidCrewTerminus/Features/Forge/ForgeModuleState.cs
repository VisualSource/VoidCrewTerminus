using System.Collections.Generic;
using CG.Ship.Modules;
using Gameplay.Utilities;

namespace VoidCrewTerminus.Forge;

// Per-module overlay state. Implements IModifierSource so its stat mods can be
// batch-removed via module.Stats.RemoveModifier(this) without tracking individual mods.
public class ForgeModuleState : IModifierSource
{
    public int Level { get; private set; } = 3;

    // Perk slots and burden flags are added in later phases.
    // ForgeOverlayTable keeps a strong reference; this field is only for mod removal.
    private CellModule _module;

    public void Attach(CellModule module)
    {
        _module = module;
        if (Level > 3) ApplyMods();
    }

    public void SetLevel(int level)
    {
        Level = System.Math.Max(3, System.Math.Min(10, level));
        RefreshMods();
    }

    // Remove all applied mods and detach from the module.
    public void Cleanup()
    {
        if (_module != null)
            _module.Stats.RemoveModifier(this);
        _module = null;
    }

    private void RefreshMods()
    {
        if (_module == null) return;
        _module.Stats.RemoveModifier(this);
        if (Level > 3) ApplyMods();
    }

    private void ApplyMods()
    {
        var mods = BuildMods();
        if (mods.Count > 0)
            _module.Stats.ApplyModifiers(mods, this);
    }

    // Build AdditiveMultiplier boosts for each bonus level above vanilla L3.
    // We create mods for all "positive" stat categories; ApplyModifiers only affects
    // stats actually registered on the module, so this is safe across all module types.
    private List<StatMod> BuildMods()
    {
        float amount = (Level - 3) * 0.08f;
        var mods = new List<StatMod>();

        var targets = new[]
        {
            // Weapons
            StatType.Damage,
            StatType.FireRate,
            StatType.Range,
            StatType.ProjectileSpeed,
            StatType.Accuracy,
            // Shields
            StatType.ShieldMaxHitPoints,
            StatType.ShieldRechargeSpeed,
            StatType.ShieldAbsorption,
            // Engines / thrust
            StatType.ForwardPower,
            StatType.EnginePower,
            StatType.YawTorque,
            StatType.ElevationPower,
            StatType.StrafePower,
            StatType.JumpChargeSpeed,
            // Reactor / power
            StatType.PowerProvided,
            StatType.BatteryRechargeAmount,
            // Utility
            StatType.ProcessingSpeed,
            StatType.HealingSpeed,
            StatType.AttractorMaxRange,
            StatType.AttractorPullVelocity,
        };

        foreach (var statType in targets)
            mods.Add(new StatMod(new FloatModifier(amount, ModifierType.AdditiveMultiplier, this), statType.Id));

        return mods;
    }
}
