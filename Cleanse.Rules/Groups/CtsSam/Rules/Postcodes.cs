using System.Text.RegularExpressions;

namespace Cleanse.Rules.Groups.CtsSam.Rules;

/// <summary>
/// How postcodes are compared across SAM and CTS: literally, once case, spacing and punctuation
/// are out of the way.
/// </summary>
/// <remarks>
/// Nothing is repaired. A CTS postcode typed with a letter O for a zero (<c>RG3O 3SE</c>) is a
/// different postcode from SAM's, and that difference is exactly what the rule exists to report.
/// </remarks>
public static partial class Postcodes
{
    /// <summary>Upper case with everything but letters and digits removed; null when nothing is left.</summary>
    public static string? Normalise(string? postcode)
    {
        if (string.IsNullOrWhiteSpace(postcode))
        {
            return null;
        }

        var normalised = NotAlphanumeric().Replace(postcode.ToUpperInvariant(), string.Empty);
        return normalised.Length == 0 ? null : normalised;
    }

    public static bool AreSame(string? left, string? right)
    {
        var a = Normalise(left);
        var b = Normalise(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.Ordinal);
    }

    [GeneratedRegex("[^A-Z0-9]")]
    private static partial Regex NotAlphanumeric();
}
