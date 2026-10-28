using System.Text.RegularExpressions;

namespace Cleanse.Rules.Groups.CtsSam.Rules;

/// <summary>
/// Decides whether two holding names identify the same place. The vocabulary of rule 3.
/// </summary>
/// <remarks>
/// The test is for names that point to a <em>different place</em>, not names that are
/// <em>written differently</em>: a caseworker who opens an issue and finds the same farm spelled
/// two ways will stop trusting the rule. Every step is therefore a statement about how names are
/// written, applied identically to both sides, and each word list below was derived from, and
/// baselined against, the KRDS snapshot of 26 October 2026 (4,012 issues from 40,473 pairs). The
/// lists are frozen: add an entry for a class of records, never for one record, because any
/// change closes and reopens live issues.
/// </remarks>
public static partial class HoldingNameEquivalence
{
    /// <summary>
    /// Names at least this similar, once canonicalised and stripped of generic words, are taken to
    /// be the same place misspelt. Between 0.85 and 0.90 the accepted pairs are typo-level
    /// (<c>Fox Covet</c> / <c>FOX COVERT</c>); below 0.85 different places start to pass.
    /// </summary>
    public const double SpellingThreshold = 0.85;

    /// <summary>Values entered where a name should be. Compared lower case after trimming.</summary>
    public static readonly IReadOnlySet<string> Placeholders = new HashSet<string>(StringComparer.Ordinal)
    {
        "n/a", "na", "n/k", "nk", "unknown", "not known", "none", "tbc", "tba", "-"
    };

