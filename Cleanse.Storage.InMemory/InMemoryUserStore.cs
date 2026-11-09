using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;

namespace Cleanse.Storage.InMemory;

public sealed class InMemoryUserStore : IUserStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, KnownUser> _users = [];

    public Task<KnownUser> UpsertLoginAsync(
        KnownUser login, IReadOnlyCollection<string> claimEmails, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(claimEmails);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var existing = _users.Values.FirstOrDefault(u =>
                    u.Issuer == login.Issuer && u.ClientId == login.ClientId && u.Subject == login.Subject)
                ?? _users.Values
                    .Where(u => u.Subject is not null && u.TenantId == login.TenantId && u.ObjectId == login.ObjectId)
                    .OrderBy(u => u.FirstLoggedInAtUtc).ThenBy(u => u.Id).FirstOrDefault()
                ?? claimEmails.Select(email => _users.Values.FirstOrDefault(
                    u => u.Subject is null && u.Email == email)).FirstOrDefault(u => u is not null);

            var user = existing is null
                ? login with { Email = IsFree(login.Email) ? login.Email : null }
                : login with
                {
                    Id = existing.Id,
                    Email = existing.Email ?? (IsFree(login.Email) ? login.Email : null),
                    FirstLoggedInAtUtc = existing.FirstLoggedInAtUtc ?? login.FirstLoggedInAtUtc,
                    LastLoggedInAtUtc = existing.LastLoggedInAtUtc > login.LastLoggedInAtUtc
                        ? existing.LastLoggedInAtUtc : login.LastLoggedInAtUtc
                };

            _users[user.Id] = user;
            return Task.FromResult(user);
        }
    }

    public Task<IReadOnlyList<UserRegistration>> RegisterAsync(
        IReadOnlyCollection<KnownUser> users, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var results = new List<UserRegistration>(users.Count);
            foreach (var user in users)
            {
                var known = _users.Values.FirstOrDefault(u => u.Email == user.Email);
                if (known is null)
                {
                    _users[user.Id] = user;
                }

                results.Add(new UserRegistration(known ?? user, known is null));
            }

            return Task.FromResult<IReadOnlyList<UserRegistration>>(results);
        }
    }

    public Task<KnownUser?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_users.GetValueOrDefault(id));
        }
    }

    public Task<PagedResult<KnownUser>> SearchAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var ordered = _users.Values
                .OrderBy(u => u.FullName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(u => u.Id)
                .ToArray();
            return Task.FromResult(new PagedResult<KnownUser>(
                [.. ordered.Skip(page.Skip).Take(page.PageSize)], page.PageNumber, page.PageSize, ordered.LongLength));
        }
    }

    private bool IsFree(string? email) => email is not null && _users.Values.All(u => u.Email != email);
}
