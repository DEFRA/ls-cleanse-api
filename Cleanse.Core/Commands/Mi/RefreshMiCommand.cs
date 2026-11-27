using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain.Mi;
using Cleanse.Core.Queries.Mi;

namespace Cleanse.Core.Commands.Mi;

/// <summary>
/// Brings a group's dashboard summary up to date. Cheap when nothing has changed: it compares a
/// fingerprint of the source with the one the summary was built from. Otherwise, or when forced,
/// it rebuilds the whole summary from the history, which keeps every figure exact.
/// </summary>
public sealed record RefreshMiCommand(string GroupId, bool Force = false) : ICommand<MiRefreshResultDto>;

internal sealed class RefreshMiCommandHandler(
    IMiStore store,
    IUserStore users,
    IDataResetStore resets,
    TimeProvider clock,
    CleanseCoreOptions options) : ICommandHandler<RefreshMiCommand, MiRefreshResultDto>
{
    public async Task<Result<MiRefreshResultDto>> HandleAsync(
        RefreshMiCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (MiReadModel.CheckGroup(command.GroupId) is { } invalid)
        {
            return invalid;
        }

        var groupId = command.GroupId.Trim();
        var current = await store.GetRefreshAsync(groupId, cancellationToken);
        var now = clock.GetUtcNow();

        if (!command.Force && current is not null &&
            current.Version == MiProjector.Version &&
            now - current.BuiltAtUtc < options.MiRebuildAfter &&
            await store.GetFingerprintAsync(groupId, cancellationToken) == current.Fingerprint)
        {
            await store.MarkCheckedAsync(groupId, now, cancellationToken);
            return new MiRefreshResultDto(false, MiReadModel.Status(groupId, current with { CheckedAtUtc = now }, now, options));
        }

        var started = clock.GetTimestamp();
        var history = await store.ReadHistoryAsync(groupId, cancellationToken);
        var people = await MiReadModel.LoadPeopleAsync(users, cancellationToken);
        var reset = await resets.GetLastResetAsync(cancellationToken);
        var resetAt = reset is not null && (reset.Scope == "all" || reset.Scope == groupId) ? reset.AtUtc : (DateTimeOffset?)null;

        var summary = MiProjector.Build(history, people, resetAt, now);
        summary = summary with
        {
            Refresh = summary.Refresh with
            {
                DurationMilliseconds = (int)Math.Min(int.MaxValue, clock.GetElapsedTime(started).TotalMilliseconds)
            }
        };

        var written = await store.ReplaceAsync(summary, cancellationToken);
        var shown = written ? summary.Refresh : await store.GetRefreshAsync(groupId, cancellationToken);
        return new MiRefreshResultDto(
            written, MiReadModel.Status(groupId, shown, now, options), written ? summary.MismatchedIssueIds : null);
    }
}
