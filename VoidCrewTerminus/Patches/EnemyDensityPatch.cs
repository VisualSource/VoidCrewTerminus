using Gameplay.NPC.AI;
using HarmonyLib;
using ResourceAssets;
using VoidCrewTerminus.EnemyScaling;
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

        int scalar = ForgeMeterController.DifficultyScalar;
        float rate = TerminusConfig.EscalationDensityScalarPerJump?.Value ?? 0.20f;
        return EnemyScalingHelpers.ScaleIntensity(n, scalar, rate);
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
