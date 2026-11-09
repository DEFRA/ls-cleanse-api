using Cleanse.Core.Domain;

namespace Cleanse.Core.Abstractions;

public interface IUserStore
{
    /// <summary>
    /// Resolves a sign-in to one user, atomically and in this order: the same Entra identity, the
    /// same person by tenant and object id, then a user who has not signed in yet whose email is
    /// one of <paramref name="claimEmails"/>; otherwise a new user. Keeps the id and first login.
    /// </summary>
    Task<KnownUser> UpsertLoginAsync(
        KnownUser login, IReadOnlyCollection<string> claimEmails, CancellationToken cancellationToken = default);

    /// <summary>Adds people before they sign in. An email already known returns that user instead.</summary>
    Task<IReadOnlyList<UserRegistration>> RegisterAsync(
        IReadOnlyCollection<KnownUser> users, CancellationToken cancellationToken = default);

    Task<KnownUser?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Every user, ordered by name.</summary>
    Task<PagedResult<KnownUser>> SearchAsync(PageRequest page, CancellationToken cancellationToken = default);
}
