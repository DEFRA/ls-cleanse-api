using System.ComponentModel.DataAnnotations;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Users;

internal sealed class UserDirectory(IUserStore users, TimeProvider clock) : IUserDirectory
{
    public async Task<Result<UserDto>> RecordLoginAsync(
        RecordUserLoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), errors, true))
        {
            return DomainErrors.Validation(string.Join(" ", errors.Select(e => e.ErrorMessage)));
        }

        // Either claim can match an admin-added address; the email claim is preferred when both exist.
        var claimEmails = new[] { request.Email, request.PreferredUsername }
            .Select(value => EmailAddress.TryNormalise(value, out var email) ? email : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var now = clock.GetUtcNow();
        var user = await users.UpsertLoginAsync(new KnownUser(
            Guid.NewGuid(), request.Issuer, request.Subject, request.TenantId, request.ObjectId,
            request.ClientId, request.FullName, now, now, claimEmails.FirstOrDefault()), claimEmails, cancellationToken);
        return ToDto(user);
    }

    public async Task<Result<RegisterUsersResultDto>> RegisterAsync(
        RegisterUsersRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entries = (request.Emails ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .ToList();

        if (entries.Count == 0)
        {
            return DomainErrors.Validation("Supply at least one email address.");
        }

        if (entries.Count > RegisterUsersRequest.MaxEmails)
        {
            return DomainErrors.Validation($"Supply at most {RegisterUsersRequest.MaxEmails} email addresses at a time.");
        }

        var invalid = entries.Where(entry => !EmailAddress.TryNormalise(entry, out _)).ToList();
        if (invalid.Count > 0)
        {
            var shown = string.Join(", ", invalid.Take(10));
            var more = invalid.Count > 10 ? $" and {invalid.Count - 10} more" : string.Empty;
            return DomainErrors.Validation($"These are not valid email addresses: {shown}{more}.");
        }

        // Names come from the address as typed, so mixed case such as "StJohn" survives.
        var newcomers = entries
            .GroupBy(entry => EmailAddress.TryNormalise(entry, out var email) ? email : entry, StringComparer.Ordinal)
            .Select(group => KnownUser.Unclaimed(group.Key, UserNames.FromEmail(group.First())))
            .ToList();

        var registered = await users.RegisterAsync(newcomers, cancellationToken);
        return new RegisterUsersResultDto(
            [.. registered.Where(r => r.Created).Select(r => ToDto(r.User))],
            [.. registered.Where(r => !r.Created).Select(r => ToDto(r.User))]);
    }

    public async Task<Result<PagedResult<UserDto>>> SearchAsync(
        PageRequest page, CancellationToken cancellationToken = default)
    {
        var result = await users.SearchAsync(page, cancellationToken);
        return result.Map(ToDto);
    }

    private static UserDto ToDto(KnownUser user) => new(
        user.Id, user.Issuer, user.Subject, user.TenantId, user.ObjectId, user.ClientId,
        user.FullName, user.FirstLoggedInAtUtc, user.LastLoggedInAtUtc, user.Email);
}
