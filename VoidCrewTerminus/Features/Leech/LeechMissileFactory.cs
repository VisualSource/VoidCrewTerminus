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

    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int EmissiveColor = Shader.PropertyToID("_EmissiveColor");

    private static int _cachedLayer = -1;
    private static Material _bodyMaterial;

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
        missile.Deployed += (_, point, normal) => LeechEncounterController.DeployBatch(point, normal);

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

        Material body = BodyMaterial();
        if (body != null) go.GetComponent<MeshRenderer>().sharedMaterial = body;

        return go;
    }

    // CreatePrimitive assigns the built-in Default-Material, whose Standard shader
    // is not in this HDRP build — the capsule then renders as nothing at all, with
    // no error. Emissive because an unlit placeholder reads as black against space.
    private static Material BodyMaterial()
    {
        if (_bodyMaterial != null) return _bodyMaterial;

        Shader shader = Shader.Find("HDRP/Lit");
        if (shader == null) shader = Shader.Find("HDRP/Unlit");
        if (shader == null) shader = BorrowSceneShader();

        if (shader == null)
        {
            BepinPlugin.Log.LogWarning("[Leech] no usable shader found; missile will be invisible.");
            return null;
        }

        _bodyMaterial = new Material(shader) { name = "LeechMissileBody" };
        if (_bodyMaterial.HasProperty(BaseColor))
            _bodyMaterial.SetColor(BaseColor, new Color(0.35f, 0.75f, 0.30f));
        if (_bodyMaterial.HasProperty(EmissiveColor))
            _bodyMaterial.SetColor(EmissiveColor, new Color(0.9f, 2.6f, 0.7f));

        BepinPlugin.Log.LogDebug($"[Leech] missile material using shader '{shader.name}'.");
        return _bodyMaterial;
    }

    private static Shader BorrowSceneShader()
    {
        foreach (MeshRenderer renderer in Object.FindObjectsOfType<MeshRenderer>())
        {
            Material candidate = renderer.sharedMaterial;
            if (candidate != null && candidate.shader != null && candidate.shader.isSupported)
                return candidate.shader;
        }
        return null;
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
