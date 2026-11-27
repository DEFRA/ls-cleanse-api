using System.Globalization;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Issues;

namespace Cleanse.Core.Commands.Holdings;

/// <summary>Assigns a holding to a known user, or with a null user unassigns it.</summary>
public sealed record AssignHoldingCommand(
    HoldingKey Holding,
    Guid? UserId,
    Actor Actor,
    int? ExpectedVersion = null) : ICommand<HoldingDto>;

internal sealed class AssignHoldingCommandHandler(
    IIssueStore issues,
    IHoldingStore holdings,
    IUserStore users,
    IAuditStore audit,
    TimeProvider clock) : ICommandHandler<AssignHoldingCommand, HoldingDto>
{
    public async Task<Result<HoldingDto>> HandleAsync(
        AssignHoldingCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        var key = command.Holding;
        if ((await issues.GetForHoldingsAsync(key.GroupId, [key.Cph], cancellationToken)).Count == 0)
        {
            return DomainErrors.HoldingNotFound(key.GroupId, key.Cph);
        }

        KnownUser? user = null;
        if (command.UserId is { } userId)
        {
            user = await users.GetAsync(userId, cancellationToken);
            if (user is null)
            {
                return DomainErrors.Validation("userId must identify a known user.");
            }
        }

        var now = clock.GetUtcNow();
        var holding = await holdings.GetAsync(key, cancellationToken) ?? Holding.New(key, now);
        var loaded = holding.Version;
        if (command.ExpectedVersion is { } expected && expected != loaded)
        {
            return DomainErrors.HoldingConcurrencyConflict(key.Cph, expected, loaded);
        }

        var change = holding.AssignKnownUser(user, now);
        if (!change.IsMaterial)
        {
            return holding.ToDto();
        }

        if (!await holdings.SaveAsync(holding, loaded, cancellationToken))
        {
            return DomainErrors.HoldingConcurrencyConflict(key.Cph, loaded, loaded + 1);
        }

        await audit.AppendAsync(AuditEntry.ForHolding(key, change, command.Actor, now), cancellationToken);
        return holding.ToDto();
    }
}

/// <summary>Adds a note to a holding. Notes are only ever added, never edited or replaced.</summary>
public sealed record AddHoldingNoteCommand(HoldingKey Holding, string Text, Actor Actor) : ICommand<HoldingNoteDto>;

internal sealed class AddHoldingNoteCommandHandler(
    IIssueStore issues,
    IHoldingActivityStore activity,
    TimeProvider clock) : ICommandHandler<AddHoldingNoteCommand, HoldingNoteDto>
{
    public async Task<Result<HoldingNoteDto>> HandleAsync(
        AddHoldingNoteCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        var note = HoldingNote.Create(command.Holding, command.Text, null, command.Actor, clock.GetUtcNow());
        if (note.IsFailure)
        {
            return note.ToFailure<HoldingNoteDto>();
        }

        var key = command.Holding;
        if ((await issues.GetForHoldingsAsync(key.GroupId, [key.Cph], cancellationToken)).Count == 0)
        {
            return DomainErrors.HoldingNotFound(key.GroupId, key.Cph);
        }

        await activity.AddNoteAsync(note.Value, cancellationToken);
        return note.Value.ToDto();
    }
}

/// <summary>
/// Records what a caseworker did about a holding: one action per type ticked, all sharing a
/// batch, with an optional note. Never changes an issue.
/// </summary>
public sealed record RecordHoldingActionsCommand(
    HoldingKey Holding,
    IReadOnlyCollection<Guid> ActionTypeIds,
    string? Note,
    Actor Actor) : ICommand<RecordedHoldingActionsDto>;

