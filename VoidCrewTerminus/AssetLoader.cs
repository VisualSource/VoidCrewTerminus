using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CG.Game.Configuration;
using CG.Graphics;
using CG.Ship.Modules;
using Gameplay.Power;
using Gameplay.Utilities;
using Photon.Pun;
using ResourceAssets;
using RuntimeAssets;
using UnityEngine;
using VC.Common;
using VC.Common.Carryables;
using VC.Common.PlayerShip;

namespace VoidCrewTerminus;

// Loads this mod's asset bundles.
//
// The bundles use the ".terminus" extension rather than ".metem" on purpose: the
// game's RuntimeAssetLoadingService detects BepInEx via the doorstop command line
// and auto-loads every *.metem under the plugins directory. That auto-load would
// race ours ("AssetBundle already loaded") and shove the module prefab through the
// game's converter, which can't handle it ("Failed to convert runtime asset ...").
// A private extension keeps this mod the bundle's sole owner. If we ever ship
// assets the game CAN convert on its own (RuntimeCarryable-style carryables or
// cosmetics), those belong in a real *.metem that this loader must NOT touch.
public class AssetLoader
{
    // Module-cell prefabs extracted from our bundles, keyed by prefab name.
    // The game's RuntimeAssetConverter only understands two prefab shapes —
    // carryables (CarryableBaseAsset) and ship cosmetics (PlayerShipVisuals).
    // Anything else (our UpgradeForgeModuleCell) would fail conversion, so those
    // prefabs are kept mod-side and instantiated by our own code instead.
    private static readonly Dictionary<string, GameObject> _modulePrefabs = new();

    public static GameObject GetModulePrefab(string name) =>
        _modulePrefabs.TryGetValue(name, out var prefab) ? prefab : null;

