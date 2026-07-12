using System.Collections.Generic;
using System.Linq;
using CG.Game.Player;
using CG.Network;
using CG.Objects;
using CG.Ship.Modules;
using CG.Ship.Object;
using Photon.Pun;
using ResourceAssets;
using UnityEngine;
using VoidCrewTerminus.Forge;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

// Phase 3 dev commands — drive the UpgradeForgeBehavior state machine end-to-end
// via chat, so the test plan runs without needing physical socket wiring.
//
// These commands all gate on TerminusConfig.EnableDevMode. Once
// ForgeInteractionPatch's socket wiring is fleshed out (post-preflight), most of
// these become redundant with the in-world interactions.
internal static class ForgeCommandHelper
{
    public static UpgradeForgeBehavior FindNearestForge()
    {
        var player = LocalPlayer.Instance;
        if (player == null) return null;
        return UpgradeForgeBehavior.FindNearest(player.transform.position);
    }

    public static Vector3 PlayerPosition() =>
        LocalPlayer.Instance != null ? LocalPlayer.Instance.transform.position : Vector3.zero;

    public static BuildBox NearestBuildBox(Vector3 pos) =>
        UnityEngine.Object.FindObjectsOfType<BuildBox>()
            .OrderBy(b => (b.transform.position - pos).sqrMagnitude)
            .FirstOrDefault();

    public static GameObject NearestRelic(Vector3 pos)
    {
        // Any CarryableObject whose (normalized) name is recognised by RelicTierData
        // or starts with "Relic_" is a candidate.
        return UnityEngine.Object.FindObjectsOfType<CarryableObject>()
            .Select(c => c.gameObject)
            .Where(UpgradeForgeBehavior.IsRelic)
            .OrderBy(g => (g.transform.position - pos).sqrMagnitude)
            .FirstOrDefault();
    }
}

internal class ForgeCostCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgecost" };
    public override string Description() => "[DevMode] Show the relic cost of a Forge upgrade path";
    public override List<Argument> Arguments() => [new("%to_level"), new("%from_level?")];
    public override string[] UsageExamples() => ["!forgecost 4", "!forgecost 3 10", "!forgecost 9 10"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var parts = (arguments ?? "").Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        int from = ForgeCostCurve.MinLevel;
        int to;
        if (parts.Length == 1 && int.TryParse(parts[0], out to))
        { /* from stays at MinLevel */ }
        else if (parts.Length == 2 && int.TryParse(parts[0], out from) && int.TryParse(parts[1], out to))
        { /* both parsed */ }
        else
        {
            Messaging.Notification("Usage: !forgecost <toLevel> | !forgecost <fromLevel> <toLevel>");
            return;
        }

        var cost = ForgeCostCurve.RelicsRequired(from, to);
        Messaging.Notification($"L{from}→L{to}: {cost} relic{(cost == 1 ? "" : "s")} (curve = [{ForgeCostCurve.DescribeCurrent()}])");
    }
}

internal class ForgeStatusCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgestatus" };
    public override string Description() => "[DevMode] Dump the state of the nearest Upgrade Forge";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!forgestatus"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var forge = ForgeCommandHelper.FindNearestForge();
        if (forge == null) { Messaging.Notification("No Upgrade Forge found in scene."); return; }

        var box = forge.ModuleBox;
        var boxDesc = box == null ? "(empty)" : $"{box.name} (ViewID={box.photonView?.ViewID})";
        var levelDesc = box == null ? "—" : $"L{forge.CurrentBoxLevel} → projected L{forge.ProjectedTargetLevel}";
        Messaging.Notification($"[Forge] socket: {boxDesc}  |  {levelDesc}");
        Messaging.Notification($"[Forge] relics: {forge.RelicCount}/{UpgradeForgeBehavior.Capacity} " +
                               $"({string.Join(", ", forge.Relics.Select(r => r == null ? "(null)" : RelicTierData.NormalizeName(r.name)))})");
    }
}

internal class ForgeTargetCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgetarget" };
    public override string Description() => "[DevMode] Load the nearest BuildBox into the nearest Forge module socket";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!forgetarget"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var forge = ForgeCommandHelper.FindNearestForge();
        if (forge == null) { Messaging.Notification("No Upgrade Forge found."); return; }
        if (forge.HasModule) { Messaging.Notification("Forge module socket is already full."); return; }

        var box = ForgeCommandHelper.NearestBuildBox(forge.transform.position);
        if (box == null) { Messaging.Notification("No BuildBox found nearby. Deconstruct a module first."); return; }

        if (forge.TryTakeModule(box))
            Messaging.Notification($"Loaded {box.name} into Forge (ViewID={box.photonView?.ViewID}, current L{forge.CurrentBoxLevel})");
        else
            Messaging.Notification("Failed to load BuildBox.");
    }
}

