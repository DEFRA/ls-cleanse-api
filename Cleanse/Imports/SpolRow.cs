using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Imports;

/// <summary>One SharePoint list item, before it is turned into an issue update.</summary>
public sealed record SpolRow
{
    /// <summary>The SharePoint item ID, which the report names the item by.</summary>
    public required long RowNumber { get; init; }

    public required string GroupId { get; init; }

    public string? Cph { get; init; }

    public string? RuleNo { get; init; }

    public string? Status { get; init; }

    public string? Notes { get; init; }

    public bool HasAssignee { get; init; }

    /// <summary>The first person in Assigned to, as SharePoint names them.</summary>
    public string? AssignedTo { get; init; }

    /// <summary>The first person in Assigned to's email address.</summary>
    public string? AssignedEmail { get; init; }

    /// <summary>Everyone in Assigned to, in order. The holding takes the first.</summary>
    public IReadOnlyList<ExternalAssignee> Assignees { get; init; } = [];

    public string? Priority { get; init; }

    public DateTimeOffset? DateReceived { get; init; }

    /// <summary>Used only to break ties between rows sharing a key.</summary>
    public DateTimeOffset? DateComplete { get; init; }

    public IReadOnlyList<ExternalContactEvent> Contacts { get; init; } = [];

    public bool IsKeyed => !string.IsNullOrWhiteSpace(Cph) && !string.IsNullOrWhiteSpace(RuleNo);

    /// <summary>
    /// Who the item names: their email address where it has one, otherwise their name with its
    /// spacing tidied. Null when it names nobody.
    /// </summary>
    public string? AssigneeKey =>
        !string.IsNullOrWhiteSpace(AssignedEmail)
            ? AssignedEmail.Trim().ToLowerInvariant()
            : string.IsNullOrWhiteSpace(AssignedTo)
                ? null
                : string.Join(' ', AssignedTo.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>
    /// Must agree with the rules engine's scheme, or every imported row would reconcile against
    /// nothing. The group is a column on the issue, not part of its identity.
    /// </summary>
    public IssueId ToIssueId() => IssueId.Derive(RuleNo!.Trim(), Cph!.Trim());
}
