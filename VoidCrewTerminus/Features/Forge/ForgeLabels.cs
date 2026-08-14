using System.Collections.Generic;
using System.Text.RegularExpressions;
using VoidCrewTerminus.Loot;

namespace VoidCrewTerminus.Forge;

// The single place forge state turns into player-facing text. Every UI surface
// (hover tooltips, fabricator panel, dev commands) formats through here so the
// vocabulary can't drift between them.
//
// Mk numbering maps DIRECTLY onto module level: L3 = Mk III (vanilla's cap),
// L10 = Mk X. The mod extends vanilla's existing Mk ladder rather than running a
// parallel scale — a forged "Mk VII" reads as strictly better than a vanilla
// "Mk III" with no translation needed.
public static class ForgeLabels
{
    // Colours kept close to vanilla's own tooltip palette (it uses #BDE7FBFF for
    // hints and plain red for warnings).
    private const string ForgeColor = "#7FD4FF";
    private const string BurdenColor = "#FF6B6B";
    private const string MutedColor = "#9AA5AD";

    // Level range is bounded 3-10 (ForgeCostCurve) and vanilla marks are 1-3, so
    // a lookup table beats a general roman-numeral algorithm here.
    private static readonly string[] _roman =
        { "0", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };

    public static string Roman(int n) =>
        n >= 0 && n < _roman.Length ? _roman[n] : n.ToString();

    public static string MarkLabel(int level) => "Mk " + Roman(level);

    // Matches a mark already present at the END of a name: "Mk III", "MK3",
    // "Mk. VII". Anchored so it can only ever strip a trailing mark token.
    //
    // English-centric by necessity — vanilla's display names live inside asset
    // bundles and aren't inspectable from the decompiled source. On a localised
    // name this simply fails to match and we fall through to a plain append,
    // which may duplicate a mark but can never corrupt the name.
    // Case-insensitive on purpose: the exact vanilla casing is unknown, and
    // over-matching is the safe direction — a mark we strip is one we immediately
    // re-append correctly, whereas a mark we miss gets duplicated.
    private static readonly Regex _trailingMark =
        new(@"\s*mk\.?\s*([ivxlc]+|\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Replace any trailing mark in a vanilla display name with the forge level's
    // mark. Safe whether or not the name already carries one.
    public static string RewriteMark(string header, int level)
    {
        string mark = MarkLabel(level);
        if (string.IsNullOrEmpty(header)) return mark;
        return _trailingMark.Replace(header, "") + " " + mark;
    }

    // "1 relic" / "2 relics". Shared so the commit lines, the host-committed
    // notification and anything else counting relics pluralise the same way.
    public static string Plural(int count, string noun) =>
        $"{count} {noun}{(count == 1 ? "" : "s")}";

    public static string BurdenName(BurdenType burden) => burden switch
    {
        BurdenType.RandomShutoff => "Random Shutoff",
        _ => burden.ToString(),
    };

    public static string TierName(RelicTier tier) => tier switch
    {
        RelicTier.Common => "Common",
        RelicTier.Rare => "Rare",
        RelicTier.Legendary => "Legendary",
        _ => tier.ToString(),
    };

    // True when this state is worth surfacing at all. Mirrors ForgeModuleState's
    // own private HasAnyOverlay predicate — an untouched module renders exactly
    // as vanilla, so "unforged" and "not yet synced on a client" collapse to the
    // same silent path.
    public static bool HasOverlay(int level, IReadOnlyList<string> perkSlots, IReadOnlyList<BurdenType> burdens)
    {
        if (level > ForgeCostCurve.MinLevel) return true;
        if (burdens != null && burdens.Count > 0) return true;
        if (perkSlots != null)
            foreach (var id in perkSlots)
                if (!string.IsNullOrEmpty(id)) return true;
        return false;
    }

    // The forge block appended to a build box / module tooltip body.
    // Perk and burden NAMES only — the perks' actual effects already appear in
    // vanilla's stat block just above as real StatMods, so descriptions here
    // would restate them at double the length.
    public static string BuildOverlayBody(int level, IReadOnlyList<string> perkSlots, IReadOnlyList<BurdenType> burdens)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"\n<color={ForgeColor}><b>FORGED  {MarkLabel(level)}</b></color>");

        if (perkSlots != null && perkSlots.Count > 0)
        {
            sb.Append($"\n<color={ForgeColor}>Perks:</color>");
            foreach (var id in perkSlots)
            {
                if (string.IsNullOrEmpty(id))
                    sb.Append($"\n  <color={MutedColor}>• — empty —</color>");
                else if (PerkPool.TryGet(id, out var perk))
                    sb.Append($"\n  • {perk.Name}");
                else
                    sb.Append($"\n  • {id}");
            }
        }

