using Cleanse.Api.Imports;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Datasets;

namespace Cleanse.Api.Test;

/// <summary>
/// Reading a dataset's rows and collapsing them, driven by the shapes the real 30,000 item list
/// contains: duplicate keys, numbered priorities, embedded newlines in notes, malformed CPHs, and
/// items that name more than one person.
/// </summary>
public class SpolDatasetReaderTests
{
    private static SpolDatasetRowDto Item(
        int itemId,
        string cph,
        string ruleNo,
        string status,
        string? notes = null,
        string? priority = "4. Low",
        string? received = "2025-11-27T00:00:00Z",
        string? first = null,
        string? second = null,
        string? third = null,
        string? letter = null,
        string? complete = null,
        string? name = null,
        string? email = null) => new()
    {
        ItemId = itemId,
        DateReceived = received,
        Priority = priority,
        Cph = cph,
        RuleNo = ruleNo,
        Status = status,
        FirstContactDate = first,
        SecondContactDate = second,
        ThirdContactDate = third,
        LetterEmailSent = letter,
        Notes = notes,
        DateComplete = complete,
        AssignedName = name,
        AssignedEmail = email
    };

    private static List<SpolRow> Read(params SpolDatasetRowDto[] items) =>
        [.. SpolDatasetReader.Read(items.Select(item => new SpolDatasetItem(item.ItemId, SpolDatasetRows.Serialize(item))), "cts-sam")];

    private static SpolBatch Project(params SpolDatasetRowDto[] items) => SpolRowMapper.Project(Read(items));

    [Fact]
    public void The_duplicate_that_wins_carries_its_assignee_trimmed()
    {
        var batch = Project(
            Item(1, "assignment", "1", "New", name: "First Person"),
            Item(2, "assignment", "1", "Completed", name: "  Winner Person  "));

        var winner = Assert.Single(batch.Rows);
        Assert.True(winner.HasAssignee);
        Assert.Equal("Winner Person", winner.AssignedTo);
        Assert.Equal(1, batch.DuplicatesCollapsed);
    }

    [Fact]
    public void The_key_matches_the_scheme_the_rules_engine_uses()
    {
        var id = Assert.Single(Read(Item(1, "01/036/8000", "1", "Completed"))).ToIssueId();

        // The group is not part of the key. If it were, an imported item could never reconcile
        // against the issue the engine raised for the same anomaly.
        Assert.Equal(IssueId.Derive("1", "01/036/8000"), id);
        Assert.True(IssueId.IsUrlSafe(id.Value), id.Value);
    }

    [Fact]
    public void Each_item_is_numbered_by_its_sharepoint_item_id() =>
        Assert.Equal(31887, Assert.Single(Read(Item(31887, "01/036/8000", "1", "Completed"))).RowNumber);

    [Theory]
    [InlineData("New", WorkflowStatus.Todo)]
    [InlineData("In Progress", WorkflowStatus.InProgress)]
    [InlineData("Completed", WorkflowStatus.Resolved)]
    [InlineData("Unable to Action", WorkflowStatus.Nonactionable)]
    public void Source_statuses_map_onto_the_workflow_axis(string source, WorkflowStatus expected) =>
        Assert.Equal(expected, SpolRowMapper.MapStatus(source));

    [Fact]
    public void An_unrecognised_or_blank_status_maps_to_nothing_rather_than_guessing() =>
        Assert.Null(SpolRowMapper.MapStatus("   "));

    [Theory]
    [InlineData("4. Low", IssuePriority.Low)]
    [InlineData("3. Medium", IssuePriority.Medium)]
    [InlineData("1. High", IssuePriority.High)]
    [InlineData("Low", IssuePriority.Low)]
    public void Numbered_priorities_lose_their_rank_prefix(string source, IssuePriority expected) =>
        Assert.Equal(expected, SpolRowMapper.MapPriority(source));

    [Fact]
    public void A_blank_priority_stays_null() => Assert.Null(SpolRowMapper.MapPriority(""));

    [Fact]
    public void Contact_dates_become_history_events_without_inferring_a_channel()
    {
        var contacts = Assert.Single(Read(Item(1, "01/036/8000", "1", "Completed",
            first: "2026-01-19T00:00:00+00:00",
            second: "2026-04-01T00:00:00+01:00",
            letter: "2026-04-07T00:00:00+01:00"))).Contacts;

        Assert.Equal(3, contacts.Count);
        Assert.Equal(HistoryActionType.PhoneCall, contacts[0].ActionType);
        Assert.Equal("1st Contact Date", contacts[0].Column);
        Assert.Equal(HistoryActionType.LetterSent, contacts[^1].ActionType);
        Assert.Equal("Letter/email sent", contacts[^1].Column);
    }

