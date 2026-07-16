using Gameplay.NPC.AI;
using HarmonyLib;
using Photon.Pun;
using ResourceAssets;
using VoidCrewTerminus.Escalation;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Patches;

// Phase 6 — density escalation. Intercepts every scenario-driven change to
// spawner intensity and re-scales it by DifficultyScalar. Both Set and Add
// mutators are patched so scenarios that flip intensity mid-encounter still
// get the escalation applied consistently.
//
// AIDirector's tick is host-only, so scale writes on non-host clients are
// harmless (their local Spawner state isn't authoritative for the actual
// spawn count). The scale itself is deterministic — same scalar, same result.

[HarmonyPatch(typeof(AIDirector), nameof(AIDirector.SetSpawnerTargetIntensity))]
internal static class AIDirectorSetTargetIntensityPatch
{
    static void Prefix(ref int desiredIntensity)
    {
        desiredIntensity = ScaleForCurrentScalar(desiredIntensity);
    }

    internal static int ScaleForCurrentScalar(int n)
    {
        // Warm-up gate: pass scenario values through unchanged until the
        // configured boss threshold has been reached this run.
        if (!SectorEscalation.IsScalingActive) return n;

        int scalar = EnemyScalingHelpers.CapScalar(
            ForgeMeterController.DifficultyScalar, TerminusConfig.EscalationScalarCap?.Value ?? 10);
        float rate = TerminusConfig.EscalationDensityScalarPerJump?.Value ?? 0.12f;
        int scaled = EnemyScalingHelpers.ScaleIntensity(n, scalar, rate);
        if (scaled != n)
            BepinPlugin.Log.LogDebug($"[Escalation] Density {n} → {scaled} (scalar {scalar}, rate {rate}).");
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

// The actual density fix. Scenarios drive spawn count by raising a spawner's
// TARGET intensity, but Spawner.SetTargetIntensity clamps it to maxTargetIntensity
// — and maxTargetIntensity is baked in from the profile at spawner creation
// (Spawner.InitSpawner(SpawnerProfile)), which never routes through the AIDirector
// mutators above. So scaling the target alone gets clipped back to the vanilla
// ceiling and produces no extra enemies. Here we raise the ceiling itself (and
// the initial target) at creation so the target scaling has headroom and
// DirectorTick actually spawns more.
//
// Host-only: maxTargetIntensity/targetIntensity are IPunObservable-synced from
// the master client (the only one that ticks spawns), so scaling on the host is
// authoritative and clients receive the boosted values. Clients scaling locally
// would just be overwritten by the next sync (and their DifficultyScalar isn't
// networked yet — Phase 8), so we skip them to avoid transient divergence.
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
            if (!SectorEscalation.IsScalingActive) return;
            if (!PhotonNetwork.IsMasterClient) return;

            int scalar = EnemyScalingHelpers.CapScalar(
                ForgeMeterController.DifficultyScalar, TerminusConfig.EscalationScalarCap?.Value ?? 10);
            if (scalar <= 0) return;

            float rate = TerminusConfig.EscalationDensityScalarPerJump?.Value ?? 0.12f;

            int oldMax = MaxRef(__instance);
            int oldTarget = TargetRef(__instance);

            int newMax = EnemyScalingHelpers.ScaleIntensity(oldMax, scalar, rate);
            int newTarget = System.Math.Min(
                EnemyScalingHelpers.ScaleIntensity(oldTarget, scalar, rate), newMax);

            MaxRef(__instance) = newMax;
            TargetRef(__instance) = newTarget;

            if (newMax != oldMax || newTarget != oldTarget)
                BepinPlugin.Log.LogDebug(
                    $"[Escalation] Spawner intensity {oldTarget}/{oldMax} → {newTarget}/{newMax} " +
                    $"(scalar {scalar}, rate {rate}).");
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Escalation] SpawnerInitIntensityScalingPatch failed: {e}");
        }
    }
}
