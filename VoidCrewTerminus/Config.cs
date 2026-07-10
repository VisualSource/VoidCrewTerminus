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
    [BindConfig("forge", 0.15f, "Fractional stat multiplier added to enemy HP and damage per DifficultyScalar tick")]
    internal static ConfigEntry<float> EscalationStatScalarPerJump;

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