using System.Collections.Generic;
using System.Text.RegularExpressions;
using VoidCrewTerminus.Loot;

namespace VoidCrewTerminus.Forge;

// The single place forge state turns into player-facing text, so vocabulary can't drift
// between surfaces. Mk numbering maps directly onto module level: L3 = Mk III (vanilla's
// cap), L10 = Mk X, extending vanilla's ladder rather than running a parallel scale.
public static class ForgeLabels
{
    // Close to vanilla's tooltip palette, which uses #BDE7FBFF for hints and red for warnings.
    private const string ForgeColor = "#7FD4FF";
    private const string BurdenColor = "#FF6B6B";
    private const string MutedColor = "#9AA5AD";

    // Levels are bounded 3-10 and vanilla marks 1-3, so a table beats a numeral algorithm.
    private static readonly string[] _roman =
        { "0", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };

    public static string Roman(int n) =>
        n >= 0 && n < _roman.Length ? _roman[n] : n.ToString();

    public static string MarkLabel(int level) => "Mk " + Roman(level);

    // Anchored so it can only ever strip a trailing mark token ("Mk III", "MK3", "Mk. VII").
    // English-centric by necessity: vanilla display names live inside asset bundles, so a
    // localised name fails to match and falls through to a plain append, never a corruption.
    private static readonly Regex _trailingMark =
        new(@"\s*mk\.?\s*([ivxlc]+|\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Safe whether or not the name already carries a mark.
    public static string RewriteMark(string header, int level)
    {
        string mark = MarkLabel(level);
        if (string.IsNullOrEmpty(header)) return mark;
        return _trailingMark.Replace(header, "") + " " + mark;
    }

    // Shared so every relic count pluralises the same way.
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

    // Mirrors ForgeModuleState's private HasAnyOverlay. An untouched module renders as
    // vanilla, so "unforged" and "not yet synced on a client" collapse to one silent path.
    public static bool HasOverlay(int level, IReadOnlyList<string> perkSlots, IReadOnlyList<BurdenType> burdens)
    {
        if (level > ForgeCostCurve.MinLevel) return true;
        if (burdens != null && burdens.Count > 0) return true;
        if (perkSlots != null)
            foreach (var id in perkSlots)
                if (!string.IsNullOrEmpty(id)) return true;
        return false;
    }

    // Names only: the perks' effects already appear as real StatMods in vanilla's stat block
    // just above, so descriptions here would restate them.
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

    // Deliberately not called rarity: the relic already shows a vanilla RarityType with
    // different criteria. The curse line states no odds because only the first cursed relic
    // in a commit contributes, and a per-relic tooltip can't see what else is loaded.
    public static string BuildRelicBody(RelicTier tier, BurdenType curse)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"\n<color={ForgeColor}>FORGE TIER: {TierName(tier)}</color>");
        if (curse != BurdenType.None)
            sb.Append($"\n<color={BurdenColor}><b>⚠ CURSED: {BurdenName(curse)}</b></color>");
        return sb.ToString();
    }

    // Reads Name/Description off the outcome's own PerkDefinition rather than looking the id
    // up in PerkPool: that lookup forces a static initialiser that can't run in the test host.
    // The "no roll" string reaches only ForgeCommit.Execute's log line, never the player.
    public static string DescribePerkResult(CommitOutcome outcome)
    {
        if (outcome.RolledPerk != null)
            return $"Perk gained in slot {outcome.TargetSlot + 1}: " +
                   $"{outcome.RolledPerk.Name} — {outcome.RolledPerk.Description}!";
        if (outcome.RollAttempted)
            return $"No perk this time ({TierName(outcome.BestTier)} · {outcome.RollChance:P0} chance).";
        return "no roll";
    }

    // Both commit entry points render through here so their wording can't drift apart.
    // `relicsRemaining` is the count AFTER the attempt; nothing is consumed on a failure, so
    // on those arms it is equally the count that was always there, which InsufficientRelics
    // reports.
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
                // An unattempted roll stays silent rather than announcing a non-event.
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