    [Theory]
    [InlineData("2025-12-05T00:00:00+00:00", "2025-12-05T00:00:00Z")]
    [InlineData("2025-12-05T00:00:00Z", "2025-12-05T00:00:00Z")]
    [InlineData("2026-04-01T00:00:00+01:00", "2026-03-31T23:00:00Z")]
    [InlineData("2026-04-01T15:16:00+01:00", "2026-04-01T14:16:00Z")]
    public void A_date_is_the_moment_its_offset_gives(string source, string utc)
    {
        var row = Assert.Single(Read(Item(1, "01/036/8000", "1", "Completed", received: source, first: source)));

        var expected = DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, row.DateReceived);
        Assert.Equal(TimeSpan.Zero, row.DateReceived!.Value.Offset);
        Assert.Equal(expected, row.Contacts[0].AtUtc);
    }

    [Fact]
    public void Notes_containing_newlines_survive()
    {
        // The real list's CSV is 50,000 lines for 30,000 items because of exactly this.
        var row = Assert.Single(Read(Item(1, "01/045/0197", "1", "Completed",
            notes: "1st attempt - no answer\n\n2nd attempt - spoke to keeper")));

        Assert.Contains("2nd attempt", row.Notes!, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_keys_collapse_to_the_most_advanced_outcome()
    {
        // Mirrors 06/206/0015 rule 9, the one duplicate pair that disagrees on status.
        var batch = Project(
            Item(1, "06/206/0015", "9", "Unable to Action", "Landline disconnected"),
            Item(2, "06/206/0015", "9", "Completed", "Mobile already on SAM. NFA"));

        var row = Assert.Single(batch.Rows);
        Assert.Equal(WorkflowStatus.Resolved, row.WorkflowStatus);
        Assert.Equal("Mobile already on SAM. NFA", row.ExternalNotes);
        Assert.Equal(1, batch.DuplicatesCollapsed);
    }

    [Fact]
    public void Collapse_does_not_depend_on_item_order()
    {
        var forwards = Project(Item(1, "06/206/0015", "9", "New"), Item(2, "06/206/0015", "9", "Completed"));
        var backwards = Project(Item(1, "06/206/0015", "9", "Completed"), Item(2, "06/206/0015", "9", "New"));

        Assert.Equal(forwards.Rows[0].WorkflowStatus, backwards.Rows[0].WorkflowStatus);
        Assert.Equal(WorkflowStatus.Resolved, forwards.Rows[0].WorkflowStatus);
    }

    [Fact]
    public void Items_without_a_usable_key_are_reported_rather_than_dropped()
    {
        var batch = Project(
            Item(1, "", "1", "Completed"),
            Item(2, "01/036/8000", "", "Completed"),
            Item(3, "01/036/8000", "1", "Completed"));

        Assert.Single(batch.Rows);
        Assert.Equal(2, batch.Invalid.Count);
    }

    [Fact]
    public void A_malformed_cph_still_produces_a_key_so_it_can_be_reported_as_unmatched()
    {
        // C3161 appears in the real list; it will simply not match an issue.
        var batch = Project(Item(1, "C3161", "4", "Completed"));

        Assert.Single(batch.Rows);
        Assert.Empty(batch.Invalid);
    }

    [Fact]
    public void Dates_that_look_like_typos_are_reported_but_the_item_is_still_applied()
    {
        var batch = Project(
            Item(1, "01/045/0197", "6", "Completed", received: "2016-10-14T00:00:00+01:00", complete: "2026-10-14T00:00:00+01:00"),
            Item(2, "02/055/0082", "6", "Completed", received: "2025-12-10T00:00:00+00:00", complete: "2025-12-01T00:00:00+00:00"),
            Item(3, "03/009/0068", "6", "Completed", received: "2026-10-15T00:00:00+01:00", complete: "2026-10-16T00:00:00+01:00"));

        Assert.Equal(3, batch.Rows.Count);
        Assert.Equal(2, batch.SuspectDates.Count);
        Assert.Contains(batch.SuspectDates, s => s.Row.Cph == "01/045/0197" && s.Detail.Contains("14/10/2016", StringComparison.Ordinal));
        Assert.Contains(batch.SuspectDates, s => s.Row.Cph == "02/055/0082");
    }

    [Fact]
    public void Projection_is_identical_across_repeated_runs()
    {
        SpolDatasetRowDto[] items =
        [
            Item(1, "01/036/8000", "1", "Completed", "note one", first: "2026-01-19T00:00:00Z"),
            Item(2, "06/206/0015", "9", "Unable to Action", "note two"),
            Item(3, "06/206/0015", "9", "Completed", "note three"),
            Item(4, "", "2", "New")
        ];

        var first = Project(items);
        var second = Project(items);

        // Compared field by field: the record holds a List, so its generated equality would
        // compare that member by reference and pass regardless of content.
        Assert.Equal(Fingerprint(first), Fingerprint(second));
        Assert.Equal(first.DuplicatesCollapsed, second.DuplicatesCollapsed);
        Assert.Equal(first.Invalid.Count, second.Invalid.Count);
    }

    [Fact]
    public void Only_one_item_per_cph_carries_the_assignee_and_an_open_item_beats_a_closed_one()
    {
        var batch = Project(
            Item(2, "01/036/8000", "1", "In progress", name: "Bob Open"),
            Item(3, "01/036/8000", "2", "Completed", name: "Alice Closed"),
            Item(4, "01/036/8001", "1", "New", name: "Carol Only"));

        var carriers = batch.Rows.Where(r => r.HasAssignee).ToList();
        Assert.Equal(["Bob Open", "Carol Only"], carriers.Select(r => r.AssignedTo).Order(StringComparer.Ordinal));
        var conflict = Assert.Single(batch.AssigneeConflicts);
        Assert.Equal("01/036/8000", conflict.Row.Cph);
        Assert.Equal(2, conflict.Row.RowNumber);
        Assert.Equal([2L, 3L], conflict.NamingRows);
    }

    [Fact]
    public void Among_open_items_the_highest_item_id_wins_and_nobody_never_beats_someone()
    {
        var batch = Project(
            Item(2, "01/036/8000", "1", "New", name: "First Open"),
            Item(3, "01/036/8000", "2", "In progress", name: "Second Open"),
            Item(4, "01/036/8000", "3", "New", name: "   "),
            Item(5, "01/036/8000", "4", "Completed", name: "Closed Person"));

        Assert.Equal("Second Open", Assert.Single(batch.Rows, r => r.HasAssignee).AssignedTo);
        Assert.Equal([2L, 3L, 5L], Assert.Single(batch.AssigneeConflicts).NamingRows);
    }

    [Fact]
    public void Items_agreeing_on_the_assignee_raise_no_warning_and_all_blank_still_unassigns()
    {
        var batch = Project(
            Item(1, "01/036/8000", "1", "New", name: "Same  Person"),
            Item(2, "01/036/8000", "2", "Completed", name: "same person"),
            Item(3, "01/036/8002", "1", "New", name: "Some One", email: "Some.One@apha.gov.uk"),
            Item(4, "01/036/8002", "2", "New", name: "Someone Else's Spelling", email: "some.one@APHA.gov.uk"),
            Item(5, "01/036/8001", "1", "New", name: ""),
            Item(6, "01/036/8001", "2", "New", name: " "));

        Assert.Empty(batch.AssigneeConflicts);
        var carriers = batch.Rows.Where(r => r.HasAssignee).ToList();
        Assert.Equal(3, carriers.Count);
        Assert.Contains(carriers, r => string.IsNullOrWhiteSpace(r.AssignedTo));
    }

    [Fact]
    public void An_item_naming_several_people_gives_the_first_and_is_called_out()
    {
        var batch = Project(Item(7, "01/036/8000", "1", "New",
            name: "Alice Smith; Bob Jones", email: "alice.smith@apha.gov.uk; bob.jones@apha.gov.uk"));

        var row = Assert.Single(batch.Rows);
        Assert.Equal("Alice Smith", row.AssignedTo);
        Assert.Equal("alice.smith@apha.gov.uk", row.AssignedEmail);
        var several = Assert.Single(batch.SeveralAssignees);
        Assert.Equal(7, several.RowNumber);
        Assert.Equal(
            [new ExternalAssignee("alice.smith@apha.gov.uk", "Alice Smith"), new ExternalAssignee("bob.jones@apha.gov.uk", "Bob Jones")],
            several.Assignees);
    }

    [Fact]
    public void Someone_with_no_email_address_is_kept_by_name()
    {
        var row = Assert.Single(Read(Item(1, "01/036/8000", "1", "New", name: "Old Account")));

        Assert.Equal("Old Account", row.AssignedTo);
        Assert.Null(row.AssignedEmail);
        Assert.Equal([new ExternalAssignee(null, "Old Account")], row.Assignees);
    }

    private static string Fingerprint(SpolBatch batch) =>
        string.Join("\n", batch.Rows
            .OrderBy(r => r.IssueId.Value, StringComparer.Ordinal)
            .Select(r => string.Join('|',
                r.IssueId.Value,
                r.WorkflowStatus,
                r.ExternalNotes,
                r.Priority,
                r.ReceivedAtUtc?.ToString("O"),
                string.Join(',', r.Contacts.Select(c => $"{c.Column}@{c.AtUtc:O}:{c.ActionType}")))));
}
