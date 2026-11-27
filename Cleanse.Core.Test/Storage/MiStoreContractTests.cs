using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Mi;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Storage;

/// <summary>
/// The behaviour every <see cref="IMiStore"/> must have. A store only reads its history and swaps
/// and counts a summary, so the same summary must give the same counts on every provider.
/// </summary>
public abstract class MiStoreContractTests
{
    private const string Group = TestData.Group;
    private const string Other = "other-group";
    private const string Cph1 = "01/001/0001";
    private const string Cph2 = "01/001/0002";

    private static readonly DateTimeOffset At = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day1 = new(2026, 11, 2);
    private static readonly DateOnly Day2 = new(2026, 11, 3);
    private static readonly Guid Alice = Guid.Parse("0a1ce000-0000-4000-8000-000000000001");
    private static readonly Guid Bob = Guid.Parse("0b0b0000-0000-4000-8000-000000000002");
    private static readonly Guid FixedInSam = Guid.Parse("f1000000-0000-4000-8000-000000000003");

    protected abstract IMiStore CreateStore();

    protected abstract IIssueStore CreateIssueStore();

    protected abstract IAuditStore CreateAuditStore();

    protected abstract IHoldingStore CreateHoldingStore();

    protected abstract IHoldingActivityStore CreateActivityStore();

    protected abstract ICodedValueStore CreateCodedValueStore();

    protected abstract IRuleRunStore CreateRuleRunStore();

