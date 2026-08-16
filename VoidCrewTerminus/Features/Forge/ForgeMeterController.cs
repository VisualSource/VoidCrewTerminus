using System;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Forge;

// The Forge levels up by filling a meter from two sources — passive (sector
// jumps, see ForgeSectorHook) and active (alloys fed to the Alloy Terminal).
// Level is the Forge's relic capacity, which gates how big an upgrade step a
// single commit can afford (e.g. L9→L10 costs 4 relics → needs Forge L4).
public static class ForgeMeterController
{
    // Raised on level change so installed Forges can update tube visibility.
    public static event Action<int> LevelChanged;

    // Swappable so ApplyNetworkState and AddMeter stay callable from the unit test
    // host: Messaging.Notification reaches into real Gameplay.Chat types that don't
    // exist there, which would otherwise crash any test exercising either path (see
    // ForgeNetSyncGateTests, which installs a no-op). Production never reassigns this.
    internal static Action<string> Notify = message => Messaging.Notification(message);

    public const int MinLevel = 1;
    // One level per relic tube on the prefab. Levels beyond 4 don't unlock bigger
    // single steps (the priciest cost-curve step is 4 relics) but allow multi-step
    // commits — e.g. 6 relics = L3→L7 in one go.
    public const int MaxLevel = 6;

    public static int Level { get; private set; } = MinLevel;
    public static float Meter { get; private set; }
    public static int DifficultyScalar { get; private set; }

    public static int Capacity => Level;

    public static bool IsMaxed => Level >= MaxLevel;

    // Base × Multiplier^(level−1) → defaults 100, 150, 225 for L1→2→3→4.
    public static float ThresholdFor(int level)
    {
        float baseThreshold = TerminusConfig.MeterBaseThreshold;
        float multiplier = TerminusConfig.MeterLevelMultiplier;
        return baseThreshold * (float)Math.Pow(multiplier, level - MinLevel);
    }

    public static void ResetForRun()
    {
        Level = MinLevel;
        Meter = 0f;
        DifficultyScalar = 0;
        LevelChanged?.Invoke(Level);
    }

    // Called by ForgeSectorHook under the same de-dup gate as the meter award, so
    // bouncing between sectors can't farm the scalar.
    public static void IncrementDifficultyScalar()
    {
        IncrementDifficultyScalarBy(1);
    }

    public static void IncrementDifficultyScalarBy(int amount)
    {
        if (amount <= 0) return;
        DifficultyScalar += amount;
        BepinPlugin.Log.LogDebug($"[Forge] DifficultyScalar +{amount} → {DifficultyScalar}");
    }

    public static void SetDifficultyScalar(int value)
    {
        DifficultyScalar = Math.Max(0, value);
        BepinPlugin.Log.LogDebug($"[Forge] DifficultyScalar set to {DifficultyScalar} (dev)");
    }

    // Client-side apply of a host-authoritative state broadcast. Never call on the
    // authority; that path goes through AddMeter/IncrementDifficultyScalar.
    //
    // A level-up DOES get a Notification here, unlike every other field applied
    // silently: Messaging.Notification only ever inserts into the caller's own
    // chat window (VoidManager doesn't broadcast it), so without this the
    // "Forge reached level N" line would only ever show up on whichever machine
    // actually spent the alloys, never on other clients — even though the
    // level-up applies to the whole crew's Forge.
    internal static void ApplyNetworkState(int scalar, float meter, int level)
    {
        DifficultyScalar = Math.Max(0, scalar);
        Meter = Math.Max(0f, meter);
        int clamped = Math.Max(MinLevel, Math.Min(MaxLevel, level));
        bool levelChanged = clamped != Level;
        Level = clamped;
        if (levelChanged)
        {
            LevelChanged?.Invoke(Level);
            Notify(LevelUpMessage(Level));
        }
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
        Notify($"Forge Meter +{amount:0.#} ({source}) — {Describe()}");

        bool leveled = false;
        while (!IsMaxed && Meter >= ThresholdFor(Level))
        {
            Meter -= ThresholdFor(Level);
            Level++;
            leveled = true;
            Notify(LevelUpMessage(Level));
        }
        if (IsMaxed) Meter = 0f;
        if (leveled) LevelChanged?.Invoke(Level);

        BepinPlugin.Log.LogInfo($"[Forge] Meter +{amount:0.#} from {source} → L{Level}, {Meter:0.#}");
    }

    // Shared with ApplyNetworkState so the host's own level-up line and the
    // client-side broadcast one can't drift apart.
    private static string LevelUpMessage(int level) => level >= MaxLevel
        ? $"The Forge reached level {level} — maximum capacity ({level} relics)."
        : $"The Forge reached level {level} — capacity {level} relics.";

    // Mirrors the Fabricator's payment flow (GameSessionSuppliesManager.
    // ModifyAlloyCount), which silently no-ops for non-master clients — hence
    // the IsMine gate with an honest message routing non-hosts through the
    // network request below instead.
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
            // Alloys are spent against the host's authoritative supplies; a client
            // asks the host to spend on its behalf and gets the result via the
            // state broadcast.
            Net.ForgeNetSync.RequestAlloySpend();
            message = "Requested the host feed the Forge — the meter will update shortly.";
            return false;
        }

        int spend = Math.Max(1, TerminusConfig.AlloySpendPerUse);
        if (supplies.AlloyAmount < spend)
        {
            message = $"Not enough alloys ({supplies.AlloyAmount}/{spend}).";
            return false;
        }

        supplies.ModifyAlloyCount(-spend, ResourceChangeAlloy.FABRICATORUPGRADE, GUIDUnion.Empty());
        AddMeter(spend * TerminusConfig.MeterPerAlloy, $"{spend} alloys");
        message = null;
        return true;
    }

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
