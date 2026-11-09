using System.Diagnostics.CodeAnalysis;

namespace Cleanse.Core.Users;

/// <summary>Email addresses as Cleanse keys people by them: trimmed and lower-cased.</summary>
internal static class EmailAddress
{
    public const int MaxLength = 320;

    public static bool TryNormalise(string? value, [NotNullWhen(true)] out string? email)
    {
        email = null;
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxLength)
        {
            return false;
        }

        var at = candidate.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != candidate.LastIndexOf('@') || at == candidate.Length - 1)
        {
            return false;
        }

        var domain = candidate[(at + 1)..];
        if (!domain.Contains('.', StringComparison.Ordinal) || domain[0] == '.' || domain[^1] == '.' ||
            candidate.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "\",;:<>()[]\\".Contains(c)))
        {
            return false;
        }

        email = candidate.ToLowerInvariant();
        return true;
    }
}
