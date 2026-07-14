using System;
using System.Collections.Generic;

namespace VoidCrewTerminus.Forge;

public enum CommitStatus
{
    Ok,
    NoModule,
    NoRelics,
    AlreadyAtMax,
    InvalidModuleLevel,
    InsufficientRelics,
    MissingViewId,
}

// Inputs to a commit. Built by UpgradedForgeBehavior from its scene state so the
// calculator stays free of Unity types.
public readonly struct CommitRequest
{
    public int CurrentLevel { get; }
    public IReadOnlyList<Loot.RelicTier> RelicTiers { get; }   // FIFO — position 0 is consumed first
    public IReadOnlyList<string> RelicNames { get; }           // FIFO, parallel to RelicTiers — used for signature lookup
    public IReadOnlyList<bool> RelicIsCursed { get; }          // FIFO, parallel to RelicTiers — Phase 7-B; consumed by Phase 7-C
    public ForgeCategory Category { get; }
    public IReadOnlyList<string> PerkSlots { get; }            // current slot contents; null/empty = free

    public CommitRequest(
        int currentLevel,
        IReadOnlyList<Loot.RelicTier> relicTiers,
        IReadOnlyList<string> relicNames,
        IReadOnlyList<bool> relicIsCursed,
        ForgeCategory category,
        IReadOnlyList<string> perkSlots)
    {
        CurrentLevel = currentLevel;
        RelicTiers = relicTiers ?? Array.Empty<Loot.RelicTier>();
        RelicNames = relicNames ?? Array.Empty<string>();
        RelicIsCursed = relicIsCursed ?? Array.Empty<bool>();
        Category = category;
        PerkSlots = perkSlots ?? Array.Empty<string>();
    }
}

// Result of a commit attempt. The caller applies successful outcomes to its state
// (mutate pending, destroy relics). Failure carries only a Status; success fields
// (NewLevel, RelicsConsumed, ...) are populated.
public readonly struct CommitOutcome
{
    public CommitStatus Status { get; }
    public int NewLevel { get; }
    public int RelicsConsumed { get; }
    public Loot.RelicTier BestTier { get; }

    // Perk-roll fields — RolledPerk is null when no roll was attempted OR when the
    // roll gate failed. RollAttempted disambiguates.
    public PerkDefinition RolledPerk { get; }
    public int TargetSlot { get; }        // -1 unless RolledPerk != null
    public float RollChance { get; }
    public bool RollAttempted { get; }

    private CommitOutcome(
        CommitStatus status, int newLevel, int relicsConsumed, Loot.RelicTier bestTier,
        PerkDefinition rolledPerk, int targetSlot, float rollChance, bool rollAttempted)
    {
        Status = status;
        NewLevel = newLevel;
        RelicsConsumed = relicsConsumed;
        BestTier = bestTier;
        RolledPerk = rolledPerk;
        TargetSlot = targetSlot;
        RollChance = rollChance;
        RollAttempted = rollAttempted;
    }

    public static CommitOutcome Failure(CommitStatus status) =>
        new(status, 0, 0, Loot.RelicTier.Common, null, -1, 0f, false);

    public static CommitOutcome Success(
        int newLevel, int relicsConsumed, Loot.RelicTier bestTier,
        PerkDefinition rolledPerk, int targetSlot, float rollChance, bool rollAttempted) =>
        new(CommitStatus.Ok, newLevel, relicsConsumed, bestTier,
            rolledPerk, targetSlot, rollChance, rollAttempted);
}

