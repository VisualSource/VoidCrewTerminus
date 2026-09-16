using System.Collections.Generic;
using CG.Space;
using CG.Space.Projectiles;
using Gameplay.Damage;
using Photon.Pun;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

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
    // Projectiles before SpaceObjects: both are in the mask, but SpaceObjects is a
    // collision-only layer that no camera draws — see LeechMissileVisual. ("MovingPlatform"
    // was in this list and is layer 27, outside the mask, so it could never have matched.)
    private static readonly string[] PreferredLayers = { "Projectiles", "SpaceObjects" };

    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int EmissiveColor = Shader.PropertyToID("_EmissiveColor");

    private static readonly Color BodyColor = new(0.35f, 0.75f, 0.30f);
    private const float EmissiveBoost = 4f;

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
        // Hollow, not 0, when the dev command spawns one with no carrier behind it:
        // SpaceCraftFaction.Neutral is 0, and IsObjectFromEnemyFaction short-circuits to
        // false whenever either side is 0, so a faction-0 missile reads as nobody's enemy
        // to every threat, marker and AI path that asks.
        missile.Faction = source.Valid() ? source.Faction : (int)SpaceCraftFaction.Hollow;
        missile.SpawnTime = PhotonNetwork.ServerTimestamp;

        missile.Arm(target, hitPoints);
        missile.Deployed += (_, point, normal) => LeechEncounterController.DeployBatch(point, normal);

        // After the behaviour exists, not inside BuildBody: the borrowed VFX hang their
        // trail's detach-and-fade off the owning projectile's events, so they need a live
        // missile to be repointed at.
        LeechMissileVisual.Attach(go, missile);

        if (TerminusConfig.DevMode) LeechVisibilityProbe.Attach(go, "missile");

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
        go.layer = ResolveTargetLayer();

        // The primitive is kept only for its collider — the surface player fire and
        // point-defense have to hit — and never drawn. Along Z to match the heading.
        var hitBox = go.GetComponent<CapsuleCollider>();
        hitBox.direction = 2;
        hitBox.height = 2.8f;

        // Immediate, not deferred: a Destroy here survives to the end of the frame, and
        // the visibility probe would bind to this doomed renderer instead of the body.
        Object.DestroyImmediate(go.GetComponent<MeshRenderer>());
        Object.DestroyImmediate(go.GetComponent<MeshFilter>());

        return go;
    }

    internal static Material BodyMaterial()
    {
        if (_bodyMaterial != null) return _bodyMaterial;

        _bodyMaterial = BuildLitMaterial(BodyColor, "LeechMissileBody")
                        ?? CloneVisibleSceneMaterial("LeechMissileBody");

        if (_bodyMaterial == null)
            BepinPlugin.Log.LogWarning("[Leech] no usable material source; missile will be invisible.");

        return _bodyMaterial;
    }

    // CreatePrimitive assigns the built-in Default-Material, whose Standard shader is
    // not in this HDRP build, so the capsule renders as nothing with no error. Finding
    // HDRP/Lit is necessary but not sufficient: a material built from a shader at
    // runtime skips the validation the importer normally runs, leaving its keywords
    // and stencil state unset, and HDRP then drops it from the deferred pass — still
    // invisible, still silent. ValidateMaterial is the step that was missing.
    internal static Material BuildLitMaterial(Color color, string name)
    {
        Shader shader = Shader.Find("HDRP/Lit");
        if (shader == null)
        {
            BepinPlugin.Log.LogWarning("[Leech] HDRP/Lit not found in this build.");
            return null;
        }

        var material = new Material(shader) { name = name };
        material.SetColor(BaseColor, color);
        material.SetColor(EmissiveColor, color * EmissiveBoost);
        HDMaterial.ValidateMaterial(material);

        BepinPlugin.Log.LogDebug(
            $"[Leech] material '{name}' built on HDRP/Lit — queue {material.renderQueue}, " +
            $"keywords [{string.Join(" ", material.shaderKeywords)}].");
        return material;
    }

    // Fallback: a material already rendering in this scene is guaranteed to carry both
    // a validated keyword set and a shader variant that survived build-time stripping.
    internal static Material CloneVisibleSceneMaterial(string name)
    {
        foreach (MeshRenderer renderer in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (!renderer.isVisible) continue;

            Material source = renderer.sharedMaterial;
            if (source == null || source.shader == null || !source.shader.isSupported) continue;

            BepinPlugin.Log.LogDebug(
                $"[Leech] material '{name}' cloned from '{source.name}' on shader '{source.shader.name}'.");
            return new Material(source) { name = name };
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
