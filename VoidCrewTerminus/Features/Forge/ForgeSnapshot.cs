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

    private readonly string[] _perkSlots;

    private ForgeSnapshot(int level, string[] perkSlots)
    {
        Level = level;
        _perkSlots = perkSlots;
        PerkSlots = perkSlots;
    }

    // The vanilla-baseline snapshot: L3, no perks. Used as the read-modify-save
    // starting point when no snapshot exists yet for a box.
    public static ForgeSnapshot Empty { get; } = new(
        ForgeCostCurve.MinLevel,
        new string[PerkPool.SlotCount]);

    public static ForgeSnapshot Create(int level, IReadOnlyList<string> perkSlots)
    {
        var copy = new string[PerkPool.SlotCount];
        if (perkSlots != null)
            for (int i = 0; i < copy.Length && i < perkSlots.Count; i++)
                copy[i] = perkSlots[i];
        return new ForgeSnapshot(ClampLevel(level), copy);
    }

    public ForgeSnapshot WithLevel(int level)
    {
        int clamped = ClampLevel(level);
        if (clamped == Level) return this;
        return new ForgeSnapshot(clamped, (string[])_perkSlots.Clone());
    }

    public ForgeSnapshot WithPerk(int slot, string perkId)
    {
        if (slot < 0 || slot >= _perkSlots.Length)
            throw new ArgumentOutOfRangeException(nameof(slot));
        var next = (string[])_perkSlots.Clone();
        next[slot] = perkId;
        return new ForgeSnapshot(Level, next);
    }

    private static int ClampLevel(int level) =>
        Math.Max(ForgeCostCurve.MinLevel, Math.Min(ForgeCostCurve.MaxLevel, level));
}
