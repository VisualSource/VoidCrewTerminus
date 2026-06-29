using System.Collections.Generic;
using CG.Ship.Modules;
using Gameplay.Tags;
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
    // Each group is narrowed by module category via ModTagConfiguration.RequiredTags so
    // mods only activate on modules that carry the matching category CsTag.
    // TagsToAdd = [Forge_Upgraded] stamps upgraded modules so later perk phases can
    // gate on RequiredLocalTags = [Forge_Upgraded].
    private List<StatMod> BuildMods()
    {
        float amount = (Level - 3) * 0.08f;
        var mods = new List<StatMod>();

        AddGroup(mods, amount, CsTagRegistry.Weapon, new[]
        {
            StatType.Damage, StatType.FireRate, StatType.Range,
            StatType.ProjectileSpeed, StatType.Accuracy,
        });
        AddGroup(mods, amount, CsTagRegistry.Defense, new[]
        {
            StatType.ShieldMaxHitPoints, StatType.ShieldRechargeSpeed, StatType.ShieldAbsorption,
        });
        AddGroup(mods, amount, CsTagRegistry.BuiltIn, new[]
        {
            StatType.ForwardPower, StatType.EnginePower, StatType.YawTorque,
            StatType.ElevationPower, StatType.StrafePower, StatType.JumpChargeSpeed,
        });
        AddGroup(mods, amount, CsTagRegistry.PowerProvider, new[]
        {
            StatType.PowerProvided, StatType.BatteryRechargeAmount,
        });
        AddGroup(mods, amount, CsTagRegistry.Utility, new[]
        {
            StatType.ProcessingSpeed, StatType.HealingSpeed,
            StatType.AttractorMaxRange, StatType.AttractorPullVelocity,
        });

        return mods;
    }

    private void AddGroup(List<StatMod> mods, float amount, CsTag categoryTag, StatType[] statTypes)
    {
        if (categoryTag == null) return;

        var tagCfg = new ModTagConfiguration
        {
            RequiredTags = new[] { categoryTag },
            TagsToAdd = new[] { CsTagRegistry.ForgeUpgraded },
        };

        foreach (var statType in statTypes)
            mods.Add(new StatMod(new FloatModifier(amount, ModifierType.AdditiveMultiplier, this), statType.Id, tagCfg));
    }
}
