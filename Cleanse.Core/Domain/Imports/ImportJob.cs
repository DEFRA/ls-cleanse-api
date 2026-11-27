namespace Cleanse.Core.Domain.Imports;

public enum ImportJobStatus
{
    Pending,
    Processing,
    Completed,
    Failed,

    /// <summary>Its worker stopped reporting. Reclaimed by the reaper, not by the worker itself.</summary>
    Abandoned
}

public sealed record ImportJobCounters(
    int TotalRows,
    int ProcessedRows,
    int Matched,
    int Unmatched,
    int DuplicatesCollapsed,
    int Invalid,
    int UsersCreated = 0)
{
    public static readonly ImportJobCounters Empty = new(0, 0, 0, 0, 0, 0);

    /// <summary>Zero rather than a divide-by-zero when the row count is not yet known.</summary>
    public int PercentComplete => TotalRows <= 0
        ? 0
        : (int)Math.Clamp(Math.Round(ProcessedRows / (double)TotalRows * 100), 0, 100);
}

/// <summary>
/// <see cref="BlobKey"/>, <see cref="TenantId"/> and <see cref="ClientId"/> belong to the file
/// uploads that came before datasets, and are kept only so those jobs and their reports still read.
/// </summary>
public sealed record ImportJobSnapshot(
    Guid Id,
    string GroupId,
    string FileName,
    string? BlobKey,
    ImportJobStatus Status,
    ImportJobCounters Counters,
    string RequestedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    DateTimeOffset? HeartbeatAtUtc,
    string? FailureReason,
    int Version,
    string? TenantId = null,
    string? ClientId = null,
    Guid? DatasetId = null);

/// <summary>
/// One processing run of a SPOL dataset. Tracks progress so a long import can be watched from the
/// front end, and carries a heartbeat so a worker killed mid-run leaves evidence rather than a
/// job that looks alive forever.
/// </summary>
public sealed class ImportJob
{
    private ImportJob(ImportJobSnapshot snapshot)
    {
        Id = snapshot.Id;
        GroupId = snapshot.GroupId;
        FileName = snapshot.FileName;
        BlobKey = snapshot.BlobKey;
        Status = snapshot.Status;
        Counters = snapshot.Counters;
        RequestedBy = snapshot.RequestedBy;
        CreatedAtUtc = snapshot.CreatedAtUtc;
        StartedAtUtc = snapshot.StartedAtUtc;
        FinishedAtUtc = snapshot.FinishedAtUtc;
        HeartbeatAtUtc = snapshot.HeartbeatAtUtc;
        FailureReason = snapshot.FailureReason;
        Version = snapshot.Version;
        TenantId = snapshot.TenantId;
        ClientId = snapshot.ClientId;
        DatasetId = snapshot.DatasetId;
    }

    public Guid Id { get; }

    public string GroupId { get; }

    /// <summary>The dataset's name when it was processed, or an earlier upload's file name.</summary>
    public string FileName { get; }

    /// <summary>Where an earlier upload's file was kept. Null for a dataset.</summary>
    public string? BlobKey { get; }

    public string? TenantId { get; }

    public string? ClientId { get; }

    /// <summary>The dataset processed. Null for an earlier upload, or once the dataset is deleted.</summary>
    public Guid? DatasetId { get; }

    public ImportJobStatus Status { get; private set; }

    public ImportJobCounters Counters { get; private set; }

