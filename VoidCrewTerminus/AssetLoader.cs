using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CG.Game.Configuration;
using CG.Graphics;
using CG.Ship.Modules;
using CG.Ship.Object;
using HarmonyLib;
using Gameplay.Power;
using Gameplay.Utilities;
using Photon.Pun;
using ResourceAssets;
using RuntimeAssets;
using UnityEngine;
using VC.Common;
using VC.Common.Carryables;
using VC.Common.PlayerShip;
using VoidCrewTerminus.Forge;

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

    // Bundles this assembly loaded, for hot-reload teardown.
    private static readonly List<AssetBundle> _loadedBundles = new();

    public static GameObject GetModulePrefab(string name) =>
        _modulePrefabs.TryGetValue(name, out var prefab) ? prefab : null;

    // Hot-reload teardown (ScriptEngine): release the bundle file handles so the
    // reloaded assembly can LoadFromFile again. Unload(false) keeps already-created
    // assets alive — live modules, registered prefabs and materials keep working;
    // only the bundle handle is freed. RuntimeAssetsRegister entries are left in
    // place: re-registration is skipped by the HasAsset guard on reload.
    public static void UnloadBundles()
    {
        foreach (var bundle in _loadedBundles)
        {
            if (bundle != null) bundle.Unload(false);
        }
        _loadedBundles.Clear();
        _modulePrefabs.Clear();
    }

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
            if (asset is GameObject go &&
                go.GetComponent<VoidCrewAsset>() is VoidCrewAsset vca &&
                go.GetComponent<CarryableBaseAsset>() == null &&
                go.GetComponent<PlayerShipVisuals>() == null)
            {
                RelinkBundleShaders(go);
                if (go.name == UpgradeForgeBehavior.BuildBoxPrefabName)
                    GraftBuildBoxComponents(go);
                else
                    GraftModuleComponents(go);
                _modulePrefabs[go.name] = go;
                RegisterModulePrefab(go, vca);
                continue;
            }
            RuntimeAssetsAPI.LoadAsset(asset);
        }

        LinkForgeBuildBox();
    }

    // AssetBundle-loaded materials reference a shader *copy* that wasn't compiled
    // with the same keyword/variant set as the one baked into the player build —
    // a well-known Unity/HDRP AssetBundle pitfall. The material's properties are
    // all correct, but it renders solid black under any light. RuntimeAssets'
    // own PlayerShipVisualsLoader hits the same issue for bundle-loaded ship
    // cosmetics and fixes it by re-resolving each material's shader by name
    // against the live build; the game's RuntimeAssetConverter never applies
    // that fix to module prefabs, and neither did we until now. Runs on
    // sharedMaterials since this operates on the prefab asset, not an instance.
    private static void RelinkBundleShaders(GameObject prefab)
    {
        foreach (var rend in prefab.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var mat in rend.sharedMaterials)
            {
                if (mat == null || mat.shader == null) continue;
                mat.shader = Shader.Find(mat.shader.name);
            }
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

        // Visual-culling parity with vanilla modules, mirroring their prefab layout:
        // the "Interior" group gets an interior-flavored OcclusionNode (component
        // defaults: zone None self-resolves after install; hidden while EVA / in a
        // turret / helm third-person), and the "Exterior" group gets an
        // Exterior-zone node that stays visible from space and turrets but is
        // culled while walking the interior. Bundles without the split fall back
        // to a single interior-flavored node on the root. The nodes' renderer
        // caches skip anything under a CarryableObject, so docked relics and the
        // BuildBox are unaffected.
        if (prefab.GetComponentInChildren<OcclusionNode>(true) == null)
        {
            var interior = prefab.transform.Find("Interior");
            var exterior = prefab.transform.Find("Exterior");
            if (interior == null && exterior == null)
            {
                prefab.AddComponent<OcclusionNode>();
                BepinPlugin.Log.LogDebug($"[AssetLoader] Grafted root OcclusionNode onto {prefab.name} (no Interior/Exterior split)");
            }
            else
            {
                if (interior != null)
                    interior.gameObject.AddComponent<OcclusionNode>();
                if (exterior != null)
                {
                    var node = exterior.gameObject.AddComponent<OcclusionNode>();
                    AccessTools.Field(typeof(OcclusionNode), "occlusionZone").SetValue(node, OcclusionZoneType.Exterior);
                    AccessTools.Field(typeof(OcclusionNode), "hideOnLocalPlayerIsInSpace").SetValue(node, false);
                    AccessTools.Field(typeof(OcclusionNode), "hideOnLocalPlayerIsInTurret").SetValue(node, false);
                }
                BepinPlugin.Log.LogDebug($"[AssetLoader] Grafted OcclusionNodes onto {prefab.name} (interior={(interior != null)}, exterior={(exterior != null)})");
            }
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

    // Vanilla BuildBox prefabs carry a Rigidbody/Collider/PhotonView and a real
    // BuildBox script from the game project; a bundle-authored prefab can't carry
    // BuildBox at all — it isn't part of the modding SDK's exposed type surface
    // (com.hutlihut.void_crew_common has no gameplay component types, only
    // markers/data — confirmed by listing its Runtime/Code — same reason
    // GraftModuleComponents exists for CellModule). Graft minimal stand-ins here,
    // same pattern. moduleRef is wired separately in LinkForgeBuildBox, once both
    // this prefab and the module prefab have registered GUIDs.
    private static void GraftBuildBoxComponents(GameObject prefab)
    {
        var box = prefab.GetComponent<BuildBox>();
        if (box == null)
        {
            box = prefab.AddComponent<BuildBox>();
            BepinPlugin.Log.LogDebug($"[AssetLoader] Grafted BuildBox onto {prefab.name}");
        }

        // CarryableObject.Awake reads GetComponent<Rigidbody>() unconditionally
        // (no RequireComponent, but later carry/physics code assumes it exists).
        if (prefab.GetComponent<Rigidbody>() == null)
        {
            var rb = prefab.AddComponent<Rigidbody>();
            rb.useGravity = false;
            BepinPlugin.Log.LogDebug($"[AssetLoader] Grafted Rigidbody onto {prefab.name}");
        }

        var view = prefab.GetComponent<PhotonView>();
        if (view == null)
        {
            view = prefab.AddComponent<PhotonView>();
            view.OwnershipTransfer = OwnershipOption.Takeover;
            view.Synchronization = ViewSynchronization.UnreliableOnChange;
            BepinPlugin.Log.LogDebug($"[AssetLoader] Grafted PhotonView onto {prefab.name}");
        }
        if (view.observableSearch == PhotonView.ObservableSearch.Manual &&
            (view.ObservedComponents == null || view.ObservedComponents.Count == 0))
        {
            view.ObservedComponents = new List<Component> { box };
        }
    }

    // Cross-links the Forge module and its dedicated BuildBox once both have
    // loaded and registered — each needs the other's GUID (moduleRef on the box,
    // BuildBoxRef on the module, both CloneStarObjectRef). Safe to call after
    // every bundle: idempotent (re-setting the same GUIDs is harmless), and
    // _modulePrefabs is static, so whichever LoadBundle call sees both prefabs
    // present is the one that actually links them — order-independent even if a
    // future bundle split puts the two prefabs in different files.
    private static void LinkForgeBuildBox()
    {
        if (!_modulePrefabs.TryGetValue(UpgradeForgeBehavior.PrefabName, out var modulePrefab)) return;
        if (!_modulePrefabs.TryGetValue(UpgradeForgeBehavior.BuildBoxPrefabName, out var boxPrefab)) return;

        var moduleVca = modulePrefab.GetComponent<VoidCrewAsset>();
        var boxVca = boxPrefab.GetComponent<VoidCrewAsset>();
        if (moduleVca == null || boxVca == null ||
            string.IsNullOrEmpty(moduleVca.AssetGuid) || string.IsNullOrEmpty(boxVca.AssetGuid))
        {
            BepinPlugin.Log.LogWarning("[AssetLoader] Forge module/BuildBox present but missing a stamped AssetGuid — cannot link.");
            return;
        }

        var moduleGuid = new GUIDUnion(moduleVca.AssetGuid);
        var boxGuid = new GUIDUnion(boxVca.AssetGuid);

        var box = boxPrefab.GetComponent<BuildBox>();
        box.moduleRef ??= new CloneStarObjectRef();
        box.moduleRef.AssetGuid = moduleGuid;
        box.moduleRef.IsRuntime = true;

        var cell = modulePrefab.GetComponent<CellModule>();
        cell.BuildBoxRef ??= new CloneStarObjectRef();
        cell.BuildBoxRef.AssetGuid = boxGuid;
        cell.BuildBoxRef.IsRuntime = true;

        BepinPlugin.Log.LogInfo($"[AssetLoader] Linked Forge BuildBox {boxGuid.AsHex()} <-> module {moduleGuid.AsHex()}");
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
}
