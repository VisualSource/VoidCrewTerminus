using System;
using System.Collections.Generic;

namespace VoidCrewTerminus.Forge;

// Opaque, immutable value carrying a module's forge overlay across the deconstruct
// → reconstruct bridge. Also the shape that UpgradeForgeBehavior.TryCommit folds
// commit outcomes into before saving.
//
// Any change to what a "forge overlay" contains happens here; the two conversion
// points on ForgeModuleState (Snapshot / ApplySnapshot) then fail to compile until
// they match — the forcing function that stops the old parallel-shapes drift.
public sealed class ForgeSnapshot
{
    public int Level { get; }
    public IReadOnlyList<string> PerkSlots { get; }
    public IReadOnlyList<BurdenType> Burdens { get; }

    private readonly string[] _perkSlots;
    private readonly BurdenType[] _burdens;

    private ForgeSnapshot(int level, string[] perkSlots, BurdenType[] burdens)
    {
        Level = level;
        _perkSlots = perkSlots;
        PerkSlots = perkSlots;
        _burdens = burdens;
        Burdens = burdens;
    }

    // The vanilla-baseline snapshot: L3, no perks, no burdens. Used as the
    // read-modify-save starting point when no snapshot exists yet for a box.
    public static ForgeSnapshot Empty { get; } = new(
        ForgeCostCurve.MinLevel,
        new string[PerkPool.SlotCount],
        Array.Empty<BurdenType>());

    public static ForgeSnapshot Create(int level, IReadOnlyList<string> perkSlots, IReadOnlyList<BurdenType> burdens = null)
    {
        var copy = new string[PerkPool.SlotCount];
        if (perkSlots != null)
            for (int i = 0; i < copy.Length && i < perkSlots.Count; i++)
                copy[i] = perkSlots[i];
        var burdensCopy = burdens != null && burdens.Count > 0
            ? DedupBurdens(burdens)
            : Array.Empty<BurdenType>();
        return new ForgeSnapshot(ClampLevel(level), copy, burdensCopy);
    }

    public ForgeSnapshot WithLevel(int level)
    {
        int clamped = ClampLevel(level);
        if (clamped == Level) return this;
        return new ForgeSnapshot(clamped, (string[])_perkSlots.Clone(), (BurdenType[])_burdens.Clone());
    }

    public ForgeSnapshot WithPerk(int slot, string perkId)
    {
        if (slot < 0 || slot >= _perkSlots.Length)
            throw new ArgumentOutOfRangeException(nameof(slot));
        var next = (string[])_perkSlots.Clone();
        next[slot] = perkId;
        return new ForgeSnapshot(Level, next, (BurdenType[])_burdens.Clone());
    }

    // Add a burden to the module's set. Idempotent — if the type is already
    // present (or if `burden == None`), returns `this` unchanged. Different
    // burden types stack; identical types don't.
    public ForgeSnapshot WithBurdenAdded(BurdenType burden)
    {
        if (burden == BurdenType.None) return this;
        for (int i = 0; i < _burdens.Length; i++)
            if (_burdens[i] == burden) return this;

        var next = new BurdenType[_burdens.Length + 1];
        Array.Copy(_burdens, next, _burdens.Length);
        next[_burdens.Length] = burden;
        return new ForgeSnapshot(Level, (string[])_perkSlots.Clone(), next);
    }

    // Dedup at construction time — Create() may receive an unfiltered list from
    // Snapshot() callers. Preserves first-occurrence order.
    private static BurdenType[] DedupBurdens(IReadOnlyList<BurdenType> source)
    {
        var seen = new List<BurdenType>();
        for (int i = 0; i < source.Count; i++)
        {
            var b = source[i];
            if (b == BurdenType.None) continue;
            if (seen.Contains(b)) continue;
            seen.Add(b);
        }
        return seen.ToArray();
    }

    private static int ClampLevel(int level) =>
        Math.Max(ForgeCostCurve.MinLevel, Math.Min(ForgeCostCurve.MaxLevel, level));
}
