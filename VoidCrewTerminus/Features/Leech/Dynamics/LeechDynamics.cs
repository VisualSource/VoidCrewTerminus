using System.Collections.Generic;
using Gameplay.Tags;
using Gameplay.Utilities;

namespace VoidCrewTerminus.Leech.Dynamics;

// Factories for StatMods that read the live Leech count, plus the teardown the game does not
// do for us. Two rules callers cannot opt out of:
//
//   1. Dynamics must be attached BEFORE the StatMod reaches a collection. ApplyModifier
//      initialises dynamics and then evaluates whether the mod is active, so assigning
//      afterwards leaves it classified inactive until some later refresh corrects it.
//   2. Condition-bearing mods must go through the PLURAL ApplyModifiers(mods, source).
//      ModDynamicCondition.Init assigns itself as the mod's source and the singular overload
//      sets the source before init, so RemoveModifier(yourSource) can never find the mod.
internal static class LeechDynamics
{
    // A fresh dynamic per StatMod, always: Init binds permanently and Destroy never resets
    // it, so a cached instance would silently fail to drive its second owner.
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

    // RemoveModifier detaches the modifier and drops the collection's subscriptions but never
    // calls DestroyDynamicRules, so without this the dynamic stays subscribed to
    // AttachedCountChanged and keeps writing Mod.Amount for an unapplied mod all session.
    internal static void Teardown(IEnumerable<StatMod> mods)
    {
        if (mods == null) return;

        foreach (StatMod mod in mods) mod?.DestroyDynamicRules();
    }
}