    private static MiEvent Event(
        long sequence, DateOnly day, MiUnit unit, MiEventKind kind, MiSource source,
        Guid? user = null, string? rule = MiScenarioRules.Rule1, string? cph = Cph1,
        string? fromWorkflow = null, string? toWorkflow = null, Guid? reason = null, string? reasonName = null,
        MiOpenChange? change = null, int delta = 0, int? elapsed = null, bool? inactiveNow = null, int? actions = null,
        string? fromStatus = null, string? toStatus = null) =>
        new(Group, sequence, new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero).AddMinutes(sequence),
            day, unit, kind, source, unit is MiUnit.Issue ? $"issue-{sequence}" : null, unit is MiUnit.Issue ? rule : null, cph,
            user, user is null ? null : "Someone", fromStatus, toStatus, fromWorkflow, toWorkflow, reason, reasonName,
            change, delta, elapsed, inactiveNow, actions);

    private static readonly MiEvent[] SampleEvents =
    [
        Event(1, Day1, MiUnit.Issue, MiEventKind.Workflow, MiSource.Cleanse, Alice,
            fromWorkflow: "todo", toWorkflow: "resolved", reason: FixedInSam, reasonName: "Fixed in SAM",
            change: MiOpenChange.Resolved, delta: -1, elapsed: 3, inactiveNow: true,
            fromStatus: MiNames.Active, toStatus: MiNames.Active),
        Event(2, Day1, MiUnit.Issue, MiEventKind.Workflow, MiSource.Cleanse, Alice, rule: MiScenarioRules.Rule2,
            fromWorkflow: "todo", toWorkflow: "resolved", change: MiOpenChange.Resolved, delta: -1, elapsed: 4, inactiveNow: false),
        Event(3, Day2, MiUnit.Issue, MiEventKind.Workflow, MiSource.Spol,
            fromWorkflow: "todo", toWorkflow: "resolved", change: MiOpenChange.Spol, delta: -1, elapsed: 5, inactiveNow: false),
        Event(4, Day2, MiUnit.Action, MiEventKind.Action, MiSource.Cleanse, Bob, reason: FixedInSam, reasonName: "Phoned"),
        Event(5, Day2, MiUnit.Note, MiEventKind.Note, MiSource.Cleanse, cph: Cph2),
        Event(6, Day2, MiUnit.Holding, MiEventKind.HoldingStatus, MiSource.Cleanse, Bob, cph: Cph2,
            fromWorkflow: "todo", toWorkflow: "closed", change: MiOpenChange.Resolved, delta: -1, actions: 2,
            fromStatus: MiNames.Active, toStatus: MiNames.Active),
        Event(7, Day2, MiUnit.Issue, MiEventKind.Raised, MiSource.Rules, cph: Cph2,
            toWorkflow: "todo", change: MiOpenChange.Raised, delta: 1, toStatus: MiNames.Active)
    ];

    private static MiSummary Sample(string group = Group, string fingerprint = "fingerprint-1") => new(
        new MiRefreshRecord(group, At, At, fingerprint, MiProjector.Version, 1234, At.AddDays(-30), 3, 7, 1),
        [
            new MiIssueState(group, "issue-1", MiScenarioRules.Rule1, Cph1, IssueStatus.Active, WorkflowStatus.Todo,
                null, null, At.AddDays(-20), Day1.AddDays(-20), At),
            new MiIssueState(group, "issue-2", MiScenarioRules.Rule2, Cph1, IssueStatus.Active, WorkflowStatus.Todo,
                null, null, At.AddDays(-20), Day1.AddDays(-20), At),
            new MiIssueState(group, "issue-3", MiScenarioRules.Rule2, Cph2, IssueStatus.Inactive, WorkflowStatus.Resolved,
                FixedInSam, "Fixed in SAM", At.AddDays(-5), Day1.AddDays(-5), At)
        ],
        [
            new MiHoldingState(group, Cph1, HoldingStatus.InProgress, true, 2, At.AddDays(-20), Day1.AddDays(-20),
                At.AddDays(-19), null, Alice, "Alice Smith"),
            new MiHoldingState(group, Cph2, HoldingStatus.Closed, false, 0, null, null, null, At, null, null),
            new MiHoldingState(group, "01/001/0003", HoldingStatus.Todo, true, 1, At.AddDays(-2), Day1.AddDays(-2),
                null, At.AddDays(-1), null, null)
        ],
        [.. SampleEvents.Select(e => e with { GroupId = group })],
        [
            new MiDailyChange(group, Day1, MiUnit.Issue, MiScenarioRules.Rule1, MiNames.Active, "todo", 5),
            new MiDailyChange(group, Day1, MiUnit.Issue, MiScenarioRules.Rule2, MiNames.Active, "todo", 2),
            new MiDailyChange(group, Day2, MiUnit.Issue, MiScenarioRules.Rule1, MiNames.Active, "todo", -1),
            new MiDailyChange(group, Day2, MiUnit.Issue, MiScenarioRules.Rule1, MiNames.Active, "resolved", 1),
            new MiDailyChange(group, Day2, MiUnit.Holding, string.Empty, MiNames.Active, "todo", 3)
        ]);

    [Fact]
    public async Task Nothing_is_there_until_a_summary_is_written()
    {
        var store = CreateStore();

        Assert.Null(await store.GetRefreshAsync(Group));
        Assert.Empty(await store.CountEventsAsync(new MiEventFilter(Group, Day1, Day2), MiEventDimensions.Day));
        Assert.Empty(await store.GetDailyChangesAsync(Group, MiUnit.Issue, null, Day2));
        Assert.Empty(await store.CountIssueStatesAsync(Group, null));
        Assert.Empty(await store.CountHoldingStatesAsync(Group));
    }

    [Fact]
    public async Task A_summary_round_trips_its_refresh_record_and_a_check_moves_only_the_checked_time()
    {
        var store = CreateStore();
        var summary = Sample();

        Assert.True(await store.ReplaceAsync(summary));
        Assert.Equal(summary.Refresh, await store.GetRefreshAsync(Group));

        await store.MarkCheckedAsync(Group, At.AddMinutes(5));
        Assert.Equal(summary.Refresh with { CheckedAtUtc = At.AddMinutes(5) }, await store.GetRefreshAsync(Group));
    }

    [Fact]
    public async Task A_new_summary_replaces_the_old_one_whole()
    {
        var store = CreateStore();
        Assert.True(await store.ReplaceAsync(Sample()));
        Assert.True(await store.ReplaceAsync(Sample(fingerprint: "fingerprint-2")));

        Assert.Equal("fingerprint-2", (await store.GetRefreshAsync(Group))!.Fingerprint);
        var all = await store.CountEventsAsync(new MiEventFilter(Group, DateOnly.MinValue, DateOnly.MaxValue), MiEventDimensions.None);
        Assert.Equal(7, Assert.Single(all).Count);
        Assert.Equal(3, (await store.CountIssueStatesAsync(Group, null)).Sum(count => count.Count));
        Assert.Equal(3, (await store.CountHoldingStatesAsync(Group)).Sum(count => count.Count));
        Assert.Equal(4, (await store.GetDailyChangesAsync(Group, MiUnit.Issue, null, Day2)).Count);
        Assert.Single(await store.GetDailyChangesAsync(Group, MiUnit.Holding, null, Day2));
    }

    [Fact]
    public async Task Groups_are_kept_apart()
    {
        var store = CreateStore();
        Assert.True(await store.ReplaceAsync(Sample()));
        Assert.True(await store.ReplaceAsync(Sample(Other, "other")));
        Assert.True(await store.ReplaceAsync(Sample(Other, "other again")));

        Assert.Equal("fingerprint-1", (await store.GetRefreshAsync(Group))!.Fingerprint);
        var all = await store.CountEventsAsync(new MiEventFilter(Group, DateOnly.MinValue, DateOnly.MaxValue), MiEventDimensions.None);
        Assert.Equal(7, Assert.Single(all).Count);
    }

    [Fact]
    public async Task Events_are_counted_by_the_dimensions_asked_for()
    {
        var store = CreateStore();
        Assert.True(await store.ReplaceAsync(Sample()));

        var byDayAndUser = await store.CountEventsAsync(
            new MiEventFilter(Group, Day1, Day2) { Units = [MiUnit.Issue], Kinds = [MiEventKind.Workflow] },
            MiEventDimensions.Day | MiEventDimensions.User);

        Assert.Equal(
            [(Day1, (Guid?)Alice, 2), (Day2, null, 1)],
            byDayAndUser.OrderBy(c => c.Day).Select(c => (c.Day!.Value, c.UserId, c.Count)));

        var latest = byDayAndUser.Single(c => c.Day == Day1).LastAtUtc;
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 2, 0, TimeSpan.Zero), latest);
    }

    [Fact]
    public async Task Every_dimension_comes_back_as_it_went_in()
    {
        var store = CreateStore();
        Assert.True(await store.ReplaceAsync(Sample()));

        var all = await store.CountEventsAsync(
            new MiEventFilter(Group, Day1, Day2),
            Enum.GetValues<MiEventDimensions>().Aggregate((a, b) => a | b));

        Assert.Equal(7, all.Count);
        var resolved = all.Single(c => c.ElapsedDays == 3);
        Assert.Equal(
            new MiEventCount(1, resolved.LastAtUtc)
            {
                Day = Day1,
                Unit = MiUnit.Issue,
                Kind = MiEventKind.Workflow,
                Source = MiSource.Cleanse,
                UserId = Alice,
                RuleId = MiScenarioRules.Rule1,
                Cph = Cph1,
                FromStatus = MiNames.Active,
                ToStatus = MiNames.Active,
                FromWorkflow = "todo",
                ToWorkflow = "resolved",
                ReasonId = FixedInSam,
                ReasonName = "Fixed in SAM",
                OpenChange = MiOpenChange.Resolved,
                OpenDelta = -1,
                ElapsedDays = 3,
                InactiveNow = true,
                ActionsSinceClosed = null
            },
            resolved);

        var closure = all.Single(c => c.Unit == MiUnit.Holding);
        Assert.Equal((2, "closed", Cph2), (closure.ActionsSinceClosed!.Value, closure.ToWorkflow, closure.Cph));
    }

    [Fact]
    public async Task Events_are_filtered_by_every_filter_given()
    {
        var store = CreateStore();
        Assert.True(await store.ReplaceAsync(Sample()));

        async Task<int> CountAsync(MiEventFilter filter) =>
            (await store.CountEventsAsync(filter, MiEventDimensions.None)).Sum(c => c.Count);

        var both = new MiEventFilter(Group, Day1, Day2);
        Assert.Equal(7, await CountAsync(both));
        Assert.Equal(2, await CountAsync(both with { ToDay = Day1 }));
        Assert.Equal(5, await CountAsync(both with { FromDay = Day2 }));
        Assert.Equal(4, await CountAsync(both with { Units = [MiUnit.Issue] }));
        Assert.Equal(2, await CountAsync(both with { Units = [MiUnit.Action, MiUnit.Note] }));
        Assert.Equal(3, await CountAsync(both with { Kinds = [MiEventKind.Workflow] }));
        Assert.Equal(5, await CountAsync(both with { Sources = [MiSource.Cleanse] }));
        Assert.Equal(1, await CountAsync(both with { RuleId = MiScenarioRules.Rule2 }));
        Assert.Equal(2, await CountAsync(both with { UserId = Bob }));
        Assert.Equal(0, await CountAsync(both with { GroupId = Other }));
    }

    [Fact]
    public async Task Daily_changes_are_read_by_unit_rule_and_day()
    {
        var store = CreateStore();
        Assert.True(await store.ReplaceAsync(Sample()));

        Assert.Equal(4, (await store.GetDailyChangesAsync(Group, MiUnit.Issue, null, Day2)).Count);
        Assert.Equal(2, (await store.GetDailyChangesAsync(Group, MiUnit.Issue, null, Day1)).Count);
        Assert.Equal(3, (await store.GetDailyChangesAsync(Group, MiUnit.Issue, MiScenarioRules.Rule1, Day2)).Count);

        var holdings = Assert.Single(await store.GetDailyChangesAsync(Group, MiUnit.Holding, null, Day2));
        Assert.Equal(new MiDailyChange(Group, Day2, MiUnit.Holding, string.Empty, MiNames.Active, "todo", 3), holdings);
    }

    [Fact]
    public async Task Issue_states_are_counted_by_rule_status_and_day_first_raised()
    {
        var store = CreateStore();
        Assert.True(await store.ReplaceAsync(Sample()));

        var all = await store.CountIssueStatesAsync(Group, null);
        Assert.Equal(
            [
                new MiIssueStateCount(MiScenarioRules.Rule1, IssueStatus.Active, WorkflowStatus.Todo, Day1.AddDays(-20), 1),
                new MiIssueStateCount(MiScenarioRules.Rule2, IssueStatus.Active, WorkflowStatus.Todo, Day1.AddDays(-20), 1),
                new MiIssueStateCount(MiScenarioRules.Rule2, IssueStatus.Inactive, WorkflowStatus.Resolved, Day1.AddDays(-5), 1)
            ],
            all.OrderBy(c => c.RuleId, StringComparer.Ordinal).ThenBy(c => c.Status));

        Assert.Equal(2, (await store.CountIssueStatesAsync(Group, MiScenarioRules.Rule2)).Sum(c => c.Count));
    }

    [Fact]
    public async Task Holding_states_are_counted_and_listed()
    {
        var store = CreateStore();
        Assert.True(await store.ReplaceAsync(Sample()));

        var counts = await store.CountHoldingStatesAsync(Group);
        Assert.Equal(3, counts.Sum(c => c.Count));
        Assert.Contains(counts, c => c == new MiHoldingStateCount(HoldingStatus.InProgress, true, Alice, Day1.AddDays(-20), 1));
        Assert.Contains(counts, c => c == new MiHoldingStateCount(HoldingStatus.Closed, false, null, null, 1));

        var oldest = await store.ListHoldingStatesAsync(new MiHoldingListQuery(Group, MiHoldingList.OldestOpen, 10));
        Assert.Equal([Cph1, "01/001/0003"], oldest.Select(h => h.Cph));
        Assert.Equal(Sample().Holdings[0], oldest[0]);
        Assert.Single(await store.ListHoldingStatesAsync(new MiHoldingListQuery(Group, MiHoldingList.OldestOpen, 1)));

        var stalled = await store.ListHoldingStatesAsync(
            new MiHoldingListQuery(Group, MiHoldingList.Stalled, 10, At.AddDays(-14)));
        Assert.Equal(Cph1, Assert.Single(stalled).Cph);
        Assert.Empty(await store.ListHoldingStatesAsync(new MiHoldingListQuery(Group, MiHoldingList.Stalled, 10, At.AddDays(-25))));
    }

    [Fact]
    public async Task The_history_is_read_from_the_group_alone()
    {
        var issues = CreateIssueStore();
        var audit = CreateAuditStore();
        var activity = CreateActivityStore();
        var codedValues = CreateCodedValueStore();
        var runs = CreateRuleRunStore();
        var night = TestData.Night1;

        var first = Issue.FromDetection(Detection(Cph1, MiScenarioRules.Rule1), Group, "run-1", night);
        var second = Issue.FromDetection(Detection(Cph2, MiScenarioRules.Rule2), Group, "run-1", night);
        var elsewhere = Issue.FromDetection(Detection(Cph1, "rule9"), Other, "run-x", night);
        await issues.SaveBatchAsync([first, second, elsewhere]);
        await issues.DeactivateStaleAsync(Group, "run-2", night.AddDays(1));

        var reason = CodedValue.Create(CodedList.ResolvedReason, "Fixed in SAM", night).Value;
        Assert.True(await codedValues.AddAsync(reason));
        var reload = (await issues.GetAsync(first.Id))!;
        var version = reload.Version;
        var resolved = reload.ChangeWorkflowStatus(WorkflowStatus.Resolved, night.AddDays(2), new OutcomeReason(reason.Id, reason.Name)).Value;
        Assert.True(await issues.SaveAsync(reload, version));
        var person = Actor.Human(Alice.ToString("D"), "Alice Smith");
        await audit.AppendBatchAsync(
        [
            AuditEntry.ForIssue(first.Id, resolved, person, night.AddDays(2)),
            AuditEntry.ForIssue(first.Id, new IssueChange(AuditEventKind.IssueNotesChanged, [new AuditChange("Notes", null, "x")]),
                person, night.AddDays(2)),
            AuditEntry.ForIssue(elsewhere.Id, resolved, person, night.AddDays(2))
        ]);

        var phoned = CodedValue.Create(CodedList.ActionType, "Phoned the keeper", night).Value;
        Assert.True(await codedValues.AddAsync(phoned));
        await activity.AddActionsAsync(
            [HoldingAction.Create(Guid.CreateVersion7(night), new HoldingKey(Group, Cph1), phoned, person, night.AddDays(3))],
            HoldingNote.Create(new HoldingKey(Group, Cph1), "No answer", null, person, night.AddDays(3)).Value);
        await activity.AddNoteAsync(HoldingNote.Create(new HoldingKey(Other, Cph1), "Elsewhere", null, person, night).Value);

        await runs.SaveAsync(RuleRun.Start("run-1", Group, night));
        await runs.SaveAsync(RuleRun.Start("run-x", Other, night.AddDays(-9)));

        var history = await CreateStore().ReadHistoryAsync(Group);

        Assert.Equal(Group, history.GroupId);
        Assert.False(string.IsNullOrEmpty(history.Fingerprint));
        Assert.Equal(night, history.FirstRunStartedAtUtc);

        var rows = history.Issues.OrderBy(i => i.Cph, StringComparer.Ordinal).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new MiHistoryIssue(first.Id.Value, MiScenarioRules.Rule1, Cph1, IssueStatus.Inactive, WorkflowStatus.Resolved,
                reason.Id, "Fixed in SAM", night, night.AddDays(1), night.AddDays(2)),
            rows[0]);
        Assert.Equal((IssueStatus.Inactive, (DateTimeOffset?)night.AddDays(1)), (rows[1].Status, rows[1].DeactivatedAtUtc));

        var fact = Assert.Single(history.AuditFacts);
        Assert.Equal(
            (first.Id.Value, AuditEventKind.IssueWorkflowStatusChanged, (WorkflowStatus?)WorkflowStatus.Todo,
                (WorkflowStatus?)WorkflowStatus.Resolved, true, (Guid?)reason.Id, "Fixed in SAM", Alice.ToString("D")),
            (fact.IssueId, fact.Kind, fact.WorkflowFrom, fact.WorkflowTo, fact.ReasonChanged, fact.ReasonId, fact.ReasonName,
                fact.Actor.Id));

        Assert.Equal(2, history.Activity.Count);
        var action = Assert.Single(history.Activity, a => a.Unit == MiUnit.Action);
        Assert.Equal((Cph1, phoned.Id, "Phoned the keeper", night.AddDays(3)), (action.Cph, action.ActionTypeId!.Value, action.ActionTypeName!, action.AtUtc));
        Assert.Equal(Cph1, Assert.Single(history.Activity, a => a.Unit == MiUnit.Note).Cph);
    }

    [Fact]
    public async Task A_reactivation_carries_the_deactivation_it_undid()
    {
        var issues = CreateIssueStore();
        var audit = CreateAuditStore();
        var night = TestData.Night1;

        var issue = Issue.FromDetection(Detection(Cph1, MiScenarioRules.Rule1), Group, "run-1", night);
        await issues.SaveBatchAsync([issue]);
        await issues.DeactivateStaleAsync(Group, "run-2", night.AddDays(1));
        var reload = (await issues.GetAsync(issue.Id))!;
        var change = reload.RecordDetection("run-3", issue.Context, night.AddDays(2));
        await issues.SaveBatchAsync([reload]);
        await audit.AppendAsync(AuditEntry.ForIssue(issue.Id, change, Actor.System, night.AddDays(2), "run-3"));

        var history = await CreateStore().ReadHistoryAsync(Group);

        var fact = Assert.Single(history.AuditFacts);
        Assert.Equal((AuditEventKind.IssueReactivated, (DateTimeOffset?)night.AddDays(1)), (fact.Kind, fact.DeactivatedFrom));
    }

    [Fact]
    public async Task The_fingerprint_changes_with_anything_a_summary_is_built_from()
    {
        var store = CreateStore();
        var issues = CreateIssueStore();
        var night = TestData.Night1;

        var empty = await store.GetFingerprintAsync(Group);
        Assert.Equal(empty, await store.GetFingerprintAsync(Group));

        var issue = Issue.FromDetection(Detection(Cph1, MiScenarioRules.Rule1), Group, "run-1", night);
        await issues.SaveBatchAsync([issue]);
        var withIssue = await store.GetFingerprintAsync(Group);
        Assert.NotEqual(empty, withIssue);

        await CreateAuditStore().AppendAsync(AuditEntry.ForIssue(
            issue.Id,
            new IssueChange(AuditEventKind.IssueWorkflowStatusChanged, [new AuditChange("WorkflowStatus", "todo", "in-progress")]),
            TestData.Alice,
            night));
        var withAudit = await store.GetFingerprintAsync(Group);
        Assert.NotEqual(withIssue, withAudit);

        await CreateActivityStore().AddNoteAsync(HoldingNote.Create(new HoldingKey(Group, Cph1), "Note", null, TestData.Alice, night).Value);
        var withNote = await store.GetFingerprintAsync(Group);
        Assert.NotEqual(withAudit, withNote);

        var holding = Holding.New(new HoldingKey(Group, Cph1), night);
        holding.SyncAssignment(null, "Alice", night);
        Assert.True(await CreateHoldingStore().SaveAsync(holding, 0));
        Assert.NotEqual(withNote, await store.GetFingerprintAsync(Group));
    }

    private static IssueDetection Detection(string cph, string ruleId) =>
        new(IssueId.Derive(ruleId, cph), ruleId, new Dictionary<string, string> { [HoldingKey.CphContextKey] = cph });
}

internal static class MiScenarioRules
{
    public const string Rule1 = "rule1";
    public const string Rule2 = "rule2";
}
