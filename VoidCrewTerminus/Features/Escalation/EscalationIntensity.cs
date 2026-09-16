namespace VoidCrewTerminus.Escalation;

// The single answer to "how much extra pressure applies right now?".
//
// Snapshot semantics: Current reads ambient state ONCE, so a patch that checks the gate and
// then computes a multiplier can't observe a scalar that changed in between. The constructor
// takes every input explicitly, which is the seam tests use, since Current reaches into two
// static singletons and the config.
public readonly struct EscalationIntensity
{
    // Loot tier biasing deliberately ignores this (see LootTableEscalationPatch), so it is
    // NOT folded into the other members.
    public bool Active { get; }

    // Uncapped scalar, as it drives loot tiers and dev display.
    public int RawScalar { get; }

    // Scalar after the enemy-scaling cap. Every enemy-pressure calculation must use this
    // rather than RawScalar, or a deep run spawns unbounded ships.
    public int Scalar { get; }

    public float StatRate { get; }
    public float DensityRate { get; }

    public EscalationIntensity(bool active, int rawScalar, int scalarCap, float statRate, float densityRate)
    {
        Active = active;
        RawScalar = rawScalar;
        Scalar = EnemyScalingHelpers.CapScalar(rawScalar, scalarCap);
        StatRate = statRate;
        DensityRate = densityRate;
    }

    public static EscalationIntensity Current => new(
        SectorEscalation.IsScalingActive,
        Forge.ForgeMeterController.DifficultyScalar,
        TerminusConfig.ScalarCap,
        TerminusConfig.StatScalarPerJump,
        TerminusConfig.DensityScalarPerJump);

    public bool AffectsEnemies => Active && Scalar > 0;

    // Checked separately from AffectsEnemies: a zero or negative rate is also a no-op, and a
    // zero-value StatMod would register a modifier for nothing.
    public float StatBonus => Scalar * StatRate;

    public float StatMultiplier => 1f + StatBonus;

    public float DensityMultiplier => 1f + Scalar * DensityRate;

    // Returns `requested` untouched while dormant, so callers can apply unconditionally.
    public int ScaleDensity(int requested) =>
        Active ? EnemyScalingHelpers.ScaleIntensity(requested, Scalar, DensityRate) : requested;
}
