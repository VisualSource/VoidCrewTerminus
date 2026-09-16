using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CG.Game;
using CG.Game.Player;
using CG.Ship.Modules;
using CG.Space;
using Gameplay.CompositeWeapons;
using UnityEngine;
using VoidCrewTerminus.Leech;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

internal class LeechMissileCommand : PublicCommand
{
    private const float DefaultLaunchDistance = 120f;

    public override string[] CommandAliases() => new[] { "leechmissile" };
    public override string Description() => "[DevMode] Launch a Leech Missile at the player ship, spawned ahead of where you are looking (default 120m)";
    public override List<Argument> Arguments() => [new("%distance?")];
    public override string[] UsageExamples() => ["!leechmissile", "!leechmissile 30", "!leechmissile 600"];

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

        if (!TryGetEyeLine(out Vector3 eye, out Vector3 look))
        {
            Messaging.Notification("No local player to spawn in front of.");
            return;
        }

        Vector3 origin = eye + look * distance;

        LeechMissileBehavior missile = LeechMissileFactory.Spawn(
            origin,
            ship,
            source: null,
            hitPoints: TerminusConfig.LeechMissileHitPoints,
            speed: TerminusConfig.LeechMissileSpeed,
            arcLength: TerminusConfig.LeechMissileTurnArc);

        // The missile turns onto the ship the moment it exists, so a close spawn is for
        // looking at the body, not at the approach — under ~100 m it is on the hull in
        // about a second.
        float closing = distance / Mathf.Max(1f, TerminusConfig.LeechMissileSpeed);
        Messaging.Notification(missile == null
            ? "Missile spawn refused — see the log (host only, and only inside a sector)."
            : $"Leech Missile {missile.ProjectileId} at {distance:0}m dead ahead — " +
              $"{missile.HitPointsRemaining} HP, roughly {closing:0.#}s to impact.");
    }

    // The camera, not the player transform: the character root carries yaw only, so a
    // transform-forward spawn lands off-screen whenever you are looking up or down.
    private static bool TryGetEyeLine(out Vector3 eye, out Vector3 look)
    {
        Camera camera = Camera.main;
        if (camera != null)
        {
            eye = camera.transform.position;
            look = camera.transform.forward;
            return true;
        }

        var player = LocalPlayer.Instance;
        if (player != null)
        {
            eye = player.transform.position;
            look = player.transform.forward;
            return true;
        }

        eye = Vector3.zero;
        look = Vector3.forward;
        return false;
    }
}

internal class LeechStatusCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "leechstatus" };
    public override string Description() => "[DevMode] Report attached Leech count, the concurrency rail, and the ship's exposure tags";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!leechstatus"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode) return;

        PlayerControlledShip ship = ClientGame.Current?.PlayerShip;
        if (ship == null)
        {
            Messaging.Notification("No player ship.");
            return;
        }

        string tags = string.Join(", ", ship.Stats.LocalTags()
            .Where(t => t != null && t.name.Contains("Leech"))
            .Select(t => t.name));

        Messaging.Notification(
            $"Leeches {LeechEncounterController.AttachedCount}/{TerminusConfig.LeechCap}" +
            $"{(LeechEncounterController.AtCapacity ? " (AT CAP)" : "")} — " +
            $"ship tags: {(string.IsNullOrEmpty(tags) ? "none" : tags)}");
    }
}

internal class LeechClearCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "leechclear" };
    public override string Description() => "[DevMode] Remove every attached Leech and clear its debuffs";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!leechclear"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode) return;

        int removed = LeechEncounterController.AttachedCount;
        LeechEncounterController.Reset();
        Messaging.Notification($"Cleared {removed} Leech(es).");
    }
}

