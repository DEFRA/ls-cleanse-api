using System.ComponentModel.DataAnnotations;
using Cleanse.Core.Users;

namespace Cleanse.Core.Contracts;

/// <summary>Identity claims attested by ls-cleanse-ui after validating an Entra login, never browser-supplied claims.</summary>
public sealed record RecordUserLoginRequest
{
    [Required, StringLength(512)]
    public required string Issuer { get; init; }

    [Required, StringLength(256)]
    public required string Subject { get; init; }

    [Required, StringLength(64)]
    public required string TenantId { get; init; }

    [Required, StringLength(64)]
    public required string ObjectId { get; init; }

    [Required, StringLength(64)]
    public required string ClientId { get; init; }

    [Required, StringLength(256)]
    public required string FullName { get; init; }

    /// <summary>The ID token's email claim, when it has one.</summary>
    [StringLength(EmailAddress.MaxLength)]
    public string? Email { get; init; }

    /// <summary>The ID token's preferred_username, usually the user principal name.</summary>
    [StringLength(EmailAddress.MaxLength)]
    public string? PreferredUsername { get; init; }
}

/// <summary>People to add before they sign in, by email address.</summary>
public sealed record RegisterUsersRequest
{
    public const int MaxEmails = 500;

    [Required]
    public required IReadOnlyList<string> Emails { get; init; }
}

public sealed record RegisterUsersResultDto(IReadOnlyList<UserDto> Created, IReadOnlyList<UserDto> Existing);

/// <summary>
/// A known user. The Entra identity and login times are null until their first sign-in.
/// </summary>
public sealed record UserDto(
    Guid Id,
    string? Issuer,
    string? Subject,
    string? TenantId,
    string? ObjectId,
    string? ClientId,
    string FullName,
    DateTimeOffset? FirstLoggedInAtUtc,
    DateTimeOffset? LastLoggedInAtUtc,
    string? Email);
