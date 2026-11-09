using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;
using Cleanse.Core.Users;

namespace Cleanse.Core.Commands.Imports;

/// <summary>
/// Applies one chunk of an external extract. Rows with no matching issue are recorded for the
/// reconciliation report rather than creating anything: the rules engine owns issue creation.
/// </summary>
public sealed record ApplyImportBatchCommand(
    Guid ImportJobId,
    IReadOnlyCollection<ExternalIssueRow> Rows,
    Actor Actor) : ICommand<ImportBatchResult>;

internal sealed class ApplyImportBatchCommandHandler(
    IImportJobStore imports,
    IIssueStore issues,
    IIssueHistoryStore history,
    IAuditStore audit,
    IUserStore users,
    TimeProvider clock)
    : ICommandHandler<ApplyImportBatchCommand, ImportBatchResult>
{
    private const string Source = "spol";

    // Excel exports carry non-breaking and doubled spaces that a typed name would not.
    private static string NameKey(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public async Task<Result<ImportBatchResult>> HandleAsync(
        ApplyImportBatchCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Rows);
        ArgumentNullException.ThrowIfNull(command.Actor);

        var job = await imports.GetAsync(command.ImportJobId, cancellationToken);
        if (job is null)
        {
            return DomainErrors.ImportNotFound(command.ImportJobId);
        }

        if (job.Status != ImportJobStatus.Processing)
        {
            return DomainErrors.ImportNotProcessing(job.Id, job.Status.ToString().ToLowerInvariant());
        }

        if (command.Rows.Count == 0)
        {
            return ImportBatchResult.Empty;
        }

        var now = clock.GetUtcNow();
        var rows = command.Rows.ToList();

        var existing = (await issues.GetManyAsync([.. rows.Select(r => r.IssueId)], cancellationToken))
            .ToDictionary(issue => issue.Id.Value, StringComparer.Ordinal);

        var auditEntries = new List<AuditEntry>();
        var historyEntries = new List<IssueHistoryEntry>();
        var rejections = new List<ImportJobRejection>();
        var directory = await LoadDirectoryAsync(cancellationToken);
        var byEmail = directory.Where(user => user.Email is not null)
            .ToDictionary(user => user.Email!, StringComparer.Ordinal);
        var byName = directory.GroupBy(user => NameKey(user.FullName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        // An address nobody has yet becomes a user, so the issue can be theirs before they sign in.
        var newcomers = rows
            .Where(row => row.HasAssignee &&
                          existing.TryGetValue(row.IssueId.Value, out var issue) &&
                          string.Equals(issue.GroupId, job.GroupId, StringComparison.Ordinal) &&
                          issue.AssignmentDetachedAtUtc is null)
            .Select(row => (Typed: row.AssignedTo?.Trim(),
                Email: EmailAddress.TryNormalise(row.AssignedTo, out var email) ? email : null))
            .Where(entry => entry.Email is not null && !byEmail.ContainsKey(entry.Email))
            .GroupBy(entry => entry.Email!, StringComparer.Ordinal)
            .Select(group => KnownUser.Unclaimed(group.Key, UserNames.FromEmail(group.First().Typed!)))
            .ToList();

        if (newcomers.Count > 0)
        {
            foreach (var registration in await users.RegisterAsync(newcomers, cancellationToken))
            {
                byEmail[registration.User.Email!] = registration.User;
                var key = NameKey(registration.User.FullName);
                if (!byName.TryGetValue(key, out var named))
                {
                    byName[key] = named = [];
                }

                if (!named.Exists(user => user.Id == registration.User.Id))
                {
                    named.Add(registration.User);
                }
            }
        }

        var matched = 0;

        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.IssueId.Value, out var issue))
            {
                rejections.Add(new ImportJobRejection(
                    job.Id,
                    row.RowNumber,
                    row.IssueId.Value,
                    ImportRejectionReasons.IssueNotFound,
                    $"No issue exists for '{row.IssueId.Value}'."));
                continue;
            }

            if (!string.Equals(issue.GroupId, job.GroupId, StringComparison.Ordinal))
            {
                rejections.Add(new ImportJobRejection(job.Id, row.RowNumber, row.IssueId.Value,
                    ImportRejectionReasons.IssueNotFound, "No issue exists in the import group."));
                continue;
            }

            matched++;
            var expectedVersion = issue.Version;
            if (row.HasAssignee && issue.AssignmentDetachedAtUtc is null)
            {
                var assignee = string.IsNullOrWhiteSpace(row.AssignedTo) ? null : row.AssignedTo.Trim();
                KnownUser? user = null;
                string? warning = null;
                if (EmailAddress.TryNormalise(assignee, out var email))
                {
                    if (!byEmail.TryGetValue(email, out user))
                    {
                        warning = ImportRejectionReasons.AssigneeUnmatched;
                    }
                }
                else if (assignee is not null)
                {
                    byName.TryGetValue(NameKey(assignee), out var candidates);
                    if (candidates is { Count: 1 })
                    {
                        user = candidates[0];
                    }
                    else
                    {
                        warning = candidates is { Count: > 1 }
                            ? ImportRejectionReasons.AssigneeAmbiguous
                            : ImportRejectionReasons.AssigneeUnmatched;
                    }
                }

                var assignment = issue.SyncAssignment(user, assignee, now);
                if (assignment.IsMaterial)
                {
                    auditEntries.Add(AuditEntry.ForIssue(issue.Id, assignment, command.Actor, now, job.Id.ToString()));
                }

                if (warning is not null)
                {
                    rejections.Add(new ImportJobRejection(job.Id, row.RowNumber, row.IssueId.Value,
                        warning, "Assignment warning: kept as text, because no single known user has that name."));
                }
            }

            var change = issue.ApplyExternalState(row.ToState(), now);
            if (change.IsMaterial)
            {
                auditEntries.Add(AuditEntry.ForIssue(issue.Id, change, command.Actor, now, job.Id.ToString()));
            }

            if (issue.Version != expectedVersion && !await issues.SaveAsync(issue, expectedVersion, cancellationToken))
            {
                return DomainErrors.ConcurrencyConflict(issue.Id.Value, expectedVersion, expectedVersion + 1);
            }

            foreach (var contact in row.Contacts)
            {
                historyEntries.Add(IssueHistoryEntry.CreateWithId(
                    DeterministicId.ForExternalHistory(issue.Id, Source, contact.Column, contact.AtUtc),
                    issue.Id,
                    contact.ActionType,
                    contact.Column,
                    command.Actor,
                    contact.AtUtc));
            }
        }

        foreach (var entry in historyEntries)
        {
            await history.AddAsync(entry, cancellationToken);
        }

        if (auditEntries.Count > 0)
        {
            await audit.AppendBatchAsync(auditEntries, cancellationToken);
        }

        if (rejections.Count > 0)
        {
            await imports.AddRejectionsAsync(rejections, cancellationToken);
        }

        var unmatched = rows.Count - matched;
        var progress = job.RecordProgress(
            ImportJobCounters.Empty with
            {
                ProcessedRows = rows.Count,
                Matched = matched,
                Unmatched = unmatched
            },
            now);

        if (progress.IsFailure)
        {
            return progress.ToFailure<ImportBatchResult>();
        }

        await imports.SaveAsync(job, cancellationToken);

        return new ImportBatchResult(rows.Count, matched, unmatched, historyEntries.Count);
    }

    private async Task<List<KnownUser>> LoadDirectoryAsync(CancellationToken cancellationToken)
    {
        var directory = new List<KnownUser>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await users.SearchAsync(new PageRequest(pageNumber, PageRequest.MaxPageSize), cancellationToken);
            directory.AddRange(page.Items.Where(user => !string.IsNullOrWhiteSpace(user.FullName)));
            if ((long)pageNumber * PageRequest.MaxPageSize >= page.TotalCount)
            {
                return directory;
            }
        }
    }
}
