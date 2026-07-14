using System.Collections.Generic;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Loot;

public enum RelicTier { Common = 0, Rare = 1, Legendary = 2 }

public readonly struct RelicTierEntry
{
    public readonly RelicTier Tier;
    public readonly bool IsCursed; // Legacy static flag; retained for compatibility. Phase 7-B replaces this with per-instance spawn-time rolls (see CursedRelicMarker).

    // Additive modifier applied to the base curse chance at spawn time. Some
    // relics are innately more curse-prone; positive values push them toward
    // cursed, negative values (rare) protect them. Range [-1, +1]; the final
    // chance is clamped to [0, 1] after adding scalar-driven bonuses.
    public readonly float BaseCurseChanceModifier;

    // Which burden types this relic can inflict when it's cursed AND the burden
    // roll passes (Phase 7-C). The calculator gathers affinities from every
    // consumed cursed relic and picks uniformly from the union. Empty = this
    // relic never causes any burden even if cursed and the roll passes.
    //
    // With only RandomShutoff shipped, the default [RandomShutoff] is the
    // sensible fallback for all cursed relics — every cursed relic today
    // manifests as random power drops. Later burden types (HeatTick,
    // ManualReset) get authored per-relic based on lore fit (temperature /
    // biomass relics → HeatTick, defects relics → ManualReset, etc.).
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

// Mod-side tier metadata for all ~29 vanilla relics.
// Key is the prefab base name (runtime name with "(Clone)" stripped).
// Tier assignments are based on effect complexity and power level.
// BaseCurseChanceModifier per-relic (Phase 7-B): relics whose vanilla effects
// hint at instability (breakers, defects, vulnerability) get positive modifiers;
// clean stat trades get negative modifiers.
public static class RelicTierData
{
    // Baseline entries with no curse modifier — most relics use these.
    private static readonly RelicTierEntry Common = new(RelicTier.Common);
    private static readonly RelicTierEntry Rare = new(RelicTier.Rare);
    private static readonly RelicTierEntry Legendary = new(RelicTier.Legendary);

    // Helpers to construct entries with a curse modifier without writing the tier twice.
    private static RelicTierEntry CommonCurse(float mod) => new(RelicTier.Common, false, mod);
    private static RelicTierEntry RareCurse(float mod) => new(RelicTier.Rare, false, mod);
    private static RelicTierEntry LegendaryCurse(float mod) => new(RelicTier.Legendary, false, mod);

    private static readonly Dictionary<string, RelicTierEntry> _map = new()
    {
        // Common — single-axis trade-offs or flat bonuses. Straight-forward effects
        // get a slight negative curse modifier (safer picks); trade-off effects stay
        // neutral.
        ["Relic_00_Solo"] = CommonCurse(-0.05f),
        ["Relic_01_A_StarboardPower"] = Common,
        ["Relic_01_B_PortPower"] = Common,
        ["Relic_04_SpeedForRotation"] = Common,
        ["Relic_05_AftDamage"] = Common,
        ["Relic_07_ThrustersForForwardMovement"] = Common,
        ["Relic_08_A_EnergyForKinetic"] = Common,
        ["Relic_08_B_KineticForEnergy"] = Common,
        ["Relic_21_DamageForRange"] = Common,

        // Rare — complex trade-offs, conditional bonuses, or weapon-specific effects.
        // Relics that lean on breakers / defects / vulnerability lore get positive
        // curse modifiers — their vanilla flavour already hints at instability.
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

        // Legendary — high-impact multi-axis effects. Higher innate curse chance
        // — the flagship relics come with strings attached.
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
