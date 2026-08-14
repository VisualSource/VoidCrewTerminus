using System.Collections.Generic;

namespace VoidCrewTerminus.Net;

// "A broadcast can outrun the object it describes."
//
// Photon delivers a mod message as soon as it arrives, which can be before the
// PhotonView the message refers to has been instantiated locally. Both the
// cursed-relic flags and the installed-module overlays hit this, so both buffer
// the value against its ViewID and drain it from OnPhotonInstantiate once the
// object turns up.
//
// The subtle part is that a drained entry must be REMOVED: leaving it behind
// would reapply the same overlay on every later instantiate that happens to
// reuse the ViewID. That one rule is why this is a module with tests rather than
// two raw dictionaries with the drain logic written out twice.
internal sealed class PendingByViewId<T>
{
    private readonly Dictionary<int, T> _pending = new();

    internal int Count => _pending.Count;

    // Latest value wins: a newer broadcast for the same ViewID supersedes an
    // older one that has not been drained yet.
    internal void Buffer(int viewId, T value) => _pending[viewId] = value;

    // Hands over the buffered value AND forgets it, so it can only ever be
    // applied once.
    internal bool TryTake(int viewId, out T value)
    {
        if (!_pending.TryGetValue(viewId, out value)) return false;
        _pending.Remove(viewId);
        return true;
    }

    // Leaving a room invalidates every buffered entry: ViewIDs are scoped to a
    // room, so a leftover would eventually be applied to an unrelated object that
    // happens to be assigned the same ID next room.
    internal void Clear() => _pending.Clear();
}
