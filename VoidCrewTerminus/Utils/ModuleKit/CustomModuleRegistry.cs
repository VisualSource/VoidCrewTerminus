using System.Collections.Generic;
using CG.Ship.Modules;
using CG.Ship.Object;
using Client.Player.Interactions;
using ResourceAssets;
using UnityEngine;
using VC.Common;

namespace VoidCrewTerminus.ModuleKit;

public static class CustomModuleRegistry
{
    private static readonly List<RegisteredModule> _modules = new();

    // Module prefabs the game's RuntimeAssetConverter can't handle (it only covers
    // carryables/cosmetics).
    private static readonly Dictionary<string, GameObject> _modulePrefabs = new();

    private static readonly Dictionary<string, GameObject> _boxTemplates = new();

    private static bool _containersPopulated;

    public static void Define(CustomModuleDefinition definition)
    {
        if (FindByPrefabName(definition.ModulePrefabName) != null) return;
        _modules.Add(new RegisteredModule(definition));
    }

    public static void SubmitPrefab(GameObject prefab, VoidCrewAsset vca)
    {
        foreach (var module in _modules)
        {
            if (prefab.name != module.Definition.BuildBoxPrefabName) continue;
            module.CaptureBuildBoxMarker(prefab, vca);
            return;
        }

        ModulePrefabGrafter.RelinkShaders(prefab);
        ModulePrefabGrafter.Graft(prefab);
        _modulePrefabs[prefab.name] = prefab;

        if (string.IsNullOrEmpty(vca.AssetGuid))
        {
            BepinPlugin.Log.LogError($"[ModuleKit] Module prefab '{prefab.name}' has no AssetGuid — re-export the bundle (the export tool stamps it).");
            return;
        }
        VanillaAssetRegistrar.RegisterAssetIfAbsent(new GUIDUnion(vca.AssetGuid), prefab, vca.Name);
    }

    // A post-pass: a module prefab and its BuildBox marker arrive from the bundle in
    // either order.
    public static void LinkBuildBoxRefs()
    {
        foreach (var module in _modules)
        {
            if (_modulePrefabs.TryGetValue(module.Definition.ModulePrefabName, out var modulePrefab))
                module.LinkBuildBoxRef(modulePrefab);
        }
    }

    // Can't run at plugin Awake: ResourcePaths, the vanilla containers and any live donor
    // BuildBox all come up later (same too-early trap as PhotonNetwork — see CLAUDE.md).
    // Idempotent, so every entry point needing a BuildBox calls it.
    public static void EnsureTemplatesReady()
    {
        EnsureContainersPopulated();

        foreach (var module in _modules)
        {
            var boxName = module.Definition.BuildBoxPrefabName;
            if (string.IsNullOrEmpty(boxName) || _boxTemplates.ContainsKey(boxName)) continue;
            if (!_modulePrefabs.TryGetValue(module.Definition.ModulePrefabName, out var modulePrefab)) continue;

            var template = module.TryBuildBuildBoxTemplate(modulePrefab);
            if (template != null) _boxTemplates[boxName] = template;
        }
    }

    public static void Clear()
    {
        // A BuildBox template is a live clone, not a bundle asset — bundle.Unload won't
        // touch it, so it leaks across hot-reloads unless destroyed here.
        foreach (var template in _boxTemplates.Values)
        {
            if (template != null) Object.Destroy(template);
        }
        _boxTemplates.Clear();
        _modulePrefabs.Clear();
        _modules.Clear();
        _containersPopulated = false;
    }

    // RegisterAssetIfAbsent only covers RuntimeAssetsRegister, but vanilla code does raw
    // container lookups with no null-check on a miss — register here rather than patch
    // every call site.
    private static void EnsureContainersPopulated()
    {
        if (_containersPopulated) return;

        foreach (var prefab in _modulePrefabs.Values)
        {
            var vca = prefab.GetComponent<VoidCrewAsset>();
            if (vca == null || string.IsNullOrEmpty(vca.AssetGuid)) continue;

            var guid = new GUIDUnion(vca.AssetGuid);
            var definition = FindByPrefabName(prefab.name)?.Definition;

            VanillaAssetRegistrar.RegisterObjectDef(guid, prefab.name, ContextInfo.Create(vca.Icon, vca.Name, vca.Description));

            if (prefab.GetComponent<CellModule>() != null)
                VanillaAssetRegistrar.RegisterModuleDef(guid, prefab.name, definition?.Category ?? ECategory.Support);

            VanillaAssetRegistrar.RegisterRarity(guid, prefab.name, definition?.Rarity ?? RarityType.Common);
        }

        _containersPopulated = true;
    }

    private static RegisteredModule FindByPrefabName(string prefabName)
    {
        foreach (var module in _modules)
        {
            if (module.Definition.ModulePrefabName == prefabName) return module;
        }
        return null;
    }
}
