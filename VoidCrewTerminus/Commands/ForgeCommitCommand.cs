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
        if (!TerminusConfig.DevMode) return;

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

// Spawns the Forge's BuildBox by its registered GUID, same as any other
// runtime-registered asset — AssetLoader.EnsureBuildBoxTemplateReady does the
// real work, cloning a live vanilla donor and presetting its moduleRef to the
// Forge module BEFORE any instance ever spawns (not relabeling one after the
// fact — an earlier version of this command tried that and left the box in a
// broken half-donor-half-Forge state: no hover label, couldn't be placed or
// dropped, held wrong, because BuildBoxActor.Awake and apparently other
// systems too key off moduleRef and had already run against the donor's
// ORIGINAL one by the time the relabel happened).
//
// This whole approach — clone a real donor instead of grafting components onto
// our own custom prefab — exists because the custom-grafted prefab never
// worked: correct Rigidbody/Collider/PhotonView/BuildBoxActor and all, spawned
// instances never got connected into MovingSpacePlatform's simulated
// PhysicsScene (no "..._simulated" shadow object ever appeared — confirmed
// live via Runtime Unity Editor) and just fell through the ship floor forever.
// Root cause never pinned down; a real vanilla BuildBox already has 100%
// correct physics/rendering/simulation, so cloning one sidesteps the mystery
// entirely instead of reverse-engineering it component by component.
//
// This IS the donor-borrowing approach the codebase used before the dedicated
// BuildBox existed (see git history) — reintroduced because the dedicated
// prefab never actually worked, this time with the donor guid found ONCE and
// cached (AssetLoader.TryFindDonorBuildBoxGuid), not re-scanned per spawn — a
// per-spawn scan of the whole module registry was the likely cause of the
// ORIGINAL !forgespawn lag spike that motivated moving away from donors in the
// first place, and that risk doesn't apply to a cached one-time lookup.
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

    // The mod's runtime-registered Forge assets (module cell at startup, BuildBox
    // lazily — see AssetLoader.EnsureBuildBoxTemplateReady) are registered by
    // AssetLoader; walk the register looking for the GameObject whose name
    // matches the shipped prefab name. Internal so BossDefeatHook's
    // award-on-2nd-boss reuses the same lookup for the box GUID.
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

// Triggers the boss-defeat care-package reward directly, without needing to
// actually defeat two bosses in a run first. Same delivery path as the real
// thing (Patches.BossDefeatHook.AwardForgeBuildBox — spawns a vanilla care
// package via SpawnUtils.SpawnCarePackage, which flies in and, once opened/
// destroyed, spawns the Forge BuildBox through LootOnDeathDropper's own
// predefined-item path) — exists specifically to make that whole pipeline
// testable in isolation, since it was still an unverified TODO item after
// everything else about the Forge BuildBox (spawn, hover, deconstruct,
// highlight) got exercised and fixed this session.
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
