using System.Collections.Concurrent;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;

namespace Cleanse.Storage.InMemory;

/// <summary>Holdings as immutable snapshots, rehydrated on every read like the issue store.</summary>
public sealed class InMemoryHoldingStore : IHoldingStore
{
    private readonly ConcurrentDictionary<HoldingKey, HoldingSnapshot> _holdings = new();

    public int Count => _holdings.Count;

    /// <summary>The stored holding's assignment, for the issue store's assignee filters.</summary>
    internal HoldingSnapshot? Find(string groupId, string cph) =>
        _holdings.TryGetValue(new HoldingKey(groupId, cph), out var snapshot) ? snapshot : null;

    public int Clear(string? groupId)
    {
        var doomed = _holdings.Keys
            .Where(key => groupId is null || string.Equals(key.GroupId, groupId, StringComparison.Ordinal))
            .ToList();

        foreach (var key in doomed)
        {
            _holdings.TryRemove(key, out _);
        }

        return doomed.Count;
    }

    public Task<Holding?> GetAsync(HoldingKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_holdings.TryGetValue(key, out var snapshot) ? Holding.FromSnapshot(snapshot) : null);
    }

    public Task<IReadOnlyList<Holding>> GetManyAsync(
        string groupId,
        IReadOnlyCollection<string> cphs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(cphs);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<Holding> found =
        [
            .. cphs
                .Where(cph => !string.IsNullOrWhiteSpace(cph))
                .Distinct(StringComparer.Ordinal)
                .Select(cph => _holdings.TryGetValue(new HoldingKey(groupId, cph), out var snapshot) ? snapshot : null)
                .OfType<HoldingSnapshot>()
                .Select(Holding.FromSnapshot)
        ];

        return Task.FromResult(found);
    }

    public Task<bool> SaveAsync(Holding holding, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(holding);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = holding.ToSnapshot();
        if (!_holdings.TryGetValue(holding.Key, out var current))
        {
            return Task.FromResult(expectedVersion == 0 && _holdings.TryAdd(holding.Key, snapshot));
        }

        return Task.FromResult(current.Version == expectedVersion && _holdings.TryUpdate(holding.Key, snapshot, current));
    }
}

/// <summary>Append-only notes and actions, held under one lock so a batch lands together.</summary>
public sealed class InMemoryHoldingActivityStore : IHoldingActivityStore
{
    private readonly Lock _gate = new();
    private readonly List<HoldingNoteSnapshot> _notes = [];
    private readonly List<HoldingActionSnapshot> _actions = [];

    public (int Notes, int Actions) Clear(string? groupId)
    {
        lock (_gate)
        {
            bool InScope(string group) => groupId is null || string.Equals(group, groupId, StringComparison.Ordinal);
            return (_notes.RemoveAll(note => InScope(note.GroupId)), _actions.RemoveAll(action => InScope(action.GroupId)));
        }
    }

    public Task AddNoteAsync(HoldingNote note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _notes.Add(note.ToSnapshot());
        }

        return Task.CompletedTask;
    }

    public Task AddActionsAsync(
        IReadOnlyCollection<HoldingAction> actions,
        HoldingNote? note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actions);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _actions.AddRange(actions.Select(action => action.ToSnapshot()));
            if (note is not null)
            {
                _notes.Add(note.ToSnapshot());
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HoldingNote>> GetNotesAsync(
        HoldingKey holding,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // Newest first, tie-broken on insertion order, as the relational providers do.
            IReadOnlyList<HoldingNote> found =
            [
                .. _notes
                    .Select((note, index) => (note, index))
                    .Where(x => x.note.GroupId == holding.GroupId && x.note.Cph == holding.Cph)
                    .OrderByDescending(x => x.note.CreatedAtUtc)
                    .ThenByDescending(x => x.index)
                    .Take(limit)
                    .Select(x => HoldingNote.FromSnapshot(x.note))
            ];

            return Task.FromResult(found);
        }
    }

    public Task<IReadOnlyList<HoldingAction>> GetActionsAsync(
        HoldingKey holding,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<HoldingAction> found =
            [
                .. _actions
                    .Select((action, index) => (action, index))
                    .Where(x => x.action.GroupId == holding.GroupId && x.action.Cph == holding.Cph)
                    .OrderByDescending(x => x.action.CreatedAtUtc)
                    .ThenByDescending(x => x.index)
                    .Take(limit)
                    .Select(x => HoldingAction.FromSnapshot(x.action))
            ];

            return Task.FromResult(found);
        }
    }
}

/// <summary>The admin-maintained lists. A reset leaves them alone, as it does users.</summary>
public sealed class InMemoryCodedValueStore : ICodedValueStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, CodedValueSnapshot> _values = [];

    public Task<CodedValue?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_values.TryGetValue(id, out var snapshot) ? CodedValue.FromSnapshot(snapshot) : null);
        }
    }

    public Task<IReadOnlyList<CodedValue>> GetManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<CodedValue> found =
            [
                .. ids.Distinct()
                    .Select(id => _values.TryGetValue(id, out var snapshot) ? snapshot : null)
                    .OfType<CodedValueSnapshot>()
                    .Select(CodedValue.FromSnapshot)
            ];

            return Task.FromResult(found);
        }
    }

    public Task<IReadOnlyList<CodedValue>> ListAsync(CodedList list, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<CodedValue> found =
            [
                .. _values.Values
                    .Where(value => value.List == list)
                    .OrderBy(value => CodedValue.KeyOf(value.Name), StringComparer.Ordinal)
                    .ThenBy(value => value.Id)
                    .Select(CodedValue.FromSnapshot)
            ];

            return Task.FromResult(found);
        }
    }

    public Task<bool> AddAsync(CodedValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_values.Values.Any(existing => existing.List == value.List &&
                                               CodedValue.KeyOf(existing.Name) == value.NameKey))
            {
                return Task.FromResult(false);
            }

            _values[value.Id] = value.ToSnapshot();
            return Task.FromResult(true);
        }
    }

    public Task SaveAsync(CodedValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _values[value.Id] = value.ToSnapshot();
        }

        return Task.CompletedTask;
    }
}