internal class ForgeReleaseModuleCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgerelease" };
    public override string Description() => "[DevMode] Release the module currently in the nearest Forge socket";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!forgerelease"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var forge = ForgeCommandHelper.FindNearestForge();
        if (forge == null) { Messaging.Notification("No Upgrade Forge found."); return; }
        if (forge.TryReleaseModule(out var box))
            Messaging.Notification($"Released {box?.name} from Forge socket.");
        else
            Messaging.Notification("Forge socket already empty.");
    }
}

internal class ForgeInsertCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgeinsert" };
    public override string Description() => "[DevMode] Insert the nearest relic into the nearest Forge (optional count)";
    public override List<Argument> Arguments() => [new("%count?")];
    public override string[] UsageExamples() => ["!forgeinsert", "!forgeinsert 4"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var forge = ForgeCommandHelper.FindNearestForge();
        if (forge == null) { Messaging.Notification("No Upgrade Forge found."); return; }

        int count = 1;
        if (!string.IsNullOrWhiteSpace(arguments) &&
            !int.TryParse(arguments.Trim(), out count))
        {
            Messaging.Notification("Usage: !forgeinsert [count]");
            return;
        }

        int inserted = 0;
        for (int i = 0; i < count; i++)
        {
            var relic = ForgeCommandHelper.NearestRelic(forge.transform.position);
            if (relic == null) break;
            if (!forge.TryInsertRelic(relic)) break;
            inserted++;
        }

        Messaging.Notification(inserted == 0
            ? "No relics could be inserted (none nearby, or Forge is full)."
            : $"Inserted {inserted} relic{(inserted == 1 ? "" : "s")}. Forge holds {forge.RelicCount}/{UpgradeForgeBehavior.Capacity}.");
    }
}

internal class ForgeEjectRelicCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgeeject" };
    public override string Description() => "[DevMode] Eject the relic at the given index (0-based) from the nearest Forge";
    public override List<Argument> Arguments() => [new("%index")];
    public override string[] UsageExamples() => ["!forgeeject 0", "!forgeeject 3"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;
        if (!int.TryParse((arguments ?? "").Trim(), out int index))
        {
            Messaging.Notification("Usage: !forgeeject <index>");
            return;
        }

        var forge = ForgeCommandHelper.FindNearestForge();
        if (forge == null) { Messaging.Notification("No Upgrade Forge found."); return; }

        if (forge.TryEjectRelic(index, out var relic))
            Messaging.Notification($"Ejected {relic?.name}. Forge holds {forge.RelicCount}.");
        else
            Messaging.Notification($"No relic at index {index}.");
    }
}

internal class ForgeMarkCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgemark" };
    public override string Description() => "[DevMode] Dump how the docked box's vanilla mark resolves (upgrade-chain lookup)";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!forgemark"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var forge = ForgeCommandHelper.FindNearestForge();
        if (forge == null) { Messaging.Notification("No Upgrade Forge found."); return; }

        var dump = forge.DescribeBoxMark();
        Messaging.Notification(dump);
        BepinPlugin.Log.LogInfo($"[Forge] {dump}");
    }
}

internal class ForgeCommitCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgecommit" };
    public override string Description() => "[DevMode] Commit the pending upgrade on the nearest Forge";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!forgecommit"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var forge = ForgeCommandHelper.FindNearestForge();
        if (forge == null) { Messaging.Notification("No Upgrade Forge found."); return; }

        var result = forge.TryCommit(out int newLevel, out int consumed, out string perkResult);
        switch (result)
        {
            case UpgradeForgeBehavior.CommitResult.Ok:
                Messaging.Notification($"Committed: L{newLevel} (consumed {consumed} relics; {forge.RelicCount} remain).");
                if (perkResult != null) Messaging.Notification(perkResult);
                break;
            case UpgradeForgeBehavior.CommitResult.NoModule:
                Messaging.Notification("Cannot commit: module socket is empty.");
                break;
            case UpgradeForgeBehavior.CommitResult.NoRelics:
                Messaging.Notification("Cannot commit: no relics inserted.");
                break;
            case UpgradeForgeBehavior.CommitResult.AlreadyAtMax:
                Messaging.Notification("Cannot commit: module already at L10.");
                break;
            case UpgradeForgeBehavior.CommitResult.InsufficientRelics:
                var cost = ForgeCostCurve.CostForNextLevel(forge.CurrentBoxLevel);
                Messaging.Notification($"Cannot commit: next level requires {cost} relics, have {forge.RelicCount}.");
                break;
            case UpgradeForgeBehavior.CommitResult.MissingViewId:
                Messaging.Notification("Cannot commit: BuildBox has no PhotonView.");
                break;
        }
    }
}

