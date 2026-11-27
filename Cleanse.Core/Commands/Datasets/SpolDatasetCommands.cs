using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Datasets;
using Cleanse.Core.Domain.Imports;

namespace Cleanse.Core.Commands.Datasets;

/// <summary>Creates an Empty dataset and gives out its key, which is never shown again.</summary>
public sealed record CreateSpolDatasetCommand(string? Name, Actor Actor) : ICommand<CreatedSpolDatasetDto>;

internal sealed class CreateSpolDatasetCommandHandler(ISpolDatasetStore datasets, TimeProvider clock)
    : ICommandHandler<CreateSpolDatasetCommand, CreatedSpolDatasetDto>
{
    public async Task<Result<CreatedSpolDatasetDto>> HandleAsync(
        CreateSpolDatasetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        var (key, hash) = SpolDatasetKey.Generate();
        var created = SpolDataset.Create(Guid.CreateVersion7(), command.Name, hash, command.Actor, clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.ToFailure<CreatedSpolDatasetDto>();
        }

        await datasets.CreateAsync(created.Value, cancellationToken);
        return new CreatedSpolDatasetDto(created.Value.ToDto(null), key);
    }
}

public sealed record RenameSpolDatasetCommand(Guid DatasetId, string? Name, int ExpectedVersion)
    : ICommand<SpolDatasetDto>;

internal sealed class RenameSpolDatasetCommandHandler(
    ISpolDatasetStore datasets,
    IImportJobStore imports,
    TimeProvider clock) : ICommandHandler<RenameSpolDatasetCommand, SpolDatasetDto>
{
    public async Task<Result<SpolDatasetDto>> HandleAsync(
        RenameSpolDatasetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dataset = await datasets.GetAsync(command.DatasetId, cancellationToken);
        if (dataset is null)
        {
            return DomainErrors.DatasetNotFound(command.DatasetId);
        }

        if (dataset.Version != command.ExpectedVersion)
        {
            return DomainErrors.DatasetConcurrencyConflict(dataset.Id);
        }

        var renamed = dataset.Rename(command.Name, clock.GetUtcNow());
        if (renamed.IsFailure)
        {
            return renamed.ToFailure<SpolDatasetDto>();
        }

        if (dataset.Version != command.ExpectedVersion &&
            !await datasets.SaveAsync(dataset, command.ExpectedVersion, cancellationToken))
        {
            return DomainErrors.DatasetConcurrencyConflict(dataset.Id);
        }

        return dataset.ToDto(await SpolDatasetReads.LastProcessingAsync(imports, dataset.Id, cancellationToken));
    }
}

/// <summary>Deletes a dataset and its rows. Its import jobs, and their reports, are kept.</summary>
public sealed record DeleteSpolDatasetCommand(Guid DatasetId) : ICommand<Guid>;

internal sealed class DeleteSpolDatasetCommandHandler(ISpolDatasetStore datasets, IImportJobStore imports)
    : ICommandHandler<DeleteSpolDatasetCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(
        DeleteSpolDatasetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await datasets.GetAsync(command.DatasetId, cancellationToken) is null)
        {
            return DomainErrors.DatasetNotFound(command.DatasetId);
        }

        if (await imports.HasActiveForDatasetAsync(command.DatasetId, cancellationToken))
        {
            return DomainErrors.DatasetProcessing(command.DatasetId);
        }

        return await datasets.DeleteAsync(command.DatasetId, cancellationToken)
            ? command.DatasetId
            : DomainErrors.DatasetNotFound(command.DatasetId);
    }
}

/// <summary>
/// One page of a load from the connector, with the dataset's key. The first page of a new load
/// replaces the dataset's rows; rows are stored by item ID, so a page sent twice changes nothing.
/// </summary>
public sealed record LoadSpolDatasetPageCommand(
    Guid DatasetId,
    string? Key,
    Guid LoadId,
    int Page,
    int PageCount,
    int TotalRows,
    IReadOnlyList<SpolDatasetRowDto?>? Rows) : ICommand<SpolDatasetPageResultDto>;