// Which of the Hollow variants reads as the right missile is a judgement call that only
// looks right in the game, so this switches the borrowed prefab between spawns instead of
// making each candidate a rebuild.
internal class LeechVisualCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "leechvisual" };
    public override string Description() => "[DevMode] List the vanilla projectile prefabs the Leech Missile can borrow its body from, or switch to one";
    public override List<Argument> Arguments() => [new("%prefab_name?")];
    public override string[] UsageExamples() => ["!leechvisual", "!leechvisual Projectile_HollowRocket_Fighter"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode) return;

        List<string> candidates = LeechMissileVisual.Candidates();

        if (string.IsNullOrWhiteSpace(arguments))
        {
            BepinPlugin.Log.LogDebug($"[Leech] borrowable projectile prefabs:\n  {string.Join("\n  ", candidates)}");
            Messaging.Notification(
                $"Currently '{TerminusConfig.LeechMissileVisualName}'. {candidates.Count} candidates listed in the log; " +
                "the Hollow ones are GuidedProjectile_HollowTorpedo, GuidedProjectile_HollowRocket, " +
                "Projectile_HollowRocket_Fighter and GuidedProjectile_HollowHeavyMissile_01.");
            return;
        }

        string requested = arguments.Trim();
        string match = Resolve(requested, candidates);

        if (match == null)
        {
            Messaging.Notification($"No projectile prefab matching '{requested}' — run !leechvisual for the list.");
            return;
        }

        TerminusConfig.LeechMissileVisualPrefab.Value = match;
        Messaging.Notification($"Leech Missile body set to '{match}'. Next !leechmissile uses it.");
    }

    // The chat censor rewrites the "pedo" in "Torpedo", so !leechvisual
    // GuidedProjectile_HollowTorpedo arrives as "...HollowTor****" and can never match
    // exactly. Everything from the first asterisk is treated as a wildcard, and the
    // shortest remaining match wins so a stem does not select a longer variant.
    private static string Resolve(string requested, List<string> candidates)
    {
        string exact = candidates.FirstOrDefault(name => name.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        int censored = requested.IndexOf('*');
        string stem = censored < 0 ? requested : requested.Substring(0, censored);
        if (string.IsNullOrEmpty(stem)) return null;

        return candidates
            .Where(name => name.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(name => name.Length)
            .FirstOrDefault();
    }
}

// Interception ranges are serialized on the module prefabs, so the decompile only shows
// the inline field defaults — KineticPointDefenseModule reads 525 m in source whatever the
// asset says, and that number has been driving the Phase 1 tuning discussion unchecked.
// These are also ModifiableFloats, so tier and any active Leech debuff move them.
internal class LeechPointDefenceCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "leechpd" };
    public override string Description() => "[DevMode] Report the live interception range of every point-defence module and auto-shooting turret on the ship";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!leechpd"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode) return;

        PlayerControlledShip ship = ClientGame.Current?.PlayerShip;
        if (ship == null)
        {
            Messaging.Notification("No player ship.");
            return;
        }

        var report = new List<string>();

        foreach (KineticPointDefenseModule kpd in ship.GetComponentsInChildren<KineticPointDefenseModule>(true))
            report.Add($"KPD {kpd.name}: tracking {kpd.TrackingRange.Value:0} m");

        foreach (AutoShootingTurretController turret in ship.GetComponentsInChildren<AutoShootingTurretController>(true))
        {
            CompositeWeaponModule weapon = turret.Turret;
            if (weapon != null) report.Add($"Turret {weapon.name}: range {weapon.Range.Value:0} m");
        }

        if (report.Count == 0)
        {
            Messaging.Notification("No point-defence modules or auto-shooting turrets found on the ship.");
            return;
        }

        BepinPlugin.Log.LogDebug($"[Leech] interception ranges:\n  {string.Join("\n  ", report)}");
        Messaging.Notification(string.Join(" | ", report));
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

        // Membership of the mask alone is not enough — SpaceObjects is in it and no camera
        // draws it, which cost two misdiagnosed rounds. A layer is only usable if it is in
        // both, so both are reported.
        Camera camera = Camera.main;

        string report = string.Join(", ", LeechMissileFactory.MaskedLayers()
            .Select(layer =>
            {
                string name = LayerMask.LayerToName(layer);
                bool drawn = camera != null && (camera.cullingMask & (1 << layer)) != 0;
                return $"{layer}={(string.IsNullOrEmpty(name) ? "<unnamed>" : name)}" +
                       $" ({(camera == null ? "no camera" : drawn ? "rendered" : "not rendered")})";
            }));

        Messaging.Notification($"Projectile TARGET_MASK layers: {report}");
    }
}
