using System;
using System.Collections.Generic;
using CG.Ship.Modules;
using CG.Ship.Object;
using Gameplay.Power;
using UnityEngine;
using VoidCrewTerminus.Loot;

namespace VoidCrewTerminus.Forge;

// Three facts that must stay together: they are read off the same object, and
// every downstream rule (best-tier tie-break, signature lookup, burden pick)
// walks them in the same FIFO order.
internal readonly struct RelicFacts
{
    internal RelicTier Tier { get; }
    internal string Name { get; }
    internal BurdenType CursedBurden { get; }   // None = not cursed

    internal RelicFacts(RelicTier tier, string name, BurdenType cursedBurden)
    {
        Tier = tier;
        Name = name;
        CursedBurden = cursedBurden;
    }

    // A relic destroyed between docking and committing. Still occupies its FIFO slot but
    // contributes nothing: Common is the floor tier, a null name matches no signature.
    internal static RelicFacts Missing => new(RelicTier.Common, null, BurdenType.None);
}

internal readonly struct CommitResolution
{
    internal CommitOutcome Outcome { get; }

    // Identical to the input when the outcome failed; nothing is half-applied.
    internal ForgeSnapshot Updated { get; }

    internal CommitResolution(CommitOutcome outcome, ForgeSnapshot updated)
    {
        Outcome = outcome;
        Updated = updated;
    }
}

// Split on the Unity line: Execute reads the scene and is untestable by construction; Resolve
// is pure. Static rather than on UpgradeForgeBehavior because the host resolving a client's
// request has no docked box of its own, so box and relics always arrive as arguments.
internal static class ForgeCommit
{
    // Runs on the authority only. Does NOT consume relics: the operator owns them and does
    // that after this returns, which is what makes the networked destroy propagate.
    internal static CommitOutcome Execute(BuildBox box, IReadOnlyList<GameObject> relics)
    {
        if (box == null) return CommitOutcome.Failure(CommitStatus.NoModule);
        if (box.photonView == null) return CommitOutcome.Failure(CommitStatus.MissingViewId);

        int viewId = box.photonView.ViewID;
        int currentLevel = UpgradeForgeBehavior.LevelOfBox(box);
        var current = ForgeStateStore.TryPeekSnapshot(viewId, out var stored) ? stored : ForgeSnapshot.Empty;
        var facts = ReadRelics(relics);

        var resolution = Resolve(
            current,
            currentLevel,
            PerkPool.CategoryOf(box.moduleRef?.Asset as CellModule),
            facts);

        var outcome = resolution.Outcome;
        if (outcome.Status != CommitStatus.Ok) return outcome;

        var rolledBurden = outcome.AppliedBurden;
        var updated = resolution.Updated;
        if (rolledBurden != BurdenType.None
            && !ForgeModuleState.CanCarry(rolledBurden, (box.moduleRef?.Asset as CellModule)?.GetComponent<PowerDrain>()))
        {
            outcome = outcome.WithoutBurden();
            updated = Apply(current, outcome);
        }

        ForgeStateStore.SaveSnapshot(viewId, updated);

        BepinPlugin.Log.LogInfo(
            $"[Forge] Committed L{currentLevel}→L{outcome.NewLevel} on ViewID={viewId} " +
            $"(consumed {ForgeLabels.Plural(outcome.RelicsConsumed, "relic")}, " +
            $"tier={outcome.BestTier}, perk={ForgeLabels.DescribePerkResult(outcome)})");

        LogPerkCausalChain(outcome, facts);
        LogBurdenCausalChain(rolledBurden, outcome.AppliedBurden, facts);

        // Push the authoritative snapshot to clients (no-ops in solo / no peers).
        Net.ForgeNetSync.BroadcastCommitResult(viewId, resolution.Updated, outcome.RelicsConsumed);

        return outcome;
    }

    // Every read is guarded: a docked relic can be destroyed before the commit lands, and the
    // host resolving a remote request may fail to resolve some of the ViewIDs it was sent.
    private static RelicFacts[] ReadRelics(IReadOnlyList<GameObject> relics)
    {
        int n = relics?.Count ?? 0;
        var facts = new RelicFacts[n];
        for (int i = 0; i < n; i++)
        {
            var go = relics[i];
            facts[i] = go == null
                ? RelicFacts.Missing
                : new RelicFacts(RelicTierData.Get(go.name).Tier, go.name, CursedRelicMarker.GetBurden(go));
        }
        return facts;
    }

