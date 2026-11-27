using Cleanse.Core.Domain;

namespace Cleanse.Core.Abstractions;

/// <summary>What people own about a holding. A holding with nothing recorded is simply absent.</summary>
public interface IHoldingStore
{
    Task<Holding?> GetAsync(HoldingKey key, CancellationToken cancellationToken = default);

    /// <summary>The stored holdings among <paramref name="cphs"/> in one group. Absent ones are left out.</summary>
    Task<IReadOnlyList<Holding>> GetManyAsync(
        string groupId,
        IReadOnlyCollection<string> cphs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Optimistic insert-or-update. Returns false when the stored version is not
    /// <paramref name="expectedVersion"/>, which is 0 for a holding not stored yet.
    /// </summary>
    Task<bool> SaveAsync(Holding holding, int expectedVersion, CancellationToken cancellationToken = default);
}

/// <summary>The append-only notes and actions recorded against holdings.</summary>
public interface IHoldingActivityStore
{
    Task AddNoteAsync(HoldingNote note, CancellationToken cancellationToken = default);

    /// <summary>Stores actions recorded together, and the note written with them if any, as one.</summary>
    Task AddActionsAsync(
        IReadOnlyCollection<HoldingAction> actions,
        HoldingNote? note,
        CancellationToken cancellationToken = default);

    /// <summary>The holding's latest notes, newest first.</summary>
    Task<IReadOnlyList<HoldingNote>> GetNotesAsync(
        HoldingKey holding,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>The holding's latest actions, newest first.</summary>
    Task<IReadOnlyList<HoldingAction>> GetActionsAsync(
        HoldingKey holding,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>The admin-maintained lists. Entries are only ever added and switched on or off.</summary>
public interface ICodedValueStore
{
    Task<CodedValue?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodedValue>> GetManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>Every entry in the list, active or not, ordered by name.</summary>
    Task<IReadOnlyList<CodedValue>> ListAsync(CodedList list, CancellationToken cancellationToken = default);

    /// <summary>Returns false, storing nothing, when the list already has an entry with the same name key.</summary>
    Task<bool> AddAsync(CodedValue value, CancellationToken cancellationToken = default);

    Task SaveAsync(CodedValue value, CancellationToken cancellationToken = default);
}
