using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CG.Ship.Modules;

namespace VoidCrewTerminus.Forge;

// Singleton table mapping live CellModule instances to their forge overlay state.
// Keys are held weakly so destroyed modules don't prevent GC.
// _allStates holds strong refs for clean enumeration during session reset.
public static class ForgeOverlayTable
{
    private static readonly ConditionalWeakTable<CellModule, ForgeModuleState> _table = new();
    private static readonly List<ForgeModuleState> _allStates = new();

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

    // Called on session start/reset — remove all stat mods and discard state.
    public static void ClearAll()
    {
        foreach (var state in _allStates)
            state.Cleanup();
        _allStates.Clear();

        // ConditionalWeakTable.Clear() removes all entries (existing keys/values collected later by GC).
        // .NET 4.7.2 supports Clear() — confirmed.
        _table.Clear();
    }
}
