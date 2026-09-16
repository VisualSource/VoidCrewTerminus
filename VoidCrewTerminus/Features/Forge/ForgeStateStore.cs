using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CG.Ship.Modules;

namespace VoidCrewTerminus.Forge;

// The single owner of per-module forge state and of snapshots riding BuildBoxes between
// deconstruct and reconstruct. Keys are held weakly so destroyed modules don't prevent GC;
// _allStates keeps strong refs, needed only for clean enumeration during ClearAll.
public static class ForgeStateStore
{
    private static readonly ConditionalWeakTable<CellModule, ForgeModuleState> _table = new();
    private static readonly List<ForgeModuleState> _allStates = new();
    private static readonly Dictionary<int, ForgeSnapshot> _snapshots = new();

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

    public static void SaveSnapshot(int boxViewId, ForgeSnapshot snapshot) =>
        _snapshots[boxViewId] = snapshot ?? ForgeSnapshot.Empty;

    // Removes the entry, handing the snapshot off to a fresh ForgeModuleState.
    public static bool TryTakeSnapshot(int boxViewId, out ForgeSnapshot snapshot)
    {
        if (!_snapshots.TryGetValue(boxViewId, out snapshot)) return false;
        _snapshots.Remove(boxViewId);
        return true;
    }

    public static bool TryPeekSnapshot(int boxViewId, out ForgeSnapshot snapshot) =>
        _snapshots.TryGetValue(boxViewId, out snapshot);

    // Copied so callers can't mutate the store while enumerating.
    public static IReadOnlyList<KeyValuePair<int, ForgeSnapshot>> AllSnapshots()
    {
        var list = new List<KeyValuePair<int, ForgeSnapshot>>(_snapshots.Count);
        foreach (var kv in _snapshots) list.Add(kv);
        return list;
    }

    // Keyed by PhotonView ViewID for the late-joiner push. A module whose view is gone is
    // skipped rather than sent with a bogus key.
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

    public static void ClearAll()
    {
        foreach (var state in _allStates)
            state.Cleanup();
        _allStates.Clear();
        _snapshots.Clear();
        _table.Clear();
    }
}
