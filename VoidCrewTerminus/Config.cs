using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace VoidCrewTerminus;

internal static class TerminusConfig
{
    // Defined entries as static ConfigEntry<TYPE> NAME
    // Assigned via reflection in Init(); suppress "never assigned" warning.
#pragma warning disable CS0649

    [BindConfig("ui", false, "Control if relics are allowed to be created from the fabricator")]
    internal static ConfigEntry<bool> AllowRelicReplication;

    [BindConfig("dev", false, "Enable dev mode")]
    internal static ConfigEntry<bool> EnableDevMode;

    [BindConfig("lobby", 0.6f, "Duration fade affect between ship when after ship selection")]
    internal static ConfigEntry<float> LobbyShipFadeDuration;

    [BindConfig("lobby", 3f, "Per-frame time budget (ms) for building hangar ship visuals during preload; lower = smoother scene start, slower build")]
    internal static ConfigEntry<float> LobbyShipBuildBudgetMs;

    // Upgrade Forge — cost curve
    [BindConfig("forge", "1,1,2,2,3,3,4", "Comma-separated relic cost per module level step L4..L10 (default: 1,1,2,2,3,3,4 = 16 total to hit L10)")]
    internal static ConfigEntry<string> ForgeCostCurve;

    // Upgrade Forge — meter & progression
    [BindConfig("forge", 20f, "Forge Meter fill per successful sector jump")]
    internal static ConfigEntry<float> ForgeMeterPerSectorJump;

    [BindConfig("forge", 1f, "Forge Meter fill per alloy spent at the Alloy Terminal")]
    internal static ConfigEntry<float> ForgeMeterPerAlloy;

    [BindConfig("forge", 10, "Alloys consumed per Alloy Terminal use")]
    internal static ConfigEntry<int> AlloyTerminalSpendPerUse;

    [BindConfig("forge", 100f, "Forge Meter threshold for L1→L2; multiplied by ForgeMeterLevelMultiplier each subsequent level")]
    internal static ConfigEntry<float> ForgeMeterBaseThreshold;

    [BindConfig("forge", 1.5f, "Multiplicative scale applied to meter threshold per Forge level")]
    internal static ConfigEntry<float> ForgeMeterLevelMultiplier;

    // Upgrade Forge — perk roll chances (0–1)
    [BindConfig("forge", 0.25f, "Perk roll chance when upgrading with a Common relic")]
    internal static ConfigEntry<float> PerkRollChanceCommon;

    [BindConfig("forge", 0.40f, "Perk roll chance when upgrading with a Rare relic")]
    internal static ConfigEntry<float> PerkRollChanceRare;

    [BindConfig("forge", 0.75f, "Perk roll chance when upgrading with a Legendary relic")]
    internal static ConfigEntry<float> PerkRollChanceLegendary;

    // Upgrade Forge — sector escalation
    [BindConfig("forge", 0.05f, "Fractional multiplier added to enemy HP and damage per DifficultyScalar tick (minor boost — density is the primary axis)")]
    internal static ConfigEntry<float> EscalationStatScalarPerJump;

    [BindConfig("forge", 0.20f, "Fractional multiplier added to enemy spawner intensity per DifficultyScalar tick (primary escalation axis — deeper sectors bring more enemies)")]
    internal static ConfigEntry<float> EscalationDensityScalarPerJump;

    [BindConfig("forge", 2, "Number of boss objectives that must be defeated in a run before any escalation (density, HP, damage, loot tier biasing) takes effect. DifficultyScalar and BossesDefeated still accumulate during the warm-up so scaling kicks in with full accumulated intensity once the threshold is crossed.")]
    internal static ConfigEntry<int> EscalationBossActivationThreshold;

    [BindConfig("forge", 0.15f, "Base chance (0-1) that a spawned relic is flagged as Cursed. Per-relic modifiers in RelicTierData.BaseCurseChanceModifier are added on top; scalar bonus is added when escalation is active. Final chance clamped to [0, 1].")]
    internal static ConfigEntry<float> RelicBaseCurseChance;

    [BindConfig("forge", 0.03f, "Additional cursed chance per DifficultyScalar tick — deeper sectors produce more cursed relics. Only applied when escalation is active.")]
    internal static ConfigEntry<float> EscalationCurseChancePerScalar;

    // Maintenance Burden (Phase 7-C) — when a cursed relic is consumed in a
    // successful commit, an independent roll decides whether the module also
    // takes on a burden. Perk roll is unaffected.
    [BindConfig("forge", 0.75f, "Chance a successful commit consuming ≥1 cursed relic attaches the relic's baked Maintenance Burden to the target module — 'high chance' per design intent")]
    internal static ConfigEntry<float> BurdenApplicationChance;

    [BindConfig("forge", 2f, "RandomShutoff burden — minimum seconds the module stays powered off during a shutoff event")]
    internal static ConfigEntry<float> BurdenShutoffMinSeconds;

