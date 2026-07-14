using System.Collections.Generic;
using System.Linq;
using CG.Game.Player;
using CG.Ship.Modules;
using UnityEngine;
using VoidCrewTerminus.Forge;
using VoidManager.Chat.Router;
using VoidManager.Utilities;
using Object = UnityEngine.Object;

namespace VoidCrewTerminus.Commands;

internal static class ModuleFinder
{
    public static CellModule NearestToPlayer()
    {
        var player = LocalPlayer.Instance;
        if (player == null) return null;
        var pos = player.transform.position;
        return Object.FindObjectsOfType<CellModule>()
            .OrderBy(m => Vector3.Distance(m.transform.position, pos))
            .FirstOrDefault();
    }
}

internal class SetLevelCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "setlevel" };
    public override string Description() => "[DevMode] Set the forge level of the nearest ship module (3-10)";
    public override List<Argument> Arguments() => [new("%level")];
    public override string[] UsageExamples() => ["!setlevel 7", "!setlevel 10"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        if (!int.TryParse(arguments?.Trim(), out int level) || level < 3 || level > 10)
        {
            Messaging.Notification("Usage: !setlevel <3-10>");
            return;
        }

        var module = ModuleFinder.NearestToPlayer();
        if (module == null)
        {
            Messaging.Notification("No module found nearby. Are you in a game session?");
            return;
        }

        var state = ForgeStateStore.GetOrCreate(module);
        state.SetLevel(level);
        Messaging.Notification($"{module.name}: level set to {level}");
    }
}

internal class GetLevelCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "getlevel" };
    public override string Description() => "[DevMode] Show the forge level of the nearest ship module";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!getlevel"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var module = ModuleFinder.NearestToPlayer();
        if (module == null)
        {
            Messaging.Notification("No module found nearby.");
            return;
        }

        if (ForgeStateStore.TryGet(module, out var state))
            Messaging.Notification($"{module.name}: L{state.Level}");
        else
            Messaging.Notification($"{module.name}: L3 (vanilla, no overlay)");
    }
}

internal class DumpTagsCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "dumptags" };
    public override string Description() => "[DevMode] Print the CsTags and runtime stat-tags of the nearest ship module";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!dumptags"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var module = ModuleFinder.NearestToPlayer();
        if (module == null)
        {
            Messaging.Notification("No module found nearby.");
            return;
        }

        var initTags = module.CsTags;
        var initNames = initTags != null && initTags.Length > 0
            ? string.Join(", ", initTags.Select(t => t.name))
            : "(none)";
        Messaging.Notification($"{module.name} — init tags: {initNames}");

        var localTags = module.Stats.LocalTags();
        var runtimeOnly = localTags
            .Where(t => initTags == null || !initTags.Contains(t))
            .Select(t => t.name)
            .ToList();
        if (runtimeOnly.Count > 0)
            Messaging.Notification($"{module.name} — runtime tags: {string.Join(", ", runtimeOnly)}");
    }
}

internal class ResetOverlayCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "resetoverlay" };
    public override string Description() => "[DevMode] Reset all module forge overlays to vanilla L3";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!resetoverlay"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;
        ForgeStateStore.ClearAll();
        Messaging.Notification("All forge overlays cleared — modules restored to vanilla L3.");
    }
}
