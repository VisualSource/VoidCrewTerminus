using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;

namespace VoidCrewTerminus;

internal static class TerminusConfig
{
    // Defined entries as static ConfigEntry<TYPE> NAME


    [BindConfig("ui", false, "Control if relics are allowed to be created from the fabricator")]
    internal static ConfigEntry<bool> AllowRelicReplication;


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