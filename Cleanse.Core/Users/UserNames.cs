namespace Cleanse.Core.Users;

/// <summary>Display names for people Cleanse knows only by their email address.</summary>
internal static class UserNames
{
    private static readonly char[] Digits = [.. "0123456789"];

    /// <summary>
    /// <c>Kyle.Lloyd2@apha.gov.uk</c> becomes "Kyle Lloyd": dots and underscores separate the
    /// names, trailing digits are dropped, and a part already in mixed case, such as "StJohn", is kept.
    /// </summary>
    public static string FromEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var local = email.Trim().Split('@')[0].Split('+')[0];
        var parts = local
            .Split(['.', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.TrimEnd(Digits))
            .Where(part => part.Length > 0)
            .Select(Capitalise)
            .ToArray();

        return parts.Length > 0 ? string.Join(' ', parts) : local;
    }

    private static string Capitalise(string part)
    {
        if (part.Any(char.IsUpper) && part.Any(char.IsLower))
        {
            return part;
        }

        var letters = part.ToLowerInvariant().ToCharArray();
        for (var index = 0; index < letters.Length; index++)
        {
            if (char.IsLetter(letters[index]) && (index == 0 || letters[index - 1] is '-' or '\''))
            {
                letters[index] = char.ToUpperInvariant(letters[index]);
            }
        }

        return new string(letters);
    }
}
