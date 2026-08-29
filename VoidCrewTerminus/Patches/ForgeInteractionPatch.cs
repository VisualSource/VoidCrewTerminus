using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CG.Client.Player.Interactions;
using CG.Game.Player;
using CG.Network;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Object;
using Gameplay.SpacePlatforms;
using HarmonyLib;
using ResourceAssets;
using UnityEngine;
using VoidCrewTerminus.Forge;
using VoidCrewTerminus.Utils;

namespace VoidCrewTerminus.Patches;

// Binds the runtime UpgradeForgeBehavior to Forge module instances as they build,
// and routes in-world clicks on the Forge's interactables into it.
//
// The prefab (Assets/voidcrewterminus.metem → UpgradeForgeModuleCell.prefab) is
// loaded through the RuntimeAssets pipeline and carries only named anchor
// transforms (RelicTubeTarget x6, InputTarget, optional CommitTarget) — no game
// components. When the crew constructs it via a BuildBox, the postfixes below
// attach a live UpgradeForgeBehavior and spawn ForgeInteractable click targets
// on the anchors.
[HarmonyPatch(typeof(BuildBox), nameof(BuildBox.BuildModule))]
internal static class ForgeBuildBoxAttachBehavior
{
    static void Postfix(CellModule __result) => ForgeAttachHelper.TryAttach(__result);
}

// Vanilla BuildBox.BuildModule resolves moduleRef through the CloneStarObjectContainer
// and dereferences the resulting def — null for runtime-registered assets like the
// Forge, so it would NRE right after instantiating. Runtime assets get their own
// factory path (RuntimeAssetsRegister-backed) instead when moduleRef.IsRuntime;
// every normal module still takes the vanilla flow, and the BuildModule postfixes
// (level restore, behavior attach) still run either way.
[HarmonyPatch(typeof(BuildBox), nameof(BuildBox.BuildModule))]
internal static class ForgeBuildBoxRuntimeModulePatch
{
    static bool Prefix(BuildBox __instance, BuildSocket targetLocation, ref CellModule __result)
    {
        var moduleRef = __instance.moduleRef;
        if (moduleRef == null || !moduleRef.IsRuntime) return true;

        if (!RuntimeAssetsRegister.Instance.HasAsset(moduleRef.AssetGuid))
        {
            BepinPlugin.Log.LogError(
                $"[Forge] BuildBox moduleRef is runtime but GUID {moduleRef.AssetGuid.AsHex()} is not in RuntimeAssetsRegister — falling through to vanilla (will likely fail).");
            return true;
        }

        var instantiationData = new Dictionary<byte, object> { { 1, targetLocation.photonView.ViewID } };
        __result = (CellModule)ObjectFactory.InstantiateRuntimeObject(
            moduleRef.AssetGuid, targetLocation.WorldPosition, targetLocation.WorldRotation, instantiationData);
        return false;
    }
}

[HarmonyPatch(typeof(CompositeWeaponBuildBox), nameof(BuildBox.BuildModule))]
internal static class ForgeCompositeBuildBoxAttachBehavior
{
    static void Postfix(CellModule __result) => ForgeAttachHelper.TryAttach(__result);
}

// CarryableInteract.StartInteraction is the game's single dispatch point for the
// player's primary interact input, both empty-handed and while carrying. When the
// target is one of our ForgeInteractables we take over completely (insert relic /
// load box) and skip the vanilla flow. Everything else — including grabbing docked
// items back out of the Forge, and the Commit button (held via a different input
// pathway, EnvironmentInteract, not this Ability) — stays vanilla; UpgradeForgeBehavior.Update
// reconciles state afterwards.
[HarmonyPatch(typeof(CarryableInteract), nameof(CarryableInteract.StartInteraction))]
internal static class ForgeCarryableInteractPatch
{
    private static readonly AccessTools.FieldRef<CarryableInteract, bool> LockInteractionRef =
        AccessTools.FieldRefAccess<CarryableInteract, bool>("lockInteraction");

    static bool Prefix(CarryableInteract __instance, AbstractInteractable interactable)
    {
        if (interactable is not ForgeInteractable forgeTarget || forgeTarget.Forge == null)
            return true;

        var player = LocalPlayer.Instance;
        if (player == null || player.IsBusy || LockInteractionRef(__instance))
            return false;

        forgeTarget.Forge.HandleInteraction(forgeTarget.Kind, forgeTarget.Anchor, player);
        return false;
    }
}

internal static class ForgeAttachHelper
{
    // MovingSpacePlatform.colliderObjects — private; reflected only so
    // RegisterShipPlatformCollision can log whether AddColliderObject actually
    // did something or found the module already registered.
    private static readonly FieldInfo _colliderObjectsField =
        AccessTools.Field(typeof(MovingSpacePlatform), "colliderObjects");

