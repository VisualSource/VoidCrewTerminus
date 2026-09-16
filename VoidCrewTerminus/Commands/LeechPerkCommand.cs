using System.Collections.Generic;
using System.Linq;
using CG.Game;
using CG.Ship.Modules;
using CG.Space;
using Gameplay.Utilities;
using VoidCrewTerminus.Leech.Dynamics;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

// Exercises the Leech dynamics end to end: whether a runtime-constructed ModDynamicValue
// actually drives Mod.Amount in the shipped game.
internal class LeechPerkCommand : PublicCommand
{
    private const float PerLeechScaling = 0.10f;
    private const int GateThreshold = 3;

    // Each condition-bearing mod needs its own source: ModDynamicCondition.Init makes
    // itself the mod's source, so a shared one lets a single removal strip the rest.
    // RemoveModifier leaves no handle for DestroyDynamicRules, hence the tracking.
    private sealed class TestPerkSource : IModifierSource
    {
    }

    private static readonly List<(CellModule Module, TestPerkSource Source, StatMod Mod)> Applied = new();

    public override string[] CommandAliases() => new[] { "leechperk" };
    public override string Description() => "[DevMode] Apply a test StatMod driven by the attached-Leech count; 'off' removes";
    public override List<Argument> Arguments() => [new("%module_or_off?")];
    public override string[] UsageExamples() => ["!leechperk", "!leechperk engine", "!leechperk off"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode) return;

        string arg = arguments?.Trim() ?? "";

        if (arg.Equals("off", System.StringComparison.OrdinalIgnoreCase))
        {
            Clear();
            return;
        }

        if (arg.Equals("status", System.StringComparison.OrdinalIgnoreCase))
        {
            Report();
            return;
        }

        PlayerControlledShip ship = ClientGame.Current?.PlayerShip;
        if (ship == null)
        {
            Messaging.Notification("No player ship.");
            return;
        }

        CellModule module = Resolve(ship, arg);
        if (module == null)
        {
            Messaging.Notification($"No module matching '{arg}'.");
            return;
        }

        Apply(module);
    }

    private static void Apply(CellModule module)
    {
        var scalingSource = new TestPerkSource();
        var gatedSource = new TestPerkSource();

        // Continuous: amount tracks the live count.
        StatMod scaling = LeechDynamics.WithLeechCountScaling(
            StatType.MaxHitPoints, PerLeechScaling, scalingSource);

        // Threshold: a flat bonus that only attaches past the gate.
        StatMod gated = LeechDynamics.GateByLeechCount(
            new StatMod(new FloatModifier(0.25f, ModifierType.AdditiveMultiplier, gatedSource), StatType.MaxHitPoints.Id),
            GateThreshold);

        // Plural overload on purpose: it re-asserts the source AFTER the condition's Init
        // overwrites it, where the singular would leave the mod impossible to remove.
        module.Stats.ApplyModifiers(new List<StatMod> { scaling }, scalingSource);
        module.Stats.ApplyModifiers(new List<StatMod> { gated }, gatedSource);

        Applied.Add((module, scalingSource, scaling));
        Applied.Add((module, gatedSource, gated));

        Messaging.Notification(
            $"Applied to {module.name}: +{PerLeechScaling:P0}/leech scaling, plus a flat +25% gated at {GateThreshold} leeches. " +
            $"'!leechperk status' to read amounts back.");
    }

    private static void Report()
    {
        if (Applied.Count == 0)
        {
            Messaging.Notification("No test perks applied.");
            return;
        }

        string report = string.Join(" | ", Applied.Select(entry =>
            $"{entry.Module?.name ?? "<gone>"}: amount={((FloatModifier)entry.Mod.Mod).Amount:0.###}" +
            $" active={entry.Mod.DynamicCondition?.IsActive().ToString() ?? "n/a"}"));

        Messaging.Notification($"Leeches={Leech.LeechEncounterController.AttachedCount} — {report}");
    }

    private static void Clear()
    {
        foreach ((CellModule module, TestPerkSource source, StatMod mod) in Applied)
        {
            if (module != null) module.Stats.RemoveModifier(source);

            // RemoveModifier does not do this, and the dynamic would stay subscribed to
            // AttachedCountChanged for the rest of the session.
            mod?.DestroyDynamicRules();
        }

        int count = Applied.Count;
        Applied.Clear();
        Messaging.Notification($"Removed {count} test perk mod(s).");
    }

    private static CellModule Resolve(PlayerControlledShip ship, string needle)
    {
        List<CellModule> modules = ship.GetAllModules().Where(m => m != null).ToList();

        return string.IsNullOrEmpty(needle)
            ? modules.FirstOrDefault()
            : modules.FirstOrDefault(m => m.name.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
