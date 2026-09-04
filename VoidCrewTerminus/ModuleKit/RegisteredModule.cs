using System;
using CG.Ship.Modules;
using CG.Ship.Object;
using Client.Player.Interactions;
using ResourceAssets;
using UnityEngine;
using VC.Common;

namespace VoidCrewTerminus.ModuleKit;

internal sealed class RegisteredModule
{
    internal CustomModuleDefinition Definition { get; }

    private GUIDUnion? _buildBoxGuid;
    private string _buildBoxName;
    private string _buildBoxDescription;
    private Sprite _buildBoxIcon;
    private GUIDUnion? _donorGuid;

    internal RegisteredModule(CustomModuleDefinition definition) => Definition = definition;

    // The marker is never instantiated — only its GUID and authored flavor text are
    // kept, the GameObject dropped.
    internal void CaptureBuildBoxMarker(GameObject marker, VoidCrewAsset vca)
    {
        if (!string.IsNullOrEmpty(vca.AssetGuid))
            _buildBoxGuid = new GUIDUnion(vca.AssetGuid);
        else
            KitLog.Log?.LogError($"[ModuleKit] BuildBox prefab '{marker.name}' has no AssetGuid — re-export the bundle (the export tool stamps it).");

        _buildBoxName = vca.Name;
        _buildBoxDescription = vca.Description;
        _buildBoxIcon = vca.Icon;
    }

    // Sets CellModule.BuildBoxRef, read by vanilla Deconstruct.CreateBuildBox. The reverse
    // link (box.moduleRef) is set later on the donor clone — see TryBuildBuildBoxTemplate.
    internal void LinkBuildBoxRef(GameObject modulePrefab)
    {
        if (!_buildBoxGuid.HasValue) return;

        var cell = modulePrefab.GetComponent<CellModule>();
        if (cell == null) return;

        cell.BuildBoxRef ??= new CloneStarObjectRef();
        cell.BuildBoxRef.AssetGuid = _buildBoxGuid.Value;
        cell.BuildBoxRef.IsRuntime = true;

        KitLog.Log?.LogDebug($"[ModuleKit] Linked {modulePrefab.name} -> BuildBox ref {_buildBoxGuid.Value.AsHex()}");
    }

    // A live vanilla BuildBox to clone instead of instantiating a grafted prefab — whose
    // Rigidbody never connected to MovingSpacePlatform's PhysicsScene and fell through the
    // floor. Cached after the first lookup to avoid a per-spawn registry scan.
    internal bool TryFindDonorGuid(out GUIDUnion guid)
    {
        if (_donorGuid.HasValue)
        {
            guid = _donorGuid.Value;
            return true;
        }

        var preferredTag = Definition.PreferredDonorTag?.Invoke();

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

            if (preferredTag != null && cell.CsTags != null && Array.IndexOf(cell.CsTags, preferredTag) >= 0)
            {
                _donorGuid = candidateGuid;
                guid = candidateGuid;
                return true;
            }

            fallback ??= candidateGuid;
        }

        if (fallback.HasValue)
        {
            _donorGuid = fallback.Value;
            guid = fallback.Value;
            return true;
        }

