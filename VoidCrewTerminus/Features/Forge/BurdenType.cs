namespace VoidCrewTerminus.Forge;

// A cursed relic in a successful commit has a chance to add ONE burden to the
// target module. Modules can accumulate multiple burden TYPES over multiple
// cursed commits, but never two of the same type — WithBurdenAdded is
// idempotent per-type.
//
// None is the "no burden this commit" sentinel used by CommitOutcome.AppliedBurden.
// It is never stored in ForgeSnapshot.Burdens.
public enum BurdenType
{
    None = 0,
    RandomShutoff = 1,
}
