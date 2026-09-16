using CG.Space;
using Gameplay.Utilities;

namespace VoidCrewTerminus.Escalation;

public static class EnemyScalingHelpers
{
    // Never shrinks the requested value: negative deltas pass through unamplified.
    // System.Math, not Mathf, so this stays testable outside Unity.
    public static int ScaleIntensity(int requested, int scalar, float rate)
    {
        if (scalar <= 0 || rate <= 0f) return requested;
        int scaled = (int)System.Math.Ceiling(requested * (1f + scalar * rate));
        return System.Math.Max(requested, scaled);
    }

    // A cap <= 0 means uncapped. Enemy pressure must plateau on a deep run even though
    // the raw scalar keeps climbing for loot tiers and display.
    public static int CapScalar(int scalar, int cap) =>
        cap > 0 ? System.Math.Min(scalar, cap) : scalar;

    // Per CG.Space.Factions: Hollows (3) and Remnant (2) are hostile; wildlife (4)
    // and neutral (0) are ignored; player Metem (1) is never touched.
    public static bool IsEnemyFaction(int faction) =>
        faction == Factions.HOLLOWS_ID || faction == Factions.REMNANT_ID;

    public static bool IsPlayerFaction(int faction) =>
        faction == Factions.METEM_ID;
}

// Shared owner for StatMods on enemy stat collections, so they can be cleared in bulk.
public sealed class EnemyScalingSource : IModifierSource
{
    public static readonly EnemyScalingSource Instance = new();
    private EnemyScalingSource() { }
}
