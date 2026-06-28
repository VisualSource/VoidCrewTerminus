using System.Collections.Generic;
using System.Linq;
using VoidCrewTerminus.Forge;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

internal class RelicTierCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "relictier" };

    public override string Description() => "[DevMode] Query or list mod-side relic tier metadata";

    public override List<Argument> Arguments() =>
    [
        new("list"),
        new("%relic_name")
    ];

    public override string[] UsageExamples() =>
    [
        "!relictier list",
        "!relictier Relic_12_BenedictionDamageForAccuracy",
        "!relictier Relic_15_BiomassForThrustersAndDamage"
    ];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        if (string.IsNullOrWhiteSpace(arguments) || arguments.Trim().Equals("list", System.StringComparison.OrdinalIgnoreCase))
        {
            ListAll();
            return;
        }

        var name = arguments.Trim();
        var known = RelicTierData.TryGet(name, out var entry);
        var normalized = RelicTierData.NormalizeName(name);
        var status = known ? "" : " (unknown — defaulting to Common)";
        Messaging.Notification($"{normalized}: {entry}{status}");
    }

    private static void ListAll()
    {
        var groups = RelicTierData.All
            .GroupBy(kv => kv.Value.Tier)
            .OrderBy(g => (int)g.Key);

        foreach (var group in groups)
        {
            var names = string.Join(", ", group.Select(kv => kv.Key));
            Messaging.Notification($"[{group.Key}] {names}");
        }
    }
}