    [BindConfig("forge", 4f, "RandomShutoff burden — maximum seconds the module stays powered off during a shutoff event")]
    internal static ConfigEntry<float> BurdenShutoffMaxSeconds;

    [BindConfig("forge", 30f, "RandomShutoff burden — minimum seconds between shutoff events")]
    internal static ConfigEntry<float> BurdenIntervalMinSeconds;

    [BindConfig("forge", 90f, "RandomShutoff burden — maximum seconds between shutoff events")]
    internal static ConfigEntry<float> BurdenIntervalMaxSeconds;

    [BindConfig("forge", 3, "DifficultyScalar at which Rare relics start dropping (below this, Rares in the loot pool are downgraded to Common)")]
    internal static ConfigEntry<int> EscalationRareUnlockScalar;

    [BindConfig("forge", 6, "DifficultyScalar at which Legendary relics start dropping (below this, Legendaries in the loot pool are downgraded to Rare)")]
    internal static ConfigEntry<int> EscalationLegendaryUnlockScalar;

    [BindConfig("forge", 1, "DifficultyScalar bump applied when a boss objective is defeated (in addition to the boss's tier-ceiling unlock)")]
    internal static ConfigEntry<int> EscalationBossScalarBonus;

#pragma warning restore CS0649

    internal static ConfigEntry<Vector3> FrigateLobbyHangerPosition;
    internal static ConfigEntry<Vector3> StrikerLobbyHangerPosition;
    internal static ConfigEntry<Vector3> DestroyerLobbyHangerPosition;

    internal static ConfigEntry<Vector3> FrigateLobbyHangerRot;
    internal static ConfigEntry<Vector3> StrikerLobbyHangerRot;
    internal static ConfigEntry<Vector3> DestroyerLobbyHangerRot;


    internal static void Init(ConfigFile cfg)
    {
        Type type = typeof(TerminusConfig);

        foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
        {
            var attr = field.GetCustomAttribute<BindConfig>();
            if (attr == null) continue;

            if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(ConfigEntry<>))
                throw new InvalidOperationException(
                    $"[BindConfig] on {field.Name}: field must be ConfigEntry<T>, got {field.FieldType}");

            Type expected = field.FieldType.GetGenericArguments()[0];

            if (attr.DefaultValue == null || attr.DefaultValue.GetType() != expected)
                throw new InvalidOperationException(
                    $"[BindConfig] on {field.Name}: default value type " +
                    $"{attr.DefaultValue?.GetType()?.ToString() ?? "null"} " +
                    $"does not match ConfigEntry<{expected}>");

            MethodInfo bindMethod = typeof(ConfigFile).GetMethods()
                .First(m => m.Name == nameof(ConfigFile.Bind)
                    && m.IsGenericMethod
                    && m.GetParameters().Length == 4
                    && m.GetParameters()[3].ParameterType == typeof(ConfigDescription))
                .MakeGenericMethod(expected);

            object entry = bindMethod.Invoke(cfg, new object[]
            {
                attr.Section,
                field.Name,
                attr.DefaultValue,
                new ConfigDescription(attr.Description, attr.AcceptableValues, attr.Tags),
            });

            field.SetValue(null, entry);
        }

        FrigateLobbyHangerPosition = cfg.Bind("lobby", "FrigateLobbyHangerPosition", new Vector3(0, 0, -75), "Position of the frigate prefab in the lobby");
        StrikerLobbyHangerPosition = cfg.Bind("lobby", "StrikerLobbyHangerPosition", new Vector3(0, 0, -75), "Position of the frigate prefab in the lobby");
        DestroyerLobbyHangerPosition = cfg.Bind("lobby", "FrigateLobbyHangerPosition", new Vector3(0, 0, -75), "Position of the frigate prefab in the lobby");

        FrigateLobbyHangerRot = cfg.Bind("lobby", "FrigateLobbyHangerRot", new Vector3(0, 20, 0), "Position of the frigate prefab in the lobby");
        StrikerLobbyHangerRot = cfg.Bind("lobby", "StrikerLobbyHangerRot", new Vector3(0, 0, 0), "Position of the frigate prefab in the lobby");
        DestroyerLobbyHangerRot = cfg.Bind("lobby", "FrigateLobbyHangerRot", new Vector3(0, 20, 0), "Position of the frigate prefab in the lobby");
    }
}


[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
internal class BindConfig : Attribute
{
    public string Section { get; set; } = "";
    public string Description { get; set; } = "";
    public object DefaultValue { get; set; }
    public object[] Tags { get; set; }
    public AcceptableValueBase AcceptableValues { get; set; } = null;

    public BindConfig(string section, object defaultValue, string desc)
    {
        Section = section;
        DefaultValue = defaultValue;
        Description = desc;
    }

}