using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Imports;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Datasets;
using Cleanse.Core.Domain.Imports;

namespace Cleanse.Api.Imports;

/// <summary>
/// Leases pending imports and applies their datasets' rows. Runs in the background so processing
/// returns straight away: 30,000 items cannot be applied inside API Gateway's 29 second ceiling.
/// </summary>
public sealed class ImportProcessorService(
    IServiceScopeFactory scopeFactory,
    ImportOptions options,
    TimeProvider clock,
    ILogger<ImportProcessorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReapStalledAsync(stoppingToken);

                if (!await TryProcessNextAsync(stoppingToken))
                {
                    await Task.Delay(options.PollInterval, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad job kill the loop; the next pass picks up where this left off.
                logger.LogError(ex, "The import processor loop failed and will retry.");
                await Task.Delay(options.PollInterval, clock, stoppingToken);
            }
        }
    }

    private async Task ReapStalledAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICleanseDispatcher>();

        var result = await dispatcher.SendAsync(
            new ReapStalledImportsCommand(options.StaleAfter), cancellationToken);

        if (result.IsSuccess && result.Value > 0)
        {
            logger.LogWarning(
                "Reclaimed {Count} import(s) whose worker stopped reporting.", result.Value);
        }
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;
        var imports = provider.GetRequiredService<IImportJobStore>();

        var job = await imports.LeaseNextPendingAsync(clock.GetUtcNow(), cancellationToken);
        if (job is null)
        {
            return false;
        }

        var dispatcher = provider.GetRequiredService<ICleanseDispatcher>();
        logger.LogInformation(
            "Processing import {ImportJobId} for group {GroupId}.", job.Id, job.GroupId);

        try
        {
            await ProcessAsync(provider, dispatcher, job, cancellationToken);
            await dispatcher.SendAsync(new CompleteImportJobCommand(job.Id), cancellationToken);
            logger.LogInformation("Import {ImportJobId} completed.", job.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import {ImportJobId} failed.", job.Id);
            await dispatcher.SendAsync(
                new FailImportJobCommand(job.Id, ex.Message), CancellationToken.None);
        }

        return true;
    }

    private async Task ProcessAsync(
        IServiceProvider provider,
        ICleanseDispatcher dispatcher,
        ImportJob job,
        CancellationToken cancellationToken)
    {
        var datasets = provider.GetRequiredService<ISpolDatasetStore>();
        var imports = provider.GetRequiredService<IImportJobStore>();

        // A job from before datasets, or one whose dataset has gone, has nothing left to read.
        var dataset = job.DatasetId is { } datasetId ? await datasets.GetAsync(datasetId, cancellationToken) : null;
        if (dataset is null)
        {
            throw new InvalidOperationException("The import's dataset no longer exists.");
        }

        if (dataset.Status != SpolDatasetStatus.Ready)
        {
            throw new InvalidOperationException(
                $"The dataset is {dataset.Status}, so not all of its rows are there. Load it again, then process it.");
        }

        // Collapsing duplicates needs every row in view, so rows are materialised before
        // chunking. 30,000 rows is a few tens of megabytes; streaming would make the rule unstable.
        var rows = SpolDatasetReader.Read(await datasets.ReadItemsAsync(dataset.Id, cancellationToken), job.GroupId)
            .ToList();
        var batch = SpolRowMapper.Project(rows);

        await RecordUnkeyedRowsAsync(imports, job, batch, cancellationToken);
        await RecordSuspectDatesAsync(imports, job, batch, cancellationToken);
        await RecordAssigneeConflictsAsync(imports, job, batch, cancellationToken);
        await RecordSeveralAssigneesAsync(imports, job, batch, cancellationToken);

        // Everyone Assigned to names becomes a user before any row is applied, so each holding
        // can be given to its person. Saved after the counts above, which the job also carries.
        var registered = await dispatcher.SendAsync(
            new RegisterImportAssigneesCommand(job.Id, [.. rows.SelectMany(row => row.Assignees)]),
            cancellationToken);
        if (registered.IsFailure)
        {
            throw new InvalidOperationException($"Adding the assignees as users failed: {registered.Error}");
        }

        foreach (var chunk in batch.Rows.Chunk(options.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await dispatcher.SendAsync(
                new ApplyImportBatchCommand(job.Id, chunk, Actor.Machine(SpolDatasetReader.Source)),
                cancellationToken);

            if (result.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Applying a batch of {chunk.Length} items failed: {result.Error}");
            }
        }
    }

    /// <summary>
    /// Warnings only: a holding takes one assignee, so where an item names several people the
    /// first was chosen, and the team should check it.
    /// </summary>
    private async Task RecordSeveralAssigneesAsync(
        IImportJobStore imports,
        ImportJob job,
        SpolBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.SeveralAssignees.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "Import {ImportJobId} has {Count} item(s) naming more than one person.",
            job.Id, batch.SeveralAssignees.Count);

        var rejections = batch.SeveralAssignees
            .Take(options.MaxRejectionsRecorded)
            .Select(row => new ImportJobRejection(
                job.Id,
                row.RowNumber,
                row.ToIssueId().Value,
                ImportRejectionReasons.AssigneeSeveral,
                $"Assigned to names {row.Assignees.Count} people. The holding takes the first."))
            .ToList();

        await imports.AddRejectionsAsync(rejections, cancellationToken);
    }

    /// <summary>
    /// Warnings only: a holding takes one assignee, so where a CPH's items disagree one of them was
    /// chosen, and the team should check it.
    /// </summary>
    private async Task RecordAssigneeConflictsAsync(
        IImportJobStore imports,
        ImportJob job,
        SpolBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.AssigneeConflicts.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "Import {ImportJobId} has {Count} CPH(s) whose items name different assignees.",
            job.Id, batch.AssigneeConflicts.Count);

        var rejections = batch.AssigneeConflicts
            .Take(options.MaxRejectionsRecorded)
            .Select(c => new ImportJobRejection(
                job.Id,
                c.Row.RowNumber,
                c.Row.ToIssueId().Value,
                ImportRejectionReasons.AssigneeConflict,
                $"Items {string.Join(", ", c.NamingRows)} for CPH {c.Row.Cph?.Trim()} name different assignees. " +
                $"The holding takes item {c.Row.RowNumber}'s."))
            .ToList();

        await imports.AddRejectionsAsync(rejections, cancellationToken);
    }

    /// <summary>Warnings only: the item is still applied exactly as the source states it.</summary>
    private async Task RecordSuspectDatesAsync(
        IImportJobStore imports,
        ImportJob job,
        SpolBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.SuspectDates.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "Import {ImportJobId} has {Count} item(s) with suspect dates.", job.Id, batch.SuspectDates.Count);

        var rejections = batch.SuspectDates
            .Take(options.MaxRejectionsRecorded)
            .Select(s => new ImportJobRejection(
                job.Id,
                s.Row.RowNumber,
                s.Row.ToIssueId().Value,
                ImportRejectionReasons.SuspectDate,
                s.Detail))
            .ToList();

        await imports.AddRejectionsAsync(rejections, cancellationToken);
    }

    /// <summary>
    /// Items with no CPH or rule number, plus the duplicates that lost the collapse. Counted up
    /// front so the progress percentage can still reach 100.
    /// </summary>
    private async Task RecordUnkeyedRowsAsync(
        IImportJobStore imports,
        ImportJob job,
        SpolBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.Invalid.Count > 0)
        {
            var rejections = batch.Invalid
                .Take(options.MaxRejectionsRecorded)
                .Select(row => new ImportJobRejection(
                    job.Id,
                    row.RowNumber,
                    null,
                    ImportRejectionReasons.InvalidRow,
                    $"Item has no usable key (CPH '{row.Cph ?? "<blank>"}', rule '{row.RuleNo ?? "<blank>"}')."))
                .ToList();

            await imports.AddRejectionsAsync(rejections, cancellationToken);
        }

        if (batch.Invalid.Count == 0 && batch.DuplicatesCollapsed == 0)
        {
            return;
        }

        var progress = job.RecordProgress(
            ImportJobCounters.Empty with
            {
                ProcessedRows = batch.Invalid.Count + batch.DuplicatesCollapsed,
                Invalid = batch.Invalid.Count,
                DuplicatesCollapsed = batch.DuplicatesCollapsed
            },
            clock.GetUtcNow());

        if (progress.IsSuccess)
        {
            await imports.SaveAsync(job, cancellationToken);
        }
    }
}