// Pure commit algorithm — cost curve walk, best-tier tie-break, perk roll.
// Unity-free. RNG is injected so both branches of the roll (gate + pool pick) are
// deterministic in tests.
public static class UpgradeCommitCalculator
{
    // nextRandom returns uniform [0, 1) values; default delegates to UnityEngine.Random.value.
    public static CommitOutcome Calculate(CommitRequest request, Func<float> nextRandom = null)
    {
        nextRandom ??= () => UnityEngine.Random.value;

        if (request.RelicTiers.Count == 0)
            return CommitOutcome.Failure(CommitStatus.NoRelics);

        int currentLevel = request.CurrentLevel;
        if (currentLevel < ForgeCostCurve.MinLevel)
            return CommitOutcome.Failure(CommitStatus.InvalidModuleLevel);
        if (currentLevel >= ForgeCostCurve.MaxLevel)
            return CommitOutcome.Failure(CommitStatus.AlreadyAtMax);

        int nextStepCost = ForgeCostCurve.CostForNextLevel(currentLevel);
        if (request.RelicTiers.Count < nextStepCost)
            return CommitOutcome.Failure(CommitStatus.InsufficientRelics);

        int newLevel = ForgeCostCurve.MaxReachable(currentLevel, request.RelicTiers.Count);
        int relicsConsumed = ForgeCostCurve.RelicsRequired(currentLevel, newLevel);

        // Best tier among the relics actually consumed drives the perk gamble.
        // Multi-relic commits roll once, at the quality of their best relic.
        var bestTier = Loot.RelicTier.Common;
        int scanUpTo = Math.Min(relicsConsumed, request.RelicTiers.Count);
        for (int i = 0; i < scanUpTo; i++)
            if (request.RelicTiers[i] > bestTier) bestTier = request.RelicTiers[i];

        return RollPerk(newLevel, relicsConsumed, bestTier, request, nextRandom);
    }

    private static CommitOutcome RollPerk(
        int newLevel, int relicsConsumed, Loot.RelicTier bestTier,
        CommitRequest request, Func<float> nextRandom)
    {
        int slot = PerkPool.TargetSlot(request.PerkSlots, bestTier);
        if (slot < 0)
            return CommitOutcome.Success(newLevel, relicsConsumed, bestTier,
                rolledPerk: null, targetSlot: -1, rollChance: 0f, rollAttempted: false);

        float chance = PerkPool.RollChance(bestTier);
        if (nextRandom() >= chance)
            return CommitOutcome.Success(newLevel, relicsConsumed, bestTier,
                rolledPerk: null, targetSlot: -1, rollChance: chance, rollAttempted: true);

        // Signature priority: walk consumed relics in FIFO order, return the first
        // signature perk we find. Multi-relic commits use the earliest flagship
        // relic's signature — matches the FIFO consumption order and keeps the
        // rule simple to reason about.
        var signaturePerk = PickSignature(relicsConsumed, request, nextRandom);
        if (signaturePerk != null)
            return CommitOutcome.Success(newLevel, relicsConsumed, bestTier,
                rolledPerk: signaturePerk, targetSlot: slot, rollChance: chance, rollAttempted: true);

        var pool = PerkPool.PoolFor(request.Category);
        if (pool.Count == 0)
            return CommitOutcome.Success(newLevel, relicsConsumed, bestTier,
                rolledPerk: null, targetSlot: -1, rollChance: chance, rollAttempted: true);

        int idx = (int)(nextRandom() * pool.Count);
        if (idx >= pool.Count) idx = pool.Count - 1;   // guard against nextRandom() == 1f
        var perk = pool[idx];

        return CommitOutcome.Success(newLevel, relicsConsumed, bestTier,
            rolledPerk: perk, targetSlot: slot, rollChance: chance, rollAttempted: true);
    }

    // Signature lookup: for each consumed relic in FIFO order, check if it has
    // authored signature perks. First flagship wins. Returns null if no signatures
    // exist for any consumed relic, in which case the caller falls back to the
    // category pool.
    private static PerkDefinition PickSignature(
        int relicsConsumed, CommitRequest request, Func<float> nextRandom)
    {
        int scanUpTo = Math.Min(relicsConsumed, request.RelicNames.Count);
        for (int i = 0; i < scanUpTo; i++)
        {
            var sigs = PerkPool.SignaturesFor(request.RelicNames[i]);
            if (sigs.Count == 0) continue;
            int idx = (int)(nextRandom() * sigs.Count);
            if (idx >= sigs.Count) idx = sigs.Count - 1;
            return sigs[idx];
        }
        return null;
    }
}
