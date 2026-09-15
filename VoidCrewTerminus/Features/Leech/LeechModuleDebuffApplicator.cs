using System.Collections.Generic;
using CG.Ship.Modules;
using Gameplay.Tags;
using Gameplay.Utilities;
using HarmonyLib;
using VoidCrewTerminus.Utils;

namespace VoidCrewTerminus.Leech;

// One IModifierSource shared by every Leech on the ship, ref-counted per module. The two
// threat axes split deliberately: the effectiveness debuff is NON-stacking (three Leeches on
// one module debuff it once) while module HP damage stacks. Ref counting is the first half.
//
// Deliberately carries no ModDynamicCondition: a condition's Init() overwrites Mod.Source to
// itself and UpdateMods detaches by source, so one condition flipping would strip this whole
// shared bundle and leave its siblings in ActiveModifiers, never re-added.
internal sealed class LeechModuleDebuffApplicator : IModifierSource
{
    internal static LeechModuleDebuffApplicator Instance { get; } = new();

    private static readonly AccessTools.FieldRef<StatTagCollection, List<CsTag>> RuntimeTagsRef =
        AccessTools.FieldRefAccess<StatTagCollection, List<CsTag>>("runtimeTags");

    private readonly Dictionary<CellModule, int> _refCounts = new();

    private LeechModuleDebuffApplicator() { }

    internal int AttachedTo(CellModule module)
        => module != null && _refCounts.TryGetValue(module, out int count) ? count : 0;

    internal void Attach(CellModule module, float magnitude)
    {
        if (module == null) return;

        _refCounts.TryGetValue(module, out int count);
        _refCounts[module] = count + 1;

        // Only the first Leech applies the bundle; the rest just raise the count.
        if (count > 0) return;

        module.Stats.ApplyModifiers(BuildMods(magnitude), this);
        SyncTag(module, present: true);

        BepinPlugin.Log.LogDebug($"[Leech] debuff applied to {module.name} at -{magnitude:P0}.");
    }

    internal void Detach(CellModule module)
    {
        if (module == null || !_refCounts.TryGetValue(module, out int count)) return;

        count--;
        if (count > 0)
        {
            _refCounts[module] = count;
            return;
        }

        _refCounts.Remove(module);
        module.Stats.RemoveModifier(this);
        SyncTag(module, present: false);

        BepinPlugin.Log.LogDebug($"[Leech] debuff cleared from {module.name}.");
    }

    // Ship death and sector teardown drop modules without detaching leech by leech.
    internal void Reset()
    {
        foreach (KeyValuePair<CellModule, int> entry in _refCounts)
        {
            if (entry.Key == null) continue;
            entry.Key.Stats.RemoveModifier(this);
            SyncTag(entry.Key, present: false);
        }

        _refCounts.Clear();
    }

    // Negative AdditiveMultiplier across every category bundle. A module carries one category
    // tag, so RequiredTags means only its own group activates and the rest sit inert.
    private List<StatMod> BuildMods(float magnitude)
    {
        var mods = new List<StatMod>();
        float amount = -magnitude;

        foreach ((CsTag tag, StatType[] stats) in ModuleStatBundles.All())
        {
            if (tag == null) continue;

            var tagCfg = new ModTagConfiguration
            {
                RequiredTags = new[] { tag },
                TagsToAdd = new[] { CsTagRegistry.ModuleLeechAttached },
            };

            foreach (StatType stat in stats)
            {
                // Int-backed stats reject a fractional multiplier.
                if (StatType.IsInt(stat.Id)) continue;
                mods.Add(new StatMod(new FloatModifier(amount, ModifierType.AdditiveMultiplier, this), stat.Id, tagCfg));
            }
        }

        // The category groups bind to stats on child collections, so a zero addend on
        // MaxHitPoints, which every OrbitObject registers, is what carries the tag onto the
        // module's own collection.
        mods.Add(new StatMod(
            new FloatModifier(0f, ModifierType.PrimaryAddend, this),
            StatType.MaxHitPoints.Id,
            new ModTagConfiguration { TagsToAdd = new[] { CsTagRegistry.ModuleLeechAttached } }));

        return mods;
    }

    // runtimeTags is rebuilt only inside UpdateMods, which fires on a mod's active/inactive
    // transition, so TagsToAdd would never surface in LocalTags(). Mirrored in directly; the
    // zero-value marker mod above keeps it alive across any later rebuild.
    private static void SyncTag(CellModule module, bool present)
    {
        List<CsTag> tags = RuntimeTagsRef(module.Stats);
        if (tags == null) return;

        CsTag tag = CsTagRegistry.ModuleLeechAttached;

        if (present)
        {
            if (!tags.Contains(tag)) tags.Add(tag);
        }
        else
        {
            tags.RemoveAll(t => t == tag);
        }
    }
}
