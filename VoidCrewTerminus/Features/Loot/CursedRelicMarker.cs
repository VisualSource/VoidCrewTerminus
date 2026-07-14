using UnityEngine;

namespace VoidCrewTerminus.Loot;

// Marker component attached to relic GameObjects that have been flagged cursed
// at spawn time. Presence of the component = cursed. Once attached, the flag is
// immutable for the lifetime of that specific relic instance.
//
// Phase 7-B scaffold — the component exists and can be queried, but nothing in
// the commit/perk pipeline consumes it yet. Phase 7-C wires it into the
// cursed-augmented perk pool and Maintenance Burden.
public sealed class CursedRelicMarker : MonoBehaviour
{
    // Returns true if the given GameObject carries a CursedRelicMarker.
    // Safe for null input and for objects without the component.
    public static bool IsCursed(GameObject go) =>
        go != null && go.GetComponent<CursedRelicMarker>() != null;

    // Attach the marker if not already present. Idempotent — a second call
    // on an already-cursed relic is a no-op.
    public static void MarkCursed(GameObject go)
    {
        if (go == null) return;
        if (go.GetComponent<CursedRelicMarker>() == null)
            go.AddComponent<CursedRelicMarker>();
    }

    // Remove the marker if present. Only used by dev commands — production
    // cursed state is baked in at spawn.
    public static void Uncurse(GameObject go)
    {
        if (go == null) return;
        var marker = go.GetComponent<CursedRelicMarker>();
        if (marker != null) UnityEngine.Object.Destroy(marker);
    }
}
