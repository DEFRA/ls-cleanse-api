namespace Cleanse.Core.Domain;

/// <summary>
/// A holding is a CPH within a rule group: the unit caseworkers work on. Not an identifier the
/// rules engine knows about, so the CPH is whatever the issues' context carries.
/// </summary>
public readonly record struct HoldingKey
{
    public HoldingKey(string groupId, string cph)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cph);
        GroupId = groupId.Trim();
        Cph = cph.Trim();
    }

    public string GroupId { get; }

    public string Cph { get; }

    /// <summary>The context key the rules engine writes the CPH under.</summary>
    public const string CphContextKey = "cph";

    /// <summary>The holding an issue belongs to, or null when its context carries no CPH.</summary>
    public static HoldingKey? For(Issue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return issue.Context.TryGetValue(CphContextKey, out var cph) && !string.IsNullOrWhiteSpace(cph)
            ? new HoldingKey(issue.GroupId, cph)
            : null;
    }

    public override string ToString() => $"{GroupId}:{Cph}";
}

public sealed record HoldingSnapshot(
    string GroupId,
    string Cph,
    string? AssignedTo,
    Guid? AssignedUserId,
    string? AssignedUserFullName,
    DateTimeOffset? AssignmentDetachedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int Version);

/// <summary>
/// What people own about a holding: who it is assigned to. Its status is not here, because it is
/// worked out from its issues every time it is read, and its notes and actions are append-only
/// records of their own. A holding is only stored once something has been recorded against it;
/// until then <see cref="Version"/> is 0.
/// </summary>
public sealed class Holding
{
    private Holding(
        HoldingKey key,
        string? assignedTo,
        Guid? assignedUserId,
        string? assignedUserFullName,
        DateTimeOffset? assignmentDetachedAtUtc,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        int version)
    {
        Key = key;
        AssignedTo = assignedTo;
        AssignedUserId = assignedUserId;
        AssignedUserFullName = assignedUserFullName;
        AssignmentDetachedAtUtc = assignmentDetachedAtUtc;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Version = version;
    }

    public HoldingKey Key { get; }

    /// <summary>The assignee as text: a known user's name, or a SPOL name that matched nobody.</summary>
    public string? AssignedTo { get; private set; }

    public Guid? AssignedUserId { get; private set; }

    /// <summary>The known user's display name at assignment time; never an identity key.</summary>
    public string? AssignedUserFullName { get; private set; }

    /// <summary>
    /// When someone first changed the assignment in Cleanse. From then on the assignment is theirs,
    /// and a SPOL import no longer changes it.
    /// </summary>
    public DateTimeOffset? AssignmentDetachedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public int Version { get; private set; }

    public bool IsAssigned => AssignedUserId is not null || !string.IsNullOrWhiteSpace(AssignedTo);

    public static Holding New(HoldingKey key, DateTimeOffset now) =>
        new(key, null, null, null, null, now, now, version: 0);

    public static Holding FromSnapshot(HoldingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new Holding(
            new HoldingKey(snapshot.GroupId, snapshot.Cph),
            snapshot.AssignedTo,
            snapshot.AssignedUserId,
            snapshot.AssignedUserFullName,
            snapshot.AssignmentDetachedAtUtc,
            snapshot.CreatedAtUtc,
            snapshot.UpdatedAtUtc,
            snapshot.Version);
    }

    public HoldingSnapshot ToSnapshot() => new(
        Key.GroupId,
        Key.Cph,
        AssignedTo,
        AssignedUserId,
        AssignedUserFullName,
        AssignmentDetachedAtUtc,
        CreatedAtUtc,
        UpdatedAtUtc,
        Version);

    /// <summary>A change made in Cleanse, which detaches the assignment from SPOL imports.</summary>
    public IssueChange AssignKnownUser(KnownUser? user, DateTimeOffset now) =>
        Assign(user?.FullName, user?.Id, user?.FullName, now, detach: true);

    /// <summary>
    /// A SPOL import's assignment: the known user if one was found, otherwise the text as given.
    /// Ignored once someone has changed the assignment in Cleanse.
    /// </summary>
    public IssueChange SyncAssignment(KnownUser? user, string? assignee, DateTimeOffset now)
    {
        if (AssignmentDetachedAtUtc is not null)
        {
            return IssueChange.None;
        }

        return user is null
            ? Assign(assignee, null, null, now, detach: false)
            : Assign(user.FullName, user.Id, user.FullName, now, detach: false);
    }

    private IssueChange Assign(string? assignee, Guid? userId, string? fullName, DateTimeOffset now, bool detach)
    {
        var normalised = string.IsNullOrWhiteSpace(assignee) ? null : assignee.Trim();
        var changes = new List<AuditChange>();
        if (!string.Equals(normalised, AssignedTo, StringComparison.Ordinal))
        {
            changes.Add(new AuditChange(nameof(AssignedTo), AssignedTo, normalised));
        }

        if (AssignedUserId != userId)
        {
            changes.Add(new AuditChange(nameof(AssignedUserId), AssignedUserId?.ToString(), userId?.ToString()));
        }

        if (!string.Equals(AssignedUserFullName, fullName, StringComparison.Ordinal))
        {
            changes.Add(new AuditChange(nameof(AssignedUserFullName), AssignedUserFullName, fullName));
        }

        if (detach && changes.Count > 0 && AssignmentDetachedAtUtc is null)
        {
            changes.Add(new AuditChange(nameof(AssignmentDetachedAtUtc), null, now.ToString("O")));
            AssignmentDetachedAtUtc = now;
        }

        if (changes.Count == 0)
        {
            return IssueChange.None;
        }

        AssignedTo = normalised;
        AssignedUserId = userId;
        AssignedUserFullName = fullName;
        UpdatedAtUtc = now;
        Version++;
        return new IssueChange(AuditEventKind.HoldingAssigned, changes);
    }
}
