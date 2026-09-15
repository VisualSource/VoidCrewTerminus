using System.Collections.Generic;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Loot;

public enum RelicTier { Common = 0, Rare = 1, Legendary = 2 }

public readonly struct RelicTierEntry
{
    public readonly RelicTier Tier;
    public readonly bool IsCursed; // Superseded by per-instance spawn rolls; see CursedRelicMarker.

    // Added to the base curse chance at spawn. Range [-1, +1]; the final chance is clamped
    // to [0, 1] after scalar-driven bonuses.
    public readonly float BaseCurseChanceModifier;

    // Which burden types this relic can inflict once cursed and the burden roll passes.
    // Empty means this relic never causes a burden even then.
    public readonly IReadOnlyList<BurdenType> BurdenAffinity;

    private static readonly IReadOnlyList<BurdenType> _defaultAffinity = new[] { BurdenType.RandomShutoff };

    public RelicTierEntry(RelicTier tier, bool isCursed = false, float baseCurseChanceModifier = 0f, IReadOnlyList<BurdenType> burdenAffinity = null)
    {
        Tier = tier;
        IsCursed = isCursed;
        BaseCurseChanceModifier = baseCurseChanceModifier;
        BurdenAffinity = burdenAffinity ?? _defaultAffinity;
    }

    public override string ToString() => $"{Tier}{(IsCursed ? " (Cursed)" : "")}{(BaseCurseChanceModifier != 0f ? $" [curse+{BaseCurseChanceModifier:+0.00;-0.00}]" : "")}";
}

// Keyed by prefab base name, with "(Clone)" stripped. Tier follows effect complexity and
// power level; BaseCurseChanceModifier follows vanilla flavour, so relics whose effects hint
// at instability go positive and clean stat trades go negative.
public static class RelicTierData
{
    private static readonly RelicTierEntry Common = new(RelicTier.Common);
    private static readonly RelicTierEntry Rare = new(RelicTier.Rare);
    private static readonly RelicTierEntry Legendary = new(RelicTier.Legendary);

    private static RelicTierEntry CommonCurse(float mod) => new(RelicTier.Common, false, mod);
    private static RelicTierEntry RareCurse(float mod) => new(RelicTier.Rare, false, mod);
    private static RelicTierEntry LegendaryCurse(float mod) => new(RelicTier.Legendary, false, mod);

    private static readonly Dictionary<string, RelicTierEntry> _map = new()
    {
        // Straightforward effects go slightly negative; trade-off effects stay neutral.
        ["Relic_00_Solo"] = CommonCurse(-0.05f),
        ["Relic_01_A_StarboardPower"] = Common,
        ["Relic_01_B_PortPower"] = Common,
        ["Relic_04_SpeedForRotation"] = Common,
        ["Relic_05_AftDamage"] = Common,
        ["Relic_07_ThrustersForForwardMovement"] = Common,
        ["Relic_08_A_EnergyForKinetic"] = Common,
        ["Relic_08_B_KineticForEnergy"] = Common,
        ["Relic_21_DamageForRange"] = Common,

        // Breakers, defects and vulnerability lore go positive.
        ["Relic_02_PowerForBreakers"] = RareCurse(+0.10f),
        ["Relic_03_VulnerabilityDuringVoidCharge"] = RareCurse(+0.10f),
        ["Relic_06_FireRateForBreakersCount"] = RareCurse(+0.10f),
        ["Relic_09_ScoopForBreakersCount"] = RareCurse(+0.05f),
        ["Relic_10_KPDEfficiencyForShieldEfficiency"] = Rare,
        ["Relic_11_A_WeaponFireRateForDefects"] = RareCurse(+0.10f),
        ["Relic_11_B_EnginePowerForDefects"] = RareCurse(+0.10f),
        ["Relic_12_BenedictionDamageForAccuracy"] = Rare,
        ["Relic_13_ConfessorFireRateForPower"] = Rare,
        ["Relic_14_LitanyForReloading"] = Rare,
        ["Relic_16_VulnerabilityForAlloyReducedSpeed"] = RareCurse(+0.10f),
        ["Relic_17_RecuserDamageForFireRate"] = Rare,
        ["Relic_18_PowerForBiomassTemperature"] = RareCurse(+0.05f),
        ["Relic_19_ShieldRechargeForBreakers"] = RareCurse(+0.10f),
        ["Relic_20_PowerHungry"] = RareCurse(+0.10f),
        ["Relic_22_BulletsForShields"] = Rare,
        ["Relic_24_EnergyDamageForBreakersCount"] = RareCurse(+0.10f),
        ["Relic_27_FireRateDuringThrusterBoost"] = Rare,

        // The flagship relics come with strings attached.
        ["Relic_15_BiomassForThrustersAndDamage"] = LegendaryCurse(+0.15f),
        ["Relic_28_PayloadRecharge"] = LegendaryCurse(+0.15f),
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
