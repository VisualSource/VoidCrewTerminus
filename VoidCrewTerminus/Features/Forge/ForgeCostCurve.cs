using System;
using System.Linq;

namespace VoidCrewTerminus.Forge;

// Cost curve for module upgrades L4..L10. Config-driven via TerminusConfig.ForgeCostCurve
// (comma-separated string). Falls back to the design default 1/1/2/2/3/3/4 = 16 total
// if the config string is malformed. Curve is re-parsed on every access so tuning during
// dev mode does not require a restart.
public static class ForgeCostCurve
{
    public const int MinLevel = 3;
    public const int MaxLevel = 10;

    private static readonly int[] Default = { 1, 1, 2, 2, 3, 3, 4 };

    // Relics required to advance from `fromLevel` to `toLevel`. Both bounded to [3, 10].
    // Returns 0 for no-op / downgrade attempts.
    public static int RelicsRequired(int fromLevel, int toLevel)
    {
        fromLevel = Math.Max(MinLevel, Math.Min(MaxLevel, fromLevel));
        toLevel = Math.Max(MinLevel, Math.Min(MaxLevel, toLevel));
        if (toLevel <= fromLevel) return 0;

        var curve = Parse();
        int sum = 0;
        for (int level = fromLevel + 1; level <= toLevel; level++)
            sum += CostForLevel(curve, level);
        return sum;
    }

    // Highest level reachable from `fromLevel` given `relicsAvailable` — greedy walk up the curve.
    public static int MaxReachable(int fromLevel, int relicsAvailable)
    {
        fromLevel = Math.Max(MinLevel, Math.Min(MaxLevel, fromLevel));
        if (relicsAvailable <= 0) return fromLevel;

        var curve = Parse();
        int level = fromLevel;
        int remaining = relicsAvailable;
        while (level < MaxLevel)
        {
            int step = CostForLevel(curve, level + 1);
            if (step > remaining) break;
            remaining -= step;
            level++;
        }
        return level;
    }

    public static int CostForNextLevel(int currentLevel)
    {
        if (currentLevel >= MaxLevel) return int.MaxValue;
        return CostForLevel(Parse(), currentLevel + 1);
    }

    private static int CostForLevel(int[] curve, int level)
    {
        // curve[0] = cost of L3→L4, curve[1] = L4→L5, ... curve[6] = L9→L10
        int idx = level - MinLevel - 1;
        if (idx < 0 || idx >= curve.Length) return int.MaxValue;
        return Math.Max(1, curve[idx]);
    }

    private static int[] Parse()
    {
        var raw = TerminusConfig.ForgeCostCurve?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return Default;

        var parts = raw.Split(',');
        if (parts.Length != Default.Length) return Default;

        var parsed = new int[Default.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out var n) || n < 1)
                return Default;
            parsed[i] = n;
        }
        return parsed;
    }

    // Diagnostic — returns the currently-effective curve as a comma-separated string
    // (default values if the config is malformed).
    public static string DescribeCurrent() => string.Join(",", Parse());

    public static int TotalToMax => Enumerable.Range(MinLevel + 1, MaxLevel - MinLevel)
        .Sum(level => CostForLevel(Parse(), level));
}
