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
using VC.Common;
using VC.Common.Carryables;
using VC.Common.PlayerShip;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus;

// Bundles use ".metem_ext" rather than ".metem": the game auto-loads every
// *.metem under the plugins directory via RuntimeAssetLoadingService, which
// would race this loader and feed our custom prefab through its converter
// (which can't handle it). Assets the game's converter CAN handle belong in a
// real *.metem instead.
public class AssetLoader
{
    // Module-cell prefabs kept mod-side, keyed by prefab name: the game's
    // RuntimeAssetConverter only understands carryables and ship cosmetics, so
    // anything else (our UpgradeForgeModuleCell) must be instantiated by our own
    // code instead. Also holds the BuildBox template once EnsureBuildBoxTemplateReady
    // builds one — a clone of a live vanilla donor, not a bundle asset, so it
    // carries no VoidCrewAsset marker.
    private static readonly Dictionary<string, GameObject> _modulePrefabs = new();

    // Bundles this assembly loaded, for hot-reload teardown.
    private static readonly List<AssetBundle> _loadedBundles = new();

    // GUID the export tool stamped onto the BuildBox bundle prefab's VoidCrewAsset
    // marker, captured at LoadBundle time. No longer used to register that prefab's
    // own shape (see EnsureBuildBoxTemplateReady) but still the stable identity
    // other code (CellModule.BuildBoxRef, BossDefeatHook, RuntimeAssetsRegister)
    // keys off.
    private static GUIDUnion? _buildBoxGuid;

    // The BuildBox marker prefab's authored Name/Description/Icon, captured at
    // LoadBundle time before the marker GameObject itself is discarded.
    // EnsureBuildBoxTemplateReady prefers these over the donor's own flavor text.
    private static string _buildBoxOwnName;
    private static string _buildBoxOwnDescription;
    private static Sprite _buildBoxOwnIcon;

    public static GameObject GetModulePrefab(string name) =>
        _modulePrefabs.TryGetValue(name, out var prefab) ? prefab : null;

