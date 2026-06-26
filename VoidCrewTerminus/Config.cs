using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace VoidCrewTerminus;

internal static class TerminusConfig
{
    // Defined entries as static ConfigEntry<TYPE> NAME

    [BindConfig("ui", false, "Control if relics are allowed to be created from the fabricator")]
    internal static ConfigEntry<bool> AllowRelicReplication;

    [BindConfig("dev", false, "Enable dev mode")]
    internal static ConfigEntry<bool> EnableDevMode;

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

        FrigateLobbyHangerPosition = cfg.Bind("lobby", "FrigateLobbyHangerPosition", new Vector3(0, -10, -75), "Position of the frigate prefab in the lobby");
        StrikerLobbyHangerPosition = cfg.Bind("lobby", "StrikerLobbyHangerPosition", new Vector3(0, -15, -75), "Position of the frigate prefab in the lobby");
        DestroyerLobbyHangerPosition = cfg.Bind("lobby", "FrigateLobbyHangerPosition", new Vector3(0, 0, -100), "Position of the frigate prefab in the lobby");

        FrigateLobbyHangerRot = cfg.Bind("lobby", "FrigateLobbyHangerRot", new Vector3(0, 20, 0), "Position of the frigate prefab in the lobby");
        StrikerLobbyHangerRot = cfg.Bind("lobby", "StrikerLobbyHangerRot", new Vector3(0, 0, 0), "Position of the frigate prefab in the lobby");
        DestroyerLobbyHangerRot = cfg.Bind("lobby", "FrigateLobbyHangerRot", new Vector3(0, 0, 0), "Position of the frigate prefab in the lobby");
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