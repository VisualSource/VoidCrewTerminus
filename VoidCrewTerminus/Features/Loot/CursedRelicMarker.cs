using UnityEngine;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.Loot;

// Presence of the component means cursed. Once attached, the flag and the baked burden are
// immutable for that relic instance: the burden is picked at spawn from the relic's
// BurdenAffinity, never re-rolled at commit time.
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

    // No-op if already present: the baked burden is immutable once set.
    public static void MarkCursed(GameObject go, BurdenType burden)
    {
        if (go == null) return;
        if (burden == BurdenType.None) return;
        var existing = go.GetComponent<CursedRelicMarker>();
        if (existing != null) return;
        var marker = go.AddComponent<CursedRelicMarker>();
        marker.BakedBurden = burden;
    }

    // Dev commands only; production cursed state is baked in at spawn.
    public static void Uncurse(GameObject go)
    {
        if (go == null) return;
        var marker = go.GetComponent<CursedRelicMarker>();
        if (marker != null) UnityEngine.Object.Destroy(marker);
    }
}
