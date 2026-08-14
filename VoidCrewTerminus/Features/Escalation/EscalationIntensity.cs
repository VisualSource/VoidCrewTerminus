namespace VoidCrewTerminus.Escalation;

// The single answer to "how much extra pressure applies right now?"
//
// Five sites used to open with the same four-line preamble — read
// IsScalingActive, read DifficultyScalar, cap it against EscalationScalarCap,
// read a per-jump rate, bail if the scalar is zero — each spelling the config
// defaults inline. The cap default alone appeared in six places with nothing
// keeping them in step, so retuning enemy pressure meant finding every copy.
//
// Snapshot semantics: Current reads ambient state ONCE, so a patch that checks
// the gate and then computes a multiplier can't observe a scalar that changed
// in between. The constructor takes every input explicitly — that's the seam the
// tests use, since Current reaches into two static singletons and the config.
public readonly struct EscalationIntensity
{
    // Whether escalation has been switched on for this run at all (enough bosses
    // defeated). Loot tier biasing deliberately ignores this — see
    // LootTableEscalationPatch — so it is NOT folded into the other members.
    public bool Active { get; }

    // Uncapped scalar, as it drives loot tiers and dev display.
    public int RawScalar { get; }

    // Scalar after the enemy-scaling cap. This is the one every enemy-pressure
    // calculation should use: the raw value keeps climbing all run, but enemy
    // pressure has to plateau or a deep run spawns unbounded networked ships.
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

    // The early-exit every enemy-scaling path shares: dormant runs and a
    // zero scalar both mean "leave vanilla alone".
    public bool AffectsEnemies => Active && Scalar > 0;

    // Fractional HP/damage bonus. Checked against zero separately from
    // AffectsEnemies because a zero or negative RATE also means no-op, and
    // applying a zero-value StatMod would register a modifier for nothing.
    public float StatBonus => Scalar * StatRate;

    public float StatMultiplier => 1f + StatBonus;

    public float DensityMultiplier => 1f + Scalar * DensityRate;

    // Spawner intensity scaled for the current pressure. Returns `requested`
    // untouched while dormant, so callers can apply it unconditionally.
    public int ScaleDensity(int requested) =>
        Active ? EnemyScalingHelpers.ScaleIntensity(requested, Scalar, DensityRate) : requested;
}
