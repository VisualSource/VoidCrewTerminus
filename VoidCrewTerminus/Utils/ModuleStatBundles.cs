using Gameplay.Tags;
using Gameplay.Utilities;

namespace VoidCrewTerminus.Utils;

// The stats that define how well a module does its job, grouped by the category CsTag that
// narrows them, so the Forge's level bonus and a Leech debuff share one list.
//
// ForgeModuleState.BuildMods still carries its own copy, not yet folded onto this.
public static class ModuleStatBundles
{
    public static readonly StatType[] Weapon =
    {
        StatType.Damage, StatType.FireRate, StatType.Range,
        StatType.ProjectileSpeed, StatType.Accuracy,
    };

    public static readonly StatType[] Defense =
    {
        StatType.ShieldMaxHitPoints, StatType.ShieldRechargeSpeed, StatType.ShieldAbsorption,
    };

    public static readonly StatType[] BuiltIn =
    {
        StatType.ForwardPower, StatType.EnginePower, StatType.YawTorque,
        StatType.ElevationPower, StatType.StrafePower, StatType.JumpChargeSpeed,
    };

    public static readonly StatType[] PowerProvider =
    {
        StatType.PowerProvided, StatType.BatteryRechargeAmount,
    };

    public static readonly StatType[] Utility =
    {
        StatType.ProcessingSpeed, StatType.HealingSpeed,
        StatType.AttractorMaxRange, StatType.AttractorPullVelocity,
    };

    // Category tag paired with the stats it narrows, for callers treating every category.
    public static (CsTag Tag, StatType[] Stats)[] All() => new[]
    {
        (CsTagRegistry.Weapon, Weapon),
        (CsTagRegistry.Defense, Defense),
        (CsTagRegistry.BuiltIn, BuiltIn),
        (CsTagRegistry.PowerProvider, PowerProvider),
        (CsTagRegistry.Utility, Utility),
    };
}
