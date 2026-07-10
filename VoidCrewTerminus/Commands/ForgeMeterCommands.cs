using System.Collections.Generic;
using VoidCrewTerminus.Forge;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

// Phase 5 dev commands for the Forge Meter. All gated on EnableDevMode.

internal class ForgeMeterCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgemeter" };
    public override string Description() => "[DevMode] Show Forge Meter progression (level, fill, capacity)";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!forgemeter"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;
        Messaging.Notification(ForgeMeterController.Describe());
    }
}

internal class SetMeterCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "setmeter" };
    public override string Description() => "[DevMode] Set the Forge Meter fill (level-ups apply immediately)";
    public override List<Argument> Arguments() => [new("%value")];
    public override string[] UsageExamples() => ["!setmeter 95", "!setmeter 500"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;
        if (!float.TryParse((arguments ?? "").Trim(), out float value) || value < 0f)
        {
            Messaging.Notification("Usage: !setmeter <value>");
            return;
        }
        ForgeMeterController.SetMeter(value);
        Messaging.Notification(ForgeMeterController.Describe());
    }
}

internal class SetForgeLevelCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "setforgelevel" };
    public override string Description() => "[DevMode] Set the Forge level directly (resets meter fill)";
    public override List<Argument> Arguments() => [new("%level")];
    public override string[] UsageExamples() => ["!setforgelevel 1", "!setforgelevel 6"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;
        if (!int.TryParse((arguments ?? "").Trim(), out int level))
        {
            Messaging.Notification($"Usage: !setforgelevel <{ForgeMeterController.MinLevel}-{ForgeMeterController.MaxLevel}>");
            return;
        }
        ForgeMeterController.SetLevel(level);
        Messaging.Notification(ForgeMeterController.Describe());
    }
}