        guid = default;
        return false;
    }

    // Presets moduleRef on the TEMPLATE before any instance's Awake runs — relabeling
    // it on the spawned instance instead left the box half-donor-half-custom, since
    // moduleRef-keyed systems (BuildBoxActor.Awake) had already run against the original.
    internal GameObject TryBuildBuildBoxTemplate(GameObject modulePrefab)
    {
        if (!_buildBoxGuid.HasValue)
        {
            KitLog.Log?.LogError($"[ModuleKit] {Definition.BuildBoxPrefabName} has no stamped AssetGuid — re-export the bundle.");
            return null;
        }

        var moduleVca = modulePrefab.GetComponent<VoidCrewAsset>();
        if (moduleVca == null || string.IsNullOrEmpty(moduleVca.AssetGuid)) return null;
        var moduleGuid = new GUIDUnion(moduleVca.AssetGuid);

        if (!TryFindDonorGuid(out var donorGuid))
        {
            KitLog.Log?.LogWarning($"[ModuleKit] No vanilla BuildBox donor found yet — {Definition.BuildBoxPrefabName} unavailable until one exists (is a module installed on the ship?).");
            return null;
        }

        var path = ResourcePaths.Instance.GetPath(donorGuid);
        var donorPrefab = string.IsNullOrEmpty(path) ? null : Resources.Load<GameObject>(path);
        if (donorPrefab == null)
        {
            KitLog.Log?.LogWarning($"[ModuleKit] Could not load the donor BuildBox prefab asset — {Definition.BuildBoxPrefabName} unavailable.");
            return null;
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
            KitLog.Log?.LogError("[ModuleKit] Donor BuildBox clone has no BuildBox component — cannot use as a template.");
            UnityEngine.Object.Destroy(template);
            return null;
        }

        box.moduleRef ??= new CloneStarObjectRef();
        box.moduleRef.AssetGuid = moduleGuid;
        // IsRuntime is [NonSerialized] — clones of this template reset it to false;
        // BuildBoxRuntimeRefPatch re-stamps it per-instance before Awake reads it.
        box.moduleRef.IsRuntime = true;

        // The box's OWN identity, distinct from moduleRef (what it builds).
        // AbstractCloneStarObject.assetGuid is a plain serialized field, so the clone
        // inherited the DONOR's guid — and that is the guid every self-lookup keys
        // off: AbstractCloneStarObject.ContextInfo resolves hover text through
        // CloneStarObjectContainer.GetContext(assetGuid), and the runtime-asset
        // registration below resolves the def (and therefore the instance's name)
        // the same way. Left unstamped, the crate showed the donor's tooltip and
        // spawned named after the donor prefab (observed live as
        // "BuildBox_GravityScoop_01"). Assigned BEFORE the registrations that read it.
        var boxGuid = _buildBoxGuid.Value;
        box.ContainerGuid = boxGuid;

        template.name = Definition.BuildBoxPrefabName;

        if (!VanillaAssetRegistrar.IsAssetRegistered(boxGuid))
        {
            VanillaAssetRegistrar.RegisterAssetIfAbsent(boxGuid, template, Definition.BuildBoxDisplayName);
        }
        else if (VanillaAssetRegistrar.GetAsset(boxGuid) != template)
        {
            // Corrected rather than skipped. RuntimeAssetsRegister is a vanilla static that
            // outlives the assembly, so after a ScriptEngine reload it still holds the
            // PREVIOUS load's template, which CustomModuleRegistry.Clear has since destroyed.
            // Skipping left every runtime-ref lookup resolving that destroyed object
            // (ResourceAssetRef.AssetInstance returns it verbatim), so the box reported as
            // "not ready" for the rest of the process.
            if (!VanillaAssetRegistrar.TryReplaceAsset(boxGuid, template))
                KitLog.Log?.LogWarning(
                    $"[ModuleKit] {template.name} {boxGuid.AsHex()} is registered to a different object and could not be corrected — " +
                    "spawns will resolve the stale one. Restart the game rather than hot-reloading.");
        }

        // A null ContextInfo falls back to "missing description" hover text — prefer the
        // crate's own authored Name/Description over the donor's, falling back to the
        // donor's for whatever wasn't authored (Icon, in particular).
        var donorContext = VanillaAssetRegistrar.GetContextInfo(donorGuid);
        var header = !string.IsNullOrEmpty(_buildBoxName) ? _buildBoxName : donorContext?.HeaderText;
        var body = !string.IsNullOrEmpty(_buildBoxDescription) ? _buildBoxDescription : donorContext?.BodyText;
        var icon = _buildBoxIcon != null ? _buildBoxIcon : donorContext?.Icon;

        VanillaAssetRegistrar.RegisterObjectDef(boxGuid, template.name, ContextInfo.Create(icon, header, body));
        VanillaAssetRegistrar.RegisterModuleDef(boxGuid, template.name, Definition.Category);
        VanillaAssetRegistrar.RegisterRarity(boxGuid, template.name, Definition.Rarity);

        KitLog.Log?.LogDebug(
            $"[ModuleKit] {template.name} template ready — cloned from donor {donorGuid.AsHex()}, " +
            $"moduleRef -> {moduleGuid.AsHex()}, registered as {boxGuid.AsHex()}.");
        return template;
    }
}
