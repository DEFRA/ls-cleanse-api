using System.Text.Json.Serialization;

namespace Cleanse.Core.Contracts;

public sealed record SpolDatasetDto(
    Guid Id,
    string Name,
    string Status,
    int RowCount,
    int? ExpectedRows,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LoadStartedAtUtc,
    DateTimeOffset? LastLoadedAtUtc,
    SpolDatasetProcessingDto? LastProcessing,
    int Version);

/// <summary>The dataset's newest import job.</summary>
public sealed record SpolDatasetProcessingDto(
    Guid ImportJobId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? FinishedAtUtc);

/// <summary>The only time the key is ever given out.</summary>
public sealed record CreatedSpolDatasetDto(SpolDatasetDto Dataset, string Key);

public sealed record SpolDatasetPageResultDto(
    Guid DatasetId,
    Guid LoadId,
    int Page,
    int PageCount,
    int RowsInPage,
    int RowCount,
    int TotalRows,
    string Status);

/// <summary>
/// One SharePoint list item, with every column of the Registration Livestock Data list, as
/// ls-cleanse-spol-connector writes it. Dates and times are ISO 8601 with an offset; everything
/// else is the text SharePoint gave. A property Cleanse does not know refuses the whole page.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SpolDatasetRowDto
{
    /// <summary>The SharePoint item ID.</summary>
    public int ItemId { get; init; }

    public string? DateReceived { get; init; }

    public string? Priority { get; init; }

    public string? Cph { get; init; }

    public string? RuleNo { get; init; }

    public string? RuleName { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorCodeDescription { get; init; }

    public string? EmailCts { get; init; }

    public string? EmailSam { get; init; }

    public string? TelNumbersCts { get; init; }

    public string? TelNumbersSam { get; init; }

    public string? FsaNumber { get; init; }

    public string? CorrespondenceAddressSam { get; init; }

    public string? CorrespondenceAddressCts { get; init; }

    public string? AssistedDigital { get; init; }

    public string? Status { get; init; }

    /// <summary>Assigned to's names, separated by semicolons when there are several.</summary>
    public string? AssignedName { get; init; }

    /// <summary>Assigned to's email addresses, in the same order as the names.</summary>
    public string? AssignedEmail { get; init; }

    public string? FirstContactDate { get; init; }

    public string? SecondContactDate { get; init; }

    public string? ThirdContactDate { get; init; }

    public string? LetterEmailSent { get; init; }

    public string? PassedToOtherAgencyDept { get; init; }

    public string? NameOfDepartment { get; init; }

    public string? CheckOnCrmRequired { get; init; }

    public string? Notes { get; init; }

    public string? DateComplete { get; init; }

    public string? ColorTag { get; init; }

    public string? ComplianceAssetId { get; init; }

    public string? Modified { get; init; }

    public string? ContentType { get; init; }

    public string? Created { get; init; }

    public string? CreatedBy { get; init; }

    public string? ModifiedBy { get; init; }

    public string? Version { get; init; }

    public string? Attachments { get; init; }

    public string? Edit { get; init; }

    public string? Type { get; init; }

    public string? ItemChildCount { get; init; }

    public string? FolderChildCount { get; init; }

    public string? LabelSetting { get; init; }

    public string? RetentionLabel { get; init; }

    public string? RetentionLabelApplied { get; init; }

    public string? LabelAppliedBy { get; init; }

    public string? ItemIsARecord { get; init; }

    public string? AppCreatedBy { get; init; }

    public string? AppModifiedBy { get; init; }
}
