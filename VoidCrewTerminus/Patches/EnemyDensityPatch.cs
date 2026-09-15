using Gameplay.NPC.AI;
using HarmonyLib;
using Photon.Pun;
using ResourceAssets;
using VoidCrewTerminus.Escalation;

namespace VoidCrewTerminus.Patches;

// Both Set and Add mutators are patched so scenarios that flip intensity mid-encounter still
// get escalation applied. AIDirector's tick is host-only, so scale writes on non-host clients
// are harmless: their Spawner state isn't authoritative and the scale is deterministic.

[HarmonyPatch(typeof(AIDirector), nameof(AIDirector.SetSpawnerTargetIntensity))]
internal static class AIDirectorSetTargetIntensityPatch
{
    static void Prefix(ref int desiredIntensity)
    {
        desiredIntensity = ScaleForCurrentScalar(desiredIntensity);
    }

    internal static int ScaleForCurrentScalar(int n)
    {
        // ScaleDensity is a no-op while dormant, so scenario values pass through
        // unchanged until the configured boss threshold has been reached this run.
        var escalation = EscalationIntensity.Current;
        int scaled = escalation.ScaleDensity(n);
        if (scaled != n)
            BepinPlugin.Log.LogDebug(
                $"[Escalation] Density {n} → {scaled} (scalar {escalation.Scalar}, rate {escalation.DensityRate}).");
        return scaled;
    }
}

[HarmonyPatch(typeof(AIDirector), nameof(AIDirector.AddSpawnerTargetIntensity))]
internal static class AIDirectorAddTargetIntensityPatch
{
    static void Prefix(ref int addedIntensity)
    {
        addedIntensity = AIDirectorSetTargetIntensityPatch.ScaleForCurrentScalar(addedIntensity);
    }
}

[HarmonyPatch(typeof(AIDirector), nameof(AIDirector.SetSpawnerMaxTargetIntensity))]
internal static class AIDirectorSetMaxTargetIntensityPatch
{
    static void Prefix(ref int maxTargetIntensity)
    {
        maxTargetIntensity = AIDirectorSetTargetIntensityPatch.ScaleForCurrentScalar(maxTargetIntensity);
    }
}

[HarmonyPatch(typeof(AIDirector), nameof(AIDirector.AddSpawnerMaxTargetIntensity))]
internal static class AIDirectorAddMaxTargetIntensityPatch
{
    static void Prefix(ref int maxTargetIntensityIncrease)
    {
        maxTargetIntensityIncrease = AIDirectorSetTargetIntensityPatch.ScaleForCurrentScalar(maxTargetIntensityIncrease);
    }
}

// Scaling the target intensity alone gets clipped: SetTargetIntensity clamps to
// maxTargetIntensity, which is baked in from the profile at spawner creation and never routes
// through the AIDirector mutators above, so the ceiling has to be raised here too.
//
// Host-only: these fields are IPunObservable-synced from the master, so a client scaling
// locally would just be overwritten by the next sync.
[HarmonyPatch(typeof(Spawner), "InitSpawner", new[] { typeof(SpawnerProfile) })]
internal static class SpawnerInitIntensityScalingPatch
{
    private static readonly AccessTools.FieldRef<Spawner, int> MaxRef =
        AccessTools.FieldRefAccess<Spawner, int>("maxTargetIntensity");
    private static readonly AccessTools.FieldRef<Spawner, int> TargetRef =
        AccessTools.FieldRefAccess<Spawner, int>("targetIntensity");

    static void Postfix(Spawner __instance)
    {
        try
        {
            if (__instance == null) return;
            if (!PhotonNetwork.IsMasterClient) return;

            var escalation = EscalationIntensity.Current;
            if (!escalation.AffectsEnemies) return;

            int oldMax = MaxRef(__instance);
            int oldTarget = TargetRef(__instance);

            int newMax = escalation.ScaleDensity(oldMax);
            int newTarget = System.Math.Min(escalation.ScaleDensity(oldTarget), newMax);

            MaxRef(__instance) = newMax;
            TargetRef(__instance) = newTarget;

            if (newMax != oldMax || newTarget != oldTarget)
                BepinPlugin.Log.LogDebug(
                    $"[Escalation] Spawner intensity {oldTarget}/{oldMax} → {newTarget}/{newMax} " +
                    $"(scalar {escalation.Scalar}, rate {escalation.DensityRate}).");
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Escalation] SpawnerInitIntensityScalingPatch failed: {e}");
        }
    }
}
