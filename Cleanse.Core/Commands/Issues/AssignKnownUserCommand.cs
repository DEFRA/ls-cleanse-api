using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Issues;

public sealed record AssignKnownUserCommand(
    IssueId IssueId,
    Guid? UserId,
    Actor Actor,
    int? ExpectedVersion = null) : ICommand<IssueDto>;

internal sealed class AssignKnownUserCommandHandler(IUserStore users, IssueMutator mutator)
    : ICommandHandler<AssignKnownUserCommand, IssueDto>
{
    public async Task<Result<IssueDto>> HandleAsync(
        AssignKnownUserCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        KnownUser? user = null;
        if (command.UserId is { } id)
        {
            user = await users.GetAsync(id, cancellationToken);
            if (user is null)
            {
                return DomainErrors.Validation("userId must identify a known user.");
            }
        }

        return await mutator.MutateAsync(
            command.IssueId, command.ExpectedVersion, command.Actor,
            (issue, now) => issue.AssignKnownUser(user, now), cancellationToken);
    }
}
