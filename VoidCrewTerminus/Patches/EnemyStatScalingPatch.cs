using System.Collections.Generic;
using CG.Space;
using Gameplay.Damage;
using Gameplay.Tags;
using Gameplay.Utilities;
using HarmonyLib;
using UnityEngine;
using VoidCrewTerminus.Escalation;

namespace VoidCrewTerminus.Patches;

// Scales enemy MaxHitPoints through the game's native StatMod pipeline rather than by
// patching stat getters. Boss exclusion is not implemented: linking a spawned ship to its
// ObjectiveData boss reference needs more pre-flight, and the default rate leaves a
// scalar-6 boss at +30% HP.
[HarmonyPatch(typeof(DestroyableComponent), nameof(DestroyableComponent.InitializeHealth))]
internal static class EnemyHealthScalingPatch
{
    static void Postfix(DestroyableComponent __instance)
    {
        try
        {
            var escalation = EscalationIntensity.Current;
            if (!escalation.AffectsEnemies) return;

            var parent = __instance?.GetParentObject();
            if (parent == null) return;
            if (!EnemyScalingHelpers.IsEnemyFaction(parent.Faction)) return;

            // A zero-value StatMod would register a modifier for nothing.
            float amount = escalation.StatBonus;
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

// Scaled on the receiver side, so one hook captures every enemy damage source (turrets,
// missiles, ramming) without walking each enemy weapon's stat collection.
[HarmonyPatch(typeof(DestroyableComponent), nameof(DestroyableComponent.CalculateRawDamage))]
internal static class EnemyDamageScalingPatch
{
    static void Postfix(DestroyableComponent __instance, OrbitObject source, ref float __result)
    {
        try
        {
            if (source == null || __instance == null) return;

            var escalation = EscalationIntensity.Current;
            if (!escalation.AffectsEnemies) return;

            var target = __instance.GetParentObject();
            if (target == null) return;

            // Enemy → player only: friendly fire, player → enemy and wildlife are untouched.
            if (!EnemyScalingHelpers.IsEnemyFaction(source.Faction)) return;
            if (!EnemyScalingHelpers.IsPlayerFaction(target.Faction)) return;

            __result *= escalation.StatMultiplier;
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Escalation] EnemyDamageScalingPatch failed: {e}");
        }
    }
}
