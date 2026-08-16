using CG.Space;
using Gameplay.Utilities;

namespace VoidCrewTerminus.Escalation;

// Kept separate from Forge: Sector Escalation is the first consumer of these
// faction filters and intensity math, but not the only one.
public static class EnemyScalingHelpers
{
    // "Never shrink the requested value" clamp: negative deltas pass through
    // unamplified. Uses System.Math (not Mathf) so this stays testable outside Unity.
    public static int ScaleIntensity(int requested, int scalar, float rate)
    {
        if (scalar <= 0 || rate <= 0f) return requested;
        int scaled = (int)System.Math.Ceiling(requested * (1f + scalar * rate));
        return System.Math.Max(requested, scaled);
    }

    // A cap <= 0 means "uncapped". Deep runs need enemy density/HP/damage to
    // plateau even though the raw scalar keeps climbing for loot tiers/display.
    public static int CapScalar(int scalar, int cap) =>
        cap > 0 ? System.Math.Min(scalar, cap) : scalar;

    // Per CG.Space.Factions: Hollows (3) and Remnant (2) are hostile; wildlife (4)
    // and neutral (0) are ignored; player Metem (1) is never touched.
    public static bool IsEnemyFaction(int faction) =>
        faction == Factions.HOLLOWS_ID || faction == Factions.REMNANT_ID;

    public static bool IsPlayerFaction(int faction) =>
        faction == Factions.METEM_ID;
}

// Marker source for StatMods applied to enemy stat collections — a shared owner
// so a future "clear all enemy scaling" pass can target it in bulk.
public sealed class EnemyScalingSource : IModifierSource
{
    public static readonly EnemyScalingSource Instance = new();
    private EnemyScalingSource() { }
}
