using System.Globalization;

namespace Cleanse.Core.Domain.Datasets;

public enum SpolDatasetStatus
{
    /// <summary>Nothing has been loaded yet.</summary>
    Empty,

    /// <summary>A load has started, and not all of its rows have arrived.</summary>
    Loading,

    /// <summary>All of the last load's rows have arrived, so it can be processed.</summary>
    Ready
}

public sealed record SpolDatasetSnapshot(
    Guid Id,
    string Name,
    string KeyHash,
    SpolDatasetStatus Status,
    int RowCount,
    int? ExpectedRows,
    int? PageCount,
    Guid? CurrentLoadId,
    DateTimeOffset? LoadStartedAtUtc,
    DateTimeOffset? LastLoadedAtUtc,
    string CreatedById,
    string CreatedByName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int Version);

/// <summary>One SharePoint list item as the connector sent it: its item ID and its JSON.</summary>
public sealed record SpolDatasetItem(int ItemId, string Data);

/// <summary>What storing a page came to.</summary>
public enum SpolDatasetPageOutcome
{
    Stored,

    /// <summary>The dataset changed since it was read, so nothing was stored.</summary>
    VersionConflict,

    /// <summary>The page would take the dataset past its load's total, so nothing was stored.</summary>
    TooManyRows
}

/// <summary>
/// A staging area for one export of the SPOL list. The connector loads it a page at a time with
/// the dataset's key, and an admin then processes it. Loading changes nothing but the dataset's
/// own rows: issues, holdings and users change only when it is processed.
/// </summary>
public sealed class SpolDataset
{
    public const int MaxNameLength = 100;

    /// <summary>The most rows one page may carry.</summary>
    public const int MaxRowsPerPage = 500;

    /// <summary>The most rows a dataset may hold. The list had 31,887 items in November 2026.</summary>
    public const int MaxRows = 200_000;

    private SpolDataset(SpolDatasetSnapshot snapshot)
    {
        Id = snapshot.Id;
        Name = snapshot.Name;
        KeyHash = snapshot.KeyHash;
        Status = snapshot.Status;
        RowCount = snapshot.RowCount;
        ExpectedRows = snapshot.ExpectedRows;
        PageCount = snapshot.PageCount;
        CurrentLoadId = snapshot.CurrentLoadId;
        LoadStartedAtUtc = snapshot.LoadStartedAtUtc;
        LastLoadedAtUtc = snapshot.LastLoadedAtUtc;
        CreatedById = snapshot.CreatedById;
        CreatedByName = snapshot.CreatedByName;
        CreatedAtUtc = snapshot.CreatedAtUtc;
        UpdatedAtUtc = snapshot.UpdatedAtUtc;
        Version = snapshot.Version;
    }

    public Guid Id { get; }

    public string Name { get; private set; }

    /// <summary>The SHA-256 of the key, as lower-case hex. The key itself is never kept.</summary>
    public string KeyHash { get; }

    public SpolDatasetStatus Status { get; private set; }

    public int RowCount { get; private set; }

    /// <summary>The current load's total, from its pages.</summary>
    public int? ExpectedRows { get; private set; }

    public int? PageCount { get; private set; }

    public Guid? CurrentLoadId { get; private set; }

    public DateTimeOffset? LoadStartedAtUtc { get; private set; }

    /// <summary>When a load last arrived in full.</summary>
    public DateTimeOffset? LastLoadedAtUtc { get; private set; }

    public string CreatedById { get; }

    public string CreatedByName { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>When it was last renamed or loaded.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public int Version { get; private set; }

    /// <summary>Without a name, a dataset is named after when it was created, in UK time.</summary>
    public static Result<SpolDataset> Create(
        Guid id, string? name, string keyHash, Actor createdBy, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHash);
        ArgumentNullException.ThrowIfNull(createdBy);

        var chosen = string.IsNullOrWhiteSpace(name)
            ? "SPOL " + UkTime.ToUk(now).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)
            : name.Trim();

        if (chosen.Length > MaxNameLength)
        {
            return DomainErrors.Validation($"A dataset name can be up to {MaxNameLength} characters.");
        }

        return new SpolDataset(new SpolDatasetSnapshot(
            id, chosen, keyHash, SpolDatasetStatus.Empty, 0, null, null, null, null, null,
            createdBy.Id, createdBy.DisplayName, now, now, 1));
    }

    public static SpolDataset FromSnapshot(SpolDatasetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SpolDataset(snapshot);
    }

    public SpolDatasetSnapshot ToSnapshot() => new(
        Id, Name, KeyHash, Status, RowCount, ExpectedRows, PageCount, CurrentLoadId, LoadStartedAtUtc,
        LastLoadedAtUtc, CreatedById, CreatedByName, CreatedAtUtc, UpdatedAtUtc, Version);

    public Result<SpolDataset> Rename(string? name, DateTimeOffset now)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return DomainErrors.Validation("Enter a name for the dataset.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            return DomainErrors.Validation($"A dataset name can be up to {MaxNameLength} characters.");
        }

        if (string.Equals(trimmed, Name, StringComparison.Ordinal))
        {
            return this;
        }

        Name = trimmed;
        UpdatedAtUtc = now;
        Version++;
        return this;
    }

    /// <summary>
    /// Takes one page of a load. The first page of a new load empties the dataset, so the result
    /// says whether its rows must be cleared. Once a new load starts, an older one's pages are
    /// refused, and every page of a load must agree on its size.
    /// </summary>
    public Result<bool> AcceptPage(Guid loadId, int page, int pageCount, int totalRows, DateTimeOffset now)
    {
        if (loadId == Guid.Empty)
        {
            return DomainErrors.Validation("loadId must be a nonempty GUID.");
        }

        if (pageCount < 1 || page < 1 || page > pageCount)
        {
            return DomainErrors.Validation("page must be between 1 and pageCount.");
        }

        if (totalRows < 1)
        {
            return DomainErrors.Validation("totalRows must be at least 1.");
        }

        if (totalRows > MaxRows)
        {
            return DomainErrors.DatasetTooLarge($"A dataset can hold up to {MaxRows} rows, not {totalRows}.");
        }

        var clear = CurrentLoadId != loadId;
        if (clear)
        {
            if (page != 1)
            {
                return DomainErrors.DatasetLoadSuperseded(Id);
            }

            CurrentLoadId = loadId;
            ExpectedRows = totalRows;
            PageCount = pageCount;
            LoadStartedAtUtc = now;
            RowCount = 0;
            Status = SpolDatasetStatus.Loading;
        }
        else if (ExpectedRows != totalRows || PageCount != pageCount)
        {
            return DomainErrors.Validation(
                $"This load has {PageCount} pages and {ExpectedRows} rows. Every page must say so.");
        }

        UpdatedAtUtc = now;
        Version++;
        return clear;
    }

    /// <summary>
    /// The rows the dataset holds once a page is stored. It is Ready when they reach the load's
    /// total, and was loaded then.
    /// </summary>
    public void RecordRowCount(int rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);

        RowCount = rowCount;
        if (rowCount != ExpectedRows)
        {
            Status = SpolDatasetStatus.Loading;
            return;
        }

        if (Status != SpolDatasetStatus.Ready)
        {
            Status = SpolDatasetStatus.Ready;
            LastLoadedAtUtc = UpdatedAtUtc;
        }
    }
}
