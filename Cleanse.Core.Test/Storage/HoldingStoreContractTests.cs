using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Storage;

/// <summary>The behaviour every <see cref="IHoldingStore"/> must have.</summary>
public abstract class HoldingStoreContractTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;
    private static readonly HoldingKey Key = new(TestData.Group, "01/036/8000");

    protected abstract IHoldingStore CreateStore();

    private static Holding AssignedTo(string assignee, HoldingKey? key = null)
    {
        var holding = Holding.New(key ?? Key, Night1);
        holding.SyncAssignment(null, assignee, Night1);
        return holding;
    }

    [Fact]
    public async Task A_holding_with_nothing_recorded_is_absent()
    {
        var store = CreateStore();

        Assert.Null(await store.GetAsync(Key));
        Assert.Empty(await store.GetManyAsync(TestData.Group, [Key.Cph]));
    }

    [Fact]
    public async Task Save_then_get_round_trips_every_field()
    {
        var store = CreateStore();
        var holding = AssignedTo("Alice");
        holding.AssignKnownUser(null, Night1.AddHours(1));

        Assert.True(await store.SaveAsync(holding, 0));

        var loaded = await store.GetAsync(Key);
        Assert.Equal(holding.ToSnapshot(), loaded!.ToSnapshot());
        Assert.Equal(Night1.AddHours(1), loaded.AssignmentDetachedAtUtc);
    }

    [Fact]
    public async Task Version_zero_only_inserts_and_any_other_version_only_updates_a_match()
    {
        var store = CreateStore();
        Assert.False(await store.SaveAsync(AssignedTo("Nobody yet"), 1));
        Assert.Null(await store.GetAsync(Key));

        Assert.True(await store.SaveAsync(AssignedTo("Alice"), 0));
        Assert.False(await store.SaveAsync(AssignedTo("Bob"), 0));

        var alice = (await store.GetAsync(Key))!;
        var bob = (await store.GetAsync(Key))!;
        var version = alice.Version;
        alice.SyncAssignment(null, "Alice Brown", Night1.AddDays(1));
        bob.SyncAssignment(null, "Bob", Night1.AddDays(1));

        Assert.True(await store.SaveAsync(alice, version));
        Assert.False(await store.SaveAsync(bob, version));

        var winner = (await store.GetAsync(Key))!;
        Assert.Equal("Alice Brown", winner.AssignedTo);
        Assert.Equal(version + 1, winner.Version);
        Assert.Equal(Night1, winner.CreatedAtUtc);
        Assert.Equal(Night1.AddDays(1), winner.UpdatedAtUtc);
    }

    [Fact]
    public async Task Get_many_returns_the_stored_holdings_named_in_the_group()
    {
        var store = CreateStore();
        Assert.True(await store.SaveAsync(AssignedTo("Alice"), 0));
        Assert.True(await store.SaveAsync(AssignedTo("Bob", new HoldingKey(TestData.Group, "01/036/8001")), 0));
        Assert.True(await store.SaveAsync(AssignedTo("Carol", new HoldingKey("elsewhere", Key.Cph)), 0));

        var found = await store.GetManyAsync(TestData.Group, [Key.Cph, "01/036/8001", "01/036/9999", Key.Cph]);

        Assert.Equal(["Alice", "Bob"], found.Select(h => h.AssignedTo).Order(StringComparer.Ordinal));
        Assert.Empty(await store.GetManyAsync(TestData.Group, []));
        Assert.Equal("Carol", (await store.GetAsync(new HoldingKey("elsewhere", Key.Cph)))!.AssignedTo);
    }
}

