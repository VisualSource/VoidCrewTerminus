using System.Collections.Generic;
using Gameplay.Tags;
using Gameplay.Utilities;

namespace VoidCrewTerminus.Leech.Dynamics;

// Factories for StatMods that read the live Leech count, plus the teardown the
// game does not do for us.
//
// Two rules callers cannot opt out of:
//
//   1. Dynamics must be attached BEFORE the StatMod reaches a collection.
//      StatTagCollection.ApplyModifier initialises dynamics and then evaluates
//      whether the mod is active; assigning afterwards leaves the mod classified
//      inactive until some later refresh happens to correct it.
//
//   2. Condition-bearing mods must go through the PLURAL ApplyModifiers(mods,
//      source). ModDynamicCondition.Init assigns itself as the mod's source, and
//      the singular ApplyModifier sets the source before init — so the condition
//      overwrites it, and a later RemoveModifier(yourSource) cannot find the mod
//      and can never remove it. The plural overload re-asserts the source after
//      init, which is why it is safe.
internal static class LeechDynamics
{
    // A fresh dynamic per StatMod, always. Init binds permanently and Destroy never
    // resets it, so a cached instance would silently fail to drive its second owner.
    internal static StatMod WithLeechCountScaling(
        StatType stat,
        float perLeech,
        IModifierSource source,
        ModTagConfiguration tagConfiguration = null)
    {
        var mod = new StatMod(
            new FloatModifier(0f, ModifierType.AdditiveMultiplier, source),
            stat.Id,
            tagConfiguration);

        mod.DynamicValue = new LeechAttachedCountValue(perLeech);
        mod.InitDynamicElements();

        return mod;
    }

    internal static StatMod GateByLeechCount(StatMod mod, int threshold, bool invert = false)
    {
        var condition = new LeechAttachedCountCondition(threshold) { InvertApplication = invert };

        mod.DynamicCondition = condition;
        mod.InitDynamicElements();

        return mod;
    }

    // StatTagCollection.RemoveModifier detaches the modifier and drops the
    // collection's own subscriptions, but never calls DestroyDynamicRules. Without
    // this the dynamic stays subscribed to AttachedCountChanged and keeps writing
    // Mod.Amount for a mod that is no longer applied, for the rest of the session.
    internal static void Teardown(IEnumerable<StatMod> mods)
    {
        if (mods == null) return;

        foreach (StatMod mod in mods) mod?.DestroyDynamicRules();
    }
}
