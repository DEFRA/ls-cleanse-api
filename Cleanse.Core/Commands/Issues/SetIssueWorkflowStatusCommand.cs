using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Issues;

/// <summary>
/// Moves an issue along the human triage axis. Suppression is not reachable here because it
/// needs an expiry; use <see cref="SuppressIssueCommand"/>. An outcome can take a reason from its
/// list, and Can't resolve must.
/// </summary>
public sealed record SetIssueWorkflowStatusCommand(
    IssueId IssueId,
    WorkflowStatus WorkflowStatus,
    Actor Actor,
    int? ExpectedVersion = null,
    Guid? ReasonId = null) : ICommand<IssueDto>;

internal sealed class SetIssueWorkflowStatusCommandHandler(IssueMutator mutator, ICodedValueStore codedValues)
    : ICommandHandler<SetIssueWorkflowStatusCommand, IssueDto>
{
    public async Task<Result<IssueDto>> HandleAsync(
        SetIssueWorkflowStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reason = await OutcomeReasons.ResolveAsync(
            codedValues, command.WorkflowStatus, command.ReasonId, cancellationToken);
        if (reason.IsFailure)
        {
            return reason.ToFailure<IssueDto>();
        }

        return await mutator.MutateAsync(
            command.IssueId,
            command.ExpectedVersion,
            command.Actor,
            (issue, now) => issue.ChangeWorkflowStatus(command.WorkflowStatus, now, reason.Value),
            cancellationToken);
    }
}

/// <summary>Turns a reason id into the reason an issue records, checking it suits the outcome.</summary>
internal static class OutcomeReasons
{
    public static async Task<Result<OutcomeReason?>> ResolveAsync(
        ICodedValueStore codedValues,
        WorkflowStatus target,
        Guid? reasonId,
        CancellationToken cancellationToken)
    {
        if (reasonId is not { } id)
        {
            return Result<OutcomeReason?>.Success(null);
        }

        if (CodedLists.ReasonsFor(target) is not { } list)
        {
            return DomainErrors.Validation($"Only an outcome can have a reason, and '{target.ToWire()}' is not one.");
        }

        var value = await codedValues.GetAsync(id, cancellationToken);
        if (value is null || value.List != list)
        {
            return DomainErrors.Validation($"'{id}' is not one of the {list.ToWire()}.");
        }

        if (!value.IsActive)
        {
            return DomainErrors.Validation($"The reason '{value.Name}' is no longer offered.");
        }

        return new OutcomeReason(value.Id, value.Name);
    }
}

/// <summary>Temporarily hides an issue from triage until <see cref="ExpiresAtUtc"/>.</summary>
public sealed record SuppressIssueCommand(
    IssueId IssueId,
    DateTimeOffset ExpiresAtUtc,
    Actor Actor,
    int? ExpectedVersion = null) : ICommand<IssueDto>;

internal sealed class SuppressIssueCommandHandler(IssueMutator mutator)
    : ICommandHandler<SuppressIssueCommand, IssueDto>
{
    public Task<Result<IssueDto>> HandleAsync(
        SuppressIssueCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return mutator.MutateAsync(
            command.IssueId,
            command.ExpectedVersion,
            command.Actor,
            (issue, now) => issue.Suppress(command.ExpiresAtUtc, now),
            cancellationToken);
    }
}

/// <summary>Lifts a suppression early, returning the issue to the triage queue.</summary>
public sealed record UnsuppressIssueCommand(
    IssueId IssueId,
    Actor Actor,
    int? ExpectedVersion = null) : ICommand<IssueDto>;

internal sealed class UnsuppressIssueCommandHandler(IssueMutator mutator)
    : ICommandHandler<UnsuppressIssueCommand, IssueDto>
{
    public Task<Result<IssueDto>> HandleAsync(
        UnsuppressIssueCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return mutator.MutateAsync(
            command.IssueId,
            command.ExpectedVersion,
            command.Actor,
            (issue, now) => issue.Unsuppress(now),
            cancellationToken);
    }
}
