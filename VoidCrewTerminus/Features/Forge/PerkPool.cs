using System.Collections.Generic;
using System.Linq;
using CG.Ship.Modules;
using Gameplay.Utilities;

namespace VoidCrewTerminus.Forge;

// Slot gating:
//   Slot 0 — any tier · Slot 1 — Rare+ · Slot 2 — Legendary only.
// A roll targets the LOWEST empty slot the relic tier is eligible for; if every
// eligible slot is taken the roll is skipped entirely (a Common after slot 0 is
// full never spills into slot 1).
public static class PerkPool
{
    public const int SlotCount = 3;

    private static readonly Dictionary<ForgeCategory, PerkDefinition[]> _pools = new()
    {
        [ForgeCategory.Weapon] = new[]
        {
            new PerkDefinition("weapon_overclocked_coils", "Overclocked Coils", ForgeCategory.Weapon,
                "+12% fire rate", (StatType.FireRate, 0.12f)),
            new PerkDefinition("weapon_focused_optics", "Focused Optics", ForgeCategory.Weapon,
                "+15% range, +5% accuracy", (StatType.Range, 0.15f), (StatType.Accuracy, 0.05f)),
            new PerkDefinition("weapon_heavy_payload", "Heavy Payload", ForgeCategory.Weapon,
                "+12% damage, -4% fire rate", (StatType.Damage, 0.12f), (StatType.FireRate, -0.04f)),
        },
        [ForgeCategory.Defense] = new[]
        {
            new PerkDefinition("defense_harmonic_plating", "Harmonic Plating", ForgeCategory.Defense,
                "+12% shield hit points", (StatType.ShieldMaxHitPoints, 0.12f)),
            new PerkDefinition("defense_rapid_cyclers", "Rapid Cyclers", ForgeCategory.Defense,
                "+15% shield recharge speed", (StatType.ShieldRechargeSpeed, 0.15f)),
            new PerkDefinition("defense_deflection_matrix", "Deflection Matrix", ForgeCategory.Defense,
                "+10% shield absorption", (StatType.ShieldAbsorption, 0.10f)),
        },
        [ForgeCategory.PowerProvider] = new[]
        {
            new PerkDefinition("power_overcharged_cells", "Overcharged Cells", ForgeCategory.PowerProvider,
                "+10% power provided", (StatType.PowerProvided, 0.10f)),
            new PerkDefinition("power_fast_breeders", "Fast Breeders", ForgeCategory.PowerProvider,
                "+15% battery recharge", (StatType.BatteryRechargeAmount, 0.15f)),
            new PerkDefinition("power_stable_output", "Stable Output", ForgeCategory.PowerProvider,
                "+6% power provided", (StatType.PowerProvided, 0.06f)),
        },
        [ForgeCategory.BuiltIn] = new[]
        {
            new PerkDefinition("builtin_tuned_manifolds", "Tuned Manifolds", ForgeCategory.BuiltIn,
                "+10% engine power", (StatType.EnginePower, 0.10f)),
            new PerkDefinition("builtin_gyro_assist", "Gyro Assist", ForgeCategory.BuiltIn,
                "+12% yaw torque", (StatType.YawTorque, 0.12f)),
            new PerkDefinition("builtin_slipstream_coating", "Slipstream Coating", ForgeCategory.BuiltIn,
                "+10% forward power", (StatType.ForwardPower, 0.10f)),
        },
        [ForgeCategory.Utility] = new[]
        {
            new PerkDefinition("utility_efficient_cycles", "Efficient Cycles", ForgeCategory.Utility,
                "+15% processing speed", (StatType.ProcessingSpeed, 0.15f)),
            new PerkDefinition("utility_extended_manipulators", "Extended Manipulators", ForgeCategory.Utility,
                "+15% attractor range", (StatType.AttractorMaxRange, 0.15f)),
            new PerkDefinition("utility_triage_protocols", "Triage Protocols", ForgeCategory.Utility,
                "+15% healing speed", (StatType.HealingSpeed, 0.15f)),
        },
    };