internal sealed class LoadSpolDatasetPageCommandHandler(
    ISpolDatasetStore datasets,
    IImportJobStore imports,
    TimeProvider clock) : ICommandHandler<LoadSpolDatasetPageCommand, SpolDatasetPageResultDto>
{
    /// <summary>A rename between reading the dataset and storing the page is not the page's fault.</summary>
    private const int MaxAttempts = 3;

    public async Task<Result<SpolDatasetPageResultDto>> HandleAsync(
        LoadSpolDatasetPageCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dataset = await datasets.GetAsync(command.DatasetId, cancellationToken);
        if (!SpolDatasetKey.Matches(dataset?.KeyHash, command.Key) || dataset is null)
        {
            return DomainErrors.DatasetUnauthorised();
        }

        var invalid = SpolDatasetRows.Validate(command.Rows);
        if (invalid is not null)
        {
            return invalid;
        }

        if (command.Rows!.Count > command.TotalRows)
        {
            return DomainErrors.Validation(
                $"The page has {command.Rows.Count} rows, more than the load's totalRows of {command.TotalRows}.");
        }

        var items = command.Rows
            .Select(row => new SpolDatasetItem(row!.ItemId, SpolDatasetRows.Serialize(row)))
            .ToList();

        for (var attempt = 1; ; attempt++)
        {
            if (await imports.HasActiveForDatasetAsync(dataset.Id, cancellationToken))
            {
                return DomainErrors.DatasetProcessing(dataset.Id);
            }

            var expectedVersion = dataset.Version;
            var accepted = dataset.AcceptPage(
                command.LoadId, command.Page, command.PageCount, command.TotalRows, clock.GetUtcNow());
            if (accepted.IsFailure)
            {
                return accepted.ToFailure<SpolDatasetPageResultDto>();
            }

            switch (await datasets.ApplyPageAsync(dataset, expectedVersion, accepted.Value, items, cancellationToken))
            {
                case SpolDatasetPageOutcome.Stored:
                    return new SpolDatasetPageResultDto(
                        dataset.Id,
                        command.LoadId,
                        command.Page,
                        command.PageCount,
                        items.Count,
                        dataset.RowCount,
                        command.TotalRows,
                        dataset.Status.ToString().ToLowerInvariant());

                case SpolDatasetPageOutcome.TooManyRows:
                    return DomainErrors.Validation(
                        $"The page would give the dataset more rows than the load's totalRows of {command.TotalRows}.");

                case SpolDatasetPageOutcome.VersionConflict when attempt < MaxAttempts:
                    dataset = await datasets.GetAsync(command.DatasetId, cancellationToken);
                    if (dataset is null)
                    {
                        return DomainErrors.DatasetUnauthorised();
                    }

                    break;

                default:
                    return DomainErrors.DatasetConcurrencyConflict(dataset.Id);
            }
        }
    }
}

/// <summary>
/// Starts an import job over a Ready dataset's rows. As with any import, a group has one import
/// job in flight at a time.
/// </summary>
public sealed record ProcessSpolDatasetCommand(Guid DatasetId, string GroupId, Actor Actor) : ICommand<ImportJobDto>;

internal sealed class ProcessSpolDatasetCommandHandler(
    ISpolDatasetStore datasets,
    IImportJobStore imports,
    TimeProvider clock) : ICommandHandler<ProcessSpolDatasetCommand, ImportJobDto>
{
    public async Task<Result<ImportJobDto>> HandleAsync(
        ProcessSpolDatasetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        if (string.IsNullOrWhiteSpace(command.GroupId))
        {
            return DomainErrors.Validation("An import must be scoped to a group.");
        }

        var dataset = await datasets.GetAsync(command.DatasetId, cancellationToken);
        if (dataset is null)
        {
            return DomainErrors.DatasetNotFound(command.DatasetId);
        }

        if (dataset.Status != SpolDatasetStatus.Ready)
        {
            return DomainErrors.DatasetNotReady(dataset.Id, dataset.Status.ToString());
        }

        var groupId = command.GroupId.Trim();
        var inFlight = await imports.GetActiveForGroupAsync(groupId, cancellationToken);
        if (inFlight is not null)
        {
            return DomainErrors.ImportAlreadyInProgress(groupId, inFlight.Id);
        }

        var job = ImportJob.Create(
            Guid.CreateVersion7(),
            groupId,
            dataset.Name,
            dataset.Id,
            command.Actor.DisplayName,
            dataset.RowCount,
            clock.GetUtcNow());

        // The unique index is the real guard; the check above only gives a better error message.
        if (!await imports.TryCreateAsync(job, cancellationToken))
        {
            return DomainErrors.ImportAlreadyInProgress(groupId, job.Id);
        }

        return job.ToDto();
    }
}

internal static class SpolDatasetReads
{
    public static async Task<ImportJob?> LastProcessingAsync(
        IImportJobStore imports, Guid datasetId, CancellationToken cancellationToken) =>
        (await imports.SearchAsync(null, new PageRequest(1, 1), datasetId, cancellationToken)).Items
        .FirstOrDefault();
}
