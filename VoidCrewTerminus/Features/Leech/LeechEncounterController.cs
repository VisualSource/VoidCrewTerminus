using System;
using System.Collections.Generic;
using System.Linq;
using CG.Game;
using CG.Ship.Modules;
using CG.Space;
using Gameplay.Tags;
using Gameplay.Utilities;
using HarmonyLib;
using UnityEngine;
using VoidCrewTerminus.Utils;

namespace VoidCrewTerminus.Leech;

// Host-side arbitration for the encounter: how many Leeches exist, whether another
// batch may spawn, and what the ship's exposure tags say. Attach/detach bookkeeping
// is per-client because the ship tag collection is not networked.
internal static class LeechEncounterController
{
    // Swarm bucket thresholds. Small at 1, Heavy at 3, Critical at 6.
    private const int HeavyThreshold = 3;
    private const int CriticalThreshold = 6;

    private static readonly AccessTools.FieldRef<StatTagCollection, List<CsTag>> RuntimeTagsRef =
        AccessTools.FieldRefAccess<StatTagCollection, List<CsTag>>("runtimeTags");

    private static readonly List<LeechController> Attached = new();

    internal static int AttachedCount => Attached.Count;

    // ModDynamicValue reads this; raised on every transition.
    internal static event Action<int> AttachedCountChanged;

    internal static bool AtCapacity => AttachedCount >= TerminusConfig.LeechCap;

    // Called from LeechMissileBehavior.Deployed on the host.
    internal static void DeployBatch(Vector3 impactPoint, Vector3 normal)
    {
        PlayerControlledShip ship = ClientGame.Current?.PlayerShip;
        if (ship == null) return;

        // Footprints are recovered by walking BuildSockets, so refresh before a
        // batch in case the crew has built or deconstructed since the last one.
        LeechVariantAssigner.RebuildCache(ship);

        if (AtCapacity)
        {
            // The impact still reads as an impact, it just carries no payload.
            BepinPlugin.Log.LogDebug($"[Leech] impact absorbed — already at the cap of {TerminusConfig.LeechCap}.");
            return;
        }

        int requested = UnityEngine.Random.Range(1, 4);
        int allowed = Mathf.Min(requested, TerminusConfig.LeechCap - AttachedCount);

        for (int i = 0; i < allowed; i++)
        {
            // Fan the anchors out slightly so a batch doesn't stack in one spot.
            Vector3 anchor = impactPoint + UnityEngine.Random.insideUnitSphere * 1.5f;
            Spawn(ship, anchor, normal);
        }

        BepinPlugin.Log.LogDebug($"[Leech] deployed {allowed} of {requested} requested — {AttachedCount} on the hull.");
    }

    private static void Spawn(PlayerControlledShip ship, Vector3 anchor, Vector3 normal)
    {
        (LeechVariant variant, CellModule module) = LeechVariantAssigner.Assign(
            anchor, ship, TerminusConfig.LeechProximityRadius);

        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Leech";
        body.transform.SetParent(ship.Transform, worldPositionStays: true);
        body.transform.position = anchor;
        body.transform.rotation = Quaternion.LookRotation(normal);
        body.transform.localScale = new Vector3(0.6f, 0.3f, 0.8f);

        var leech = body.AddComponent<LeechController>();

        if (variant == LeechVariant.ModuleBiter)
        {
            leech.AttachToModule(
                module,
                debuffMagnitude: ParseLeadBand(TerminusConfig.LeechDebuffMagnitudeRaw, 0.25f),
                damageFraction: TerminusConfig.LeechModuleDamagePerTick,
                tickInterval: TerminusConfig.LeechModuleTickInterval);
        }
        else
        {
            leech.AttachToHull(
                ship,
                damageFraction: TerminusConfig.LeechHullDamagePerChomp,
                chompMin: TerminusConfig.LeechChompMinInterval,
                chompMax: TerminusConfig.LeechChompMaxInterval);
        }

        Attached.Add(leech);
        OnCountChanged();
    }

    internal static void Forget(LeechController leech)
    {
        if (!Attached.Remove(leech)) return;
        OnCountChanged();
    }

    // Ship death, sector teardown and run end all land here.
    internal static void Reset()
    {
        foreach (LeechController leech in Attached.ToArray())
            if (leech != null)
                UnityEngine.Object.Destroy(leech.gameObject);

        Attached.Clear();
        LeechModuleDebuffApplicator.Instance.Reset();
        OnCountChanged();
    }

    private static void OnCountChanged()
    {
        SyncShipTags();
        AttachedCountChanged?.Invoke(AttachedCount);
    }

    // Vanilla clobbers the ship's runtimeTags often (seat sit/stand, void jumps, sector
    // twists), so these are re-asserted on every transition rather than written once.
    private static void SyncShipTags()
    {
        PlayerControlledShip ship = ClientGame.Current?.PlayerShip;
        if (ship == null) return;

        List<CsTag> tags = RuntimeTagsRef(ship.Stats);
        if (tags == null) return;

        int count = AttachedCount;

        Set(tags, CsTagRegistry.ShipHasLeeches, count >= 1);
        Set(tags, CsTagRegistry.ShipLeechSwarmSmall, count >= 1 && count < HeavyThreshold);
        Set(tags, CsTagRegistry.ShipLeechSwarmHeavy, count >= HeavyThreshold && count < CriticalThreshold);
        Set(tags, CsTagRegistry.ShipLeechSwarmCritical, count >= CriticalThreshold);
    }

    private static void Set(List<CsTag> tags, CsTag tag, bool present)
    {
        if (present)
        {
            if (!tags.Contains(tag)) tags.Add(tag);
        }
        else
        {
            tags.RemoveAll(t => t == tag);
        }
    }

    // Every band read takes the first entry, the scalar 2-3 value, until the real
    // DifficultyScalar band lookup lands.
    private static float ParseLeadBand(string raw, float fallback)
        => float.TryParse(raw?.Split(',').FirstOrDefault(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;
}
