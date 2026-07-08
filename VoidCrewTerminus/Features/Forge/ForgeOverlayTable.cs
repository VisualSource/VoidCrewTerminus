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
    // Overlay state riding a BuildBox between deconstruct and reconstruct.
    public sealed class PendingState
    {
        public int Level = 3;
        public readonly string[] PerkSlots = new string[PerkPool.SlotCount];
    }

    private static readonly ConditionalWeakTable<CellModule, ForgeModuleState> _table = new();
    private static readonly List<ForgeModuleState> _allStates = new();
    private static readonly Dictionary<int, PendingState> _pending = new();

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

    // Get-or-create the pending record for a BuildBox (level defaults to vanilla L3).
    public static PendingState GetOrCreatePending(int boxViewId)
    {
        if (!_pending.TryGetValue(boxViewId, out var pending))
        {
            pending = new PendingState();
            _pending[boxViewId] = pending;
        }
        return pending;
    }

    // Persist forge level through deconstruct: call when the BuildBox is created.
    public static void SavePendingLevel(int boxViewId, int level) =>
        GetOrCreatePending(boxViewId).Level = level;

    // Persist the full overlay (level + perks) through deconstruct.
    public static void SavePendingState(int boxViewId, ForgeModuleState state)
    {
        var pending = GetOrCreatePending(boxViewId);
        pending.Level = state.Level;
        for (int i = 0; i < pending.PerkSlots.Length; i++)
            pending.PerkSlots[i] = i < state.PerkSlots.Count ? state.PerkSlots[i] : null;
    }

    // Consume the pending state during reconstruction (removes entry on success).
    public static bool TryRestorePending(int boxViewId, out PendingState pending)
    {
        if (!_pending.TryGetValue(boxViewId, out pending)) return false;
        _pending.Remove(boxViewId);
        return true;
    }

    // Read the pending state without consuming it (recycle scaling, forge status).
    public static bool TryPeekPending(int boxViewId, out PendingState pending) =>
        _pending.TryGetValue(boxViewId, out pending);

    // Read a pending level without consuming it (used by recycle alloy scaling).
    public static bool TryPeekPendingLevel(int boxViewId, out int level)
    {
        if (_pending.TryGetValue(boxViewId, out var pending)) { level = pending.Level; return true; }
        level = 3;
        return false;
    }

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