    internal static CommitResolution Resolve(
        ForgeSnapshot current, int currentLevel, ForgeCategory category,
        IReadOnlyList<RelicFacts> relics, Func<float> nextRandom = null)
    {
        var outcome = UpgradeCommitCalculator.Calculate(
            ToRequest(current, currentLevel, category, relics), nextRandom);

        return outcome.Status == CommitStatus.Ok
            ? new CommitResolution(outcome, Apply(current, outcome))
            : new CommitResolution(outcome, current);
    }

    // The calculator takes one parallel FIFO array per fact. Unpacked in exactly one place,
    // so the arrays cannot fall out of alignment anywhere else.
    private static CommitRequest ToRequest(
        ForgeSnapshot current, int currentLevel, ForgeCategory category,
        IReadOnlyList<RelicFacts> relics)
    {
        int n = relics?.Count ?? 0;
        var tiers = new RelicTier[n];
        var names = new string[n];
        var burdens = new BurdenType[n];
        for (int i = 0; i < n; i++)
        {
            tiers[i] = relics[i].Tier;
            names[i] = relics[i].Name;
            burdens[i] = relics[i].CursedBurden;
        }
        return new CommitRequest(currentLevel, tiers, names, burdens, category, current.PerkSlots);
    }

    // An edit, not a replacement: perks and burdens from earlier commits ride through
    // untouched. Private on purpose; exposing it would not buy the perk fold a test, since a
    // test cannot produce a non-null RolledPerk without the game runtime. See ForgeCommitTests.
    private static ForgeSnapshot Apply(ForgeSnapshot current, CommitOutcome outcome)
    {
        var updated = current.WithLevel(outcome.NewLevel);
        if (outcome.RolledPerk != null)
            updated = updated.WithPerk(outcome.TargetSlot, outcome.RolledPerk.Id);
        if (outcome.AppliedBurden != BurdenType.None)
            updated = updated.WithBurdenAdded(outcome.AppliedBurden); // idempotent
        return updated;
    }

    // The signature-vs-pool unit test is skipped (StatType init), so these lines are the only
    // evidence of which path produced the perk.
    private static void LogPerkCausalChain(CommitOutcome outcome, IReadOnlyList<RelicFacts> relics)
    {
        if (!outcome.RollAttempted)
        {
            BepinPlugin.Log.LogDebug("[Forge] Perk: no roll attempted (no eligible slot for this tier).");
            return;
        }

        if (outcome.RolledPerk == null)
        {
            BepinPlugin.Log.LogDebug(
                $"[Forge] Perk: roll FAILED at {outcome.RollChance:P0} ({outcome.BestTier}) — no perk.");
            return;
        }

        string consumed = NamesOf(relics);

        if (outcome.RolledPerk.IsSignature)
            BepinPlugin.Log.LogDebug(
                $"[Forge] Perk: SIGNATURE '{outcome.RolledPerk.Id}' preferred over category pool " +
                $"(flagship relic {outcome.RolledPerk.SignatureRelicId}; consumed [{consumed}]) → slot {outcome.TargetSlot + 1}.");
        else
            BepinPlugin.Log.LogDebug(
                $"[Forge] Perk: POOL draw '{outcome.RolledPerk.Id}' (no signature among consumed [{consumed}]) " +
                $"→ slot {outcome.TargetSlot + 1}.");
    }

    // Three distinct outcomes: no roll (no cursed relics), roll failed, and rolled
    // but the target module rejected it (AutoPowerOn) so the relic burned for nothing.
    private static void LogBurdenCausalChain(BurdenType rolled, BurdenType applied, IReadOnlyList<RelicFacts> relics)
    {
        int cursedCount = 0;
        if (relics != null)
            for (int i = 0; i < relics.Count; i++)
                if (relics[i].CursedBurden != BurdenType.None) cursedCount++;

        if (cursedCount == 0)
        {
            BepinPlugin.Log.LogDebug("[Forge] Burden: no cursed relics consumed — no roll.");
            return;
        }

        float chance = TerminusConfig.BurdenChance;
        string result = applied != BurdenType.None ? $"APPLIED {applied}"
            : rolled != BurdenType.None ? $"rolled {rolled}, but target can't carry it (AutoPowerOn) — no effect"
            : "none (roll failed)";
        BepinPlugin.Log.LogInfo($"[Forge] Burden: cursed x{cursedCount} consumed, roll {chance:P0} → {result}.");
    }

    private static string NamesOf(IReadOnlyList<RelicFacts> relics)
    {
        if (relics == null || relics.Count == 0) return "?";
        var names = new string[relics.Count];
        for (int i = 0; i < relics.Count; i++) names[i] = relics[i].Name;
        return string.Join(",", names);
    }
}
