using UnityEngine;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Loot;

// Marker component attached to relic GameObjects that have been flagged cursed
// at spawn time. Presence of the component = cursed. Once attached, the flag
// AND the baked burden type are immutable for the lifetime of that specific
// relic instance.
//
// Phase 7-C: the marker now also carries the specific BurdenType this relic
// will inflict if used in a commit. Picked at spawn time from the relic's
// RelicTierEntry.BurdenAffinity — so the burden is a property of the relic
// instance, not something re-rolled at commit time.
public sealed class CursedRelicMarker : MonoBehaviour
{
    // The specific burden this relic will apply if used in a commit and the
    // application roll passes. Set once at spawn time via MarkCursed.
    public BurdenType BakedBurden { get; private set; } = BurdenType.RandomShutoff;

    // Returns true if the given GameObject carries a CursedRelicMarker.
    // Safe for null input and for objects without the component.
    public static bool IsCursed(GameObject go) =>
        go != null && go.GetComponent<CursedRelicMarker>() != null;

    // Returns the baked burden if the object is cursed; BurdenType.None otherwise.
    public static BurdenType GetBurden(GameObject go)
    {
        if (go == null) return BurdenType.None;
        var marker = go.GetComponent<CursedRelicMarker>();
        return marker != null ? marker.BakedBurden : BurdenType.None;
    }

    // Attach the marker with the specified burden type. If already present,
    // no-op — the baked burden is immutable once set. Reflects the design:
    // curse identity is fixed the moment the relic exists in the world.
    public static void MarkCursed(GameObject go, BurdenType burden)
    {
        if (go == null) return;
        if (burden == BurdenType.None) return;
        var existing = go.GetComponent<CursedRelicMarker>();
        if (existing != null) return;
        var marker = go.AddComponent<CursedRelicMarker>();
        marker.BakedBurden = burden;
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
