using System;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Forge;

// Phase 5: run-scoped Forge progression. The Forge levels up by filling a meter
// from two sources — passive (+ForgeMeterPerSectorJump per new sector entered,
// see ForgeSectorHook) and active (alloys fed to the Alloy Terminal). The Forge's
// level is its relic capacity, which is what gates how big an upgrade step a
// single commit can afford (L9→L10 costs 4 relics → needs Forge L4).
//
// DifficultyScalar is tracked here but unused until Phase 6 (sector escalation).
// State is local/host-side; Phase 8 adds multiplayer sync.
public static class ForgeMeterController
{
    // Raised whenever the Forge level changes (level-up, dev set, run reset) so
    // installed Forges can update their tube visibility.
    public static event Action<int> LevelChanged;

    public const int MinLevel = 1;
    // One level per relic tube on the prefab. Levels beyond 4 don't unlock bigger
    // single steps (the priciest cost-curve step is 4 relics) but allow multi-step
    // commits — e.g. 6 relics = L3→L7 in one go.
    public const int MaxLevel = 6;

    public static int Level { get; private set; } = MinLevel;
    public static float Meter { get; private set; }
    public static float DifficultyScalar { get; private set; } // Phase 6

    // The first OnSectorEntered of a run is the starting sector, not a jump.
    private static bool _skipNextSectorAward = true;

    public static int Capacity => Level;

    public static bool IsMaxed => Level >= MaxLevel;

    // Meter needed to go from `level` to `level + 1`:
    // Base × Multiplier^(level−1) → defaults 100, 150, 225 for L1→2→3→4.
    public static float ThresholdFor(int level)
    {
        float baseThreshold = TerminusConfig.ForgeMeterBaseThreshold?.Value ?? 100f;
        float multiplier = TerminusConfig.ForgeMeterLevelMultiplier?.Value ?? 1.5f;
        return baseThreshold * (float)Math.Pow(multiplier, level - MinLevel);
    }

    public static void ResetForRun()
    {
        Level = MinLevel;
        Meter = 0f;
        DifficultyScalar = 0f;
        _skipNextSectorAward = true;
        LevelChanged?.Invoke(Level);
    }

    // Consumed by ForgeSectorHook exactly once per run.
    public static bool ConsumeInitialSectorSkip()
    {
        if (!_skipNextSectorAward) return false;
        _skipNextSectorAward = false;
        return true;
    }

    public static void AddMeter(float amount, string source)
    {
        if (amount <= 0f) return;
        if (IsMaxed)
        {
            Meter = 0f;
            return;
        }

        Meter += amount;
        Messaging.Notification($"Forge Meter +{amount:0.#} ({source}) — {Describe()}");

        bool leveled = false;
        while (!IsMaxed && Meter >= ThresholdFor(Level))
        {
            Meter -= ThresholdFor(Level);
            Level++;
            leveled = true;
            Messaging.Notification(Level >= MaxLevel
                ? $"The Forge reached level {Level} — maximum capacity ({Capacity} relics)."
                : $"The Forge reached level {Level} — capacity {Capacity} relics.");
        }
        if (IsMaxed) Meter = 0f;
        if (leveled) LevelChanged?.Invoke(Level);

        BepinPlugin.Log.LogInfo($"[Forge] Meter +{amount:0.#} from {source} → L{Level}, {Meter:0.#}");
    }

    // Alloy Terminal spend. Mirrors the Fabricator's payment flow
    // (GameSessionSuppliesManager.ModifyAlloyCount), which silently no-ops for
    // non-master clients — hence the IsMine gate with an honest message until the
    // Phase 8 ModMessage hop exists.
    public static bool TrySpendAlloys(out string message)
    {
        if (IsMaxed)
        {
            message = $"The Forge is already at maximum level ({MaxLevel}).";
            return false;
        }

        var supplies = GameSessionSuppliesManager.Instance;
        if (supplies == null)
        {
            message = "No supplies available — not in an active run?";
            return false;
        }
        if (!supplies.photonView.IsMine)
        {
            message = "Only the host can feed the Forge alloys (for now).";
            return false;
        }

        int spend = Math.Max(1, TerminusConfig.AlloyTerminalSpendPerUse?.Value ?? 10);
        if (supplies.AlloyAmount < spend)
        {
            message = $"Not enough alloys ({supplies.AlloyAmount}/{spend}).";
            return false;
        }

        supplies.ModifyAlloyCount(-spend, ResourceChangeAlloy.FABRICATORUPGRADE, GUIDUnion.Empty());
        AddMeter(spend * (TerminusConfig.ForgeMeterPerAlloy?.Value ?? 1f), $"{spend} alloys");
        message = null;
        return true;
    }

    // Dev-mode helpers.
    public static void SetLevel(int level)
    {
        Level = Math.Max(MinLevel, Math.Min(MaxLevel, level));
        Meter = 0f;
        LevelChanged?.Invoke(Level);
    }

    public static void SetMeter(float value)
    {
        if (IsMaxed) return;
        Meter = 0f;
        AddMeter(Math.Max(0f, value), "dev");
    }

    public static string Describe() => IsMaxed
        ? $"Forge L{Level} (max) — capacity {Capacity} relics"
        : $"Forge L{Level} — {Meter:0.#}/{ThresholdFor(Level):0.#} to L{Level + 1}, capacity {Capacity} relics";
}
