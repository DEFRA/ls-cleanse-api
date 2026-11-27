namespace Cleanse.Core.Domain;

/// <summary>The admin-maintained lists. They share one shape and one set of rules.</summary>
public enum CodedList
{
    /// <summary>Things caseworkers do about a holding, such as "Updated SAM".</summary>
    ActionType,

    /// <summary>Optional reasons an issue was resolved.</summary>
    ResolvedReason,

    /// <summary>Reasons an issue can't be resolved. One is required to give that outcome.</summary>
    CantResolveReason
}

public static class CodedLists
{
    public const string ActionTypes = "action-types";
    public const string ResolvedReasons = "resolved-reasons";
    public const string CantResolveReasons = "cant-resolve-reasons";

    public static string ToWire(this CodedList list) => list switch
    {
        CodedList.ActionType => ActionTypes,
        CodedList.ResolvedReason => ResolvedReasons,
        CodedList.CantResolveReason => CantResolveReasons,
        _ => throw new ArgumentOutOfRangeException(nameof(list), list, null)
    };

    public static bool TryParse(string? value, out CodedList list)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case ActionTypes:
                list = CodedList.ActionType;
                return true;
            case ResolvedReasons:
                list = CodedList.ResolvedReason;
                return true;
            case CantResolveReasons:
                list = CodedList.CantResolveReason;
                return true;
            default:
                list = default;
                return false;
        }
    }

    /// <summary>The list an outcome takes its reasons from, or null for a status that is not one.</summary>
    public static CodedList? ReasonsFor(WorkflowStatus status) => status switch
    {
        WorkflowStatus.Resolved => CodedList.ResolvedReason,
        WorkflowStatus.Nonactionable => CodedList.CantResolveReason,
        _ => null
    };
}

public sealed record CodedValueSnapshot(
    Guid Id,
    CodedList List,
    string Name,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// One entry in an admin-maintained list. Entries are never renamed or deleted, so what was
/// recorded against one keeps its meaning; a deactivated entry is just no longer offered.
/// </summary>
public sealed class CodedValue
{
    public const int MaxNameLength = 100;

    private CodedValue(
        Guid id, CodedList list, string name, bool isActive, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        Id = id;
        List = list;
        Name = name;
        IsActive = isActive;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid Id { get; }

    public CodedList List { get; }

    public string Name { get; }

    /// <summary>What makes two names the same entry: case and surrounding space are ignored.</summary>
    public string NameKey => KeyOf(Name);

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static string KeyOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().ToLowerInvariant();
    }

    public static Result<CodedValue> Create(CodedList list, string? name, DateTimeOffset now)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return DomainErrors.Validation("Enter a name.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            return DomainErrors.Validation($"A name can be up to {MaxNameLength} characters.");
        }

        return new CodedValue(Guid.CreateVersion7(now), list, trimmed, isActive: true, now, now);
    }

    public static CodedValue FromSnapshot(CodedValueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new CodedValue(
            snapshot.Id, snapshot.List, snapshot.Name, snapshot.IsActive, snapshot.CreatedAtUtc, snapshot.UpdatedAtUtc);
    }

    public CodedValueSnapshot ToSnapshot() => new(Id, List, Name, IsActive, CreatedAtUtc, UpdatedAtUtc);

    public IssueChange SetActive(bool active, DateTimeOffset now)
    {
        if (IsActive == active)
        {
            return IssueChange.None;
        }

        var change = new AuditChange(nameof(IsActive), IsActive.ToString(), active.ToString());
        IsActive = active;
        UpdatedAtUtc = now;
        return new IssueChange(active ? AuditEventKind.CodedValueActivated : AuditEventKind.CodedValueDeactivated, [change]);
    }
}
