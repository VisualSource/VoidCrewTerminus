using System.Collections.Generic;
using System.Linq;
using CG.Game.Player;
using CG.Game.Scenarios;
using CG.Network;
using CG.Objects;
using CG.Ship.Modules;
using CG.Ship.Object;
using ResourceAssets;
using UnityEngine;
using VoidCrewTerminus.Forge;
using VoidManager.Chat.Router;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Commands;

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
        if (!TerminusConfig.DevMode) return;

        var parts = (arguments ?? "").Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        int from = ForgeCostCurve.MinLevel;
        int to;
        if (parts.Length == 1 && int.TryParse(parts[0], out to))
        { }
        else if (parts.Length == 2 && int.TryParse(parts[0], out from) && int.TryParse(parts[1], out to))
        { }
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
        if (!TerminusConfig.DevMode) return;

        var forge = ForgeCommandHelper.FindNearestForge();
        if (forge == null) { Messaging.Notification("No Upgrade Forge found in scene."); return; }

        var box = forge.ModuleBox;
        var boxDesc = box == null ? "(empty)" : $"{box.name} (ViewID={box.photonView?.ViewID})";
        var levelDesc = box == null ? "—" : $"L{forge.CurrentBoxLevel} → projected L{forge.ProjectedTargetLevel}";
        Messaging.Notification($"[Forge] socket: {boxDesc}  |  {levelDesc}");
        Messaging.Notification($"[Forge] relics: {forge.RelicCount}/{UpgradeForgeBehavior.Capacity} " +
                               $"({string.Join(", ", forge.Relics.Select(r => r == null ? "(null)" : Loot.RelicTierData.NormalizeName(r.name)))})");
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
        if (!TerminusConfig.DevMode) return;

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
        if (!TerminusConfig.DevMode) return;

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
        if (!TerminusConfig.DevMode) return;

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
        if (!TerminusConfig.DevMode) return;
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
        if (!TerminusConfig.DevMode) return;

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
        if (!TerminusConfig.DevMode) return;

        var forge = ForgeCommandHelper.FindNearestForge();
        if (forge == null) { Messaging.Notification("No Upgrade Forge found."); return; }

        // Deliberately renders the same lines the in-world commit button shows —
        // a dev testing !forgecommit should be reading exactly what a player
        // reads, not a parallel set of paraphrases.
        var outcome = forge.TryCommit();
        foreach (var line in ForgeLabels.DescribeCommit(outcome, forge.CurrentBoxLevel, forge.RelicCount))
            Messaging.Notification(line);
    }
}

// Spawns the Forge's BuildBox via AssetLoader.EnsureBuildBoxTemplateReady, which
// clones a live vanilla donor and presets moduleRef on the template before any
// instance's Awake runs. A custom-grafted prefab was tried first, but its
// Rigidbody never connected into MovingSpacePlatform's simulated PhysicsScene
// (root cause never pinned down), so it fell through the ship floor forever;
// cloning a real donor — which already has correct physics/rendering — sidesteps
// that entirely. The donor guid is found once and cached
// (AssetLoader.TryFindDonorBuildBoxGuid); a per-spawn scan of the whole module
// registry previously caused a !forgespawn lag spike.
internal class ForgeSpawnCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgespawn" };
    public override string Description() => "[DevMode] Spawn the Upgrade Forge's BuildBox";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!forgespawn"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode) return;

        var player = LocalPlayer.Instance;
        if (player == null) { Messaging.Notification("Not in an active session."); return; }

        AssetLoader.EnsureBuildBoxTemplateReady();

        if (!TryFindForgeAssetGuid(UpgradeForgeBehavior.BuildBoxPrefabName, out var boxGuid))
        { Messaging.Notification("Forge BuildBox not ready yet — no vanilla BuildBox donor found (is a module installed on the ship?)."); return; }

        var spawnPos = player.transform.position + player.transform.forward * 2f + Vector3.up * 0.5f;
        var spawned = SpawnUtils.SpawnCarryable(boxGuid, spawnPos, Quaternion.identity);
        var box = spawned != null ? spawned.GetComponent<BuildBox>() : null;
        if (box == null) { Messaging.Notification("Failed to instantiate Forge BuildBox."); return; }

        BepinPlugin.Log.LogInfo($"[Forge] Spawned Forge BuildBox ({boxGuid.AsHex()}).");
        Messaging.Notification("Spawned Forge BuildBox. Carry to an empty socket to install.");
    }

    // Walks AssetLoader's runtime asset register for the GameObject whose name
    // matches the shipped prefab name. Internal so BossDefeatHook's award-on-2nd-boss
    // reuses the same lookup for the box GUID.
    internal static bool TryFindForgeAssetGuid(string prefabName, out GUIDUnion guid)
    {
        guid = default;
        var reg = RuntimeAssetsRegister.Instance;
        foreach (var id in reg.GetAllIds())
        {
            var asset = reg.GetAsset(id);
            if (asset != null && asset.name == prefabName)
            {
                guid = id;
                return true;
            }
        }
        return false;
    }
}

// Triggers the boss-defeat care-package reward directly, without needing to defeat
// two bosses first. Same delivery path as the real thing
// (Patches.BossDefeatHook.AwardForgeBuildBox spawns a care package that, once
// opened/destroyed, drops the Forge BuildBox via LootOnDeathDropper).
internal class ForgeBoxDropCommand : PublicCommand
{
    public override string[] CommandAliases() => new[] { "forgeboxdrop" };
    public override string Description() => "[DevMode] Trigger the Forge BuildBox boss-reward care package immediately (same delivery as the real 2nd-boss reward)";
    public override List<Argument> Arguments() => [];
    public override string[] UsageExamples() => ["!forgeboxdrop"];

    public override void Execute(string arguments, int sender)
    {
        if (!TerminusConfig.DevMode) return;
        if (!Net.ForgeNetSync.IsAuthority) { Messaging.Notification("Only the host can trigger the care package."); return; }

        Patches.BossDefeatHook.DebugAwardForgeBuildBox();
        Messaging.Notification("Care package triggered — watch for it flying in near the ship.");
    }
}