    /// <summary>Token-level expansions, applied before any phrase is removed.</summary>
    public static readonly IReadOnlyDictionary<string, string> Abbreviations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["fm"] = "farm",
        ["frm"] = "farm",
        ["fms"] = "farms",
        ["ho"] = "house",
        ["hse"] = "house",
        ["cott"] = "cottage",
        ["cotts"] = "cottages",
        ["ctg"] = "cottage",
        ["rd"] = "road",
        ["ln"] = "lane",
        ["nr"] = "near",
        ["est"] = "estate",
        ["gt"] = "great",
        ["lwr"] = "lower",
        ["upr"] = "upper",
        ["mt"] = "mount",
        ["saint"] = "st",
        ["iso"] = "isolation",
        ["isol"] = "isolation"
    };

    /// <summary>Words that carry no identity. <c>at</c> is what remains of a stripped <c>@</c>.</summary>
    public static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "the", "at", "ltd", "llp", "limited", "plc"
    };

    /// <summary>A holding is a farm by default; these words are ignored when the rest agrees.</summary>
    public static readonly IReadOnlySet<string> GenericWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "farm", "farms", "farmhouse"
    };

    /// <summary>
    /// Words that distinguish neighbouring holdings. Two names that swap one of these are
    /// different places however similar the rest, so spelling tolerance must not pass them.
    /// </summary>
    public static readonly IReadOnlySet<string> Qualifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        "east", "west", "north", "south", "higher", "lower", "upper", "middle", "great", "little",
        "old", "new", "inner", "outer", "top", "bottom", "big", "small", "far", "nether", "over", "under"
    };

    /// <summary>
    /// Reduces a name to the words that identify the place, lower case and in order. Empty means
    /// the name identifies nothing: it was blank, or only ever described a relationship to a place
    /// (<c>Land At</c>, <c>Fields Off</c>).
    /// </summary>
    public static IReadOnlyList<string> Canonicalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var text = name.ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal).Replace("@", " at ", StringComparison.Ordinal);
        text = Apostrophes().Replace(text, string.Empty);
        text = NonAlphanumeric().Replace(text, " ").Trim();
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Expand));
        text = LeadingLocative().Replace(text, string.Empty);
        text = LandAt().Replace(text, " ").Trim();
        text = LeadingPrefix().Replace(text, string.Empty);
        text = LeadingNumber().Replace(text, string.Empty);

        return [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => !StopWords.Contains(token))];
    }

    /// <summary>The tokens with generic words removed, unless that would leave nothing.</summary>
    public static IReadOnlyList<string> Distinctive(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var distinctive = tokens.Where(token => !GenericWords.Contains(token)).ToList();
        return distinctive.Count == 0 ? tokens : distinctive;
    }

    /// <summary>Whether a SAM name is blank, a placeholder, or empty once canonicalised.</summary>
    public static bool IsPlaceholder(string? samName) =>
        string.IsNullOrWhiteSpace(samName)
        || Placeholders.Contains(samName.Trim().ToLowerInvariant())
        || Canonicalise(samName).Count == 0;

    /// <summary>
    /// Compares two canonicalised names. Containment is only safe against the CTS name line:
    /// address line 2 is usually the village, and almost any name mentioning the village would
    /// otherwise pass.
    /// </summary>
    public static NameComparison Compare(IReadOnlyList<string> sam, IReadOnlyList<string> cts, bool allowContainment)
    {
        ArgumentNullException.ThrowIfNull(sam);
        ArgumentNullException.ThrowIfNull(cts);

        if (sam.Count == 0 || cts.Count == 0)
        {
            return new NameComparison(NameMatch.Different, 0d);
        }

        if (string.Concat(sam) == string.Concat(cts))
        {
            return new NameComparison(NameMatch.Canonical, 1d);
        }

        var samDistinctive = Distinctive(sam);
        var ctsDistinctive = Distinctive(cts);
        var samText = string.Concat(samDistinctive);
        var ctsText = string.Concat(ctsDistinctive);

        if (samText == ctsText)
        {
            return new NameComparison(NameMatch.Generic, 1d);
        }

        var similarity = Similarity(samText, ctsText);
        var samWords = samDistinctive.ToHashSet(StringComparer.Ordinal);
        var ctsWords = ctsDistinctive.ToHashSet(StringComparer.Ordinal);

        if (allowContainment && (ctsWords.IsSubsetOf(samWords) || samWords.IsSubsetOf(ctsWords)))
        {
            return new NameComparison(NameMatch.Contains, similarity);
        }

        var swapsQualifier = samWords.Except(ctsWords).Any(Qualifiers.Contains)
            && ctsWords.Except(samWords).Any(Qualifiers.Contains);

        if (similarity >= SpellingThreshold && !swapsQualifier)
        {
            return new NameComparison(NameMatch.Spelling, similarity);
        }

        return new NameComparison(NameMatch.Different, similarity);
    }

    /// <summary>
    /// Whether the CTS name line names a place at all. A numbered postal address, a trading or
    /// care-of line, the holding's own street, or its own town or village means CTS named the
    /// location by its address, and there is nothing a SAM name could be corrected against.
    /// </summary>
    public static bool IsPlaceName(string? ctsName, string? samStreet, string? samTown, string? samLocality)
    {
        var tokens = Canonicalise(ctsName);
        if (tokens.Count == 0 || Numbered().IsMatch(ctsName!) || NotAPlace().IsMatch(ctsName!))
        {
            return false;
        }

        var text = string.Concat(tokens);

        if (!string.IsNullOrWhiteSpace(samStreet)
            && StreetWord().IsMatch(samStreet.Trim())
            && text == string.Concat(Canonicalise(samStreet)))
        {
            return false;
        }

        return !SameAs(text, samTown) && !SameAs(text, samLocality);

        static bool SameAs(string text, string? other) =>
            !string.IsNullOrWhiteSpace(other) && text == string.Concat(Canonicalise(other));
    }

    /// <summary>1.0 for identical strings, falling by one share per edit of the longer string.</summary>
    public static double Similarity(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var longest = Math.Max(a.Length, b.Length);
        return longest == 0 ? 1d : 1d - (double)LevenshteinDistance(a, b) / longest;
    }

    /// <summary>Single-character insertions, deletions and substitutions. Two-row dynamic programming.</summary>
    public static int LevenshteinDistance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 0;
        }

        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static string Expand(string token) =>
        Abbreviations.TryGetValue(token, out var expansion) ? expansion : token;

    [GeneratedRegex(@"['\u2019`]")]
    private static partial Regex Apostrophes();

    [GeneratedRegex("[^0-9a-z]+")]
    private static partial Regex NonAlphanumeric();

    /// <summary>
    /// A leading phrase that relates the holding to a place rather than naming it: "land at",
    /// "on land near", "fields off", "plot of land adjacent to", "additional cph at".
    /// </summary>
    [GeneratedRegex(
        @"^(?:on\s+)?(?:plot\s+of\s+land|grazing\s+land|land|fields?|grazing|cattle\s+unit|additional\s+(?:cph|holding))\s+" +
        @"(?:at|near|off|by|adjacent\s+to|adj\s+to|adj|next\s+to|behind|opposite|in|on|surrounding|around|beside|alongside|above|below|between|" +
        @"(?:to\s+the\s+)?(?:rear|part|north|south|east|west)\s+of)(?:\s+|$)")]
    private static partial Regex LeadingLocative();

    [GeneratedRegex(@"\bland\s+at\b")]
    private static partial Regex LandAt();

    /// <summary>"at", "t/a", "c/o", "p/a" and "trading as" once punctuation has become spaces.</summary>
    [GeneratedRegex(@"^(?:at|t\s?a|c\s?o|p\s?a|trading\s+as)\s+")]
    private static partial Regex LeadingPrefix();

    [GeneratedRegex(@"^\d+[a-z]?\s+(?=[a-z])")]
    private static partial Regex LeadingNumber();

    /// <summary>Applied to the raw CTS name line.</summary>
    [GeneratedRegex(@"^\s*\d+[a-z]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Numbered();

    /// <summary>Applied to the raw CTS name line.</summary>
    [GeneratedRegex(@"^\s*(?:t\s*/?\s*a|c\s*/?\s*o|p\s*/?\s*a|trading\s+as)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotAPlace();

    /// <summary>Applied to the raw SAM street. A street that is really a named property has none of these.</summary>
    [GeneratedRegex(
        @"\b(road|lane|street|close|avenue|drive|way|crescent|terrace|gardens|rise|walk|mews|hill|row|green|end|" +
        @"bank|common|moor|heath|park|view|place|square|grove|court|cross|gate|side)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex StreetWord();
}

/// <summary>How two names were found to be the same place, or that they were not.</summary>
public enum NameMatch
{
    /// <summary>Identical once canonicalised.</summary>
    Canonical,

    /// <summary>Identical once generic words such as "farm" are ignored.</summary>
    Generic,

    /// <summary>Every distinctive word of one name appears in the other.</summary>
    Contains,

    /// <summary>Within <see cref="HoldingNameEquivalence.SpellingThreshold"/>.</summary>
    Spelling,

    Different
}

/// <param name="Similarity">
/// Of the distinctive text, from 0 to 1. Carried for every outcome so that near misses can be
/// sorted to the top of a triage list.
/// </param>
public readonly record struct NameComparison(NameMatch Match, double Similarity)
{
    public bool IsEquivalent => Match != NameMatch.Different;
}
