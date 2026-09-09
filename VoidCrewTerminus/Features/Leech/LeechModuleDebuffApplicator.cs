using System.Collections.Generic;
using CG.Ship.Modules;
using Gameplay.Tags;
using Gameplay.Utilities;
using HarmonyLib;
using VoidCrewTerminus.Utils;

namespace VoidCrewTerminus.Leech;

// One IModifierSource for every Leech on the ship, ref-counted per module.
//
// The design splits the two threat axes deliberately: the effectiveness debuff is
// NON-stacking (three Leeches on one module debuff it once) while module HP damage
// stacks (each Leech ticks independently). Ref counting here is what implements the
// first half — LeechController owns the second.
//
// Deliberately carries no ModDynamicCondition. A condition's Init() overwrites
// Mod.Source to itself, and UpdateMods detaches by source, so a condition flipping
// on one mod would strip this whole shared bundle while leaving its siblings in
// ActiveModifiers, never to be re-added. Conditions belong on per-mod unique
// sources; see the Leech Dynamic Source work.
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

    // Negative AdditiveMultiplier across every category bundle. A module only
    // carries one category tag, so RequiredTags means only its own group activates
    // and the rest sit inert — the same shape the Forge uses for its level bonus.
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

        // The category groups above bind to stats on child collections, so without
        // a mod on the module's own collection the tag never reaches its LocalTags.
        // A zero addend on MaxHitPoints — which every OrbitObject registers —
        // carries the tag without altering anything.
        mods.Add(new StatMod(
            new FloatModifier(0f, ModifierType.PrimaryAddend, this),
            StatType.MaxHitPoints.Id,
            new ModTagConfiguration { TagsToAdd = new[] { CsTagRegistry.ModuleLeechAttached } }));

        return mods;
    }

    // The game rebuilds runtimeTags only inside UpdateMods, which fires on a mod's
    // active/inactive transition — ApplyModifiers alone never triggers it, so
    // TagsToAdd would never surface in LocalTags(). Mirror it in directly; the
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
