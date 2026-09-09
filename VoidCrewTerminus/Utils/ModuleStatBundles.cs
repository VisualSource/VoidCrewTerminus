using Gameplay.Tags;
using Gameplay.Utilities;

namespace VoidCrewTerminus.Utils;

// The stats that define "how well does this module do its job", grouped by the
// category CsTag that narrows them. Any effect wanting to scale a module's
// effectiveness as a whole — the Forge's level bonus upward, a Leech debuff
// downward — needs the same per-category list, so it lives in one place.
//
// ForgeModuleState.BuildMods still carries its own copy; it predates this and is
// shipped, working code. Worth folding onto this once there's a reason to touch it.
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

    // Category tag paired with the stats it narrows, for callers that apply the
    // same treatment across every category.
    public static (CsTag Tag, StatType[] Stats)[] All() => new[]
    {
        (CsTagRegistry.Weapon, Weapon),
        (CsTagRegistry.Defense, Defense),
        (CsTagRegistry.BuiltIn, BuiltIn),
        (CsTagRegistry.PowerProvider, PowerProvider),
        (CsTagRegistry.Utility, Utility),
    };
}
