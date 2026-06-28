using System.Collections.Generic;

namespace VoidCrewTerminus.Forge;

public enum RelicTier { Common = 0, Rare = 1, Legendary = 2 }

public readonly struct RelicTierEntry
{
    public readonly RelicTier Tier;
    public readonly bool IsCursed;

    public RelicTierEntry(RelicTier tier, bool isCursed)
    {
        Tier = tier;
        IsCursed = isCursed;
    }

    public override string ToString() => $"{Tier}{(IsCursed ? " (Cursed)" : "")}";
}

// Mod-side tier metadata for all ~29 vanilla relics.
// Key is the prefab base name (runtime name with "(Clone)" stripped).
// IsCursed is false for all entries in Phase 1; cursed flags are authored in Phase 7.
// Tier assignments are based on effect complexity and power level.
public static class RelicTierData
{
    private static readonly RelicTierEntry Common    = new(RelicTier.Common,    false);
    private static readonly RelicTierEntry Rare      = new(RelicTier.Rare,      false);
    private static readonly RelicTierEntry Legendary = new(RelicTier.Legendary, false);

    private static readonly Dictionary<string, RelicTierEntry> _map = new()
    {
        // Common — single-axis trade-offs or flat bonuses
        ["Relic_00_Solo"]                              = Common,
        ["Relic_01_A_StarboardPower"]                  = Common,
        ["Relic_01_B_PortPower"]                       = Common,
        ["Relic_04_SpeedForRotation"]                  = Common,
        ["Relic_05_AftDamage"]                         = Common,
        ["Relic_07_ThrustersForForwardMovement"]       = Common,
        ["Relic_08_A_EnergyForKinetic"]                = Common,
        ["Relic_08_B_KineticForEnergy"]                = Common,
        ["Relic_21_DamageForRange"]                    = Common,

        // Rare — complex trade-offs, conditional bonuses, or weapon-specific effects
        ["Relic_02_PowerForBreakers"]                  = Rare,
        ["Relic_03_VulnerabilityDuringVoidCharge"]     = Rare,
        ["Relic_06_FireRateForBreakersCount"]          = Rare,
        ["Relic_09_ScoopForBreakersCount"]             = Rare,
        ["Relic_10_KPDEfficiencyForShieldEfficiency"]  = Rare,
        ["Relic_11_A_WeaponFireRateForDefects"]        = Rare,
        ["Relic_11_B_EnginePowerForDefects"]           = Rare,
        ["Relic_12_BenedictionDamageForAccuracy"]      = Rare,
        ["Relic_13_ConfessorFireRateForPower"]         = Rare,
        ["Relic_14_LitanyForReloading"]                = Rare,
        ["Relic_16_VulnerabilityForAlloyReducedSpeed"] = Rare,
        ["Relic_17_RecuserDamageForFireRate"]          = Rare,
        ["Relic_18_PowerForBiomassTemperature"]        = Rare,
        ["Relic_19_ShieldRechargeForBreakers"]         = Rare,
        ["Relic_20_PowerHungry"]                       = Rare,
        ["Relic_22_BulletsForShields"]                 = Rare,
        ["Relic_24_EnergyDamageForBreakersCount"]      = Rare,
        ["Relic_27_FireRateDuringThrusterBoost"]       = Rare,

        // Legendary — high-impact multi-axis effects
        ["Relic_15_BiomassForThrustersAndDamage"]      = Legendary,
        ["Relic_28_PayloadRecharge"]                   = Legendary,
    };

    private static readonly RelicTierEntry _fallback = Common;

    public static RelicTierEntry Get(string relicName)
    {
        var key = NormalizeName(relicName);
        return _map.TryGetValue(key, out var entry) ? entry : _fallback;
    }

    public static bool TryGet(string relicName, out RelicTierEntry entry)
    {
        entry = Get(relicName);
        return _map.ContainsKey(NormalizeName(relicName));
    }

    public static IReadOnlyDictionary<string, RelicTierEntry> All => _map;

    // Strip Unity "(Clone)" suffix appended to instantiated prefabs.
    public static string NormalizeName(string name) =>
        name?.EndsWith("(Clone)") == true ? name[..^7].TrimEnd() : name ?? "";
}