internal sealed class RecordHoldingActionsCommandHandler(
    IIssueStore issues,
    ICodedValueStore codedValues,
    IHoldingActivityStore activity,
    TimeProvider clock) : ICommandHandler<RecordHoldingActionsCommand, RecordedHoldingActionsDto>
{
    public async Task<Result<RecordedHoldingActionsDto>> HandleAsync(
        RecordHoldingActionsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.ActionTypeIds);
        ArgumentNullException.ThrowIfNull(command.Actor);

        var ids = command.ActionTypeIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return DomainErrors.Validation("Choose at least one action.");
        }

        var key = command.Holding;
        if ((await issues.GetForHoldingsAsync(key.GroupId, [key.Cph], cancellationToken)).Count == 0)
        {
            return DomainErrors.HoldingNotFound(key.GroupId, key.Cph);
        }

        var types = (await codedValues.GetManyAsync(ids, cancellationToken)).ToDictionary(type => type.Id);
        foreach (var id in ids)
        {
            if (!types.TryGetValue(id, out var type) || type.List != CodedList.ActionType)
            {
                return DomainErrors.Validation($"'{id}' is not an action type.");
            }

            if (!type.IsActive)
            {
                return DomainErrors.Validation($"The action type '{type.Name}' is no longer offered.");
            }
        }

        var now = clock.GetUtcNow();
        var batch = Guid.CreateVersion7(now);
        var chosen = ids.Select(id => types[id]).OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var names = chosen.Select(type => type.Name).ToList();

        HoldingNote? note = null;
        if (!string.IsNullOrWhiteSpace(command.Note))
        {
            var written = HoldingNote.Create(key, command.Note, $"Recorded: {string.Join(", ", names)}", command.Actor, now);
            if (written.IsFailure)
            {
                return written.ToFailure<RecordedHoldingActionsDto>();
            }

            note = written.Value;
        }

        var actions = chosen.Select(type => HoldingAction.Create(batch, key, type, command.Actor, now)).ToList();
        await activity.AddActionsAsync(actions, note, cancellationToken);

        return new RecordedHoldingActionsDto(batch, names, note?.ToDto());
    }
}

/// <summary>An issue picked on the holding page, with the version the page showed.</summary>
public sealed record IssueVersion(IssueId Id, int ExpectedVersion);

/// <summary>
/// Gives several of a holding's issues the same new status in one step: all of them or none.
/// Suppressed takes an end date; an outcome takes a reason from its list, which Can't resolve
/// requires. Nothing changes if any issue cannot move, or has changed since it was shown.
/// </summary>
public sealed record UpdateHoldingIssuesCommand(
    HoldingKey Holding,
    IReadOnlyCollection<IssueVersion> Issues,
    WorkflowStatus WorkflowStatus,
    Actor Actor,
    Guid? ReasonId = null,
    DateTimeOffset? SuppressUntilUtc = null,
    string? Note = null) : ICommand<HoldingIssuesUpdatedDto>;

