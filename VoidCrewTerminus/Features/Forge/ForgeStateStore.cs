using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CG.Ship.Modules;

namespace VoidCrewTerminus.Forge;

// The single owner of per-module forge state AND of snapshots riding BuildBoxes
// between deconstruct and reconstruct. Callers never see the storage shape — only
// ForgeModuleState (per-instance) and ForgeSnapshot (opaque value at the seam).
//
// Keys are held weakly (ConditionalWeakTable) so destroyed modules don't prevent
// GC. _allStates keeps strong refs for clean enumeration during ClearAll only.
public static class ForgeStateStore
{
    private static readonly ConditionalWeakTable<CellModule, ForgeModuleState> _table = new();
    private static readonly List<ForgeModuleState> _allStates = new();
    private static readonly Dictionary<int, ForgeSnapshot> _snapshots = new();

    // ---- per-module lifetime ------------------------------------------------

    public static ForgeModuleState GetOrCreate(CellModule module)
    {
        if (!_table.TryGetValue(module, out var state))
        {
            state = new ForgeModuleState();
            _table.Add(module, state);
            _allStates.Add(state);
            state.Attach(module);
        }
        return state;
    }

    public static bool TryGet(CellModule module, out ForgeModuleState state) =>
        _table.TryGetValue(module, out state);

    // ---- deconstruct → reconstruct snapshot bridge --------------------------

    // Persist a snapshot against a BuildBox's Photon ViewID. Overwrites any prior.
    public static void SaveSnapshot(int boxViewId, ForgeSnapshot snapshot) =>
        _snapshots[boxViewId] = snapshot ?? ForgeSnapshot.Empty;

    // Consume the snapshot for a box (removes the entry). Used by the reconstruct
    // patch to hand the snapshot off to a freshly-created ForgeModuleState.
    public static bool TryTakeSnapshot(int boxViewId, out ForgeSnapshot snapshot)
    {
        if (!_snapshots.TryGetValue(boxViewId, out snapshot)) return false;
        _snapshots.Remove(boxViewId);
        return true;
    }

    // Read without consuming. Used by TryCommit (folding outcome into current state)
    // and the recycle-alloy scaling patch (reads .Level for the multiplier).
    public static bool TryPeekSnapshot(int boxViewId, out ForgeSnapshot snapshot) =>
        _snapshots.TryGetValue(boxViewId, out snapshot);

    // Phase 8-C — all live box snapshots, for the late-joiner overlay push. Copy
    // so callers can't mutate the store while enumerating.
    public static IReadOnlyList<KeyValuePair<int, ForgeSnapshot>> AllSnapshots()
    {
        var list = new List<KeyValuePair<int, ForgeSnapshot>>(_snapshots.Count);
        foreach (var kv in _snapshots) list.Add(kv);
        return list;
    }

    // Phase 8-D — every INSTALLED module's overlay keyed by its PhotonView ViewID,
    // for the late-joiner push. Modules whose view is gone (destroyed, or not yet
    // networked) are skipped rather than sent with a bogus key.
    public static IReadOnlyList<(int ViewId, ForgeSnapshot Snapshot)> AllModuleStates()
    {
        var list = new List<(int, ForgeSnapshot)>();
        foreach (var state in _allStates)
        {
            int viewId = state.ModuleViewId;
            if (viewId <= 0) continue;
            list.Add((viewId, state.Snapshot()));
        }
        return list;
    }

    // ---- run reset ----------------------------------------------------------

    public static void ClearAll()
    {
        foreach (var state in _allStates)
            state.Cleanup();
        _allStates.Clear();
        _snapshots.Clear();
        _table.Clear();
    }
}
