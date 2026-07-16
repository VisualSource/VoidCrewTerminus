using System.Collections.Generic;
using CG.Space;
using Gameplay.Damage;
using Gameplay.Tags;
using Gameplay.Utilities;
using HarmonyLib;
using UnityEngine;
using VoidCrewTerminus.Escalation;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Patches;

// Phase 6 — HP scaling half. Postfixes DestroyableComponent.InitializeHealth
// so every enemy component gets a MaxHitPoints AdditiveMultiplier proportional
// to the current DifficultyScalar. Uses the game's native StatMod pipeline
// (same as ForgeModuleState for player modules) rather than patching stat
// getters directly.
//
// Boss exclusion is NOT implemented in this pass — linking a spawned ship to
// its ObjectiveData boss reference needs another chunk of pre-flight and the
// "minor boost" rate (0.05/scalar default) keeps a scalar-6 boss at +30% HP,
// which is meaningful but not game-breaking.
[HarmonyPatch(typeof(DestroyableComponent), nameof(DestroyableComponent.InitializeHealth))]
internal static class EnemyHealthScalingPatch
{
    static void Postfix(DestroyableComponent __instance)
    {
        try
        {
            if (!SectorEscalation.IsScalingActive) return;

            int scalar = EnemyScalingHelpers.CapScalar(
                ForgeMeterController.DifficultyScalar, TerminusConfig.EscalationScalarCap?.Value ?? 10);
            if (scalar <= 0) return;

            var parent = __instance?.GetParentObject();
            if (parent == null) return;
            if (!EnemyScalingHelpers.IsEnemyFaction(parent.Faction)) return;

            float rate = TerminusConfig.EscalationStatScalarPerJump?.Value ?? 0.05f;
            float amount = scalar * rate;
            if (amount <= 0f) return;

            var mods = new List<StatMod>
            {
                new StatMod(
                    new FloatModifier(amount, ModifierType.AdditiveMultiplier, EnemyScalingSource.Instance),
                    StatType.MaxHitPoints.Id,
                    new ModTagConfiguration()),
            };
            __instance.Stats.ApplyModifiers(mods, EnemyScalingSource.Instance);
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Escalation] EnemyHealthScalingPatch failed: {e}");
        }
    }
}

// Phase 6 — damage scaling half. Postfixes DestroyableComponent.CalculateRawDamage
// on the RECEIVER side: when an enemy hits a player-faction object, we scale
// the damage before it's applied. One hook captures every enemy damage source
// (turrets, missiles, ramming) without needing to walk each enemy weapon's
// stat collection.
[HarmonyPatch(typeof(DestroyableComponent), nameof(DestroyableComponent.CalculateRawDamage))]
internal static class EnemyDamageScalingPatch
{
    static void Postfix(DestroyableComponent __instance, OrbitObject source, ref float __result)
    {
        try
        {
            if (source == null || __instance == null) return;
            if (!SectorEscalation.IsScalingActive) return;

            int scalar = EnemyScalingHelpers.CapScalar(
                ForgeMeterController.DifficultyScalar, TerminusConfig.EscalationScalarCap?.Value ?? 10);
            if (scalar <= 0) return;

            var target = __instance.GetParentObject();
            if (target == null) return;

            // Only scale enemy → player damage. Enemy → enemy (friendly fire),
            // player → enemy, and PvE wildlife interactions are untouched.
            if (!EnemyScalingHelpers.IsEnemyFaction(source.Faction)) return;
            if (!EnemyScalingHelpers.IsPlayerFaction(target.Faction)) return;

            float rate = TerminusConfig.EscalationStatScalarPerJump?.Value ?? 0.05f;
            __result *= (1f + scalar * rate);
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Escalation] EnemyDamageScalingPatch failed: {e}");
        }
    }
}
