using Gameplay.Tags;
using UnityEngine;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Utils;

public static class CsTagRegistry
{
    private static CsTag _weapon;
    private static CsTag _defense;
    private static CsTag _powerProvider;
    private static CsTag _utility;
    private static CsTag _builtIn;
    private static CsTag _forgeUpgraded;
    private static CsTag _forgeModule;
    private static CsTag _relic;
    private static CsTag _burdenRandomShutoff;

    private static CsTag _moduleMkIII;
    private static CsTag _moduleMkII;
    private static CsTag _moduleMkI;

    public static CsTag ModuleMkIII => _moduleMkIII ??= Resolve("Module_Mark_3");
    public static CsTag ModuleMkII => _moduleMkII ??= Resolve("Module_Mark_2");
    public static CsTag ModuleMkI => _moduleMkI ??= Resolve("Module_Mark_1");

    public static CsTag Weapon => _weapon ??= Resolve("Module_Category_Weapon");
    public static CsTag Defense => _defense ??= Resolve("Module_Category_Defense");
    public static CsTag PowerProvider => _powerProvider ??= Resolve("Module_Category_PowerProvider");
    public static CsTag Utility => _utility ??= Resolve("Module_Category_Utility");
    public static CsTag BuiltIn => _builtIn ??= Resolve("Module_Category_BuiltIn");

    // Stamped on every Forge-applied StatMod via TagsToAdd, so perk mods can narrow with
    // RequiredLocalTags.
    public static CsTag ForgeUpgraded
    {
        get
        {
            if (_forgeUpgraded != null) return _forgeUpgraded;
            _forgeUpgraded = ScriptableObject.CreateInstance<CsTag>();
            _forgeUpgraded.name = "Tag_Forge_Upgraded";
            return _forgeUpgraded;
        }
    }

    // Stamped onto the Forge CellModule when the behavior attaches, so instances are
    // identifiable by tag rather than prefab name everywhere past the initial build.
    public static CsTag ForgeModule
    {
        get
        {
            if (_forgeModule != null) return _forgeModule;
            _forgeModule = ScriptableObject.CreateInstance<CsTag>();
            _forgeModule.name = "Tag_Module_Type_UpgradeForge";
            return _forgeModule;
        }
    }

    private static CsTag _moduleLeechAttached;
    private static CsTag _shipHasLeeches;
    private static CsTag _shipLeechSwarmSmall;
    private static CsTag _shipLeechSwarmHeavy;
    private static CsTag _shipLeechSwarmCritical;

    // Stamped on a module while at least one Leech is attached to it.
    public static CsTag ModuleLeechAttached => Authored(ref _moduleLeechAttached, "Tag_Module_Leech_Attached");

    // Ship-level exposure. Bucketing runs per-client; none of this is networked.
    public static CsTag ShipHasLeeches => Authored(ref _shipHasLeeches, "Tag_Ship_HasLeeches");
    public static CsTag ShipLeechSwarmSmall => Authored(ref _shipLeechSwarmSmall, "Tag_Ship_LeechSwarm_Small");
    public static CsTag ShipLeechSwarmHeavy => Authored(ref _shipLeechSwarmHeavy, "Tag_Ship_LeechSwarm_Heavy");
    public static CsTag ShipLeechSwarmCritical => Authored(ref _shipLeechSwarmCritical, "Tag_Ship_LeechSwarm_Critical");

    // Bundled CsTag ScriptableObjects cannot resolve through the game's table, so
    // mod-authored tags are constructed at runtime. The Tag_ prefix matches the game's.
    private static CsTag Authored(ref CsTag cache, string name)
    {
        if (cache != null) return cache;
        cache = ScriptableObject.CreateInstance<CsTag>();
        cache.name = name;
        return cache;
    }

    // The same asset RuntimeCarryable stamps onto modded relics, and vanilla relics carry it
    // in their serialized CsTags, so reference equality identifies a relic without name
    // matching.
    public static CsTag Relic
    {
        get
        {
            if (_relic != null) return _relic;
            _relic = DataTable<RuntimeAssetTable>.Instance?.RelicTag?.Asset as CsTag;
            if (_relic == null)
                BepinPlugin.Log.LogWarning("[CsTagRegistry] RuntimeAssetTable.RelicTag not resolvable (yet).");
            return _relic;
        }
    }

    // Projected onto a burdened module by a zero-value marker StatMod in BuildMods, the same
    // way ForgeUpgraded is, so "is this module burdened?" is a tag query.
    public static CsTag BurdenRandomShutoff
    {
        get
        {
            if (_burdenRandomShutoff != null) return _burdenRandomShutoff;
            _burdenRandomShutoff = ScriptableObject.CreateInstance<CsTag>();
            _burdenRandomShutoff.name = "Tag_Burden_RandomShutoff";
            return _burdenRandomShutoff;
        }
    }

    public static CsTag BurdenTagFor(BurdenType burden) => burden switch
    {
        BurdenType.RandomShutoff => BurdenRandomShutoff,
        _ => null,
    };

    private static CsTag Resolve(string name)
    {
        if (DataTable<CsTagTable>.Instance.TryGetTagByName(name, out var tag))
            return tag;
        BepinPlugin.Log.LogWarning($"[CsTagRegistry] Tag not found: {name}");
        return null;
    }
}
