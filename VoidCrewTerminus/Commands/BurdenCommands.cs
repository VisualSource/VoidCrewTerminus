using System.Collections.Generic;
using System.Linq;
using CG.Ship.Modules;
using UnityEngine;
using VoidCrewTerminus.Forge;
using VoidCrewTerminus.Forge.Burdens;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

// Phase 7-C dev commands — inspect / trigger Maintenance Burdens. Gated on EnableDevMode.

internal class ListBurdensCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "listburdens" };
    public override string Description() => "[DevMode] List all modules carrying an active Maintenance Burden and their scheduling state";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!listburdens"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var burdens = Object.FindObjectsOfType<MaintenanceBurdenBehavior>();
        if (burdens.Length == 0)
        {
            Messaging.Notification("No modules currently carry a Maintenance Burden.");
            return;
        }

        foreach (var b in burdens)
        {
            var module = b.GetComponent<CellModule>();
            string moduleName = module != null ? module.name : b.gameObject.name;

            string detail = b switch
            {
                RandomShutoffBehavior rs => rs.IsShutOff
                    ? $"SHUT OFF ({rs.SecondsUntilRecovery:0.0}s until recovery)"
                    : $"{rs.SecondsUntilNextShutoff:0.0}s until next shutoff",
                _ => "(no detail)",
            };
            Messaging.Notification($"{moduleName}: {b.BurdenType} — {detail}");
        }
    }
}

internal class TriggerBurdenCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "triggerburden" };
    public override string Description() => "[DevMode] Force every active Maintenance Burden's next event to fire now (useful for testing shutoff behaviour without waiting the interval)";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!triggerburden"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var triggered = 0;
        foreach (var rs in Object.FindObjectsOfType<RandomShutoffBehavior>())
        {
            if (!rs.IsShutOff)
            {
                rs.TriggerImmediately();
                triggered++;
            }
        }

        Messaging.Notification(triggered > 0
            ? $"Triggered next shutoff on {triggered} module(s)."
            : "No idle burdens to trigger (all are already shut off or absent).");
    }
}
