using System.Collections.Generic;
using System.Linq;
using CG.Ship.Modules;
using Gameplay.Utilities;

namespace VoidCrewTerminus.Forge;

// Category-wide perk pools (~3 per category, v1 authoring) and the roll entry
// point. Signature perks and cursed-augmented pools are Phase 7.
//
// Slot gating (design: "Perks & Slots"):
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

    private static Dictionary<string, PerkDefinition> _byId;

    public static bool TryGet(string perkId, out PerkDefinition perk)
    {
        _byId ??= _pools.Values.SelectMany(p => p).ToDictionary(p => p.Id);
        return _byId.TryGetValue(perkId ?? "", out perk);
    }

    public static IReadOnlyList<PerkDefinition> PoolFor(ForgeCategory category)
    {
        // Short-circuit before touching _pools so callers can query the Unknown case
        // without triggering the dictionary's static initializer (which references
        // StatType and other game types).
        if (category == ForgeCategory.Unknown) return System.Array.Empty<PerkDefinition>();
        return _pools.TryGetValue(category, out var pool) ? pool : System.Array.Empty<PerkDefinition>();
    }

    public static IEnumerable<PerkDefinition> AllPerks() => _pools.Values.SelectMany(p => p);

    // Highest slot index a relic tier may fill (inclusive).
    public static int MaxSlotForTier(RelicTier tier) => tier switch
    {
        RelicTier.Legendary => 2,
        RelicTier.Rare => 1,
        _ => 0,
    };

    // Lowest empty slot the tier is eligible for, or -1 when every eligible slot
    // is occupied. `slots` uses null/empty = free.
    public static int TargetSlot(IReadOnlyList<string> slots, RelicTier tier)
    {
        int max = MaxSlotForTier(tier);
        for (int i = 0; i <= max && i < SlotCount; i++)
            if (string.IsNullOrEmpty(slots[i]))
                return i;
        return -1;
    }

    public static float RollChance(RelicTier tier) => tier switch
    {
        RelicTier.Legendary => TerminusConfig.PerkRollChanceLegendary?.Value ?? 0.75f,
        RelicTier.Rare => TerminusConfig.PerkRollChanceRare?.Value ?? 0.40f,
        _ => TerminusConfig.PerkRollChanceCommon?.Value ?? 0.25f,
    };

    // Resolve a module's Forge category from its CsTags (checked against the
    // built-in category tags by reference).
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