    public static void TryLoadAssetBundlesNextToDLL()
    {
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

    // Splits bundle content between the game's RuntimeAssets pipeline (carryables,
    // cosmetics, scriptable objects) and our own module-prefab registry. The bundle
    // is intentionally never unloaded — both converted assets and extracted prefabs
    // keep referencing its content.
    private static void LoadBundle(string filepath)
    {
        var bundle = AssetBundle.LoadFromFile(filepath);
        if (!(bool)bundle)
        {
            BepinPlugin.Log.LogError($"[AssetLoader] Failed to load AssetBundle at {filepath}");
            return;
        }

        foreach (var asset in bundle.LoadAllAssets())
        {
            if (asset is GameObject go &&
                go.GetComponent<VoidCrewAsset>() is VoidCrewAsset vca &&
                go.GetComponent<CarryableBaseAsset>() == null &&
                go.GetComponent<PlayerShipVisuals>() == null)
            {
                RemapShaders(go);
                GraftModuleComponents(go);
                _modulePrefabs[go.name] = go;
                RegisterModulePrefab(go, vca);
                continue;
            }
            RuntimeAssetsAPI.LoadAsset(asset);
        }
    }

    // Vanilla module prefabs carry a PhotonView and a fully configured CellModule
    // from the game project; bundle prefabs can't (game script components don't
    // survive the bundle pipeline). Graft minimal stand-ins at load time so the
    // prefab survives PhotonNetwork.Instantiate and the BuildBox build flow:
    //   - CellModule with 1×1×1 BuildingConstraints (BuildBox.GetBuildSize reads it
    //     the moment the box is socketed) and deconstruction forbidden — there is no
    //     Forge BuildBoxRef yet, and Deconstruct would NRE on it.
    //   - MaxHitPoints / Invulnerability initialized — OrbitObject.Start converts
    //     MaxHitPoints unconditionally, which NREs on a default-constructed component.
    //   - PhotonView observing the CellModule, since network instantiation requires
    //     a view on the prefab root.
    // Components added to a loaded asset don't run Awake/Start; those fire on the
    // instances the game creates from it.
    private static void GraftModuleComponents(GameObject prefab)
    {
        var cell = prefab.GetComponent<CellModule>();
        if (cell == null)
        {
            cell = prefab.AddComponent<CellModule>();
            cell.BuildingConstraints = BuildingConstraints.Default;
            cell.BuildingConstraints.AllowDeconstruction = false;
            cell.TimeToBoot = 1f;
            BepinPlugin.Log.LogDebug($"[AssetLoader] Grafted CellModule onto {prefab.name}");
        }
        cell.MaxHitPoints ??= new ModifiableFloat { BaseValue = 750f };
        cell.Invulnerability ??= new ModifiableInt();

        // BuildSocket.SetModule dereferences module.PowerDrain unconditionally
        // (module.PowerDrain.ConnectToPowerSystem), so the module needs a real
        // drain. PowerWanted stays at 0 — the Forge is free to run — and
        // AutoPowerOn brings it up as soon as the socket connects it. PowerDrain's
        // own Awake wires DrainUser back to the CellModule via IPowerDrainUser.
        var drain = prefab.GetComponent<PowerDrain>();
        if (drain == null)
        {
            drain = prefab.AddComponent<PowerDrain>();
            drain.PowerWanted = new ModifiableInt();
            drain.IsOn = false;
            drain.AutoPowerOn = true;
            BepinPlugin.Log.LogDebug($"[AssetLoader] Grafted PowerDrain onto {prefab.name}");
        }
        if (cell.PowerDrain == null) cell.PowerDrain = drain;

        // Visual-culling parity with vanilla modules: an OcclusionNode enrolls the
        // module's renderers in the ship's interior occlusion (hidden while EVA,
        // in a turret, or in helm third-person). The component defaults are exactly
        // right for an interior module — zone None self-resolves to the parent or
        // nearest node after install, and both hide-flags start true. Note: its
        // renderer cache skips anything under a CarryableObject, so docked relics
        // and the BuildBox are unaffected.
        if (prefab.GetComponent<OcclusionNode>() == null)
        {
            prefab.AddComponent<OcclusionNode>();
            BepinPlugin.Log.LogDebug($"[AssetLoader] Grafted OcclusionNode onto {prefab.name}");
        }

        var view = prefab.GetComponent<PhotonView>();
        if (view == null)
        {
            view = prefab.AddComponent<PhotonView>();
            view.OwnershipTransfer = OwnershipOption.Takeover;
            view.Synchronization = ViewSynchronization.UnreliableOnChange;
            BepinPlugin.Log.LogDebug($"[AssetLoader] Grafted PhotonView onto {prefab.name}");
        }
        // Whether grafted above or authored in the editor, guarantee the view
        // observes the module: a Manual-search view with an empty list would sync
        // nothing (the editor can't reference the runtime-grafted CellModule).
        if (view.observableSearch == PhotonView.ObservableSearch.Manual &&
            (view.ObservedComponents == null || view.ObservedComponents.Count == 0))
        {
            view.ObservedComponents = new List<Component> { cell };
        }
    }

    // Registers the prefab in the game's RuntimeAssetsRegister under the GUID the
    // export tool stamped onto its VoidCrewAsset marker. This is the same register
    // the game's converter uses for carryables, and it is what makes the GUID
    // resolvable everywhere downstream: CustomObjectPool (PUN instantiation via
    // "#guid" prefab ids, used by BuildBox.BuildModule), ResourceAssetRef lookups
    // with IsRuntime = true, and the !forgespawn dev command's register walk.
    private static void RegisterModulePrefab(GameObject prefab, VoidCrewAsset vca)
    {
        if (string.IsNullOrEmpty(vca.AssetGuid))
        {
            BepinPlugin.Log.LogError($"[AssetLoader] Module prefab '{prefab.name}' has no AssetGuid — re-export the bundle (the export tool stamps it).");
            return;
        }

        var guid = new GUIDUnion(vca.AssetGuid);
        if (RuntimeAssetsRegister.Instance.HasAsset(guid)) return;

        RuntimeAssetsRegister.Instance.RegisterAsset(
            guid, prefab, SessionModificationEffect.IsNetworkSpawned,
            new RuntimeAssetInfo { Name = prefab.name, DisplayName = vca.Name });
        BepinPlugin.Log.LogInfo($"[AssetLoader] Registered module prefab: {prefab.name} ({guid.AsHex()})");
    }

    // Bundle-embedded shaders are compiled for the build target the bundle was
    // exported on; when that doesn't match the game (e.g. a Linux-editor export vs
    // the D3D11 game build → "Desired shader compiler platform 4 is not available"),
    // materials render broken. Rebinding each material to the game's own copy of the
    // same-named shader fixes rendering regardless of export platform.
    private static void RemapShaders(GameObject prefab)
    {
        foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null || mat.shader == null) continue;
                var gameShader = Shader.Find(mat.shader.name);
                if (gameShader != null && gameShader != mat.shader)
                    mat.shader = gameShader;
                else if (gameShader == null)
                    BepinPlugin.Log.LogWarning($"[AssetLoader] No game shader named '{mat.shader.name}' for material '{mat.name}' — it may render broken.");
            }
        }
    }
}
