using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RuntimeAssets;
using UnityEngine;
using UnityEngine.UIElements;
using VC.Common;
using VC.Common.Carryables;
using VC.Common.PlayerShip;
using VoidCrewTerminus.Forge;
using VoidCrewTerminus.ModuleKit;
using VoidCrewTerminus.Utils;

namespace VoidCrewTerminus;

// ".metem_ext" avoids the game's own *.metem auto-loader (RuntimeAssetLoadingService),
// which would race this loader and choke on our custom prefab.
//
// Bundle discovery and routing only — what it takes to make a bundle prefab work as a
// ship module lives in ModuleKit/, configured by ForgeModule below.
public class AssetLoader
{
    // Bundles this assembly loaded, for hot-reload teardown.
    private static readonly List<AssetBundle> _loadedBundles = new();

    // Unity-authored UI Toolkit assets for ForgeScreenDisplay, captured by name
    // from the bundle. Null until the bundle loads; ForgeScreenDisplay logs and
    // no-ops if either is still missing when a Forge builds its screen.
    private static VisualTreeAsset _forgeScreenVisualTree;
    private static PanelSettings _forgeScreenPanelSettingsTemplate;

    private static readonly CustomModuleDefinition ForgeModule = new()
    {
        ModulePrefabName = UpgradeForgeBehavior.PrefabName,
        BuildBoxPrefabName = UpgradeForgeBehavior.BuildBoxPrefabName,
        BuildBoxDisplayName = "Upgrade Forge BuildBox",
        PreferredDonorTag = () => CsTagRegistry.Utility,
    };

    public static VisualTreeAsset ForgeScreenVisualTree => _forgeScreenVisualTree;
    public static PanelSettings ForgeScreenPanelSettingsTemplate => _forgeScreenPanelSettingsTemplate;

    // Hot-reload teardown. Unload(false) frees the bundle handle only — live assets
    // (modules/materials already in use) keep working.
    public static void UnloadBundles()
    {
        foreach (var bundle in _loadedBundles)
        {
            if (bundle != null) bundle.Unload(false);
        }
        _loadedBundles.Clear();
        CustomModuleRegistry.Clear();
    }

    public static void TryLoadAssetBundlesNextToDLL()
    {
        CustomModuleRegistry.Define(ForgeModule);

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var dllPath = !string.IsNullOrWhiteSpace(asm.Location) ? asm.Location : new Uri(asm.CodeBase).LocalPath;

            var dir = Path.GetDirectoryName(dllPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                BepinPlugin.Log.LogError($"[AssetLoader] Could not resolve DLL Directory (dllPath='{dllPath}')");
                return;
            }

            BepinPlugin.Log.LogDebug($"[AssetLoader] Scanning for asset bundle manifests in: {dir}");

            foreach (var filepath in Directory.EnumerateFiles(dir, "*.metem_ext", SearchOption.TopDirectoryOnly))
            {
                var filename = Path.GetFileName(filepath);
                try
                {
                    LoadBundle(filepath);
                    BepinPlugin.Log.LogDebug($"[AssetLoader] Loaded asset bundle: {filename}");
                }
                catch (Exception e)
                {
                    BepinPlugin.Log.LogError($"[AssetLoader] Error while loading '{filename}': {e}");
                }
            }
        }
        catch (Exception e)
        {
            BepinPlugin.Log.LogError($"[AssetLoader] Failed loading asset bundles: {e}");
        }
    }

    // Routes bundle content to the game's RuntimeAssets pipeline or to ModuleKit,
    // depending on what the game's converter can handle (only carryables/cosmetics).
    private static void LoadBundle(string filepath)
    {
        var bundle = AssetBundle.LoadFromFile(filepath);
        if (!(bool)bundle)
        {
            // A previous (hot-reloaded) copy of this plugin may still hold the
            // bundle; reuse the in-memory instance rather than failing.
            var name = Path.GetFileName(filepath);
            foreach (var loaded in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (loaded.name == name) { bundle = loaded; break; }
            }
        }
        if (!(bool)bundle)
        {
            BepinPlugin.Log.LogError($"[AssetLoader] Failed to load AssetBundle at {filepath}");
            return;
        }
        _loadedBundles.Add(bundle);

        foreach (var asset in bundle.LoadAllAssets())
        {
            // A VisualTreeAsset/PanelSettings isn't a VoidCrewAsset-tagged GameObject,
            // so these two are matched by asset name instead of the usual VCA lookup.
            if (asset is VisualTreeAsset vta && vta.name == UpgradeForgeBehavior.ForgeScreenLayoutName)
            {
                _forgeScreenVisualTree = vta;
                continue;
            }
            if (asset is PanelSettings ps && ps.name == UpgradeForgeBehavior.ForgeScreenPanelSettingsName)
            {
                _forgeScreenPanelSettingsTemplate = ps;
                continue;
            }
            if (asset is GameObject go &&
                go.GetComponent<VoidCrewAsset>() is VoidCrewAsset vca &&
                go.GetComponent<CarryableBaseAsset>() == null &&
                go.GetComponent<PlayerShipVisuals>() == null)
            {
                CustomModuleRegistry.SubmitPrefab(go, vca);
                continue;
            }
            RuntimeAssetsAPI.LoadAsset(asset);
        }

        CustomModuleRegistry.LinkBuildBoxRefs();
    }
}
