using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using Xunit;

namespace VoidCrewTerminus.Tests;

// TerminusConfig.BindAttributedFields throws when a default's type disagrees with
// its ConfigEntry<T>. Untested, that mismatch first surfaces as a failed mod load
// inside the game, a long way from the attribute that caused it.
[Collection(SharedStaticStateCollection.Name)]
public sealed class TerminusConfigBindingTests
{
    private const int ExpectedBandCount = 3;

    private static FieldInfo[] BoundFields => typeof(TerminusConfig)
        .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        .Where(f => f.GetCustomAttribute<BindConfig>() != null)
        .ToArray();

    private static ConfigFile BindAll(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"vct-cfg-{Guid.NewGuid():N}.cfg");
        var cfg = new ConfigFile(path, saveOnInit: false);
        TerminusConfig.BindAttributedFields(cfg);
        return cfg;
    }

    [Fact]
    public void Init_binds_every_attributed_field()
    {
        BindAll(out string path);

        try
        {
            var unbound = BoundFields.Where(f => f.GetValue(null) == null).Select(f => f.Name).ToArray();
            Assert.Empty(unbound);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Leech_keys_are_written_under_their_own_section()
    {
        ConfigFile cfg = BindAll(out string path);

        try
        {
            cfg.Save();
            string toml = File.ReadAllText(path);

            Assert.Contains("[leech]", toml);

            foreach (FieldInfo field in BoundFields.Where(f => f.Name.StartsWith("Leech", StringComparison.Ordinal)))
                Assert.Contains(field.Name, toml);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The escalation curves are read positionally as DifficultyScalar 2-3 / 4-5 / 6+,
    // so a default with the wrong arity is a silent off-by-one band, not a parse error.
    [Fact]
    public void Band_defaults_have_one_entry_per_escalation_band()
    {
        BindAll(out string path);

        try
        {
            FieldInfo[] bandFields = BoundFields
                .Where(f => f.Name.EndsWith("Bands", StringComparison.Ordinal))
                .ToArray();

            Assert.NotEmpty(bandFields);

            foreach (FieldInfo field in bandFields)
            {
                var entry = (ConfigEntry<string>)field.GetValue(null);
                string[] bands = entry.Value.Split(',');

                Assert.Equal(ExpectedBandCount, bands.Length);

                foreach (string band in bands)
                    Assert.True(
                        float.TryParse(band, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                        $"{field.Name} band '{band}' is not numeric");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Accessors_fall_back_to_the_attribute_default_when_unbound()
    {
        // Guards the pattern the class documents: accessors must read through the
        // const default, never Entry.Value, so a dev command can't NRE pre-Init.
        foreach (FieldInfo field in BoundFields)
            field.SetValue(null, null);

        Assert.Equal(2, TerminusConfig.LeechGate);
        Assert.Equal(8, TerminusConfig.LeechCap);
        Assert.Equal(1.5f, TerminusConfig.LeechProximityRadius);
        Assert.False(TerminusConfig.DevMode);
        Assert.Equal("1.0,1.3,1.6", TerminusConfig.LeechTurnRateRaw);
    }
}