    public string RequestedBy { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? FinishedAtUtc { get; private set; }

    public DateTimeOffset? HeartbeatAtUtc { get; private set; }

    public string? FailureReason { get; private set; }

    public int Version { get; private set; }

    public bool IsTerminal => Status is ImportJobStatus.Completed or ImportJobStatus.Failed
        or ImportJobStatus.Abandoned;

    /// <summary>Processes <paramref name="datasetId"/>'s rows, of which there are <paramref name="totalRows"/>.</summary>
    public static ImportJob Create(
        Guid id,
        string groupId,
        string datasetName,
        Guid datasetId,
        string requestedBy,
        int totalRows,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        ArgumentOutOfRangeException.ThrowIfNegative(totalRows);

        return new ImportJob(new ImportJobSnapshot(
            id,
            groupId.Trim(),
            datasetName.Trim(),
            BlobKey: null,
            ImportJobStatus.Pending,
            ImportJobCounters.Empty with { TotalRows = totalRows },
            requestedBy.Trim(),
            now,
            StartedAtUtc: null,
            FinishedAtUtc: null,
            HeartbeatAtUtc: null,
            FailureReason: null,
            Version: 1,
            DatasetId: datasetId));
    }

    public static ImportJob FromSnapshot(ImportJobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ImportJob(snapshot);
    }

    public ImportJobSnapshot ToSnapshot() => new(
        Id, GroupId, FileName, BlobKey, Status, Counters, RequestedBy,
        CreatedAtUtc, StartedAtUtc, FinishedAtUtc, HeartbeatAtUtc, FailureReason, Version, TenantId, ClientId,
        DatasetId);

    public Result<ImportJob> Start(DateTimeOffset now)
    {
        if (Status != ImportJobStatus.Pending)
        {
            return DomainErrors.ImportNotPending(Id, Status.ToString().ToLowerInvariant());
        }

        Status = ImportJobStatus.Processing;
        StartedAtUtc = now;
        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    public Result<ImportJob> RecordProgress(ImportJobCounters delta, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (Status != ImportJobStatus.Processing)
        {
            return DomainErrors.ImportNotProcessing(Id, Status.ToString().ToLowerInvariant());
        }

        Counters = Counters with
        {
            ProcessedRows = Counters.ProcessedRows + delta.ProcessedRows,
            Matched = Counters.Matched + delta.Matched,
            Unmatched = Counters.Unmatched + delta.Unmatched,
            DuplicatesCollapsed = Counters.DuplicatesCollapsed + delta.DuplicatesCollapsed,
            Invalid = Counters.Invalid + delta.Invalid,
            UsersCreated = Counters.UsersCreated + delta.UsersCreated
        };

        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    public Result<ImportJob> Complete(DateTimeOffset now)
    {
        if (Status != ImportJobStatus.Processing)
        {
            return DomainErrors.ImportNotProcessing(Id, Status.ToString().ToLowerInvariant());
        }

        Status = ImportJobStatus.Completed;
        FinishedAtUtc = now;
        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    public Result<ImportJob> Fail(string reason, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return DomainErrors.ImportAlreadyFinished(Id, Status.ToString().ToLowerInvariant());
        }

        Status = ImportJobStatus.Failed;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        FinishedAtUtc = now;
        Version++;
        return this;
    }

    /// <summary>Reclaims a job whose worker stopped reporting. Only the reaper calls this.</summary>
    public Result<ImportJob> Abandon(string reason, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return DomainErrors.ImportAlreadyFinished(Id, Status.ToString().ToLowerInvariant());
        }

        Status = ImportJobStatus.Abandoned;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        FinishedAtUtc = now;
        Version++;
        return this;
    }

    public bool HasStalled(DateTimeOffset now, TimeSpan threshold) =>
        Status == ImportJobStatus.Processing &&
        HeartbeatAtUtc is { } beat &&
        now - beat > threshold;
}

/// <summary>A row the import could not apply, kept for the reconciliation report.</summary>
public sealed record ImportJobRejection(
    Guid ImportJobId,
    long RowNumber,
    string? IssueId,
    string Reason,
    string Detail);

public static class ImportRejectionReasons
{
    public const string IssueNotFound = "issue_not_found";
    public const string InvalidRow = "invalid_row";
    public const string SuspectDate = "suspect_date";
    public const string AssigneeUnmatched = "assignee_unmatched";
    public const string AssigneeAmbiguous = "assignee_ambiguous";

    /// <summary>A CPH's items named different people. One was given the holding.</summary>
    public const string AssigneeConflict = "assignee_conflict";

    /// <summary>An item named more than one person. The first was given the holding.</summary>
    public const string AssigneeSeveral = "assignee_several";
}
