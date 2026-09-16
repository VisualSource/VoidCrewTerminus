using System;
using System.Collections.Generic;
using CG;
using CG.Ship.Modules;
using CG.Ship.Object;
using Client.Player.Interactions;
using HarmonyLib;
using ResourceAssets;
using UnityEngine;

namespace VoidCrewTerminus.ModuleKit;

// Four independent registries, hit by vanilla code with no null-check on a miss. A guid in
// one and absent from another surfaces as missing hover text, no rarity band, a wrong
// category material, or a KeyNotFoundException every tick from sector-map marker creation.
internal static class VanillaAssetRegistrar
{
    private static CloneStarObjectContainer Objects =>
        ResourceAssetContainer<CloneStarObjectContainer, AbstractCloneStarObject, CloneStarObjectDef>.Instance;

    private static ModuleContainer Modules =>
        ResourceAssetContainer<ModuleContainer, CellModule, ModuleDef>.Instance;

    private static UnlockContainer Unlocks =>
        ResourceAssetContainer<UnlockContainer, UnityEngine.Object, UnlockItemDef>.Instance;

    internal static UnityEngine.Object GetAsset(GUIDUnion guid) => RuntimeAssetsRegister.Instance.GetAsset(guid);

    // The register the game's own converter uses for carryables, making the object resolvable
    // by CustomObjectPool ("#guid" PUN instantiation). Add-only; see TryReplaceAsset.
    internal static void RegisterAssetIfAbsent(GUIDUnion guid, UnityEngine.Object asset, string displayName)
    {
        if (RuntimeAssetsRegister.Instance.HasAsset(guid)) return;

        RuntimeAssetsRegister.Instance.RegisterAsset(
            guid, asset, SessionModificationEffect.IsNetworkSpawned,
            new RuntimeAssetInfo { Name = asset.name, DisplayName = displayName });
        BepinPlugin.Log.LogDebug($"[ModuleKit] Registered asset {asset.name} ({guid.AsHex()}).");
    }

    // RuntimeAssetsRegister exposes no update or remove: RegisterAsset's TryAdd keeps the
    // existing value, and its info dictionary uses Add, which throws on a duplicate key.
    // The backing dictionary is reached directly so an existing entry can be corrected.
    internal static bool TryReplaceAsset(GUIDUnion guid, UnityEngine.Object asset)
    {
        var assets = AccessTools.Field(typeof(RuntimeAssetsRegister), "_assets")
            ?.GetValue(RuntimeAssetsRegister.Instance) as Dictionary<GUIDUnion, UnityEngine.Object>;
        if (assets == null) return false;

        assets[guid] = asset;
        return true;
    }

    // Corrects an existing entry rather than skipping it: the game's own import scans the same
    // bundle and can get here first with a Path resolved off the wrong object, and Path is
    // what every spawned instance is named from. IsRuntime is set on the correction path too,
    // because Path is a RuntimeAssetsRegister key and a non-runtime def resolves through neither.
    internal static void RegisterObjectDef(GUIDUnion guid, string path, ContextInfo context)
    {
        var def = Objects.GetAssetDefById(guid, verbose: false);
        bool fresh = def == null;
        if (fresh) def = new CloneStarObjectDef(guid, path);

        AssignPath(def.Ref, path);
        def.Ref.IsRuntime = true;
        def.ContextInfo = context;

        if (fresh) Objects.RegisterRuntimeAsset(guid, def);
        BepinPlugin.Log.LogDebug($"[ModuleKit] CloneStarObject def for {path} ({guid.AsHex()}) {(fresh ? "registered" : "corrected")}.");
    }

    // The hover subtitle's category band is this lookup, not ContextInfo, and BuildBoxActor
    // reads it for the crate's material. Caught rather than thrown: thinly exercised against
    // live ModuleContainer wiring, so it should fail loud but survivable.
    internal static void RegisterModuleDef(GUIDUnion guid, string path, ECategory category)
    {
        try
        {
            var def = Modules.GetAssetDefById(guid, verbose: false);
            bool fresh = def == null;
            if (fresh) def = new ModuleDef(guid, path);

            def.Category = category;
            AssignPath(def.Ref, path);
            def.Ref.IsRuntime = true;

            if (fresh) Modules.RegisterRuntimeAsset(guid, def);
            BepinPlugin.Log.LogDebug($"[ModuleKit] Module def for {path} ({guid.AsHex()}) {(fresh ? "registered" : "corrected")} as {category}.");
        }
        catch (Exception ex)
        {
            BepinPlugin.Log.LogError($"[ModuleKit] Failed to register {path} ({guid.AsHex()}) into vanilla ModuleContainer: {ex}");
        }
    }

    // An unregistered guid gets no band at all. Add-only: UnlockItemDef holds no object
    // reference, so an entry from a previous load survives a hot-reload intact.
    internal static void RegisterRarity(GUIDUnion guid, string path, RarityType rarity)
    {
        if (Unlocks.HasItem(guid)) return;

        try
        {
            Unlocks.RegisterRuntimeAsset(guid, new UnlockItemDef { AssetGuid = guid, Path = path, rarity = rarity });
            BepinPlugin.Log.LogDebug($"[ModuleKit] Registered {path} ({guid.AsHex()}) into vanilla UnlockContainer as {rarity}.");
        }
        catch (Exception ex)
        {
            BepinPlugin.Log.LogError($"[ModuleKit] Failed to register {path} ({guid.AsHex()}) into vanilla UnlockContainer: {ex}");
        }
    }

    // A plain `def.Path = path` does not stick: the setter writes _pathCache but leaves
    // _cachedPathGuid at Empty, so the next getter sees them disagree, re-resolves through
    // ResourcePaths (which knows nothing about a runtime guid) and overwrites with "".
    // Reading once first reconciles the two, after which the assignment survives.
    private static void AssignPath(ResourceAssetRef reference, string path)
    {
        _ = reference.Path;
        reference.Path = path;
    }

    internal static IResourceAssetContextInfo GetContextInfo(GUIDUnion guid) =>
        Objects.GetAssetDefById(guid, verbose: false)?.ContextInfo;

    internal static string GetObjectDefPath(GUIDUnion guid) =>
        Objects.GetAssetDefById(guid, verbose: false)?.Path;
}
