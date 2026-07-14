using System.Collections.Generic;
using CG.Game;
using Gameplay.Loot;
using HarmonyLib;
using ResourceAssets;
using VC.Common.CoreData;
using VoidCrewTerminus.Forge;
using VoidCrewTerminus.Escalation;

namespace VoidCrewTerminus.Patches;

// Phase 6 — hooks LootManager's per-sector list build. Runs once per sector on
// every client (setup is deterministic across the network — the mutation is
// seeded from quest+sector so all clients converge on the same reshaped lists).
// Only the host actually consumes the lists to spawn drops.
[HarmonyPatch(typeof(LootManager), "SetupCurrentSectorLootLists")]
internal static class LootTableEscalationPatch
{
    private static readonly AccessTools.FieldRef<LootManager, Dictionary<LootRarities, List<CraftableItemRef>>> LootListsRef =
        AccessTools.FieldRefAccess<LootManager, Dictionary<LootRarities, List<CraftableItemRef>>>("CurrentSectorLootLists");

    static void Postfix(LootManager __instance)
    {
        try
        {
            var lists = LootListsRef(__instance);
            if (lists == null) return;

            // Escalation warm-up gate: no reshaping until the player has cleared
            // the configured number of boss objectives this run.
            if (!SectorEscalation.IsScalingActive) return;

            int scalar = ForgeMeterController.DifficultyScalar;
            int bosses = SectorEscalation.BossesDefeated;
            int seed = ResolveSeed();

            foreach (var kv in lists)
            {
                // Seed per-rarity so a change in one list's contents doesn't
                // shift picks in the others — keeps tuning intuitive.
                SectorEscalation.DowngradeRelics(
                    kv.Value,
                    r => r?.Filename,
                    scalar,
                    bosses,
                    unchecked(seed * 397 ^ (int)kv.Key));
            }

            BepinPlugin.Log.LogDebug(
                $"[Forge] Sector loot reshaped (scalar {scalar}, bosses {bosses}, seed {seed}).");
        }
        catch (System.Exception e)
        {
            BepinPlugin.Log.LogError($"[Forge] LootTableEscalationPatch failed: {e}");
        }
    }

    // Mirror the seed the vanilla shuffle uses (quest.Seed + sector.Id), so our
    // reshape is deterministic across clients and stable within a run.
    private static int ResolveSeed()
    {
        int seed = 0;
        var quest = GameSessionManager.ActiveSession?.ActiveQuest;
        if (quest != null) seed += quest.Seed;
        if (GameSessionManager.ActiveSector != null) seed += GameSessionManager.ActiveSector.Id;
        return seed;
    }
}
