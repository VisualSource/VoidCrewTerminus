using Gameplay.Tags;
using UnityEngine;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Utils;

// Lazily resolves built-in CsTag ScriptableObjects from the game's CsTagTable and
// exposes the mod-authored Forge_Upgraded tag created at runtime.
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

    // Mod-authored tag: stamped on every Forge-applied StatMod via TagsToAdd so that
    // future perk mods can narrow with RequiredLocalTags = [Forge_Upgraded].
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

    // Mod-authored tag stamped onto the Upgrade Forge CellModule when the behavior
    // attaches (ForgeAttachHelper), so Forge instances are identifiable by tag
    // instead of prefab name everywhere past the initial build.
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

    // The game's canonical relic tag. This is the same asset RuntimeCarryable stamps
    // onto RuntimeAssets-modded relics (RuntimeAssetTable.RelicTag), and vanilla relic
    // carryables carry it in their serialized CsTags — so a reference-equality check
    // against CarryableObject.CsTags identifies relics without name matching.
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

    // Mod-authored burden tag: stamped on modules that carry a RandomShutoff burden
    // (Phase 7-C). Following the ForgeUpgraded pattern — a zero-value marker
    // StatMod in BuildMods projects this tag onto the module for game-visible
    // queries ("is this module burdened?" as a one-liner).
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

    // Map a BurdenType enum to its CsTag. Returns null for None.
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