    // Tied to specific relic identities. Roll only when that exact relic is
    // consumed in a commit; take priority over category pool draws. Grouped by
    // SignatureRelicId at first-touch (see EnsureSignatureIndex).
    private static readonly PerkDefinition[] _signatures = new[]
    {
        new PerkDefinition("sig_biomass_ram", "Biomass Ram", ForgeCategory.BuiltIn,
            "+20% forward power, +15% ram damage",
            signatureRelicId: "Relic_15_BiomassForThrustersAndDamage",
            payload: new[] { (StatType.ForwardPower, 0.20f), (StatType.Damage, 0.15f) }),
        new PerkDefinition("sig_sustained_payload", "Sustained Payload", ForgeCategory.Weapon,
            "+25% fire rate, +10% damage",
            signatureRelicId: "Relic_28_PayloadRecharge",
            payload: new[] { (StatType.FireRate, 0.25f), (StatType.Damage, 0.10f) }),

        new PerkDefinition("sig_overcharged_grid", "Overcharged Grid", ForgeCategory.PowerProvider,
            "+15% power provided, +20% battery recharge",
            signatureRelicId: "Relic_02_PowerForBreakers",
            payload: new[] { (StatType.PowerProvided, 0.15f), (StatType.BatteryRechargeAmount, 0.20f) }),
        new PerkDefinition("sig_holy_purpose", "Holy Purpose", ForgeCategory.Weapon,
            "+20% damage, +10% accuracy",
            signatureRelicId: "Relic_12_BenedictionDamageForAccuracy",
            payload: new[] { (StatType.Damage, 0.20f), (StatType.Accuracy, 0.10f) }),
        new PerkDefinition("sig_confessor_cadence", "Confessor Cadence", ForgeCategory.Weapon,
            "+30% fire rate, +10% projectile speed",
            signatureRelicId: "Relic_13_ConfessorFireRateForPower",
            payload: new[] { (StatType.FireRate, 0.30f), (StatType.ProjectileSpeed, 0.10f) }),
    };

    // Built lazily on first access to keep the static ctor cheap and avoid
    // StatType touches during test-host init.
    private static Dictionary<string, List<PerkDefinition>> _signaturesByRelic;

    private static void EnsureSignatureIndex()
    {
        if (_signaturesByRelic != null) return;
        var idx = new Dictionary<string, List<PerkDefinition>>();
        foreach (var sig in _signatures)
        {
            if (!idx.TryGetValue(sig.SignatureRelicId, out var list))
            {
                list = new List<PerkDefinition>();
                idx[sig.SignatureRelicId] = list;
            }
            list.Add(sig);
        }
        _signaturesByRelic = idx;
    }

    // Returns empty if the relic has no authored signatures.
    public static IReadOnlyList<PerkDefinition> SignaturesFor(string relicName)
    {
        if (string.IsNullOrEmpty(relicName)) return System.Array.Empty<PerkDefinition>();
        var normalized = Loot.RelicTierData.NormalizeName(relicName);
        EnsureSignatureIndex();
        return _signaturesByRelic.TryGetValue(normalized, out var list)
            ? list
            : System.Array.Empty<PerkDefinition>();
    }

    private static Dictionary<string, PerkDefinition> _byId;

    public static bool TryGet(string perkId, out PerkDefinition perk)
    {
        _byId ??= _pools.Values.SelectMany(p => p)
            .Concat(_signatures)
            .ToDictionary(p => p.Id);
        return _byId.TryGetValue(perkId ?? "", out perk);
    }

    public static IReadOnlyList<PerkDefinition> PoolFor(ForgeCategory category)
    {
        // Short-circuits before touching _pools so callers can query Unknown
        // without triggering the dictionary's static initializer, which references
        // StatType and other game types.
        if (category == ForgeCategory.Unknown) return System.Array.Empty<PerkDefinition>();
        return _pools.TryGetValue(category, out var pool) ? pool : System.Array.Empty<PerkDefinition>();
    }

    public static IEnumerable<PerkDefinition> AllPerks() =>
        _pools.Values.SelectMany(p => p).Concat(_signatures);

    // Inclusive.
    public static int MaxSlotForTier(Loot.RelicTier tier) => tier switch
    {
        Loot.RelicTier.Legendary => 2,
        Loot.RelicTier.Rare => 1,
        _ => 0,
    };

    // -1 when every eligible slot is occupied. `slots` uses null/empty = free.
    public static int TargetSlot(IReadOnlyList<string> slots, Loot.RelicTier tier)
    {
        int max = MaxSlotForTier(tier);
        for (int i = 0; i <= max && i < SlotCount; i++)
            if (string.IsNullOrEmpty(slots[i]))
                return i;
        return -1;
    }

    public static float RollChance(Loot.RelicTier tier) => tier switch
    {
        Loot.RelicTier.Legendary => TerminusConfig.PerkChanceLegendary,
        Loot.RelicTier.Rare => TerminusConfig.PerkChanceRare,
        _ => TerminusConfig.PerkChanceCommon,
    };

    // Checked against the built-in category tags by reference.
    public static ForgeCategory CategoryOf(CellModule module)
    {
        if (module == null || module.CsTags == null) return ForgeCategory.Unknown;
        foreach (ForgeCategory category in System.Enum.GetValues(typeof(ForgeCategory)))
        {
            if (category == ForgeCategory.Unknown) continue;
            var tag = category.ToCsTag();
            if (tag != null && System.Array.IndexOf(module.CsTags, tag) >= 0)
                return category;
        }
        return ForgeCategory.Unknown;
    }
}
