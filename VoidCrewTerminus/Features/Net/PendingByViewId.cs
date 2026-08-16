using System.Collections.Generic;

namespace VoidCrewTerminus.Net;

// Photon can deliver a mod message before the PhotonView it targets has been
// instantiated locally, so the value is buffered by ViewID and drained from
// OnPhotonInstantiate once the object appears. A drained entry must be removed,
// not left behind, or it would reapply to any later instantiate that reuses
// the same ViewID.
internal sealed class PendingByViewId<T>
{
    private readonly Dictionary<int, T> _pending = new();

    internal int Count => _pending.Count;

    // A newer broadcast for the same ViewID overwrites an undrained older one.
    internal void Buffer(int viewId, T value) => _pending[viewId] = value;

    internal bool TryTake(int viewId, out T value)
    {
        if (!_pending.TryGetValue(viewId, out value)) return false;
        _pending.Remove(viewId);
        return true;
    }

    // ViewIDs are scoped to a room; a leftover entry could apply to an unrelated
    // object reassigned the same ID in the next room.
    internal void Clear() => _pending.Clear();
}
