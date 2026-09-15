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

    internal void CaptureBuildBoxMarker(GameObject marker, VoidCrewAsset vca)
    {
        if (!string.IsNullOrEmpty(vca.AssetGuid))
            _buildBoxGuid = new GUIDUnion(vca.AssetGuid);
        else
            BepinPlugin.Log.LogError($"[ModuleKit] BuildBox prefab '{marker.name}' has no AssetGuid — re-export the bundle (the export tool stamps it).");

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

        BepinPlugin.Log.LogDebug($"[ModuleKit] Linked {modulePrefab.name} -> BuildBox ref {_buildBoxGuid.Value.AsHex()}");
    }

    // A live vanilla BuildBox to clone: a grafted prefab's Rigidbody never connects to
    // MovingSpacePlatform's PhysicsScene and falls through the floor. Cached after the
    // first lookup to avoid a per-spawn registry scan.
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

            // CompositeWeaponBuildBox reads WeaponDataRef instead of moduleRef, which a
            // moduleRef-based clone leaves null, NREing everywhere downstream.
            var candidatePrefab = LoadPrefab(candidateGuid);
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

    // moduleRef is preset on the TEMPLATE, before any instance's Awake: moduleRef-keyed
    // systems like BuildBoxActor.Awake run too early for a per-instance relabel to reach
    // them, leaving the box half donor and half custom.
    internal GameObject TryBuildBuildBoxTemplate(GameObject modulePrefab)
    {
        if (!_buildBoxGuid.HasValue)
        {
            BepinPlugin.Log.LogError($"[ModuleKit] {Definition.BuildBoxPrefabName} has no stamped AssetGuid — re-export the bundle.");
            return null;
        }

        var moduleVca = modulePrefab.GetComponent<VoidCrewAsset>();
        if (moduleVca == null || string.IsNullOrEmpty(moduleVca.AssetGuid)) return null;
        var moduleGuid = new GUIDUnion(moduleVca.AssetGuid);

        if (!TryFindDonorGuid(out var donorGuid))
        {
            BepinPlugin.Log.LogWarning($"[ModuleKit] No vanilla BuildBox donor found yet — {Definition.BuildBoxPrefabName} unavailable until one exists (is a module installed on the ship?).");
            return null;
        }

        var donorPrefab = LoadPrefab(donorGuid);
        if (donorPrefab == null)
        {
            BepinPlugin.Log.LogWarning($"[ModuleKit] Could not load the donor BuildBox prefab asset — {Definition.BuildBoxPrefabName} unavailable.");
            return null;
        }

        // Cloned while inactive so Awake never runs on a template, the same active-state
        // dance CustomObjectPool.Instantiate does around real spawns. Synchronous, so
        // nothing else observes the donor's brief inactive state.
        var donorWasActive = donorPrefab.activeSelf;
        donorPrefab.SetActive(false);
        var template = UnityEngine.Object.Instantiate(donorPrefab);
        donorPrefab.SetActive(donorWasActive);

        var box = template.GetComponent<BuildBox>();
        if (box == null)
        {
            BepinPlugin.Log.LogError("[ModuleKit] Donor BuildBox clone has no BuildBox component — cannot use as a template.");
            UnityEngine.Object.Destroy(template);
            return null;
        }

        box.moduleRef ??= new CloneStarObjectRef();
        box.moduleRef.AssetGuid = moduleGuid;
        // IsRuntime is [NonSerialized], so clones of this template reset it to false;
        // BuildBoxRuntimeRefPatch re-stamps it per-instance before Awake reads it.
        box.moduleRef.IsRuntime = true;

        // The box's OWN identity, distinct from moduleRef (what it builds). assetGuid is a
        // plain serialized field, so the clone inherits the DONOR's, and every self-lookup
        // keys off it. Assigned before the registrations below, which read it.
        var boxGuid = _buildBoxGuid.Value;
        box.ContainerGuid = boxGuid;

        template.name = Definition.BuildBoxPrefabName;

        VanillaAssetRegistrar.RegisterAssetIfAbsent(boxGuid, template, Definition.BuildBoxDisplayName);

        if (VanillaAssetRegistrar.GetAsset(boxGuid) != template)
        {
            // Corrected rather than skipped: RuntimeAssetsRegister is a vanilla static that
            // outlives the assembly, so after a hot-reload it still holds the previous
            // load's template, which Clear has since destroyed.
            if (!VanillaAssetRegistrar.TryReplaceAsset(boxGuid, template))
                BepinPlugin.Log.LogWarning(
                    $"[ModuleKit] {template.name} {boxGuid.AsHex()} is registered to a different object and could not be corrected — " +
                    "spawns will resolve the stale one. Restart the game rather than hot-reloading.");
        }

        // A null ContextInfo falls back to "missing description" hover text, so the crate's
        // own authored fields win and the donor's cover whatever was not authored.
        var donorContext = VanillaAssetRegistrar.GetContextInfo(donorGuid);
        var header = !string.IsNullOrEmpty(_buildBoxName) ? _buildBoxName : donorContext?.HeaderText;
        var body = !string.IsNullOrEmpty(_buildBoxDescription) ? _buildBoxDescription : donorContext?.BodyText;
        var icon = _buildBoxIcon != null ? _buildBoxIcon : donorContext?.Icon;

        VanillaAssetRegistrar.RegisterObjectDef(boxGuid, template.name, ContextInfo.Create(icon, header, body));
        VanillaAssetRegistrar.RegisterModuleDef(boxGuid, template.name, Definition.Category);
        VanillaAssetRegistrar.RegisterRarity(boxGuid, template.name, Definition.Rarity);

        // Read back out of the container rather than echoed from the locals above, so the
        // line catches another registrar winning the race and leaving the donor's name in place.
        BepinPlugin.Log.LogInfo(
            $"[ModuleKit] {template.name} template ready — cloned from donor {donorGuid.AsHex()}, " +
            $"moduleRef -> {moduleGuid.AsHex()}, registered as {boxGuid.AsHex()}; " +
            $"def path='{VanillaAssetRegistrar.GetObjectDefPath(boxGuid)}', " +
            $"header='{VanillaAssetRegistrar.GetContextInfo(boxGuid)?.HeaderText}'; " +
            $"runtime asset {(VanillaAssetRegistrar.GetAsset(boxGuid) == template ? "is this template" : "IS NOT this template")}.");
        return template;
    }

    private static GameObject LoadPrefab(GUIDUnion guid)
    {
        var path = ResourcePaths.Instance.GetPath(guid);
        return string.IsNullOrEmpty(path) ? null : Resources.Load<GameObject>(path);
    }
}
