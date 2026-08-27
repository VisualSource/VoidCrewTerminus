using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CG;
using CG.Game.Configuration;
using CG.Graphics;
using CG.Ship.Modules;
using CG.Ship.Object;
using Client.Player.Interactions;
using HarmonyLib;
using Gameplay.Power;
using Gameplay.Utilities;
using Photon.Pun;
using ResourceAssets;
using RuntimeAssets;
using UnityEngine;
using UnityEngine.UIElements;
using VC.Common;
using VC.Common.Carryables;
using VC.Common.PlayerShip;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus;

// ".metem_ext" avoids the game's own *.metem auto-loader (RuntimeAssetLoadingService),
// which would race this loader and choke on our custom prefab.
public class AssetLoader
{
    // Module prefabs the game's RuntimeAssetConverter can't handle (only carryables/cosmetics),
    // keyed by name. Also holds the cloned BuildBox template once built (see EnsureBuildBoxTemplateReady).
    private static readonly Dictionary<string, GameObject> _modulePrefabs = new();

    // Bundles this assembly loaded, for hot-reload teardown.
    private static readonly List<AssetBundle> _loadedBundles = new();

    // Stable BuildBox identity other code keys off (CellModule.BuildBoxRef, BossDefeatHook,
    // RuntimeAssetsRegister) — captured at LoadBundle time from the marker prefab's GUID.
    private static GUIDUnion? _buildBoxGuid;

    // Marker prefab's authored flavor text, captured before it's discarded;
    // EnsureBuildBoxTemplateReady prefers these over the donor's own text.
    private static string _buildBoxOwnName;
    private static string _buildBoxOwnDescription;
    private static Sprite _buildBoxOwnIcon;

    // Unity-authored UI Toolkit assets for ForgeScreenDisplay, captured by name
    // from the bundle (see UpgradeForgeBehavior.ForgeScreenLayoutName/
    // ForgeScreenPanelSettingsName). Null until the bundle loads; ForgeScreenDisplay
    // logs and no-ops if either is still missing when a Forge builds its screen.
    private static VisualTreeAsset _forgeScreenVisualTree;
    private static PanelSettings _forgeScreenPanelSettingsTemplate;

    public static GameObject GetModulePrefab(string name) =>
        _modulePrefabs.TryGetValue(name, out var prefab) ? prefab : null;

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
        // The BuildBox template is a live clone, not a bundle asset — bundle.Unload
        // won't touch it, so it leaks across hot-reloads unless destroyed here.
        if (_modulePrefabs.TryGetValue(UpgradeForgeBehavior.BuildBoxPrefabName, out var boxTemplate) && boxTemplate != null)
            UnityEngine.Object.Destroy(boxTemplate);
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

    // Routes bundle content to the game's RuntimeAssets pipeline or our own
    // module-prefab registry, depending on what the game's converter can handle.
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
                if (go.name == UpgradeForgeBehavior.BuildBoxPrefabName)
                {
                    // This placeholder is never instantiated (see EnsureBuildBoxTemplateReady) —
                    // only its GUID and authored flavor text are kept; the GameObject is dropped.
                    if (!string.IsNullOrEmpty(vca.AssetGuid))
                        _buildBoxGuid = new GUIDUnion(vca.AssetGuid);
                    else
                        BepinPlugin.Log.LogError($"[AssetLoader] BuildBox prefab '{go.name}' has no AssetGuid — re-export the bundle (the export tool stamps it).");
                    _buildBoxOwnName = vca.Name;
                    _buildBoxOwnDescription = vca.Description;
                    _buildBoxOwnIcon = vca.Icon;
                    continue;
                }