// Phase-3 test-only path for installing the Forge before shipping a bespoke BuildBox
// prefab. Spawns a vanilla module BuildBox as a "donor" (so we get a live networked
// BuildBox with all its carryable/construction plumbing intact), then re-points its
// moduleRef at the modded Forge GUID. When the player completes construction on a
// socket, BuildBox.BuildModule reads moduleRef.AssetGuid and instantiates the Forge.
//
// Caveats:
//   - The donor's visual mesh is what the player sees while carrying — the "correct"
//     Forge mesh only appears once construction completes.
//   - The moduleRef mutation is client-local. BuildModule fires on whoever completes
//     construction, so the tester needs to be that player (typically the host).
//   - Deconstructing the Forge afterwards would throw ArgumentNullException — the
//     Forge module prefab has no BuildBoxRef set. Not required for phase-3 tests.
internal class ForgeSpawnCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgespawn" };
    public override string Description() => "[DevMode] Spawn a BuildBox re-pointed to build the Upgrade Forge";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!forgespawn"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.EnableDevMode.Value) return;

        var player = LocalPlayer.Instance;
        if (player == null) { Messaging.Notification("Not in an active session."); return; }
        if (!PhotonNetwork.IsMasterClient)
        { Messaging.Notification("Only the host can spawn a Forge BuildBox (moduleRef mutation is client-local)."); return; }

        if (!TryFindForgeModuleGuid(out var forgeGuid, out var forgeAssetName))
        { Messaging.Notification("Forge module not registered in RuntimeAssetsRegister — did the metem bundle load?"); return; }

        if (!TryFindDonorBuildBoxGuid(out var donorGuid, out var donorModuleName))
        { Messaging.Notification("No donor module BuildBox found in ModuleContainer."); return; }

        var spawnPos = player.transform.position + player.transform.forward * 2f + Vector3.up * 0.5f;
        var box = ObjectFactory.InstantiateSpaceObjectByGUID<BuildBox>(donorGuid, spawnPos, Quaternion.identity);
        if (box == null) { Messaging.Notification($"Failed to instantiate donor BuildBox ({donorModuleName})."); return; }

        if (box.moduleRef == null)
            box.moduleRef = new CloneStarObjectRef(forgeGuid) { IsRuntime = true };
        else
        {
            box.moduleRef.AssetGuid = forgeGuid;
            box.moduleRef.IsRuntime = true;
        }

        BepinPlugin.Log.LogInfo(
            $"[Forge] Spawned Forge BuildBox: donor='{donorModuleName}' → moduleRef re-pointed to '{forgeAssetName}' ({forgeGuid.AsHex()})");
        Messaging.Notification(
            $"Spawned Forge BuildBox (donor art: {donorModuleName}). Carry to an empty socket to install.");
    }

    // The mod's UpgradeForgeModuleCell.prefab is registered by RuntimeAssetsAPI at
    // startup (see AssetLoader); walk the register looking for the GameObject whose
    // name matches the shipped prefab name.
    private static bool TryFindForgeModuleGuid(out GUIDUnion guid, out string assetName)
    {
        guid = default;
        assetName = null;
        var reg = RuntimeAssetsRegister.Instance;
        foreach (var id in reg.GetAllIds())
        {
            var asset = reg.GetAsset(id);
            if (asset == null) continue;
            var goName = asset.name ?? "";
            if (goName == UpgradeForgeBehavior.PrefabName)
            {
                guid = id;
                assetName = goName;
                return true;
            }
        }
        return false;
    }

    // Any vanilla CellModule with a BuildBoxRef pointing to a plain (non-composite)
    // BuildBox works as a donor. We just want a networked BuildBox instance we can
    // mutate — the module it *would* build if left alone is irrelevant.
    private static bool TryFindDonorBuildBoxGuid(out GUIDUnion guid, out string moduleName)
    {
        guid = default;
        moduleName = null;
        foreach (var def in ResourceAssetContainer<ModuleContainer, CellModule, ModuleDef>.Instance.AssetDescriptions)
        {
            if (def == null) continue;
            var module = def.Asset;
            if (module == null) continue;
            var boxRef = module.BuildBoxRef;
            if (boxRef == null || boxRef.IsNull) continue;
            var boxAsset = boxRef.Asset;
            if (boxAsset == null || boxAsset is CompositeWeaponBuildBox) continue;
            guid = boxRef.AssetGuid;
            moduleName = module.name;
            return true;
        }
        return false;
    }
}
