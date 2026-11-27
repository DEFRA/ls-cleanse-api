using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;
using Cleanse.Core.Users;

namespace Cleanse.Core.Commands.Imports;

/// <summary>
/// Adds each assignee whose email address nobody has as a user who has not signed in yet, named
/// as the extract names them, so that holdings can be theirs before they first sign in. People
/// Cleanse already knows keep their names, and an assignee with no email address is not added.
/// The job counts the users added.
/// </summary>
public sealed record RegisterImportAssigneesCommand(
    Guid ImportJobId,
    IReadOnlyCollection<ExternalAssignee> Assignees) : ICommand<int>;

internal sealed class RegisterImportAssigneesCommandHandler(
    IImportJobStore imports,
    IUserStore users,
    TimeProvider clock) : ICommandHandler<RegisterImportAssigneesCommand, int>
{
    /// <summary>The users table's limit on a name.</summary>
    private const int MaxNameLength = 256;

    public async Task<Result<int>> HandleAsync(
        RegisterImportAssigneesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Assignees);

        var job = await imports.GetAsync(command.ImportJobId, cancellationToken);
        if (job is null)
        {
            return DomainErrors.ImportNotFound(command.ImportJobId);
        }

        if (job.Status != ImportJobStatus.Processing)
        {
            return DomainErrors.ImportNotProcessing(job.Id, job.Status.ToString().ToLowerInvariant());
        }

        var newcomers = command.Assignees
            .Select(assignee => (
                Typed: assignee.Email?.Trim(),
                Email: EmailAddress.TryNormalise(assignee.Email, out var email) ? email : null,
                Name: NameOf(assignee.Name)))
            .Where(assignee => assignee.Email is not null)
            .GroupBy(assignee => assignee.Email!, StringComparer.Ordinal)
            .Select(group => KnownUser.Unclaimed(
                group.Key,
                group.Select(assignee => assignee.Name).FirstOrDefault(name => name is not null)
                    ?? UserNames.FromEmail(group.First().Typed!)))
            .ToList();

        if (newcomers.Count == 0)
        {
            return 0;
        }

        var created = (await users.RegisterAsync(newcomers, cancellationToken)).Count(r => r.Created);
        if (created > 0)
        {
            var progress = job.RecordProgress(ImportJobCounters.Empty with { UsersCreated = created }, clock.GetUtcNow());
            if (progress.IsFailure)
            {
                return progress.ToFailure<int>();
            }

            await imports.SaveAsync(job, cancellationToken);
        }

        return created;
    }

    // SharePoint's names can carry non-breaking and doubled spaces that a typed name would not.
    private static string? NameOf(string? name)
    {
        var tidy = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return tidy.Length is > 0 and <= MaxNameLength ? tidy : null;
    }
}
