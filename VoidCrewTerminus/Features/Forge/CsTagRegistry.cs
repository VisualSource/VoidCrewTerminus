using Gameplay.Tags;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

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

    public static CsTag Weapon        => _weapon        ??= Resolve("Module_Category_Weapon");
    public static CsTag Defense       => _defense       ??= Resolve("Module_Category_Defense");
    public static CsTag PowerProvider => _powerProvider ??= Resolve("Module_Category_PowerProvider");
    public static CsTag Utility       => _utility       ??= Resolve("Module_Category_Utility");
    public static CsTag BuiltIn       => _builtIn       ??= Resolve("Module_Category_BuiltIn");

    // Mod-authored tag: stamped on every Forge-applied StatMod via TagsToAdd so that
    // future perk mods can narrow with RequiredLocalTags = [Forge_Upgraded].
    public static CsTag ForgeUpgraded
    {
        get
        {
            if (_forgeUpgraded != null) return _forgeUpgraded;
            _forgeUpgraded = ScriptableObject.CreateInstance<CsTag>();
            ((Object)_forgeUpgraded).name = "Tag_Forge_Upgraded";
            return _forgeUpgraded;
        }
    }

    private static CsTag Resolve(string name)
    {
        if (DataTable<CsTagTable>.Instance.TryGetTagByName(name, out var tag))
            return tag;
        BepinPlugin.Log.LogWarning($"[CsTagRegistry] Tag not found: {name}");
        return null;
    }
}