    internal static void TryAttach(CellModule module)
    {
        if (module == null) return;
        if (!IsForgeModule(module)) return;

        // The stat collection snapshots CsTags during Awake, before this postfix
        // runs, so newly stamped tags must be resynced via OverrideInitTags.
        bool tagsChanged = EnsureTag(module, CsTagRegistry.ForgeModule);
        tagsChanged |= EnsureTag(module, CsTagRegistry.Utility);
        if (tagsChanged)
            module.Stats.OverrideInitTags(module.CsTags);

        var behavior = module.GetComponent<UpgradeForgeBehavior>();
        if (behavior == null)
        {
            behavior = module.gameObject.AddComponent<UpgradeForgeBehavior>();
            BepinPlugin.Log.LogInfo($"[Forge] Attached UpgradeForgeBehavior to {module.name}");
        }
        behavior.BuildInteractables();

        // Bundle prefabs get none of the mediator wiring vanilla modules do, so the
        // Forge's light had no idea the ship's power system existed.
        ForgePowerLights.Attach(module);

        bool relayered = RelayerHullColliders(module);
        RegisterShipPlatformCollision(module, forceRebuild: relayered);
    }

    // The bundle prefab's solid colliders are all on layer 0 (Default), which the
    // game's collision matrix doesn't pair with carryables — dropped items fall
    // through the Forge. Vanilla module hull geometry is on "MovingPlatform".
    // Forced by name at runtime for the same reason BuildAnchorClickRegion forces
    // "InteractiveObjects": the SDK's layer table doesn't match the game's. The
    // interaction colliders are already triggers by this point and are skipped.
    private static bool RelayerHullColliders(CellModule module)
    {
        int mp = LayerMask.NameToLayer("MovingPlatform");
        if (mp < 0)
        {
            BepinPlugin.Log.LogWarning(
                "[Forge] Layer 'MovingPlatform' not found — hull colliders left as authored; dropped items may fall through.");
            return false;
        }

        int changed = 0;
        foreach (var col in module.GetComponentsInChildren<Collider>(includeInactive: true))
        {
            if (col.isTrigger || col.gameObject.layer == mp) continue;
            col.gameObject.layer = mp;
            changed++;
        }
        if (changed > 0)
            BepinPlugin.Log.LogDebug($"[Forge] Re-layered {changed} hull collider(s) on {module.name} to MovingPlatform.");
        return changed > 0;
    }

    // A module riding the ship only gets solid-geometry collision through
    // MovingSpacePlatform's own PhysicsScene, which mirrors in only the colliders
    // explicitly handed to AddColliderObject (normally done by CellModule.OnPhotonInstantiate
    // -> BuildSocket.SetModule). Since a bundle-loaded module has repeatedly turned
    // out not to get things vanilla modules get for free (see GraftModuleComponents),
    // this registers defensively rather than trusting that chain blind.
    private static void RegisterShipPlatformCollision(CellModule module, bool forceRebuild = false)
    {
        var platform = module.GetComponentInParent<MovingSpacePlatform>();
        if (platform == null)
        {
            BepinPlugin.Log.LogDebug($"[Forge] {module.name}: no MovingSpacePlatform in parents yet — skipping collider registration.");
            return;
        }

        bool alreadyRegistered = _colliderObjectsField?.GetValue(platform) is IDictionary dict
            && dict.Contains(module.gameObject);

        // AddColliderObject is NOT idempotent: it clones fresh shadow colliders into
        // the sim scene before colliderObjects.TryAdd(obj, list), and silently drops
        // the clones it just made if the key's already present — they stay in the
        // scene untracked, leaking an uncollectable set every call. So Add only when
        // missing; to relayer, Remove first (which destroys the tracked clones) then
        // Add so the rebuilt shadows carry the new layer.
        if (!alreadyRegistered)
            platform.AddColliderObject(module.gameObject);
        else if (forceRebuild)
        {
            platform.RemoveColliderObject(module.gameObject);
            platform.AddColliderObject(module.gameObject);
        }
        BepinPlugin.Log.LogDebug(
            $"[Forge] Ship-platform collider registration for {module.name}: " +
            (alreadyRegistered
                ? (forceRebuild ? "rebuilt shadow colliders on the re-layered hull." : "was already present (vanilla's own path ran).")
                : "was MISSING — registered it now."));
    }

    private static bool EnsureTag(CellModule module, Gameplay.Tags.CsTag tag)
    {
        if (tag == null) return false;
        if (module.CsTags != null && System.Array.IndexOf(module.CsTags, tag) >= 0) return false;
        module.AddTag(tag);
        return true;
    }

    private static bool HasForgeTag(CellModule module) =>
        module.CsTags != null &&
        System.Array.IndexOf(module.CsTags, CsTagRegistry.ForgeModule) >= 0;

    // Tag check first; otherwise fall back to the prefab name. The name fallback
    // can't be replaced by a tag-only check: the metem prefab carries only the
    // VoidCrewAsset marker and anchor transforms — game CsTag assets can't be
    // serialized into it, so a fresh build's CellModule arrives untagged.
    private static bool IsForgeModule(CellModule module)
    {
        if (module == null) return false;
        if (HasForgeTag(module)) return true;
        var name = module.name ?? "";
        if (name.EndsWith("(Clone)")) name = name[..^7].TrimEnd();
        return name == UpgradeForgeBehavior.PrefabName;
    }
}
