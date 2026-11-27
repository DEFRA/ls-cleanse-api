namespace Cleanse.Core.Domain;

public sealed record HoldingNoteSnapshot(
    Guid Id,
    string GroupId,
    string Cph,
    string Text,
    string? Context,
    DateTimeOffset CreatedAtUtc,
    Actor CreatedBy);

/// <summary>
/// Free text a caseworker writes against a holding. Append-only: a new note never replaces an
/// earlier one. <see cref="Context"/> says what it was written with, such as the actions recorded
/// or the status change it explains; it is null for a note written on its own.
/// </summary>
public sealed class HoldingNote
{
    public const int MaxLength = 2000;

    public const int MaxContextLength = 500;

    private HoldingNote(Guid id, HoldingKey holding, string text, string? context, DateTimeOffset createdAtUtc, Actor createdBy)
    {
        Id = id;
        Holding = holding;
        Text = text;
        Context = context;
        CreatedAtUtc = createdAtUtc;
        CreatedBy = createdBy;
    }

    public Guid Id { get; }

    public HoldingKey Holding { get; }

    public string Text { get; }

    public string? Context { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public Actor CreatedBy { get; }

    public static Result<HoldingNote> Create(
        HoldingKey holding, string? text, string? context, Actor createdBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(createdBy);

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return DomainErrors.Validation("A note needs some text.");
        }

        if (trimmed.Length > MaxLength)
        {
            return DomainErrors.Validation($"A note can be up to {MaxLength} characters.");
        }

        var about = string.IsNullOrWhiteSpace(context) ? null : context.Trim();
        if (about is { Length: > MaxContextLength })
        {
            about = about[..MaxContextLength];
        }

        // Version 7 so ids sort by creation time on any store that indexes them.
        return new HoldingNote(Guid.CreateVersion7(now), holding, trimmed, about, now, createdBy);
    }

    public static HoldingNote FromSnapshot(HoldingNoteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new HoldingNote(
            snapshot.Id,
            new HoldingKey(snapshot.GroupId, snapshot.Cph),
            snapshot.Text,
            snapshot.Context,
            snapshot.CreatedAtUtc,
            snapshot.CreatedBy);
    }

    public HoldingNoteSnapshot ToSnapshot() =>
        new(Id, Holding.GroupId, Holding.Cph, Text, Context, CreatedAtUtc, CreatedBy);
}

public sealed record HoldingActionSnapshot(
    Guid Id,
    Guid BatchId,
    string GroupId,
    string Cph,
    Guid ActionTypeId,
    string ActionTypeName,
    DateTimeOffset CreatedAtUtc,
    Actor CreatedBy);

/// <summary>
/// One thing a caseworker did about a holding, of one admin-defined action type. Stored and
/// counted on its own, so MI can say which actions are taken and by whom. Actions saved together
/// share a <see cref="BatchId"/>. Recording one never changes an issue.
/// </summary>
public sealed class HoldingAction
{
    private HoldingAction(
        Guid id, Guid batchId, HoldingKey holding, Guid actionTypeId, string actionTypeName,
        DateTimeOffset createdAtUtc, Actor createdBy)
    {
        Id = id;
        BatchId = batchId;
        Holding = holding;
        ActionTypeId = actionTypeId;
        ActionTypeName = actionTypeName;
        CreatedAtUtc = createdAtUtc;
        CreatedBy = createdBy;
    }

    public Guid Id { get; }

    public Guid BatchId { get; }

    public HoldingKey Holding { get; }

    public Guid ActionTypeId { get; }

    /// <summary>The type's name when the action was recorded. Types are never renamed.</summary>
    public string ActionTypeName { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public Actor CreatedBy { get; }

    public static HoldingAction Create(
        Guid batchId, HoldingKey holding, CodedValue actionType, Actor createdBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actionType);
        ArgumentNullException.ThrowIfNull(createdBy);
        if (actionType.List != CodedList.ActionType)
        {
            throw new ArgumentException("An action must be of an action type.", nameof(actionType));
        }

        return new HoldingAction(
            Guid.CreateVersion7(now), batchId, holding, actionType.Id, actionType.Name, now, createdBy);
    }

    public static HoldingAction FromSnapshot(HoldingActionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new HoldingAction(
            snapshot.Id,
            snapshot.BatchId,
            new HoldingKey(snapshot.GroupId, snapshot.Cph),
            snapshot.ActionTypeId,
            snapshot.ActionTypeName,
            snapshot.CreatedAtUtc,
            snapshot.CreatedBy);
    }

    public HoldingActionSnapshot ToSnapshot() => new(
        Id, BatchId, Holding.GroupId, Holding.Cph, ActionTypeId, ActionTypeName, CreatedAtUtc, CreatedBy);
}
