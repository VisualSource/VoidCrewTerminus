using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CG.Game;
using CG.Game.Player;
using CG.Space;
using UnityEngine;
using VoidCrewTerminus.Leech;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

internal class LeechMissileCommand : PublicCommand
{
    private const float DefaultLaunchDistance = 400f;

    public override string[] CommandAliases() => new[] { "leechmissile" };
    public override string Description() => "[DevMode] Launch a Leech Missile at the player ship from a given distance (default 400m)";
    public override List<Argument> Arguments() => [new("%distance?")];
    public override string[] UsageExamples() => ["!leechmissile", "!leechmissile 600"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode) return;

        PlayerControlledShip ship = ClientGame.Current?.PlayerShip;
        if (ship == null)
        {
            Messaging.Notification("No player ship — load into a sector first.");
            return;
        }

        float distance = DefaultLaunchDistance;
        if (!string.IsNullOrWhiteSpace(arguments)
            && !float.TryParse(arguments.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out distance))
        {
            Messaging.Notification($"Could not read '{arguments}' as a distance.");
            return;
        }

        // Launch off the ship's bow so the missile has room to turn and the crew
        // can actually watch it come in.
        Vector3 origin = ship.WorldPosition + ship.Transform.forward * distance + ship.Transform.up * (distance * 0.25f);

        LeechMissileBehavior missile = LeechMissileFactory.Spawn(
            origin,
            ship,
            source: null,
            hitPoints: TerminusConfig.LeechMissileHitPoints,
            speed: TerminusConfig.LeechMissileSpeed,
            arcLength: TerminusConfig.LeechMissileTurnArc);

        Messaging.Notification(missile == null
            ? "Missile spawn refused — see the log (host only, and only inside a sector)."
            : $"Leech Missile {missile.ProjectileId} launched from {distance:0}m — {missile.HitPointsRemaining} HP.");
    }
}

// The projectile target mask is asset-side data the decompile can't resolve, so
// this reports what the layer names actually are at runtime.
internal class LeechLayersCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "leechlayers" };
    public override string Description() => "[DevMode] Report the layer names inside the projectile TARGET_MASK";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!leechlayers"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode) return;

        string report = string.Join(", ", LeechMissileFactory.MaskedLayers()
            .Select(layer =>
            {
                string name = LayerMask.LayerToName(layer);
                return $"{layer}={(string.IsNullOrEmpty(name) ? "<unnamed>" : name)}";
            }));

        Messaging.Notification($"Projectile TARGET_MASK layers: {report}");
    }
}
