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

// The metem prefab carries only named anchor transforms and no game components, so the
// postfixes below attach UpgradeForgeBehavior and spawn click targets as modules build.
[HarmonyPatch(typeof(BuildBox), nameof(BuildBox.BuildModule))]
internal static class ForgeBuildBoxAttachBehavior
{
    static void Postfix(CellModule __result) => ForgeAttachHelper.TryAttach(__result);
}

// Vanilla BuildModule dereferences a def that is null for runtime-registered assets and would
// NRE right after instantiating, so runtime assets take their own factory path. Normal modules
// still take the vanilla flow, and the BuildModule postfixes run either way.
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

// The game's single dispatch point for primary interact, empty-handed or carrying. Forge
// interactables are taken over completely; everything else stays vanilla, including grabbing
// docked items back out and the Commit button (held through EnvironmentInteract instead).
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
    // Private; reflected only so RegisterShipPlatformCollision can log whether
    // AddColliderObject did something or found the module already registered.
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
            BepinPlugin.Log.LogDebug($"[Forge] Attached UpgradeForgeBehavior to {module.name}");
        }
        behavior.BuildInteractables();

        // Bundle prefabs get none of the mediator wiring vanilla modules do, power included.
        ForgePowerLights.Attach(module);

        bool relayered = RelayerHullColliders(module);
        RegisterShipPlatformCollision(module, forceRebuild: relayered);
    }

    // Bundle colliders arrive on layer 0, which the collision matrix doesn't pair with
    // carryables, so dropped items fall through. Vanilla hull geometry is on "MovingPlatform",
    // resolved by name because the SDK's layer table doesn't match the game's.
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

    // Solid-geometry collision comes only from MovingSpacePlatform's PhysicsScene, which
    // mirrors in just the colliders handed to AddColliderObject (normally via
    // BuildSocket.SetModule). Registered defensively because bundle-loaded modules miss
    // wiring vanilla modules get for free.
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

        // AddColliderObject is NOT idempotent: it clones shadow colliders before the TryAdd
        // and silently drops the clones if the key is present, leaking them into the scene
        // untracked. So add only when missing; to relayer, Remove first then Add.
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

    // The name fallback can't be replaced by a tag-only check: game CsTag assets can't be
    // serialized into a metem prefab, so a fresh build's CellModule arrives untagged.
    private static bool IsForgeModule(CellModule module)
    {
        if (module == null) return false;
        if (HasForgeTag(module)) return true;
        var name = module.name ?? "";
        if (name.EndsWith("(Clone)")) name = name[..^7].TrimEnd();
        return name == UpgradeForgeBehavior.PrefabName;
    }
}