    // Hot-reload teardown: release bundle handles so LoadFromFile can run again.
    // Unload(false) keeps already-created assets alive (live modules/materials
    // keep working); only the handle is freed. RuntimeAssetsRegister entries stay
    // in place — re-registration is skipped by the HasAsset guard on reload.
    public static void UnloadBundles()
    {
        foreach (var bundle in _loadedBundles)
        {
            if (bundle != null) bundle.Unload(false);
        }
        _loadedBundles.Clear();
        // The BuildBox template (if built) is a live clone, not a bundle asset —
        // bundle.Unload doesn't touch it, so it needs its own cleanup or it leaks
        // an inert orphan GameObject across hot-reloads.
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

    // Splits bundle content between the game's RuntimeAssets pipeline (carryables,
    // cosmetics, scriptable objects) and our own module-prefab registry. Never
    // unloaded — converted assets and extracted prefabs keep referencing its content.
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
                if (go.name == UpgradeForgeBehavior.BuildBoxPrefabName)
                {
                    // This placeholder is never instantiated directly — see
                    // EnsureBuildBoxTemplateReady (grafted components never
                    // connected to the ship's simulated physics). Only its
                    // stamped GUID is used, as the identity the donor-derived
                    // template gets registered under.
                    if (!string.IsNullOrEmpty(vca.AssetGuid))
                        _buildBoxGuid = new GUIDUnion(vca.AssetGuid);
                    else
                        BepinPlugin.Log.LogError($"[AssetLoader] BuildBox prefab '{go.name}' has no AssetGuid — re-export the bundle (the export tool stamps it).");
                    // The marker GameObject itself is dropped (never added to
                    // _modulePrefabs); its authored Name/Description/Icon are kept
                    // since EnsureBuildBoxTemplateReady prefers them over the
                    // donor's own flavor text.
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

    // AssetBundle-loaded materials reference a shader copy with a different
    // keyword/variant set than the one baked into the player build — a known
    // Unity AssetBundle pitfall that renders them solid black under any light.
    // Re-resolve each shader by name against the live build (the same fix
    // RuntimeAssets' PlayerShipVisualsLoader applies for bundle-loaded ship
    // cosmetics, but the game's converter never applies to module prefabs).
    // Uses sharedMaterials since this runs on the prefab asset, not an instance.
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

    // Points the Forge module at its BuildBox's stable GUID (CellModule.BuildBoxRef
    // — read by vanilla Deconstruct.CreateBuildBox when the module is taken apart).
    // Only this direction is set at bundle-load time; the reverse link (the box's
    // moduleRef, pointing back at the Forge module) is set later, on the
    // donor-derived template — see EnsureBuildBoxTemplateReady.
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

    // A live vanilla module's own BuildBox guid — a fully-wired vanilla item
    // (physics, simulated-platform connection, materials). EnsureBuildBoxTemplateReady
    // clones this instead of instantiating a custom-grafted prefab, whose Rigidbody
    // never connected into MovingSpacePlatform's simulated PhysicsScene (it fell
    // through the ship floor; root cause never pinned down). Cached after the first
    // lookup — a per-spawn scan of the whole module registry previously caused a
    // !forgespawn lag spike.
    internal static bool TryFindDonorBuildBoxGuid(out GUIDUnion guid)
    {
        if (_donorBuildBoxGuid.HasValue)
        {
            guid = _donorBuildBoxGuid.Value;
            return true;
        }

        // Prefer a donor whose own module is tagged Utility (Module_Category_Utility
        // — the same CsTag ForgeAttachHelper stamps onto the Forge module itself),
        // so the borrowed crate's own context-info label ("Utility Build Box") reads
        // as the right kind of box for what the Forge thematically is, rather than
        // whatever module happened to be first in scene-iteration order. Falls back
        // to any plain (non-weapon) BuildBox if no Utility-tagged module happens to
        // be installed on this particular ship.
        GUIDUnion? fallback = null;
        foreach (var cell in UnityEngine.Object.FindObjectsOfType<CellModule>())
        {
            if (cell.BuildBoxRef == null || cell.BuildBoxRef.IsNull) continue;
            var candidateGuid = cell.BuildBoxRef.AssetGuid;

            // CompositeWeaponBuildBox (composite/modular weapons) overrides
            // GetBuildSize/ContextInfo to read WeaponDataRef instead of
            // moduleRef — cloning one and setting moduleRef (this class's whole
            // approach) leaves WeaponDataRef null, which NREs everywhere
            // downstream: context info, hover, socket highlighting, even the
            // spawn's own sector-registration marker creation (confirmed live —
            // !forgespawn threw inside SpawnCarryable itself when the donor
            // scan happened to land on one of these). Only a plain BuildBox
            // donor works with this moduleRef-based approach.
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

    // Clones a live vanilla donor and presets moduleRef on the TEMPLATE before any
    // instance's Awake runs. Relabeling moduleRef on the spawned instance after
    // the fact instead left the box half-donor-half-Forge: BuildBoxActor.Awake and
    // other moduleRef-keyed systems had already run against the donor's original
    // ref (no hover label, couldn't be placed/dropped, held wrong).
    //
    // Deferred rather than run from LoadBundle: that fires at plugin Awake, before
    // ResourcePaths or any vanilla module instance exists — the same "touched game
    // state before the game set it up" trap as the PhotonNetwork-at-Awake bug (see
    // CLAUDE.md). Call this before spawning or looking up the Forge BuildBox guid;
    // idempotent, does the work once.
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

        // Clone while the donor is momentarily inactive so the clone starts
        // inactive too (Unity mirrors the source's active state at Instantiate
        // time) — Awake must NOT run on this clone; it's a template, only ever
        // used as a source for further clones (via RuntimeAssetsRegister /
        // CustomObjectPool, which does this exact same active-state dance
        // itself around every real spawn — see CustomObjectPool.Instantiate).
        // The toggle is synchronous with no yield in between, so nothing else
        // can observe the shared donor asset in its briefly-inactive state.
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
        // IsRuntime is [NonSerialized] — every future clone made FROM this
        // template starts with it reset to false regardless of what's set here;
        // Patches/BuildBoxRuntimeRefPatch.cs re-stamps it on each real instance
        // right before that instance's own Awake reads it. Set here anyway for
        // consistency/in case anything reads it straight off the template itself.
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
            // A fresh CloneStarObjectDef's contextInfo field is null, and
            // CloneStarObjectDef.ContextInfo falls back to
            // DataTable<DefaultAssetTable>.Instance.MissingContextInfo in that
            // case — the "missing description" hover text. Prefer the crate's own
            // Unity-authored Name/Description (captured off the bundle marker
            // prefab in LoadBundle — see _buildBoxOwnName/_buildBoxOwnDescription)
            // over the donor's borrowed flavor text; fall back to the donor's own
            // text/icon for whichever fields weren't authored (Icon in particular —
            // no sprite has been set on the marker yet).
            var donorDef = container.GetAssetDefById(donorGuid, verbose: false);
            var header = !string.IsNullOrEmpty(_buildBoxOwnName) ? _buildBoxOwnName : donorDef?.ContextInfo?.HeaderText;
            var body = !string.IsNullOrEmpty(_buildBoxOwnDescription) ? _buildBoxOwnDescription : donorDef?.ContextInfo?.BodyText;
            var icon = _buildBoxOwnIcon != null ? _buildBoxOwnIcon : donorDef?.ContextInfo?.Icon;
            def.ContextInfo = ContextInfo.Create(icon, header, body);
            container.RegisterRuntimeAsset(boxGuid, def);
        }

        // The crate's own "Utility System Module" subtitle: unclear whether the
        // box's hover resolves its ContextInfoViewModel target as its own self
        // guid (boxGuid) or the module it builds (moduleGuid) — register under
        // BOTH so the subtitle shows regardless of which one it actually is.
        // moduleGuid is registered separately in
        // EnsureRuntimeAssetsRegisteredInVanillaContainers (called above, at the
        // top of this method) — this only needs to add boxGuid.
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

    // RegisterModulePrefab only registers into RuntimeAssetsRegister, but plenty of
    // vanilla code does raw CloneStarObjectContainer lookups with no fallback and no
    // null-check on a miss — e.g. sector map marker creation (CsObjectReference.
    // IsInstanceOf → GetItem), which throws KeyNotFoundException every FixedUpdate
    // tick for a spawned box and aborts that scheduler batch (observed live as "no
    // collider, falls through the floor" despite a real collider). Register via
    // ResourceAssetContainer.RegisterRuntimeAsset instead of patching every call
    // site. Deferred like EnsureBuildBoxTemplateReady: not populated at plugin Awake.
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
                // IsRuntime is [NonSerialized] but this CloneStarObjectDef is a plain
                // C# object living only in the container's own dictionary/list — it
                // never goes through Unity's prefab-clone pipeline, so the flag
                // sticks (unlike the same field on a GameObject-hosted component,
                // see Patches/BuildBoxRuntimeRefPatch.cs).
                //
                // ContextInfo comes straight from the prefab's own VoidCrewAsset
                // fields (Name/Description/Icon, authored in Unity) instead of being
                // left null — a null ContextInfo falls back to
                // DefaultAssetTable.MissingContextInfo, the "missing description"
                // hover text.
                var def = new CloneStarObjectDef(guid, prefab.name) { Ref = { IsRuntime = true } };
                def.ContextInfo = ContextInfo.Create(vca.Icon, vca.Name, vca.Description);
                container.RegisterRuntimeAsset(guid, def);
                BepinPlugin.Log.LogInfo($"[AssetLoader] Registered {prefab.name} ({guid.AsHex()}) into vanilla CloneStarObjectContainer.");
            }

            // The "Utility System Module" (etc.) hover subtitle is a completely
            // separate lookup from ContextInfo — ContextInfoViewModel resolves it
            // via ResourceAssetContainer<ModuleContainer, CellModule, ModuleDef>,
            // keyed by whatever guid AbstractCloneStarObject.ContextInfo passes as
            // `target` (that object's own self assetGuid — NOT necessarily
            // moduleRef.AssetGuid), reading ModuleDef.Category through
            // ModuleCategoryLocalizationTable. Registered for both this prefab's
            // own guid (covers the installed module's hover, whose self-guid is
            // this one) — gated on actually carrying a CellModule, since only
            // that one prefab in _modulePrefabs qualifies.
            //
            // try/catch: unlike the CloneStarObjectContainer registration above
            // (already proven working — real Name/Description now show live),
            // this is untested against the live game's actual ModuleContainer
            // wiring. A throw here must not be silent or take down the rest of
            // this method — log it plainly so a failure is diagnosable from the
            // BepInEx log instead of showing up as just "the tag never appeared."
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

    // The tooltip's left-edge rarity band comes from a THIRD lookup, separate from
    // both ContextInfo and the ModuleContainer/Category subtitle:
    // ContextInfoViewModel reads RarityType via
    // ResourceAssetContainer<UnlockContainer, Object, UnlockItemDef>.GetRarity(target),
    // which returns RarityType.None (no band) for any guid it doesn't recognize.
    // Register Common — the tier every plain vanilla module/box shows — under the
    // same guid(s) the ContextInfo/ModuleContainer fixes already use.
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