internal sealed class UpdateHoldingIssuesCommandHandler(
    IIssueStore issues,
    ICodedValueStore codedValues,
    IHoldingActivityStore activity,
    IAuditStore audit,
    IssueReadModel readModel,
    TimeProvider clock) : ICommandHandler<UpdateHoldingIssuesCommand, HoldingIssuesUpdatedDto>
{
    public async Task<Result<HoldingIssuesUpdatedDto>> HandleAsync(
        UpdateHoldingIssuesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Issues);
        ArgumentNullException.ThrowIfNull(command.Actor);

        if (command.Issues.Count == 0)
        {
            return DomainErrors.Validation("Select at least one issue.");
        }

        if (command.Issues.Select(i => i.Id).Distinct().Count() != command.Issues.Count)
        {
            return DomainErrors.Validation("Each issue can only be selected once.");
        }

        var suppressing = command.WorkflowStatus == WorkflowStatus.Suppressed;
        if (suppressing && command.SuppressUntilUtc is null)
        {
            return DomainErrors.Validation("Choose how long to suppress the issues for.");
        }

        if (!suppressing && command.SuppressUntilUtc is not null)
        {
            return DomainErrors.Validation("Only suppressing issues takes an end date.");
        }

        var reason = await OutcomeReasons.ResolveAsync(
            codedValues, command.WorkflowStatus, command.ReasonId, cancellationToken);
        if (reason.IsFailure)
        {
            return reason.ToFailure<HoldingIssuesUpdatedDto>();
        }

        var key = command.Holding;
        var atHolding = (await issues.GetForHoldingsAsync(key.GroupId, [key.Cph], cancellationToken))
            .ToDictionary(issue => issue.Id.Value, StringComparer.Ordinal);
        if (atHolding.Count == 0)
        {
            return DomainErrors.HoldingNotFound(key.GroupId, key.Cph);
        }

        var now = clock.GetUtcNow();
        var selected = new List<Issue>(command.Issues.Count);
        var changed = new List<(Issue Issue, int Loaded, IssueChange Change)>();

        foreach (var requested in command.Issues)
        {
            if (!atHolding.TryGetValue(requested.Id.Value, out var issue))
            {
                return DomainErrors.Validation($"Issue '{requested.Id}' is not at CPH '{key.Cph}'.", requested.Id.Value);
            }

            var loaded = issue.Version;
            if (requested.ExpectedVersion != loaded)
            {
                return DomainErrors.ConcurrencyConflict(issue.Id.Value, requested.ExpectedVersion, loaded);
            }

            var result = suppressing
                ? issue.Suppress(command.SuppressUntilUtc!.Value, now)
                : issue.ChangeWorkflowStatus(command.WorkflowStatus, now, reason.Value);
            if (result.IsFailure)
            {
                return result.ToFailure<HoldingIssuesUpdatedDto>();
            }

            selected.Add(issue);
            if (result.Value.IsMaterial)
            {
                changed.Add((issue, loaded, result.Value));
            }
        }

        HoldingNote? note = null;
        if (!string.IsNullOrWhiteSpace(command.Note))
        {
            var context = Describe(command.WorkflowStatus, reason.Value, command.SuppressUntilUtc, selected);
            var written = HoldingNote.Create(key, command.Note, context, command.Actor, now);
            if (written.IsFailure)
            {
                return written.ToFailure<HoldingIssuesUpdatedDto>();
            }

            note = written.Value;
        }

        if (changed.Count > 0)
        {
            if (!await issues.SaveAllAsync([.. changed.Select(c => (c.Issue, c.Loaded))], cancellationToken))
            {
                return new DomainError(
                    ErrorCodes.IssueConcurrencyConflict,
                    "One of the selected issues changed while it was being saved.",
                    key.Cph);
            }

            await audit.AppendBatchAsync(
                [.. changed.Select(c => AuditEntry.ForIssue(c.Issue.Id, c.Change, command.Actor, now))],
                cancellationToken);
        }

        if (note is not null)
        {
            await activity.AddNoteAsync(note, cancellationToken);
        }

        return new HoldingIssuesUpdatedDto(
            await readModel.ToDtosAsync(selected, cancellationToken),
            note?.ToDto());
    }

    /// <summary>What a note written with the change was about, for example "Resolved (Fixed in SAM): rules 1 and 3".</summary>
    internal static string Describe(
        WorkflowStatus status, OutcomeReason? reason, DateTimeOffset? until, IReadOnlyCollection<Issue> issues)
    {
        var what = status == WorkflowStatus.Suppressed && until is { } end
            ? $"Suppressed until {end.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
            : reason is null ? status.ToLabel() : $"{status.ToLabel()} ({reason.Name})";

        return $"{what}: {RuleList(issues)}";
    }

    internal static string RuleList(IReadOnlyCollection<Issue> issues)
    {
        var rules = issues
            .Select(RuleLabel)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(label => int.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue)
            .ThenBy(label => label, StringComparer.Ordinal)
            .ToList();

        return rules.Count == 1
            ? $"rule {rules[0]}"
            : $"rules {string.Join(", ", rules.Take(rules.Count - 1))} and {rules[^1]}";
    }

    private static string RuleLabel(Issue issue) =>
        issue.Context.TryGetValue("ruleNumber", out var number) && !string.IsNullOrWhiteSpace(number)
            ? number.Trim()
            : issue.RuleId;
}
