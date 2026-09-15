namespace VoidCrewTerminus.Forge;

// A cursed relic in a successful commit can add ONE burden. A module accumulates multiple
// burden TYPES over multiple commits but never two of the same, since WithBurdenAdded is
// idempotent per type. None is the "no burden this commit" sentinel and is never stored in
// ForgeSnapshot.Burdens.
public enum BurdenType
{
    None = 0,
    RandomShutoff = 1,
}
