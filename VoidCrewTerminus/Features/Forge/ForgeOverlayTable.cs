using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CG.Ship.Modules;

namespace VoidCrewTerminus.Forge;

// Singleton table mapping live CellModule instances to their forge overlay state.
// Keys are held weakly so destroyed modules don't prevent GC.
// _allStates holds strong refs for clean enumeration during session reset.
// _pending bridges deconstruct → reconstruct: keyed by BuildBox Photon ViewID.
public static class ForgeOverlayTable
{
    private static readonly ConditionalWeakTable<CellModule, ForgeModuleState> _table = new();
    private static readonly List<ForgeModuleState> _allStates = new();
    private static readonly Dictionary<int, int> _pending = new();

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

    // Persist forge level through deconstruct: call when the BuildBox is created.
    public static void SavePendingLevel(int boxViewId, int level) => _pending[boxViewId] = level;

    // Consume a pending level during reconstruction (removes entry on success).
    public static bool TryRestoreLevel(int boxViewId, out int level)
    {
        if (!_pending.TryGetValue(boxViewId, out level)) return false;
        _pending.Remove(boxViewId);
        return true;
    }

    // Read a pending level without consuming it (used by recycle alloy scaling).
    public static bool TryPeekPendingLevel(int boxViewId, out int level) =>
        _pending.TryGetValue(boxViewId, out level);

    // Called on session start/reset — remove all stat mods and discard state.
    public static void ClearAll()
    {
        foreach (var state in _allStates)
            state.Cleanup();
        _allStates.Clear();
        _pending.Clear();

        // ConditionalWeakTable.Clear() removes all entries (existing keys/values collected later by GC).
        // .NET 4.7.2 supports Clear() — confirmed.
        _table.Clear();
    }
}