                RelinkBundleShaders(go);
                GraftModuleComponents(go);
                _modulePrefabs[go.name] = go;
                RegisterModulePrefab(go, vca);
                continue;
            }
            RuntimeAssetsAPI.LoadAsset(asset);
        }

        LinkForgeBuildBoxRef();
    }

    // Bundle-loaded shaders carry a different keyword/variant set than the player
    // build's copy, which renders solid black — re-resolve by name to fix it.
    // sharedMaterials since this runs on the prefab asset, not an instance.
    private static void RelinkBundleShaders(GameObject prefab)
    {
        foreach (var rend in prefab.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var mat in rend.sharedMaterials)
            {
                if (mat == null || mat.shader == null) continue;
                var shader = Shader.Find(mat.shader.name);
                if (shader == null) continue;

                // Reassigning .shader resets renderQueue to the new shader's default
                // (opaque range) even though it leaves _SurfaceType/_SrcBlend/_DstBlend
                // untouched — HDRP sorts opaque-vs-transparent off renderQueue alone, so
                // a transparent material (e.g. Glass) silently draws fully opaque here.
                var queue = mat.renderQueue;
                mat.shader = shader;
                mat.renderQueue = queue;
            }
        }
    }

    // Bundle prefabs can't carry a fully configured CellModule/PhotonView from the game
    // project (script components don't survive the bundle pipeline) — graft minimal
    // stand-ins so the prefab survives PhotonNetwork.Instantiate and the build flow.
    // Deconstruction forbidden here since there's no BuildBoxRef yet (Deconstruct would NRE).
    // MaxHitPoints/Invulnerability must be initialized: OrbitObject.Start NREs otherwise.
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

        // BuildSocket.SetModule dereferences module.PowerDrain unconditionally, so it needs
        // a real one. PowerWanted stays 0 (Forge runs free); AutoPowerOn brings it up on connect.
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

        // Mirrors vanilla culling: Interior/Exterior child groups each get their own
        // OcclusionNode (Exterior stays visible from space/turrets); no split falls
        // back to one node on the root.
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
        // A Manual-search view with an empty ObservedComponents list syncs nothing —
        // ensure it observes the module even when authored in the editor.
        if (view.observableSearch == PhotonView.ObservableSearch.Manual &&
            (view.ObservedComponents == null || view.ObservedComponents.Count == 0))
        {
            view.ObservedComponents = new List<Component> { cell };
        }
    }

    // Sets CellModule.BuildBoxRef, read by vanilla Deconstruct.CreateBuildBox. The reverse
    // link (box.moduleRef) is set later on the donor clone — see EnsureBuildBoxTemplateReady.
    private static void LinkForgeBuildBoxRef()
    {
        if (!_buildBoxGuid.HasValue) return;
        if (!_modulePrefabs.TryGetValue(UpgradeForgeBehavior.PrefabName, out var modulePrefab)) return;

        var cell = modulePrefab.GetComponent<CellModule>();
        if (cell == null) return;

        cell.BuildBoxRef ??= new CloneStarObjectRef();
        cell.BuildBoxRef.AssetGuid = _buildBoxGuid.Value;
        cell.BuildBoxRef.IsRuntime = true;

        BepinPlugin.Log.LogInfo($"[AssetLoader] Linked Forge module -> BuildBox ref {_buildBoxGuid.Value.AsHex()}");
    }

    private static GUIDUnion? _donorBuildBoxGuid;

    // A live vanilla BuildBox to clone instead of instantiating our own grafted prefab —
    // whose Rigidbody never connected to MovingSpacePlatform's PhysicsScene and fell through
    // the floor. Cached after the first lookup to avoid a per-spawn registry scan.
    internal static bool TryFindDonorBuildBoxGuid(out GUIDUnion guid)
    {
        if (_donorBuildBoxGuid.HasValue)
        {
            guid = _donorBuildBoxGuid.Value;
            return true;
        }

        // Prefer a donor tagged Utility so the borrowed crate's label reads right for
        // the Forge; fall back to any plain (non-weapon) BuildBox otherwise.
        GUIDUnion? fallback = null;
        foreach (var cell in UnityEngine.Object.FindObjectsOfType<CellModule>())
        {
            if (cell.BuildBoxRef == null || cell.BuildBoxRef.IsNull) continue;
            var candidateGuid = cell.BuildBoxRef.AssetGuid;

            // CompositeWeaponBuildBox reads WeaponDataRef instead of moduleRef, which our
            // moduleRef-based clone leaves null — NREs everywhere downstream. Skip it.
            var path = ResourcePaths.Instance.GetPath(candidateGuid);
            if (string.IsNullOrEmpty(path)) continue;
            var candidatePrefab = Resources.Load<GameObject>(path);
            var candidateBox = candidatePrefab != null ? candidatePrefab.GetComponent<BuildBox>() : null;
            if (candidateBox == null || candidateBox is CompositeWeaponBuildBox) continue;

            var isUtility = cell.CsTags != null &&
                System.Array.IndexOf(cell.CsTags, VoidCrewTerminus.Utils.CsTagRegistry.Utility) >= 0;
            if (isUtility)
            {
                _donorBuildBoxGuid = candidateGuid;
                guid = candidateGuid;
                return true;
            }

            fallback ??= candidateGuid;
        }

        if (fallback.HasValue)
        {
            _donorBuildBoxGuid = fallback.Value;
            guid = fallback.Value;
            return true;
        }

        guid = default;
        return false;
    }

    // Presets moduleRef on the TEMPLATE before any instance's Awake runs — relabeling
    // it on the spawned instance instead left the box half-donor-half-Forge, since
    // moduleRef-keyed systems (BuildBoxActor.Awake) had already run against the original.
    // Deferred from LoadBundle, which fires before ResourcePaths/any vanilla module exists
    // (same Awake-too-early trap as the PhotonNetwork case in CLAUDE.md). Idempotent.
    internal static void EnsureBuildBoxTemplateReady()
    {
        EnsureRuntimeAssetsRegisteredInVanillaContainers();

        if (_modulePrefabs.ContainsKey(UpgradeForgeBehavior.BuildBoxPrefabName)) return;
        if (!_buildBoxGuid.HasValue)
        {
            BepinPlugin.Log.LogError("[AssetLoader] Forge BuildBox has no stamped AssetGuid — re-export the bundle.");
            return;
        }
        if (!_modulePrefabs.TryGetValue(UpgradeForgeBehavior.PrefabName, out var modulePrefab)) return;
        var moduleVca = modulePrefab.GetComponent<VoidCrewAsset>();
        if (moduleVca == null || string.IsNullOrEmpty(moduleVca.AssetGuid)) return;
        var moduleGuid = new GUIDUnion(moduleVca.AssetGuid);

        if (!TryFindDonorBuildBoxGuid(out var donorGuid))
        {
            BepinPlugin.Log.LogWarning("[AssetLoader] No vanilla BuildBox donor found yet — Forge BuildBox unavailable until one exists (is a module installed on the ship?).");
            return;
        }

        var path = ResourcePaths.Instance.GetPath(donorGuid);
        var donorPrefab = string.IsNullOrEmpty(path) ? null : Resources.Load<GameObject>(path);
        if (donorPrefab == null)
        {
            BepinPlugin.Log.LogWarning("[AssetLoader] Could not load the donor BuildBox prefab asset — Forge BuildBox unavailable.");
            return;
        }

        // Clone while inactive so Awake doesn't run on the clone (it's a template only,
        // same active-state dance CustomObjectPool.Instantiate does around real spawns).
        // Synchronous, no yield — nothing else observes the donor's brief inactive state.
        var donorWasActive = donorPrefab.activeSelf;
        donorPrefab.SetActive(false);
        var template = UnityEngine.Object.Instantiate(donorPrefab);
        donorPrefab.SetActive(donorWasActive);

        var box = template.GetComponent<BuildBox>();
        if (box == null)
        {
            BepinPlugin.Log.LogError("[AssetLoader] Donor BuildBox clone has no BuildBox component — cannot use as a template.");
            UnityEngine.Object.Destroy(template);
            return;
        }

        box.moduleRef ??= new CloneStarObjectRef();
        box.moduleRef.AssetGuid = moduleGuid;
        // IsRuntime is [NonSerialized] — clones of this template reset it to false;
        // BuildBoxRuntimeRefPatch.cs re-stamps it per-instance before Awake reads it.
        box.moduleRef.IsRuntime = true;

        template.name = UpgradeForgeBehavior.BuildBoxPrefabName;
        _modulePrefabs[UpgradeForgeBehavior.BuildBoxPrefabName] = template;

        var boxGuid = _buildBoxGuid.Value;
        if (!RuntimeAssetsRegister.Instance.HasAsset(boxGuid))
        {
            RuntimeAssetsRegister.Instance.RegisterAsset(
                boxGuid, template, SessionModificationEffect.IsNetworkSpawned,
                new RuntimeAssetInfo { Name = template.name, DisplayName = "Upgrade Forge BuildBox" });
        }

        var container = ResourceAssetContainer<CloneStarObjectContainer, AbstractCloneStarObject, CloneStarObjectDef>.Instance;
        if (!container.HasItem(boxGuid))
        {
            var def = new CloneStarObjectDef(boxGuid, template.name) { Ref = { IsRuntime = true } };
            // A null ContextInfo falls back to "missing description" hover text — prefer
            // the crate's own authored Name/Description over the donor's, falling back to
            // the donor's for whatever wasn't authored (Icon, in particular).
            var donorDef = container.GetAssetDefById(donorGuid, verbose: false);
            var header = !string.IsNullOrEmpty(_buildBoxOwnName) ? _buildBoxOwnName : donorDef?.ContextInfo?.HeaderText;
            var body = !string.IsNullOrEmpty(_buildBoxOwnDescription) ? _buildBoxOwnDescription : donorDef?.ContextInfo?.BodyText;
            var icon = _buildBoxOwnIcon != null ? _buildBoxOwnIcon : donorDef?.ContextInfo?.Icon;
            def.ContextInfo = ContextInfo.Create(icon, header, body);
            container.RegisterRuntimeAsset(boxGuid, def);
        }

        // Unclear whether the box's hover subtitle resolves off boxGuid or moduleGuid —
        // register both (moduleGuid via EnsureRuntimeAssetsRegisteredInVanillaContainers above).
        var moduleContainer = ResourceAssetContainer<ModuleContainer, CellModule, ModuleDef>.Instance;
        if (!moduleContainer.HasItem(boxGuid))
        {
            try
            {
                var boxModuleDef = new ModuleDef(boxGuid, template.name) { Category = ECategory.Support };
                moduleContainer.RegisterRuntimeAsset(boxGuid, boxModuleDef);
                BepinPlugin.Log.LogInfo($"[AssetLoader] Registered BuildBox {boxGuid.AsHex()} into vanilla ModuleContainer as {ECategory.Support}.");
            }
            catch (System.Exception ex)
            {
                BepinPlugin.Log.LogError($"[AssetLoader] Failed to register BuildBox {boxGuid.AsHex()} into vanilla ModuleContainer: {ex}");
            }
        }

        RegisterCommonRarity(boxGuid, template.name);

        BepinPlugin.Log.LogInfo($"[AssetLoader] Forge BuildBox template ready — cloned from donor {donorGuid.AsHex()}, moduleRef -> {moduleGuid.AsHex()}, registered as {boxGuid.AsHex()}.");
    }

    private static bool _runtimeAssetsRegisteredInVanillaContainers;

    // RegisterModulePrefab only covers RuntimeAssetsRegister, but vanilla code does raw
    // CloneStarObjectContainer lookups with no null-check on a miss (e.g. sector map marker
    // creation throws KeyNotFoundException every tick, observed live as "falls through the
    // floor"). Register here instead of patching every call site. Deferred like
    // EnsureBuildBoxTemplateReady — not populated at plugin Awake.
    private static void EnsureRuntimeAssetsRegisteredInVanillaContainers()
    {
        if (_runtimeAssetsRegisteredInVanillaContainers) return;

        var container = ResourceAssetContainer<CloneStarObjectContainer, AbstractCloneStarObject, CloneStarObjectDef>.Instance;
        var moduleContainer = ResourceAssetContainer<ModuleContainer, CellModule, ModuleDef>.Instance;
        foreach (var prefab in _modulePrefabs.Values)
        {
            var vca = prefab.GetComponent<VoidCrewAsset>();
            if (vca == null || string.IsNullOrEmpty(vca.AssetGuid)) continue;

            var guid = new GUIDUnion(vca.AssetGuid);
            if (!container.HasItem(guid))
            {
                // IsRuntime sticks here (unlike Patches/BuildBoxRuntimeRefPatch.cs) since this
                // def is a plain C# object, never cloned through Unity's prefab pipeline.
                // ContextInfo comes from the prefab's own VoidCrewAsset fields — left null
                // it falls back to the generic "missing description" hover text.
                var def = new CloneStarObjectDef(guid, prefab.name) { Ref = { IsRuntime = true } };
                def.ContextInfo = ContextInfo.Create(vca.Icon, vca.Name, vca.Description);
                container.RegisterRuntimeAsset(guid, def);
                BepinPlugin.Log.LogInfo($"[AssetLoader] Registered {prefab.name} ({guid.AsHex()}) into vanilla CloneStarObjectContainer.");
            }

            // The hover subtitle is a separate ModuleContainer/ModuleDef.Category lookup
            // from ContextInfo, keyed by the object's own self guid. Gated on carrying a
            // CellModule. try/catch: untested against live ModuleContainer wiring — log
            // plainly rather than fail silently.
            if (prefab.GetComponent<CellModule>() != null && !moduleContainer.HasItem(guid))
            {
                try
                {
                    var moduleDef = new ModuleDef(guid, prefab.name) { Category = ECategory.Support };
                    moduleContainer.RegisterRuntimeAsset(guid, moduleDef);
                    BepinPlugin.Log.LogInfo($"[AssetLoader] Registered {prefab.name} ({guid.AsHex()}) into vanilla ModuleContainer as {ECategory.Support}.");
                }
                catch (System.Exception ex)
                {
                    BepinPlugin.Log.LogError($"[AssetLoader] Failed to register {prefab.name} ({guid.AsHex()}) into vanilla ModuleContainer: {ex}");
                }
            }

            RegisterCommonRarity(guid, prefab.name);
        }

        _runtimeAssetsRegisteredInVanillaContainers = true;
    }

    // The tooltip's rarity band is a third, separate lookup (UnlockContainer) that
    // defaults to no band for any unregistered guid — register Common to match vanilla.
    private static void RegisterCommonRarity(GUIDUnion guid, string name)
    {
        var unlockContainer = ResourceAssetContainer<UnlockContainer, UnityEngine.Object, UnlockItemDef>.Instance;
        if (unlockContainer.HasItem(guid)) return;

        try
        {
            var unlockDef = new UnlockItemDef { AssetGuid = guid, Path = name, rarity = RarityType.Common };
            unlockContainer.RegisterRuntimeAsset(guid, unlockDef);
            BepinPlugin.Log.LogInfo($"[AssetLoader] Registered {name} ({guid.AsHex()}) into vanilla UnlockContainer as {RarityType.Common}.");
        }
        catch (System.Exception ex)
        {
            BepinPlugin.Log.LogError($"[AssetLoader] Failed to register {name} ({guid.AsHex()}) into vanilla UnlockContainer: {ex}");
        }
    }

    // Registers under the export tool's stamped GUID — the same register the game's
    // converter uses for carryables, making it resolvable by CustomObjectPool
    // ("#guid" PUN instantiation), ResourceAssetRef lookups, and !forgespawn.
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
