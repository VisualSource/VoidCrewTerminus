using System;
using System.Collections.Generic;

namespace VoidCrewTerminus.Forge;

// Immutable overlay carried across the deconstruct/reconstruct bridge. Any change to what an
// overlay contains happens here; ForgeModuleState's two conversions then fail to compile.
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

    // The read-modify-save starting point when no snapshot exists yet for a box.
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

    // Idempotent. Different burden types stack; identical types don't.
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

    // Layout: [int viewId, int level, string[] perkSlots, int[] burdens, int relicsConsumed].
    // viewId keys a BuildBox for commit results and a CellModule for installed overlays;
    // relicsConsumed is 0 for late-joiner pushes, which replay state rather than report a commit.
    //
    // Create() takes its arguments positionally, so a new snapshot field simply isn't passed
    // and nothing fails to build. ToPayload_CarriesEveryPublicSnapshotField guards that.
    public const int PayloadLength = 5;

    // Empty perk slots travel as "" rather than null, and TryFromPayload normalises them
    // back. Both read as empty elsewhere, but only one spelling should cross the wire.
    public object[] ToPayload(int viewId, int relicsConsumed)
    {
        var perks = new string[_perkSlots.Length];
        for (int i = 0; i < _perkSlots.Length; i++)
            perks[i] = _perkSlots[i] ?? "";

        var burdens = new int[_burdens.Length];
        for (int i = 0; i < _burdens.Length; i++)
            burdens[i] = (int)_burdens[i];

        return new object[] { viewId, Level, perks, burdens, relicsConsumed };
    }

    // An arity check is the only validation the wire has; out parameters take safe defaults
    // when it fails. Level clamping and burden dedup come free via Create.
    public static bool TryFromPayload(
        object[] payload, out int viewId, out ForgeSnapshot snapshot, out int relicsConsumed)
    {
        viewId = 0;
        snapshot = Empty;
        relicsConsumed = 0;
        if (payload == null || payload.Length < PayloadLength) return false;

        viewId = Convert.ToInt32(payload[0]);
        int level = Convert.ToInt32(payload[1]);
        relicsConsumed = Convert.ToInt32(payload[4]);

        var rawPerks = payload[2] as string[] ?? Array.Empty<string>();
        var perks = new string[rawPerks.Length];
        for (int i = 0; i < rawPerks.Length; i++)
            perks[i] = string.IsNullOrEmpty(rawPerks[i]) ? null : rawPerks[i];

        var rawBurdens = payload[3] as int[] ?? Array.Empty<int>();
        var burdens = new BurdenType[rawBurdens.Length];
        for (int i = 0; i < rawBurdens.Length; i++)
            burdens[i] = (BurdenType)rawBurdens[i];

        snapshot = Create(level, perks, burdens);
        return true;
    }

    // Create() may receive an unfiltered list from Snapshot() callers; preserves
    // first-occurrence order.
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