        if (burdens != null && burdens.Count > 0)
        {
            sb.Append($"\n<color={BurdenColor}>Burdens:</color>");
            foreach (var b in burdens)
                sb.Append($"\n  <color={BurdenColor}>• {BurdenName(b)}</color>");
        }

        return sb.ToString();
    }

    // The block appended to a relic tooltip.
    //
    // "Forge Tier" is deliberately NOT called rarity: the relic already shows a
    // vanilla RarityType, which is authored on different criteria and has a
    // different shape (it includes Epic). What this value actually governs is how
    // many perk slots a commit can unlock, so it's labelled for that.
    //
    // The curse line names the burden with no odds and no hedge. Stating a
    // probability here would be wrong in the common case anyway: only the FIRST
    // cursed relic in a commit contributes its burden, and a per-relic tooltip
    // can't see what else is loaded in the tube.
    public static string BuildRelicBody(RelicTier tier, BurdenType curse)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"\n<color={ForgeColor}>FORGE TIER: {TierName(tier)}</color>");
        if (curse != BurdenType.None)
            sb.Append($"\n<color={BurdenColor}><b>⚠ CURSED: {BurdenName(curse)}</b></color>");
        return sb.ToString();
    }

    // ---- commit results --------------------------------------------------

    // Human-readable perk-roll summary. Reads Name / Description straight off the
    // outcome's own PerkDefinition rather than looking the id up in PerkPool —
    // that lookup forces PerkPool's static initialiser, which touches game
    // StatTypes and so cannot run in the test host.
    //
    // The "no roll" string is only ever reached by the causal log line in
    // ForgeCommit.Execute; DescribeCommit gates it out of player-facing text.
    public static string DescribePerkResult(CommitOutcome outcome)
    {
        if (outcome.RolledPerk != null)
            return $"Perk gained in slot {outcome.TargetSlot + 1}: " +
                   $"{outcome.RolledPerk.Name} — {outcome.RolledPerk.Description}!";
        if (outcome.RollAttempted)
            return $"No perk this time ({TierName(outcome.BestTier)} · {outcome.RollChance:P0} chance).";
        return "no roll";
    }

    // The notification lines for a commit attempt, in display order.
    //
    // Both commit entry points render through here — the in-world commit button
    // (UpgradeForgeBehavior.DoCommit) and the !forgecommit dev command. They
    // previously carried separate copies of this switch which had already drifted
    // apart on every single arm ("This module is already at L10." against
    // "Cannot commit: module already at L10."), which is exactly the vocabulary
    // split this module exists to prevent.
    //
    // `relicsRemaining` is the Forge's relic count AFTER the attempt. Nothing is
    // consumed on a failure, so on those arms it is equally the count that was
    // there all along — which is what InsufficientRelics needs to report.
    public static IReadOnlyList<string> DescribeCommit(
        CommitOutcome outcome, int currentLevel, int relicsRemaining)
    {
        switch (outcome.Status)
        {
            case CommitStatus.Ok:
                var lines = new List<string>(2)
                {
                    $"Upgrade committed: L{outcome.NewLevel} " +
                    $"(consumed {Plural(outcome.RelicsConsumed, "relic")}, {relicsRemaining} remaining). " +
                    "Rebuild the module to apply.",
                };
                // A commit with no eligible slot shouldn't announce a non-event,
                // so an unattempted roll stays silent.
                if (outcome.RolledPerk != null || outcome.RollAttempted)
                    lines.Add(DescribePerkResult(outcome));
                return lines;

            case CommitStatus.NoModule:
                return new[] { "Load a deconstructed module box into the Forge first." };

            case CommitStatus.NoRelics:
                return new[] { "Insert relics into the tubes before committing." };

            case CommitStatus.AlreadyAtMax:
                return new[] { $"This module is already at L{ForgeCostCurve.MaxLevel}." };

            case CommitStatus.InsufficientRelics:
                return new[]
                {
                    $"Next level requires {ForgeCostCurve.CostForNextLevel(currentLevel)} relics; " +
                    $"the Forge holds {relicsRemaining}.",
                };

            case CommitStatus.MissingViewId:
                return new[] { "Forge error: module box has no network identity." };

            case CommitStatus.InvalidModuleLevel:
                return new[] { "Only Mark III modules can be forged — upgrade it with module chips first." };

            default:
                return System.Array.Empty<string>();
        }
    }
}
