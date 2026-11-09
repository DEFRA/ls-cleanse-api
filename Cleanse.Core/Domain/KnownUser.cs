namespace Cleanse.Core.Domain;

/// <summary>
/// Someone issues can be assigned to. Added when they first sign in, or before that by an admin
/// or a SPOL import, in which case the Entra identity stays empty until that first sign-in.
/// </summary>
public sealed record KnownUser(
    Guid Id,
    string? Issuer,
    string? Subject,
    string? TenantId,
    string? ObjectId,
    string? ClientId,
    string FullName,
    DateTimeOffset? FirstLoggedInAtUtc,
    DateTimeOffset? LastLoggedInAtUtc,
    string? Email = null)
{
    /// <summary>Someone who has not signed in yet, known only by a normalised email address.</summary>
    public static KnownUser Unclaimed(string email, string fullName) =>
        new(Guid.NewGuid(), null, null, null, null, null, fullName, null, null, email);
}

/// <summary>The outcome of adding one person: the user, and whether they were new.</summary>
public sealed record UserRegistration(KnownUser User, bool Created);
