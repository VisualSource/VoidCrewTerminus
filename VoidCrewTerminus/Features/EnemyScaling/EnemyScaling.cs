using CG.Space;
using Gameplay.Utilities;

namespace VoidCrewTerminus.EnemyScaling;

// Enemy-scaling primitives — kept as a distinct feature area (not under Forge)
// because Sector Escalation is the first consumer but not the only one: the
// Leech encounter and any future scalar-driven mechanic will also want the
// same faction filters, stat-mod source, and intensity math.
//
// Pure static helpers so the density math is testable without a game runtime.
// The IModifierSource singleton at the bottom is what stat-mod patches use to
// tag their StatMods (matches the ForgeModuleState pattern for player mods).
public static class EnemyScalingHelpers
{
    // Scale a spawner intensity int by the current DifficultyScalar. Multiplicative
    // with a "never shrink the requested value" clamp so negative deltas
    // (scenarios reducing intensity) pass through unamplified.
    //
    // Uses System.Math (not UnityEngine.Mathf) so the pure algorithm is testable
    // outside the Unity runtime.
    public static int ScaleIntensity(int requested, int scalar, float rate)
    {
        if (scalar <= 0 || rate <= 0f) return requested;
        int scaled = (int)System.Math.Ceiling(requested * (1f + scalar * rate));
        // Max preserves the intended direction in both signs: positive requests
        // grow (Max of (5, 8) = 8), negative deltas resist amplification (Max of
        // (-5, -8) = -5), zero stays zero.
        return System.Math.Max(requested, scaled);
    }

    // Enemy factions per CG.Space.Factions. Hollows (3) and Remnant (2) are the
    // two hostile factions we scale; wildlife (4) and neutral (0) are ignored,
    // player Metem (1) is never touched.
    public static bool IsEnemyFaction(int faction) =>
        faction == Factions.HOLLOWS_ID || faction == Factions.REMNANT_ID;

    public static bool IsPlayerFaction(int faction) =>
        faction == Factions.METEM_ID;
}

// Marker source for StatMods applied to enemy stat collections. One singleton
// so all enemy-scaling mods share an owner, which lets a future "clear all
// enemy scaling" pass call Stats.RemoveModifier(EnemyScalingSource.Instance)
// on any collection. Not currently used (mods stick for the object's lifetime),
// but keeps the door open for tuning-during-run dev commands.
public sealed class EnemyScalingSource : IModifierSource
{
    public static readonly EnemyScalingSource Instance = new();
    private EnemyScalingSource() { }
}
