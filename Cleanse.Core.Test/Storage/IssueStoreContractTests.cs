using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Storage;

/// <summary>
/// The behaviour every <see cref="IIssueStore"/> implementation must exhibit. A Postgres, SQLite
/// or Mongo provider inherits this class and implements <see cref="CreateStore"/>; if it passes,
/// Core will behave identically on it. These are the semantics the domain actually relies on,
/// so this suite is the real contract, not the interface signature.
/// </summary>
public abstract class IssueStoreContractTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;
    private static readonly DateTimeOffset Night2 = Night1.AddDays(1);

    protected abstract IIssueStore CreateStore();

    /// <summary>A holding store over the same data as <see cref="CreateStore"/>, for the assignee filters.</summary>
    protected abstract IHoldingStore CreateHoldingStore();

    /// <summary>The admin lists over the same data, because an issue's outcome reason refers to one.</summary>
    protected abstract ICodedValueStore CreateCodedValueStore();

    private async Task<IIssueStore> SeedAsync(params Issue[] issues)
    {
        var store = CreateStore();
        await store.SaveBatchAsync(issues);
        return store;
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_id()
    {
        var store = CreateStore();

        Assert.Null(await store.GetAsync(TestData.Key("nope")));
    }

    [Fact]
    public async Task Save_then_get_round_trips_every_field()
    {
        var issue = TestData.NewIssue("12/345/6789");
        issue.AssignTo("alice", Night1);
        issue.SetNotes("chased", Night1);
        Assert.True(issue.Suppress(Night1.AddDays(5), Night1).IsSuccess);

        var store = await SeedAsync(issue);
        var loaded = await store.GetAsync(issue.Id);

        Assert.NotNull(loaded);
        Assert.Equal(issue.ToSnapshot(), loaded.ToSnapshot());
    }

    [Fact]
    public async Task Reads_are_isolated_from_each_other()
    {
        var store = await SeedAsync(TestData.NewIssue("12/345/6789"));
        var id = TestData.Key("12/345/6789");

        var first = await store.GetAsync(id);
        first!.AssignTo("mallory", Night2);

        var second = await store.GetAsync(id);

        Assert.Null(second!.AssignedTo);
    }

    [Fact]
    public async Task Get_many_returns_only_what_exists()
    {
        var store = await SeedAsync(TestData.NewIssue("a"), TestData.NewIssue("b"));

        var found = await store.GetManyAsync([TestData.Key("a"), TestData.Key("missing"), TestData.Key("b")]);

        Assert.Equal(2, found.Count);
        Assert.Equivalent(
            new[] { TestData.Key("a").Value, TestData.Key("b").Value },
            found.Select(i => i.Id.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Get_many_tolerates_an_empty_request()
    {
        var store = CreateStore();

        Assert.Empty(await store.GetManyAsync([]));
    }

    [Fact]
    public async Task Optimistic_save_succeeds_when_the_version_still_matches()
    {
        var store = await SeedAsync(TestData.NewIssue("12/345/6789"));
        var issue = await store.GetAsync(TestData.Key("12/345/6789"));
        var loadedVersion = issue!.Version;
        issue.AssignTo("alice", Night2);

        Assert.True(await store.SaveAsync(issue, loadedVersion));

        var reloaded = await store.GetAsync(issue.Id);
        Assert.Equal("alice", reloaded!.AssignedTo);
        Assert.Equal(loadedVersion + 1, reloaded.Version);
    }

    [Fact]
    public async Task Optimistic_save_fails_when_somebody_else_got_there_first()
    {
        var store = await SeedAsync(TestData.NewIssue("12/345/6789"));
        var id = TestData.Key("12/345/6789");

        var alice = await store.GetAsync(id);
        var bob = await store.GetAsync(id);
        var version = alice!.Version;

        alice.AssignTo("alice", Night2);
        Assert.True(await store.SaveAsync(alice, version));

        bob!.AssignTo("bob", Night2);
        Assert.False(await store.SaveAsync(bob, version));

        var winner = await store.GetAsync(id);
        Assert.Equal("alice", winner!.AssignedTo);
    }

    [Fact]
    public async Task A_null_expected_version_writes_unconditionally()
    {
        var store = await SeedAsync(TestData.NewIssue("12/345/6789"));
        var issue = await store.GetAsync(TestData.Key("12/345/6789"));
        issue!.AssignTo("alice", Night2);
        issue.AssignTo("bob", Night2);

        Assert.True(await store.SaveAsync(issue, expectedVersion: null));

        Assert.Equal("bob", (await store.GetAsync(issue.Id))!.AssignedTo);
    }

    [Fact]
    public async Task Save_batch_inserts_and_updates_in_one_call()
    {
        var existing = TestData.NewIssue("a");
        var store = await SeedAsync(existing);

        existing.AssignTo("alice", Night2);
        await store.SaveBatchAsync([existing, TestData.NewIssue("b")]);

        Assert.Equal("alice", (await store.GetAsync(TestData.Key("a")))!.AssignedTo);
        Assert.NotNull(await store.GetAsync(TestData.Key("b")));
    }

    [Fact]
    public async Task Sweep_deactivates_only_issues_the_current_run_did_not_touch()
    {
        var touched = TestData.NewIssue("touched", runId: "run-2");
        var stale = TestData.NewIssue("stale", runId: "run-1");
        var store = await SeedAsync(touched, stale);

        var deactivated = await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2);

        Assert.Equal(1, deactivated);
        Assert.Equal(IssueStatus.Active, (await store.GetAsync(touched.Id))!.Status);
        Assert.Equal(IssueStatus.Inactive, (await store.GetAsync(stale.Id))!.Status);
    }

    [Fact]
    public async Task Sweep_is_scoped_to_its_group()
    {
        var mine = TestData.NewIssue("mine", runId: "run-1");
        var theirs = TestData.NewIssue("theirs", runId: "run-1", groupId: "other-group");
        var store = await SeedAsync(mine, theirs);

        await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2);

        Assert.Equal(IssueStatus.Inactive, (await store.GetAsync(mine.Id))!.Status);
        Assert.Equal(IssueStatus.Active, (await store.GetAsync(theirs.Id))!.Status);
    }

    [Fact]
    public async Task Sweep_leaves_the_human_axis_alone()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");
        issue.AssignTo("alice", Night1);
        Assert.True(issue.ChangeWorkflowStatus(WorkflowStatus.InProgress, Night1).IsSuccess);
        var store = await SeedAsync(issue);

        await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2);

        var swept = await store.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Inactive, swept!.Status);
        Assert.Equal(WorkflowStatus.InProgress, swept.WorkflowStatus);
        Assert.Equal("alice", swept.AssignedTo);
    }

    [Fact]
    public async Task Sweep_does_not_recount_already_inactive_issues()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");
        var store = await SeedAsync(issue);

        Assert.Equal(1, await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2));
        Assert.Equal(0, await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2));
    }

    /// <summary>
    /// The sweep is set-based SQL rather than a call to Issue.Deactivate, so the two have to be
    /// held to the same outcome explicitly or they will drift.
    /// </summary>
    [Fact]
    public async Task Sweep_records_when_it_deactivated_each_issue()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");
        var store = await SeedAsync(issue);

        await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2);

        var swept = await store.GetAsync(issue.Id);
        Assert.Equal(Night2, swept!.DeactivatedAtUtc);
        Assert.Equal(Night1, swept.ActivatedAtUtc);
    }

    [Fact]
    public async Task A_swept_issue_reactivates_with_a_fresh_activation()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");
        var store = await SeedAsync(issue);
        await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2);

        var reactivated = await store.GetAsync(issue.Id);
        var third = Night2.AddDays(1);
        reactivated!.RecordDetection("run-3", reactivated.Context, third);
        await store.SaveBatchAsync([reactivated]);

        var stored = await store.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Active, stored!.Status);
        Assert.Equal(third, stored.ActivatedAtUtc);
        Assert.Null(stored.DeactivatedAtUtc);
    }

    [Fact]
    public async Task Expired_suppressions_are_returned_oldest_first_and_capped()
    {
        var store = CreateStore();
        var issues = new List<Issue>();
        for (var i = 0; i < 4; i++)
        {
            var issue = TestData.NewIssue($"cph-{i}");
            Assert.True(issue.Suppress(Night1.AddDays(i + 1), Night1).IsSuccess);
            issues.Add(issue);
        }

        var notYetDue = TestData.NewIssue("cph-later");
        Assert.True(notYetDue.Suppress(Night1.AddDays(30), Night1).IsSuccess);
        issues.Add(notYetDue);
        await store.SaveBatchAsync(issues);

        var due = await store.FindExpiredSuppressionsAsync(Night1.AddDays(4), limit: 2);

        Assert.Equal(2, due.Count);
        Assert.Equal(Night1.AddDays(1), due[0].SuppressionExpiresAtUtc);
        Assert.Equal(Night1.AddDays(2), due[1].SuppressionExpiresAtUtc);
    }

    [Fact]
    public async Task Unsuppressed_issues_are_never_returned_as_expired()
    {
        var store = await SeedAsync(TestData.NewIssue("12/345/6789"));

        Assert.Empty(await store.FindExpiredSuppressionsAsync(Night1.AddYears(1), limit: 10));
    }

    [Fact]
    public async Task Search_filters_on_group_rule_run_and_status()
    {
        var mine = TestData.NewIssue("a", runId: "run-1");
        var otherRule = TestData.NewIssue("b", runId: "run-1", ruleId: TestData.Rule5);
        var otherGroup = TestData.NewIssue("c", runId: "run-1", groupId: "elsewhere");
        var store = await SeedAsync(mine, otherRule, otherGroup);

        var byGroup = await store.SearchAsync(new IssueFilter { GroupId = TestData.Group });
        Assert.Equal(2, byGroup.TotalCount);

        var byRule = await store.SearchAsync(new IssueFilter { RuleId = TestData.Rule5 });
        Assert.Equal(otherRule.Id.Value, Assert.Single(byRule.Items).Id.Value);

        var byRun = await store.SearchAsync(new IssueFilter { RunId = "run-1" });
        Assert.Equal(3, byRun.TotalCount);

        var byStatus = await store.SearchAsync(new IssueFilter { Status = IssueStatus.Inactive });
        Assert.Equal(0, byStatus.TotalCount);
    }

    [Fact]
    public async Task Search_filters_on_the_human_axis()
    {
        var assigned = TestData.NewIssue("a");
        Assert.True(assigned.ChangeWorkflowStatus(WorkflowStatus.InProgress, Night1).IsSuccess);
        var sameHolding = TestData.NewIssue("a", ruleId: TestData.Rule5);

        // An assignment on the issue itself, from before assignment moved to the holding, is not read.
        var unassigned = TestData.NewIssue("b");
        unassigned.AssignTo("alice", Night1);
        var store = await SeedAsync(assigned, sameHolding, unassigned);

        var holdings = CreateHoldingStore();
        var alices = Holding.New(new HoldingKey(TestData.Group, "a"), Night1);
        alices.SyncAssignment(null, "Alice", Night1);
        Assert.True(await holdings.SaveAsync(alices, 0));
        var elsewhere = Holding.New(new HoldingKey("elsewhere", "b"), Night1);
        elsewhere.SyncAssignment(null, "Alice", Night1);
        Assert.True(await holdings.SaveAsync(elsewhere, 0));

        var mine = await store.SearchAsync(new IssueFilter { AssignedTo = "ALICE" });
        Assert.Equal(2, mine.TotalCount);
        Assert.DoesNotContain(mine.Items, issue => issue.Id == unassigned.Id);

        var queue = await store.SearchAsync(new IssueFilter { Unassigned = true });
        Assert.Equal(unassigned.Id.Value, Assert.Single(queue.Items).Id.Value);

        var inProgress = await store.SearchAsync(new IssueFilter
        {
            WorkflowStatuses = [WorkflowStatus.InProgress, WorkflowStatus.Resolved]
        });
        Assert.Equal(assigned.Id.Value, Assert.Single(inProgress.Items).Id.Value);
    }

    [Fact]
    public async Task Issues_are_found_by_their_holding()
    {
        var first = TestData.NewIssue("01/001/0001");
        var second = TestData.NewIssue("01/001/0001", ruleId: TestData.Rule5);
        var neighbour = TestData.NewIssue("01/001/0002");
        var otherGroup = TestData.NewIssue("01/001/0001", groupId: "elsewhere", ruleId: "rule9");
        var keyless = Issue.FromDetection(
            new IssueDetection(TestData.Key("x"), TestData.Rule2, new Dictionary<string, string> { ["keeper"] = "J Smith" }),
            TestData.Group, "run-1", Night1);
        var store = await SeedAsync(first, second, neighbour, otherGroup, keyless);

        var found = await store.GetForHoldingsAsync(TestData.Group, ["01/001/0001", "missing"]);

        Assert.Equal(
            new[] { first.Id.Value, second.Id.Value }.Order(StringComparer.Ordinal),
            found.Select(issue => issue.Id.Value).Order(StringComparer.Ordinal));
        Assert.Equal(3, (await store.GetForHoldingsAsync(TestData.Group, ["01/001/0001", "01/001/0002"])).Count);
        Assert.Empty(await store.GetForHoldingsAsync(TestData.Group, []));
    }

    [Fact]
    public async Task Save_all_writes_every_issue_or_none()
    {
        var store = await SeedAsync(TestData.NewIssue("a"), TestData.NewIssue("b"));
        var first = (await store.GetAsync(TestData.Key("a")))!;
        var second = (await store.GetAsync(TestData.Key("b")))!;
        var (firstVersion, secondVersion) = (first.Version, second.Version);
        first.SetNotes("first", Night2);
        second.SetNotes("second", Night2);

        Assert.False(await store.SaveAllAsync([(first, firstVersion), (second, secondVersion + 1)]));
        Assert.Null((await store.GetAsync(first.Id))!.Notes);
        Assert.Null((await store.GetAsync(second.Id))!.Notes);

        Assert.True(await store.SaveAllAsync([(first, firstVersion), (second, secondVersion)]));
        Assert.Equal("first", (await store.GetAsync(first.Id))!.Notes);
        Assert.Equal("second", (await store.GetAsync(second.Id))!.Notes);
        Assert.True(await store.SaveAllAsync([]));
    }

    [Fact]
    public async Task An_outcome_reason_round_trips_on_single_and_batch_saves()
    {
        var duplicate = CodedValue.Create(CodedList.CantResolveReason, "Duplicate", Night1).Value;
        Assert.True(await CreateCodedValueStore().AddAsync(duplicate));
        var reason = new OutcomeReason(duplicate.Id, duplicate.Name);
        var issues = Enumerable.Range(0, 5).Select(i => TestData.NewIssue($"reason-{i}")).ToArray();
        foreach (var issue in issues)
        {
            Assert.True(issue.ChangeWorkflowStatus(WorkflowStatus.Nonactionable, Night1, reason).IsSuccess);
        }

        // Five is over the Postgres harness's COPY threshold, so both batch write shapes are covered.
        var store = await SeedAsync(issues);
        var single = TestData.NewIssue("reason-single");
        Assert.True(single.ChangeWorkflowStatus(WorkflowStatus.Nonactionable, Night1, reason).IsSuccess);
        Assert.True(await store.SaveAsync(single, null));

        foreach (var issue in issues.Append(single))
        {
            Assert.Equal(issue.ToSnapshot(), (await store.GetAsync(issue.Id))!.ToSnapshot());
        }
    }

    [Fact]
    public async Task Search_matches_on_context_pairs()
    {
        var match = Issue.FromDetection(
            TestData.Detection("a", TestData.Rule2, ("keeper", "J Smith")), TestData.Group, "run-1", Night1);
        var other = Issue.FromDetection(
            TestData.Detection("b", TestData.Rule2, ("keeper", "A Jones")), TestData.Group, "run-1", Night1);
        var store = await SeedAsync(match, other);

        var found = await store.SearchAsync(new IssueFilter
        {
            ContextMatches = new Dictionary<string, string> { ["keeper"] = "J Smith" }
        });

        Assert.Equal(match.Id.Value, Assert.Single(found.Items).Id.Value);
    }

    [Fact]
    public async Task Search_term_covers_id_notes_and_context_values()
    {
        var byNotes = TestData.NewIssue("a");
        byNotes.SetNotes("awaiting LETTER from keeper", Night1);
        var byContext = Issue.FromDetection(
            TestData.Detection("b", TestData.Rule2, ("keeper", "Letterman")), TestData.Group, "run-1", Night1);
        var unrelated = TestData.NewIssue("c");
        var store = await SeedAsync(byNotes, byContext, unrelated);

        var found = await store.SearchAsync(new IssueFilter { SearchTerm = "letter" });

        Assert.Equal(2, found.TotalCount);
    }

    [Fact]
    public async Task Search_pages_and_reports_the_unpaged_total()
    {
        var store = CreateStore();
        var issues = Enumerable.Range(0, 5)
            .Select(i => TestData.NewIssue($"cph-{i}", at: Night1.AddMinutes(i)))
            .ToArray();
        await store.SaveBatchAsync(issues);

        var filter = new IssueFilter
        {
            SortBy = IssueSortField.CreatedAtUtc,
            SortDirection = SortDirection.Ascending,
            Page = new PageRequest(2, 2)
        };

        var page = await store.SearchAsync(filter);

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(issues[2].Id.Value, page.Items[0].Id.Value);
        Assert.Equal(issues[3].Id.Value, page.Items[1].Id.Value);
    }

    [Fact]
    public async Task Search_sorts_descending_by_default_on_last_update()
    {
        var older = TestData.NewIssue("older", at: Night1);
        var newer = TestData.NewIssue("newer", at: Night2);
        var store = await SeedAsync(older, newer);

        var page = await store.SearchAsync(new IssueFilter());

        Assert.Equal(newer.Id.Value, page.Items[0].Id.Value);
        Assert.Equal(older.Id.Value, page.Items[1].Id.Value);
    }

    [Fact]
    public async Task Grouped_search_pages_groups_newest_first_and_keeps_each_group_whole()
    {
        var aOld = TestData.NewIssue("A", at: Night1);
        var aNew = TestData.NewIssue("A", ruleId: TestData.Rule5, at: Night2);
        var b = TestData.NewIssue("B", at: Night1.AddHours(1));
        var c = TestData.NewIssue("C", at: Night1);
        var store = await SeedAsync(aOld, aNew, b, c);

        var first = await store.SearchGroupedAsync(new IssueFilter { Page = new PageRequest(1, 2) }, "cph");

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(["A", "B"], first.Items.Select(g => g.Key));
        Assert.Equal(2, first.Items[0].IssueCount);
        Assert.Equal(Night2, first.Items[0].LatestUpdatedAtUtc);
        Assert.Equal([aNew.Id.Value, aOld.Id.Value], first.Items[0].Issues.Select(i => i.Id.Value));
        Assert.Equal(b.Id.Value, Assert.Single(first.Items[1].Issues).Id.Value);

        var second = await store.SearchGroupedAsync(new IssueFilter { Page = new PageRequest(2, 2) }, "cph");

        var last = Assert.Single(second.Items);
        Assert.Equal("C", last.Key);
        Assert.Equal(c.Id.Value, Assert.Single(last.Issues).Id.Value);
    }

    [Fact]
    public async Task Grouped_search_applies_the_filter_and_gathers_keyless_issues_under_the_empty_key()
    {
        var kept = TestData.NewIssue("A");
        var filteredOut = TestData.NewIssue("A", ruleId: TestData.Rule5);
        var keyless = Issue.FromDetection(
            new IssueDetection(TestData.Key("x"), TestData.Rule2, new Dictionary<string, string> { ["keeper"] = "J Smith" }),
            TestData.Group, "run-1", Night1);
        var store = await SeedAsync(kept, filteredOut, keyless);

        var page = await store.SearchGroupedAsync(new IssueFilter { RuleId = TestData.Rule2 }, "cph");

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(["", "A"], page.Items.Select(g => g.Key));
        Assert.Equal(keyless.Id.Value, Assert.Single(page.Items[0].Issues).Id.Value);
        Assert.Equal(kept.Id.Value, Assert.Single(page.Items[1].Issues).Id.Value);
    }

    [Theory]
    [InlineData(IssueGroupSortField.IssueCount, SortDirection.Descending, "A,B,C")]
    [InlineData(IssueGroupSortField.IssueCount, SortDirection.Ascending, "C,B,A")]
    [InlineData(IssueGroupSortField.LatestUpdatedAtUtc, SortDirection.Descending, "C,A,B")]
    [InlineData(IssueGroupSortField.LatestUpdatedAtUtc, SortDirection.Ascending, "B,A,C")]
    public async Task Grouped_search_orders_groups_by_the_requested_field_across_pages(
        IssueGroupSortField sortBy, SortDirection direction, string expected)
    {
        var store = await SeedAsync(
            TestData.NewIssue("A", at: Night1.AddHours(2)),
            TestData.NewIssue("A", ruleId: TestData.Rule5, at: Night1.AddHours(2)),
            TestData.NewIssue("A", ruleId: "rule7", at: Night1.AddHours(2)),
            TestData.NewIssue("B", at: Night1),
            TestData.NewIssue("B", ruleId: TestData.Rule5, at: Night1),
            TestData.NewIssue("C", at: Night2));

        var keys = new List<string>();
        for (var page = 1; page <= 2; page++)
        {
            var result = await store.SearchGroupedAsync(new IssueFilter
            {
                GroupSortBy = sortBy,
                GroupSortDirection = direction,
                Page = new PageRequest(page, 2)
            }, "cph");
            keys.AddRange(result.Items.Select(g => g.Key));
        }

        Assert.Equal(expected, string.Join(',', keys));
    }

    [Fact]
    public async Task Grouped_search_reports_no_groups_when_nothing_matches()
    {
        var store = await SeedAsync(TestData.NewIssue("A"));

        var page = await store.SearchGroupedAsync(new IssueFilter { GroupId = "elsewhere" }, "cph");

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Count_by_status_breaks_down_both_axes_for_one_group()
    {
        var active = TestData.NewIssue("a");
        var triaged = TestData.NewIssue("b");
        Assert.True(triaged.ChangeWorkflowStatus(WorkflowStatus.Resolved, Night1).IsSuccess);
        var inactive = TestData.NewIssue("c");
        inactive.Deactivate(Night2);
        var elsewhere = TestData.NewIssue("d", groupId: "elsewhere");
        var store = await SeedAsync(active, triaged, inactive, elsewhere);

        var counts = await store.CountByStatusAsync(TestData.Group);

        Assert.Equal(3, counts.Sum(c => c.Count));
        Assert.Equal(1, counts.Single(c => c is
            { Status: IssueStatus.Active, WorkflowStatus: WorkflowStatus.Resolved }).Count);
        Assert.Equal(1, counts.Single(c => c is
            { Status: IssueStatus.Inactive, WorkflowStatus: WorkflowStatus.Todo }).Count);
    }
}
