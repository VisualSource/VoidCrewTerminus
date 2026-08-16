using System;
using System.Collections.Generic;

namespace VoidCrewTerminus.Forge;

// Opaque, immutable value carrying a module's forge overlay across the deconstruct
// → reconstruct bridge. Also the shape ForgeCommit folds commit outcomes into
// before saving.
//
// Any change to what a "forge overlay" contains happens here; the two conversion
// points on ForgeModuleState (Snapshot / ApplySnapshot) then fail to compile until
// they match — the compiler is the forcing function against shape drift.
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

    // Idempotent — if the type is already present (or `burden == None`), returns
    // `this` unchanged. Different burden types stack; identical types don't.
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

    // ---- wire form -----------------------------------------------------------
    //
    // The CommitResult and ModuleOverlay messages both carry a snapshot plus a
    // two-field envelope: the PhotonView ViewID the snapshot is keyed to (a
    // BuildBox for commit results, a CellModule for installed overlays), and a
    // relics-consumed count — 0 for the late-joiner pushes, which replay existing
    // state rather than report a fresh commit.
    //
    // Layout: [int viewId, int level, string[] perkSlots, int[] burdens, int relicsConsumed]
    //
    // This lives HERE rather than in the net layer because the net layer had three
    // hand-rolled copies of it, so adding a field to a snapshot used to compile fine
    // and then silently drop it on the wire.
    //
    // Unlike the ForgeModuleState conversions above, the compiler can't catch that
    // hole: Create() takes its arguments positionally, so a new field simply isn't
    // passed and nothing fails to build. The guard is
    // ToPayload_CarriesEveryPublicSnapshotField in ForgeSnapshotTests, which
    // reflects over this type's public properties — keep it updated alongside the
    // two methods below.
    public const int PayloadLength = 5;

    // Empty perk slots travel as "" rather than null; TryFromPayload normalises
    // them back. Both spellings read as empty everywhere else in the mod, but
    // only one should ever cross the wire.
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

    // Returns false with every out parameter at a safe default when the payload is
    // absent or too short — an arity check is the only validation the wire has ever
    // had. Level clamping and burden dedup come free via Create.
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
