using System.Collections.Generic;
using CG.Space;
using CG.Space.Projectiles;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewTerminus.Leech;

// Builds a Leech Missile from scratch. Every vanilla spawn path is closed to us —
// NpcShooter needs a real SpaceCraft, PlayerSyncedProjectilesShooter hardcodes the
// player ship as Source so it could never damage it — so this reproduces
// NpcShooter.CreateProjectile's field-for-field setup over a runtime primitive.
internal static class LeechMissileFactory
{
    // SyncedProjectile.TARGET_MASK, which is protected. Bits 10, 12 and 13 are the
    // only layers a projectile raycast will register, so the missile has to sit on
    // one of them to be shootable.
    private const int ProjectileTargetMask = 13312;

    // Layer numbers are asset-side, so resolve by name and let the mask arbitrate.
    private static readonly string[] PreferredLayers = { "SpaceObjects", "MovingPlatform" };

    private static int _cachedLayer = -1;

    internal static LeechMissileBehavior Spawn(
        Vector3 origin,
        OrbitObject target,
        OrbitObject source,
        int hitPoints,
        float speed,
        float arcLength)
    {
        // GetNextProjectileIndex throws on a remote client by design.
        if (!PhotonNetwork.IsMasterClient)
        {
            BepinPlugin.Log.LogDebug("[Leech] missile spawn refused — not master client.");
            return null;
        }

        SyncedProjectilesSynchronizer sync = SyncedProjectilesSynchronizer.Instance;
        if (sync == null)
        {
            BepinPlugin.Log.LogWarning("[Leech] missile spawn refused — no projectile synchronizer (outside a sector?).");
            return null;
        }

        Vector3 heading = target.Valid()
            ? (target.WorldPosition - origin).normalized
            : Vector3.forward;

        GameObject go = BuildBody(origin, heading);
        var missile = go.AddComponent<LeechMissileBehavior>();

        // Zero damage is the design contract, not a placeholder: the payload is
        // the leeches, and a damaging missile collapses the encounter into
        // another damage source.
        missile.Damage.SetBaseValue(0f);
        missile.Speed.SetBaseValue(speed);
        missile.AdjustAngularMultiplier(speed);
        missile.Velocity = heading * speed;
        missile.Range.SetBaseValue(speed * 20f);
        missile.MovementArcLength = arcLength;

        missile.ProjectileId = sync.GetNextProjectileIndex();
        missile.Source = source;
        missile.Faction = source.Valid() ? source.Faction : 0;
        missile.SpawnTime = PhotonNetwork.ServerTimestamp;

        missile.Arm(target, hitPoints);

        // Registration is explicit — point-defense finds NPC projectiles through
        // the synchronizer's dictionary, not by scanning colliders.
        sync.RegisterNpcProjectile(missile);

        BepinPlugin.Log.LogDebug(
            $"[Leech] missile {missile.ProjectileId} away — {hitPoints} HP, {speed} m/s, arc {arcLength}, layer {go.layer}.");

        return missile;
    }

    private static GameObject BuildBody(Vector3 origin, Vector3 heading)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "LeechMissile";
        go.transform.position = origin;
        go.transform.rotation = Quaternion.LookRotation(heading);
        go.transform.localScale = new Vector3(0.5f, 1.4f, 0.5f);
        go.layer = ResolveTargetLayer();
        return go;
    }

    // Picks the named layer inside TARGET_MASK. Falls back to the mask's lowest
    // bit so the missile is always shootable even if the names move.
    private static int ResolveTargetLayer()
    {
        if (_cachedLayer >= 0) return _cachedLayer;

        List<int> masked = MaskedLayers();

        foreach (string name in PreferredLayers)
        {
            int layer = LayerMask.NameToLayer(name);
            if (layer >= 0 && masked.Contains(layer))
            {
                _cachedLayer = layer;
                BepinPlugin.Log.LogDebug($"[Leech] missile layer resolved to {layer} ('{name}').");
                return _cachedLayer;
            }
        }

        _cachedLayer = masked[0];
        BepinPlugin.Log.LogWarning(
            $"[Leech] no preferred layer found in TARGET_MASK; falling back to {_cachedLayer} " +
            $"('{LayerMask.LayerToName(_cachedLayer)}'). Missile may not be shootable.");
        return _cachedLayer;
    }

    internal static List<int> MaskedLayers()
    {
        var layers = new List<int>();
        for (int layer = 0; layer < 32; layer++)
            if ((ProjectileTargetMask & (1 << layer)) != 0)
                layers.Add(layer);
        return layers;
    }
}