/// <summary>The behaviour every <see cref="IHoldingActivityStore"/> must have.</summary>
public abstract class HoldingActivityStoreContractTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;
    private static readonly HoldingKey Key = new(TestData.Group, "01/036/8000");

    protected abstract IHoldingActivityStore CreateStore();

    /// <summary>Over the same data: actions refer to an action type.</summary>
    protected abstract ICodedValueStore CreateCodedValueStore();

    private async Task<CodedValue> ActionTypeAsync(string name)
    {
        var type = CodedValue.Create(CodedList.ActionType, name, Night1).Value;
        Assert.True(await CreateCodedValueStore().AddAsync(type));
        return type;
    }

    private static HoldingNote Note(string text, DateTimeOffset at, HoldingKey? key = null, string? context = null) =>
        HoldingNote.Create(key ?? Key, text, context, TestData.Alice, at).Value;

    [Fact]
    public async Task Notes_come_back_newest_first_for_their_holding_only()
    {
        var store = CreateStore();
        await store.AddNoteAsync(Note("first", Night1));
        await store.AddNoteAsync(Note("third", Night1.AddDays(2)));
        await store.AddNoteAsync(Note("second", Night1.AddDays(1)));
        await store.AddNoteAsync(Note("elsewhere", Night1, new HoldingKey(TestData.Group, "01/036/8001")));
        await store.AddNoteAsync(Note("other group", Night1, new HoldingKey("elsewhere", Key.Cph)));

        var notes = await store.GetNotesAsync(Key, 10);

        Assert.Equal(["third", "second", "first"], notes.Select(n => n.Text).ToArray());
        Assert.Equal(["third", "second"], (await store.GetNotesAsync(Key, 2)).Select(n => n.Text).ToArray());
    }

    [Fact]
    public async Task A_note_round_trips_its_author_and_context()
    {
        var store = CreateStore();
        var note = Note("Spoke to the keeper", Night1, context: "Resolved (Fixed in SAM): rule 1");

        await store.AddNoteAsync(note);

        var loaded = Assert.Single(await store.GetNotesAsync(Key, 10));
        Assert.Equal(note.ToSnapshot(), loaded.ToSnapshot());
    }

    [Fact]
    public async Task Notes_sharing_an_instant_keep_the_order_they_were_written_in()
    {
        var store = CreateStore();
        for (var i = 0; i < 4; i++)
        {
            await store.AddNoteAsync(Note($"note {i}", Night1));
        }

        Assert.Equal(
            ["note 3", "note 2", "note 1", "note 0"],
            (await store.GetNotesAsync(Key, 10)).Select(n => n.Text).ToArray());
    }

    [Fact]
    public async Task Actions_recorded_together_are_stored_with_their_note()
    {
        var store = CreateStore();
        var phoned = await ActionTypeAsync("Phoned the keeper");
        var wrote = await ActionTypeAsync("Wrote to the keeper");
        var batch = Guid.CreateVersion7(Night1);
        HoldingAction[] actions =
        [
            HoldingAction.Create(batch, Key, phoned, TestData.Alice, Night1),
            HoldingAction.Create(batch, Key, wrote, TestData.Alice, Night1)
        ];

        await store.AddActionsAsync(actions, Note("No answer", Night1, context: "Recorded: Phoned the keeper"));
        await store.AddActionsAsync([HoldingAction.Create(Guid.CreateVersion7(Night1), Key, phoned, TestData.Alice, Night1.AddDays(1))], null);

        var stored = await store.GetActionsAsync(Key, 10);
        Assert.Equal(3, stored.Count);
        Assert.Equal(Night1.AddDays(1), stored[0].CreatedAtUtc);
        Assert.Equal(
            actions.Select(a => a.ToSnapshot()).OrderBy(s => s.ActionTypeName, StringComparer.Ordinal),
            stored.Skip(1).Select(a => a.ToSnapshot()).OrderBy(s => s.ActionTypeName, StringComparer.Ordinal));
        Assert.Equal("No answer", Assert.Single(await store.GetNotesAsync(Key, 10)).Text);
        Assert.Empty(await store.GetActionsAsync(new HoldingKey(TestData.Group, "01/036/8001"), 10));
    }
}

/// <summary>The behaviour every <see cref="ICodedValueStore"/> must have.</summary>
public abstract class CodedValueStoreContractTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;

    protected abstract ICodedValueStore CreateStore();

    private static CodedValue Value(CodedList list, string name) => CodedValue.Create(list, name, Night1).Value;

    [Fact]
    public async Task An_entry_round_trips()
    {
        var store = CreateStore();
        var value = Value(CodedList.ResolvedReason, "Fixed in SAM");

        Assert.True(await store.AddAsync(value));

        Assert.Equal(value.ToSnapshot(), (await store.GetAsync(value.Id))!.ToSnapshot());
        Assert.Null(await store.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_name_is_unique_within_its_list_ignoring_case_and_spaces()
    {
        var store = CreateStore();
        Assert.True(await store.AddAsync(Value(CodedList.ActionType, "Updated SAM")));

        Assert.False(await store.AddAsync(Value(CodedList.ActionType, "  updated sam ")));
        Assert.True(await store.AddAsync(Value(CodedList.ResolvedReason, "Updated SAM")));
        Assert.Single(await store.ListAsync(CodedList.ActionType));
    }

    [Fact]
    public async Task A_list_holds_its_own_entries_ordered_by_name()
    {
        var store = CreateStore();
        foreach (var name in new[] { "Wrote to the keeper", "added email address", "Phoned the keeper" })
        {
            Assert.True(await store.AddAsync(Value(CodedList.ActionType, name)));
        }

        Assert.True(await store.AddAsync(Value(CodedList.CantResolveReason, "Duplicate")));

        Assert.Equal(
            ["added email address", "Phoned the keeper", "Wrote to the keeper"],
            (await store.ListAsync(CodedList.ActionType)).Select(v => v.Name).ToArray());
        Assert.Empty(await store.ListAsync(CodedList.ResolvedReason));
    }

    [Fact]
    public async Task Switching_an_entry_off_is_saved_and_it_stays_listed()
    {
        var store = CreateStore();
        var value = Value(CodedList.CantResolveReason, "Duplicate");
        Assert.True(await store.AddAsync(value));

        var loaded = (await store.GetAsync(value.Id))!;
        Assert.True(loaded.SetActive(false, Night1.AddDays(1)).IsMaterial);
        await store.SaveAsync(loaded);

        var saved = Assert.Single(await store.ListAsync(CodedList.CantResolveReason));
        Assert.False(saved.IsActive);
        Assert.Equal(Night1.AddDays(1), saved.UpdatedAtUtc);
        Assert.Equal(Night1, saved.CreatedAtUtc);
    }

    [Fact]
    public async Task Get_many_returns_only_what_exists()
    {
        var store = CreateStore();
        var first = Value(CodedList.ActionType, "Updated SAM");
        var second = Value(CodedList.ResolvedReason, "Fixed in SAM");
        Assert.True(await store.AddAsync(first));
        Assert.True(await store.AddAsync(second));

        var found = await store.GetManyAsync([first.Id, Guid.NewGuid(), second.Id, first.Id]);

        Assert.Equal(
            new[] { first.Id, second.Id }.Order(),
            found.Select(v => v.Id).Order());
        Assert.Empty(await store.GetManyAsync([]));
    }
}
