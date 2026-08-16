using UnityEngine;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Loot;

// Marker component attached to relic GameObjects flagged cursed at spawn time.
// Presence of the component = cursed. Once attached, the flag AND the baked
// burden type are immutable for the lifetime of that specific relic instance —
// the burden is a property of the relic, picked from its RelicTierEntry.
// BurdenAffinity at spawn, not re-rolled at commit time.
public sealed class CursedRelicMarker : MonoBehaviour
{
    public BurdenType BakedBurden { get; private set; } = BurdenType.RandomShutoff;

    public static bool IsCursed(GameObject go) =>
        go != null && go.GetComponent<CursedRelicMarker>() != null;

    public static BurdenType GetBurden(GameObject go)
    {
        if (go == null) return BurdenType.None;
        var marker = go.GetComponent<CursedRelicMarker>();
        return marker != null ? marker.BakedBurden : BurdenType.None;
    }

    // If already present, no-op — the baked burden is immutable once set.
    public static void MarkCursed(GameObject go, BurdenType burden)
    {
        if (go == null) return;
        if (burden == BurdenType.None) return;
        var existing = go.GetComponent<CursedRelicMarker>();
        if (existing != null) return;
        var marker = go.AddComponent<CursedRelicMarker>();
        marker.BakedBurden = burden;
    }

    // Only used by dev commands — production cursed state is baked in at spawn.
    public static void Uncurse(GameObject go)
    {
        if (go == null) return;
        var marker = go.GetComponent<CursedRelicMarker>();
        if (marker != null) UnityEngine.Object.Destroy(marker);
    }
}
