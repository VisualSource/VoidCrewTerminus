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

// Four independent registries, hit by vanilla code with no null-check on a miss. A guid
// present in one and absent from another surfaces as "missing description" hover text,
// no rarity band, a wrong category material, or a KeyNotFoundException every tick out of
// sector-map marker creation.
internal static class VanillaAssetRegistrar
{
    private static CloneStarObjectContainer Objects =>
        ResourceAssetContainer<CloneStarObjectContainer, AbstractCloneStarObject, CloneStarObjectDef>.Instance;

    private static ModuleContainer Modules =>
        ResourceAssetContainer<ModuleContainer, CellModule, ModuleDef>.Instance;

    private static UnlockContainer Unlocks =>
        ResourceAssetContainer<UnlockContainer, UnityEngine.Object, UnlockItemDef>.Instance;

    internal static bool IsAssetRegistered(GUIDUnion guid) => RuntimeAssetsRegister.Instance.HasAsset(guid);

    internal static UnityEngine.Object GetAsset(GUIDUnion guid) => RuntimeAssetsRegister.Instance.GetAsset(guid);

    // The register the game's own converter uses for carryables, making the object
    // resolvable by CustomObjectPool ("#guid" PUN instantiation) and ResourceAssetRef
    // lookups. Add-only — see TryReplaceAsset.
    internal static void RegisterAssetIfAbsent(GUIDUnion guid, UnityEngine.Object asset, string displayName)
    {
        if (RuntimeAssetsRegister.Instance.HasAsset(guid)) return;

        RuntimeAssetsRegister.Instance.RegisterAsset(
            guid, asset, SessionModificationEffect.IsNetworkSpawned,
            new RuntimeAssetInfo { Name = asset.name, DisplayName = displayName });
        KitLog.Log?.LogDebug($"[ModuleKit] Registered asset {asset.name} ({guid.AsHex()}).");
    }

    // RuntimeAssetsRegister exposes no update or remove: RegisterAsset's TryAdd keeps the
    // existing value (logging an error), and its info dictionary uses Add, which throws
    // outright on a duplicate key. The backing dictionary's type arguments are both
    // public, so an existing entry is corrected through it directly.
    internal static bool TryReplaceAsset(GUIDUnion guid, UnityEngine.Object asset)
    {
        var assets = AccessTools.Field(typeof(RuntimeAssetsRegister), "_assets")
            ?.GetValue(RuntimeAssetsRegister.Instance) as Dictionary<GUIDUnion, UnityEngine.Object>;
        if (assets == null) return false;

        assets[guid] = asset;
        return true;
    }

    // Corrects an existing entry rather than skipping it: the game's own runtime-asset
    // import scans the same bundle, so it can get here first with a Path resolved off the
    // wrong object — and Path is what ObjectFactory.InstantiateSpaceObjectByGUID names
    // every spawned instance from. Any entry under one of our guids is ours by
    // construction, so overwriting is safe.
    //
    // IsRuntime on the correction path too: Path is a RuntimeAssetsRegister key, not a
    // Resources path, and a def marked non-runtime resolves through neither.
    internal static void RegisterObjectDef(GUIDUnion guid, string path, ContextInfo context)
    {
        var def = Objects.GetAssetDefById(guid, verbose: false);
        bool fresh = def == null;
        if (fresh) def = new CloneStarObjectDef(guid, path);

        def.Path = path;
        def.Ref.IsRuntime = true;
        def.ContextInfo = context;

        if (fresh) Objects.RegisterRuntimeAsset(guid, def);
        KitLog.Log?.LogDebug($"[ModuleKit] CloneStarObject def for {path} ({guid.AsHex()}) {(fresh ? "registered" : "corrected")}.");
    }

    // The hover subtitle's category band is this lookup, not ContextInfo — and
    // BuildBoxActor.Awake reads it too, for the crate's category material. try/catch:
    // thinly exercised against live ModuleContainer wiring, so fail loud but survivable.
    internal static void RegisterModuleDef(GUIDUnion guid, string path, ECategory category)
    {
        try
        {
            var def = Modules.GetAssetDefById(guid, verbose: false);
            bool fresh = def == null;
            if (fresh) def = new ModuleDef(guid, path);

            def.Category = category;
            def.Path = path;
            def.Ref.IsRuntime = true;

            if (fresh) Modules.RegisterRuntimeAsset(guid, def);
            KitLog.Log?.LogDebug($"[ModuleKit] Module def for {path} ({guid.AsHex()}) {(fresh ? "registered" : "corrected")} as {category}.");
        }
        catch (Exception ex)
        {
            KitLog.Log?.LogError($"[ModuleKit] Failed to register {path} ({guid.AsHex()}) into vanilla ModuleContainer: {ex}");
        }
    }

    // Defaults to no band at all for an unregistered guid. Add-only: UnlockItemDef holds
    // no object reference, so an entry from a previous load survives a hot-reload intact.
    internal static void RegisterRarity(GUIDUnion guid, string path, RarityType rarity)
    {
        if (Unlocks.HasItem(guid)) return;

        try
        {
            Unlocks.RegisterRuntimeAsset(guid, new UnlockItemDef { AssetGuid = guid, Path = path, rarity = rarity });
            KitLog.Log?.LogDebug($"[ModuleKit] Registered {path} ({guid.AsHex()}) into vanilla UnlockContainer as {rarity}.");
        }
        catch (Exception ex)
        {
            KitLog.Log?.LogError($"[ModuleKit] Failed to register {path} ({guid.AsHex()}) into vanilla UnlockContainer: {ex}");
        }
    }

    internal static IResourceAssetContextInfo GetContextInfo(GUIDUnion guid) =>
        Objects.GetAssetDefById(guid, verbose: false)?.ContextInfo;
}
