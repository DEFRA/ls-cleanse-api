using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;

namespace Cleanse.Core.Commands.Imports;

public sealed record CompleteImportJobCommand(Guid ImportJobId) : ICommand<ImportJobDto>;

internal sealed class CompleteImportJobCommandHandler(IImportJobStore imports, TimeProvider clock)
    : ICommandHandler<CompleteImportJobCommand, ImportJobDto>
{
    public async Task<Result<ImportJobDto>> HandleAsync(
        CompleteImportJobCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var job = await imports.GetAsync(command.ImportJobId, cancellationToken);
        if (job is null)
        {
            return DomainErrors.ImportNotFound(command.ImportJobId);
        }

        var completed = job.Complete(clock.GetUtcNow());
        if (completed.IsFailure)
        {
            return completed.ToFailure<ImportJobDto>();
        }

        await imports.SaveAsync(job, cancellationToken);
        return job.ToDto();
    }
}

public sealed record FailImportJobCommand(Guid ImportJobId, string Reason) : ICommand<ImportJobDto>;

internal sealed class FailImportJobCommandHandler(IImportJobStore imports, TimeProvider clock)
    : ICommandHandler<FailImportJobCommand, ImportJobDto>
{
    public async Task<Result<ImportJobDto>> HandleAsync(
        FailImportJobCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var job = await imports.GetAsync(command.ImportJobId, cancellationToken);
        if (job is null)
        {
            return DomainErrors.ImportNotFound(command.ImportJobId);
        }

        var failed = job.Fail(command.Reason, clock.GetUtcNow());
        if (failed.IsFailure)
        {
            return failed.ToFailure<ImportJobDto>();
        }

        await imports.SaveAsync(job, cancellationToken);
        return job.ToDto();
    }
}

/// <summary>
/// Reclaims jobs whose worker stopped reporting. Without this a container killed mid-import
/// leaves a job that looks alive forever and blocks the group's unique index.
/// </summary>
public sealed record ReapStalledImportsCommand(TimeSpan StaleAfter, int Limit = 50)
    : ICommand<int>;

internal sealed class ReapStalledImportsCommandHandler(IImportJobStore imports, TimeProvider clock)
    : ICommandHandler<ReapStalledImportsCommand, int>
{
    public async Task<Result<int>> HandleAsync(
        ReapStalledImportsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = clock.GetUtcNow();
        var stalled = await imports.FindStalledAsync(now - command.StaleAfter, command.Limit, cancellationToken);

        var reaped = 0;
        foreach (var job in stalled)
        {
            var abandoned = job.Abandon(
                $"No progress reported for {command.StaleAfter.TotalMinutes:0} minutes.", now);

            if (abandoned.IsFailure)
            {
                continue;
            }

            await imports.SaveAsync(job, cancellationToken);
            reaped++;
        }

        return reaped;
    }
}
