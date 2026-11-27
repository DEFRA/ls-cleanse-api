using Cleanse.Core.Commands.CodedValues;
using Cleanse.Core.Commands.Maintenance;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Mi;
using Cleanse.Core.Queries.Mi;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Mi;

/// <summary>The dashboard's acceptance criteria, driven through the real commands.</summary>
public class MiDashboardTests
{
    private const string A = "01/001/0001";
    private const string B = "01/001/0002";
    private const string C = "01/001/0003";
    private const string D = "01/001/0004";
    private const string E = "01/001/0005";
    private const string F = "01/001/0006";

    private static (string, string) At(string cph, string rule = MiScenario.Rule2) => (cph, rule);

    [Fact]
    public async Task Issues_resolved_in_the_past_week_by_person()
    {
        using var s = new MiScenario();
        var (alice, asAlice) = await s.PersonAsync("Alice Smith");
        var (bob, asBob) = await s.PersonAsync("Bob Jones");
        var reason = await s.Harness.AddCodedValueAsync(CodedList.CantResolveReason, "Keeper not contactable");

        await s.NightAsync(At(A), At(B), At(C), At(D), At(E), At(F));
        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved);
        await s.SetAsync(asAlice, B, WorkflowStatus.Resolved);
        await s.SetAsync(asAlice, C, WorkflowStatus.Resolved);
        await s.SetAsync(asBob, D, WorkflowStatus.Resolved);
        await s.SetAsync(asBob, E, WorkflowStatus.Resolved);
        await s.SetAsync(asBob, F, WorkflowStatus.Nonactionable, reason);

        var work = await s.WorkAsync();

        Assert.Equal(5, work.Headline.Resolved.Value);
        Assert.Equal(1, work.Headline.CantResolve.Value);
        Assert.Equal(2, work.Headline.PeopleWorking.Value);
        Assert.Equal(6, work.Headline.HoldingsClosed.Value);

        var aliceRow = Assert.Single(work.People, p => p.UserId == alice.Id);
        var bobRow = Assert.Single(work.People, p => p.UserId == bob.Id);
        Assert.Equal((3, 0, 3), (aliceRow.Resolved, aliceRow.CantResolve, aliceRow.HoldingsClosed));
        Assert.Equal((2, 1, 3), (bobRow.Resolved, bobRow.CantResolve, bobRow.HoldingsClosed));

        var keeper = Assert.Single(work.CantResolveByReason);
        Assert.Equal(("Keeper not contactable", 1), (keeper.Name, keeper.Count));
    }

    [Fact]
    public async Task SPOL_imports_are_no_ones_work_but_move_the_backlog()
    {
        using var s = new MiScenario();
        var (_, asCarol) = await s.PersonAsync("Carol Admin");

        await s.NightAsync(At(A), At(B), At(C));
        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.SpolAsync(asCarol, A, WorkflowStatus.Resolved);
        await s.SpolAsync(asCarol, B, WorkflowStatus.Resolved);

        var work = await s.WorkAsync();
        Assert.Equal(0, work.Headline.Resolved.Value);
        Assert.Empty(work.People);

        var backlog = await s.BacklogAsync();
        var spol = Assert.Single(backlog.Waterfall.Steps, step => step.Reason == "spol");
        Assert.Equal((0, 2), (spol.Up, spol.Down));
        Assert.Equal(1, backlog.OpenAtEnd);
    }

    [Fact]
    public async Task Each_outcome_counts_and_a_reopening_is_counted_too()
    {
        using var s = new MiScenario();
        var (alice, asAlice) = await s.PersonAsync("Alice Smith");

        await s.NightAsync(At(A));
        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved);
        s.Clock.Advance(TimeSpan.FromHours(1));
        await s.SetAsync(asAlice, A, WorkflowStatus.Todo);
        s.Clock.Advance(TimeSpan.FromHours(1));
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved);

        var work = await s.WorkAsync();
        var row = Assert.Single(work.People);
        Assert.Equal((alice.Id, 2, 1), (row.UserId!.Value, row.Resolved, row.Reopened));
        Assert.Equal(2, work.Headline.Resolved.Value);
    }

    [Fact]
    public async Task A_holding_closed_is_credited_to_whoever_closed_it()
    {
        using var s = new MiScenario();
        var (alice, asAlice) = await s.PersonAsync("Alice Smith");
        var (bob, asBob) = await s.PersonAsync("Bob Jones");
        var reason = await s.Harness.AddCodedValueAsync(CodedList.CantResolveReason, "Third party did not respond");

        await s.NightAsync(At(A, MiScenario.Rule1), At(A, MiScenario.Rule2));
        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved, rules: MiScenario.Rule1);
        s.Clock.Advance(TimeSpan.FromHours(1));
        await s.SetAsync(asBob, A, WorkflowStatus.Nonactionable, reason, rules: MiScenario.Rule2);

        var work = await s.WorkAsync();

        Assert.Equal(0, Assert.Single(work.People, p => p.UserId == alice.Id).HoldingsClosed);
        Assert.Equal(1, Assert.Single(work.People, p => p.UserId == bob.Id).HoldingsClosed);
        Assert.All(work.People, p => Assert.Equal(1, p.HoldingsWorked));
        Assert.Equal(1, work.Team.HoldingsWorked);
        Assert.Equal(1, work.Headline.HoldingsWorked.Value);
    }

    [Fact]
    public async Task A_holding_the_rules_close_is_no_ones_work()
    {
        using var s = new MiScenario();
        await s.NightAsync(At(A), At(B));
        s.Clock.AdvanceDays(1);
        await s.NightAsync(At(B));

        var work = await s.WorkAsync();
        Assert.Equal(0, work.Headline.HoldingsClosed.Value);

        var backlog = await s.BacklogAsync(unit: "holdings");
        Assert.Equal(1, Assert.Single(backlog.Waterfall.Steps, step => step.Reason == "no-longer-found").Down);
    }

    [Fact]
    public async Task Changes_under_the_shared_password_count_for_not_named()
    {
        using var s = new MiScenario();
        await s.NightAsync(At(A));
        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.SetAsync(Actor.Machine("ls-cleanse-ui"), A, WorkflowStatus.Resolved);

        var work = await s.WorkAsync();

        var row = Assert.Single(work.People);
        Assert.Null(row.UserId);
        Assert.False(row.Named);
        Assert.Equal(("Not named", 1), (row.Name, row.Resolved));
        Assert.Equal(0, work.Headline.PeopleWorking.Value);
    }

    [Fact]
    public async Task Each_figure_is_compared_with_the_period_before()
    {
        using var s = new MiScenario();
        var (_, asAlice) = await s.PersonAsync("Alice Smith");
        await s.NightAsync(At(A), At(B), At(C));

        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved);

        s.Clock.AdvanceDays(7);
        await s.SetAsync(asAlice, B, WorkflowStatus.Resolved);
        await s.SetAsync(asAlice, C, WorkflowStatus.Resolved);

        var work = await s.WorkAsync();

        Assert.Equal(2, work.Headline.Resolved.Value);
        Assert.Equal(1, work.Headline.Resolved.Previous);
        Assert.True(work.Period.HasPrevious);
        Assert.Equal(7, work.Headline.Resolved.Series.Count);
        Assert.Equal(2, work.Headline.Resolved.Series[^1]);
    }

    [Fact]
    public async Task Actions_by_type_and_who_does_what()
    {
        using var s = new MiScenario();
        var (alice, asAlice) = await s.PersonAsync("Alice Smith");
        var (bob, asBob) = await s.PersonAsync("Bob Jones");
        var phoned = await s.Harness.AddCodedValueAsync(CodedList.ActionType, "Phoned the keeper");
        var updated = await s.Harness.AddCodedValueAsync(CodedList.ActionType, "Updated SAM");
        await s.Harness.AddCodedValueAsync(CodedList.ActionType, "Added email address");
        var letter = await s.Harness.AddCodedValueAsync(CodedList.ActionType, "Sent a letter");

        await s.NightAsync(At(A), At(B));
        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.ActAsync(asAlice, A, phoned);
        await s.ActAsync(asAlice, B, phoned);
        await s.ActAsync(asAlice, A, phoned, letter);
        await s.ActAsync(asBob, B, updated);
        await s.Harness.SendAsync(new SetCodedValueActiveCommand(CodedList.ActionType, letter, false, TestData.Alice));

        var work = await s.WorkAsync();

        Assert.Equal(
            [("Phoned the keeper", 3, true), ("Sent a letter", 1, false), ("Updated SAM", 1, true), ("Added email address", 0, true)],
            work.ActionTypes.Select(t => (t.Name, t.Count, t.Active)));
        Assert.Equal(5, work.Headline.Actions.Value);
        Assert.Contains(work.ActionsByPerson, a => a.UserId == alice.Id && a.ActionTypeId == phoned && a.Count == 3);
        Assert.Contains(work.ActionsByPerson, a => a.UserId == bob.Id && a.ActionTypeId == updated && a.Count == 1);
        Assert.Equal(4, Assert.Single(work.People, p => p.UserId == alice.Id).Actions);
    }

    [Fact]
    public async Task Actions_recorded_on_each_holding_closed()
    {
        using var s = new MiScenario();
        var (_, asAlice) = await s.PersonAsync("Alice Smith");
        var phoned = await s.Harness.AddCodedValueAsync(CodedList.ActionType, "Phoned the keeper");

        await s.NightAsync(At(A), At(B));
        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.ActAsync(asAlice, A, phoned);
        await s.ActAsync(asAlice, A, phoned);
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved);
        await s.SetAsync(asAlice, B, WorkflowStatus.Resolved);

        var work = await s.WorkAsync();

        Assert.Equal(1, Assert.Single(work.ActionsPerHoldingClosed, band => band.Band == "0").Count);
        Assert.Equal(1, Assert.Single(work.ActionsPerHoldingClosed, band => band.Band == "2").Count);
        Assert.Equal(0, work.MedianActionsPerHoldingClosed);
    }

    [Fact]
    public async Task Outcomes_by_reason_include_unused_active_reasons_and_no_reason()
    {
        using var s = new MiScenario();
        var (_, asAlice) = await s.PersonAsync("Alice Smith");
        var fixedInSam = await s.Harness.AddCodedValueAsync(CodedList.ResolvedReason, "Fixed in SAM");
        await s.Harness.AddCodedValueAsync(CodedList.ResolvedReason, "Fixed in CTS");

        await s.NightAsync(At(A), At(B), At(C));
        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved, fixedInSam);
        await s.SetAsync(asAlice, B, WorkflowStatus.Resolved, fixedInSam);
        await s.SetAsync(asAlice, C, WorkflowStatus.Resolved);

        var work = await s.WorkAsync();

        Assert.Equal(
            [("Fixed in SAM", 2), ("No reason given", 1), ("Fixed in CTS", 0)],
            work.ResolvedByReason.Select(r => (r.Name, r.Count)));
    }

    [Fact]
    public async Task Fixes_the_rules_confirm_are_no_longer_found()
    {
        using var s = new MiScenario();
        var (_, asAlice) = await s.PersonAsync("Alice Smith");
        var fixedInSam = await s.Harness.AddCodedValueAsync(CodedList.ResolvedReason, "Fixed in SAM");

        await s.NightAsync(At(A), At(B), At(C), At(D), At(E));
        s.Clock.Advance(TimeSpan.FromHours(8));
        foreach (var cph in new[] { A, B, C, D, E })
        {
            await s.SetAsync(asAlice, cph, WorkflowStatus.Resolved, fixedInSam);
        }

        s.Clock.Advance(TimeSpan.FromHours(16));
        await s.NightAsync(At(D));

        var work = await s.WorkAsync();

        var reason = Assert.Single(work.ResolvedByReason);
        Assert.Equal((5, 4), (reason.Count, reason.NoLongerFound));
        Assert.Equal((5, 4), (work.Headline.Confirmed.Resolved, work.Headline.Confirmed.NoLongerFound));
    }

    [Fact]
    public async Task How_issues_moved_and_how_long_they_took()
    {
        using var s = new MiScenario();
        var (_, asAlice) = await s.PersonAsync("Alice Smith");

        await s.NightAsync(At(A), At(B));
        s.Clock.AdvanceDays(3);
        await s.SetAsync(asAlice, A, WorkflowStatus.InProgress);
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved);
        s.Clock.AdvanceDays(1);
        await s.SetAsync(asAlice, B, WorkflowStatus.Resolved);

        var work = await s.WorkAsync();

        Assert.Contains(work.Transitions, t => (t.From, t.To, t.Count) == ("todo", "in-progress", 1));
        Assert.Contains(work.Transitions, t => (t.From, t.To, t.Count) == ("in-progress", "resolved", 1));
        Assert.Contains(work.Transitions, t => (t.From, t.To, t.Count) == ("todo", "resolved", 1));
        Assert.Equal(2, Assert.Single(work.TimeToOutcome.Bands, band => band.Band == "under-1-week").Resolved);
        Assert.Equal(3, work.TimeToOutcome.MedianDays);
        Assert.Equal(4, work.TimeToOutcome.Percentile85Days);
        Assert.Equal(2, Assert.Single(work.OutcomesByRule).Resolved);
    }

    [Fact]
    public async Task Active_issues_by_workflow_status_over_time()
    {
        using var s = new MiScenario();
        var (_, asAlice) = await s.PersonAsync("Alice Smith");

        await s.NightAsync(At(A), At(B), At(C));
        await s.SetAsync(asAlice, C, WorkflowStatus.InProgress);
        s.Clock.AdvanceDays(1);
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved);

        var backlog = await s.BacklogAsync(MiScenario.Day(0), MiScenario.Day(1), "day");

        Assert.Equal(["todo", "in-progress", "suppressed", "resolved", "nonactionable"], backlog.States);
        Assert.Equal((2, 1, 0), (backlog.ByStatus[0].Counts["todo"], backlog.ByStatus[0].Counts["in-progress"], backlog.ByStatus[0].Counts["resolved"]));
        Assert.Equal((1, 1, 1), (backlog.ByStatus[1].Counts["todo"], backlog.ByStatus[1].Counts["in-progress"], backlog.ByStatus[1].Counts["resolved"]));
        Assert.Equal([3, 2], backlog.OpenOverTime);
    }

    [Fact]
    public async Task Active_and_inactive_over_time_with_the_rules_changes()
    {
        using var s = new MiScenario();
        await s.NightAsync(At(A), At(B));
        s.Clock.AdvanceDays(1);
        await s.NightAsync(At(B), At(C), At(D), At(E));

        var backlog = await s.BacklogAsync(MiScenario.Day(0), MiScenario.Day(1), "day");

        Assert.Equal((2, 0, 2, 0, 0), Point(backlog.ActiveInactive[0]));
        Assert.Equal((4, 1, 3, 0, 1), Point(backlog.ActiveInactive[1]));

        static (int, int, int, int, int) Point(MiActivityPointDto p) => (p.Active, p.Inactive, p.Raised, p.CameBack, p.NoLongerFound);
    }

    [Fact]
    public async Task The_reasons_the_open_issues_changed_add_up()
    {
        using var s = new MiScenario();
        var (_, asAlice) = await s.PersonAsync("Alice Smith");
        var (_, asCarol) = await s.PersonAsync("Carol Admin");

        await s.NightAsync(At(A), At(B), At(C), At(D));
        s.Clock.AdvanceDays(1);
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved);
        await s.SetAsync(asAlice, B, WorkflowStatus.Suppressed, until: s.Clock.GetUtcNow().AddDays(7));
        await s.SpolAsync(asCarol, C, WorkflowStatus.Resolved);
        s.Clock.Advance(TimeSpan.FromHours(20));
        await s.NightAsync(At(B), At(C), At(E), At(F));
        s.Clock.Advance(TimeSpan.FromHours(2));
        await s.SetAsync(asAlice, A, WorkflowStatus.Todo);

        var backlog = await s.BacklogAsync(MiScenario.Day(1), MiScenario.Day(2), "day");
        var steps = backlog.Waterfall.Steps;

        Assert.Equal(4, backlog.Waterfall.Start);
        Assert.Equal(backlog.Waterfall.End, backlog.Waterfall.Start + steps.Sum(step => step.Up - step.Down));
        Assert.Equal(backlog.OpenAtEnd, backlog.Waterfall.End);
        Assert.Equal(2, Assert.Single(steps, step => step.Reason == "raised").Up);
        Assert.Equal(1, Assert.Single(steps, step => step.Reason == "resolved").Down);
        Assert.Equal(1, Assert.Single(steps, step => step.Reason == "suppressed").Down);
        Assert.Equal(1, Assert.Single(steps, step => step.Reason == "spol").Down);
        Assert.Equal(1, Assert.Single(steps, step => step.Reason == "no-longer-found").Down);
        Assert.Equal(0, Assert.Single(steps, step => step.Reason == "came-back").Up);
        Assert.Equal(0, Assert.Single(steps, step => step.Reason == "reopened").Up);
        Assert.DoesNotContain(steps, step => step.Reason == "correction");
    }

    [Fact]
    public async Task Holdings_count_once_by_their_status()
    {
        using var s = new MiScenario();
        var (_, asAlice) = await s.PersonAsync("Alice Smith");

        await s.NightAsync(At(A, MiScenario.Rule1), At(A, MiScenario.Rule2), At(B, MiScenario.Rule2));
        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved, rules: MiScenario.Rule1);

        var backlog = await s.BacklogAsync(unit: "holdings");

        Assert.Equal(["todo", "in-progress", "suppressed", "closed"], backlog.States);
        Assert.Equal(2, backlog.ByStatus[^1].Counts["todo"]);
        Assert.Equal(2, backlog.OpenAtEnd);
        Assert.Empty(backlog.ByRule);
    }

    [Fact]
    public async Task Holdings_cannot_be_split_by_rule()
    {
        using var s = new MiScenario();
        await s.RefreshAsync();

        var result = await s.Harness.TryQueryAsync(
            new GetMiBacklogQuery(TestData.Group, RuleId: MiScenario.Rule1, Unit: "holdings"));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error.Code);
    }

    [Fact]
    public async Task Open_work_that_is_too_new_to_list_is_shown_apart()
    {
        using var s = new MiScenario();
        await s.NightAsync(At(A));
        s.Clock.AdvanceDays(20);
        await s.NightAsync(At(A), At(B), At(C));

        var backlog = await s.BacklogAsync();

        Assert.Equal(14, backlog.MinimumIssueAgeDays);
        var fresh = Assert.Single(backlog.Age, band => band.Band == "under-1-week");
        Assert.Equal((2, 2), (fresh.Todo, fresh.TooNewToList));
        var older = Assert.Single(backlog.Age, band => band.Band == "2-4-weeks");
        Assert.Equal((1, 0), (older.Todo, older.TooNewToList));
        Assert.Equal(A, backlog.OldestOpenHoldings[0].Cph);
    }

    [Fact]
    public async Task An_in_progress_holding_nobody_has_touched_for_14_days_is_stalled()
    {
        using var s = new MiScenario();
        var (alice, asAlice) = await s.PersonAsync("Alice Smith");
        var phoned = await s.Harness.AddCodedValueAsync(CodedList.ActionType, "Phoned the keeper");

        await s.NightAsync(At(A), At(B));
        await s.SetAsync(asAlice, A, WorkflowStatus.InProgress);
        await s.SetAsync(asAlice, B, WorkflowStatus.InProgress);
        await s.AssignAsync(asAlice, A, alice);
        s.Clock.AdvanceDays(5);
        await s.ActAsync(asAlice, B, phoned);
        s.Clock.AdvanceDays(10);

        var backlog = await s.BacklogAsync();

        Assert.Equal(1, backlog.Stalled.Count);
        var stalled = Assert.Single(backlog.Stalled.Holdings);
        Assert.Equal((A, alice.Id, "Alice Smith"), (stalled.Cph, stalled.AssignedUserId!.Value, stalled.AssignedTo!));
    }

    [Fact]
    public async Task The_history_from_before_the_first_refresh_is_all_there()
    {
        using var s = new MiScenario();
        var (_, asAlice) = await s.PersonAsync("Alice Smith");

        await s.NightAsync(At(A), At(B));
        for (var night = 1; night <= 10; night++)
        {
            s.Clock.AdvanceDays(1);
            await s.NightAsync(At(A), At(B));
        }

        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved);

        var backlog = await s.BacklogAsync(MiScenario.Day(0), s.Today, "day");

        Assert.Equal(11, backlog.ByStatus.Count);
        Assert.All(backlog.ByStatus.Take(10), point => Assert.Equal(2, point.Counts["todo"]));
        Assert.Equal(1, backlog.ByStatus[^1].Counts["todo"]);
        Assert.Equal(MiScenario.Start, (await s.RefreshAsync()).Status.HistoryFromUtc);
    }

    [Fact]
    public async Task An_issue_the_sweep_made_inactive_and_the_rules_found_again()
    {
        using var s = new MiScenario();
        await s.NightAsync(At(A), At(B));
        s.Clock.AdvanceDays(5);
        await s.NightAsync(At(B));
        s.Clock.AdvanceDays(4);
        await s.NightAsync(At(A), At(B));

        var backlog = await s.BacklogAsync(MiScenario.Day(0), MiScenario.Day(9), "day");
        var active = backlog.ActiveInactive.Select(point => point.Active).ToList();

        Assert.Equal([2, 2, 2, 2, 2, 1, 1, 1, 1, 2], active);
        Assert.Equal(1, backlog.ActiveInactive[5].NoLongerFound);
        Assert.Equal(1, backlog.ActiveInactive[9].CameBack);
    }

    [Fact]
    public async Task The_history_starts_at_the_last_reset()
    {
        using var s = new MiScenario();
        await s.NightAsync(At(A));
        s.Clock.AdvanceDays(3);
        await s.Harness.SendAsync(new ResetDataCommand(TestData.Group, TestData.Alice));
        var resetAt = s.Clock.GetUtcNow();
        s.Clock.AdvanceDays(1);
        await s.NightAsync(At(B));

        var work = await s.WorkAsync();

        Assert.Equal(resetAt, work.Status.HistoryFromUtc);
        Assert.Equal(MiCalendar.UkDay(resetAt), work.Period.From);
        Assert.False(work.Period.HasPrevious);
        Assert.Contains(work.Markers, marker => marker.Kind == "history-start");
    }

    [Fact]
    public async Task A_history_that_does_not_lead_to_the_issue_is_corrected_and_counted()
    {
        using var s = new MiScenario();
        await s.NightAsync(At(A), At(B));
        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.SetWithoutAuditAsync(A, WorkflowStatus.Resolved);

        var refreshed = await s.RefreshAsync();
        var backlog = await s.BacklogAsync();

        Assert.Equal(1, refreshed.Status.Mismatches);
        Assert.Equal([MiScenario.Id(A).Value], refreshed.MismatchedIssueIds);
        Assert.Equal(1, backlog.OpenAtEnd);
        Assert.Equal(1, Assert.Single(backlog.Waterfall.Steps, step => step.Reason == "correction").Down);
    }

    [Fact]
    public async Task A_refresh_with_nothing_new_only_checks()
    {
        using var s = new MiScenario();
        await s.NightAsync(At(A));

        var first = await s.RefreshAsync(force: false);
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await s.RefreshAsync(force: false);
        await s.NightAsync(At(A), At(B));
        var third = await s.RefreshAsync(force: false);

        Assert.True(first.Rebuilt);
        Assert.False(second.Rebuilt);
        Assert.Equal(first.Status.BuiltAtUtc, second.Status.BuiltAtUtc);
        Assert.Equal(s.Clock.GetUtcNow(), second.Status.AsAtUtc);
        Assert.True(third.Rebuilt);
    }

    [Fact]
    public async Task Figures_nobody_has_confirmed_for_a_while_are_stale()
    {
        using var s = new MiScenario();
        await s.NightAsync(At(A));
        await s.RefreshAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(20));

        var status = await s.Harness.QueryAsync(new GetMiStatusQuery(TestData.Group));

        Assert.True(status.Built);
        Assert.True(status.Stale);
    }

    [Fact]
    public async Task The_person_filter_keeps_one_persons_work()
    {
        using var s = new MiScenario();
        var (alice, asAlice) = await s.PersonAsync("Alice Smith");
        var (_, asBob) = await s.PersonAsync("Bob Jones");

        await s.NightAsync(At(A), At(B));
        s.Clock.Advance(TimeSpan.FromHours(8));
        await s.SetAsync(asAlice, A, WorkflowStatus.Resolved);
        await s.SetAsync(asBob, B, WorkflowStatus.Resolved);

        var work = await s.WorkAsync(user: alice.Id);

        Assert.Equal(1, work.Headline.Resolved.Value);
        Assert.Equal(alice.Id, Assert.Single(work.People).UserId);
    }

    [Fact]
    public async Task Ignored_in_the_audit_trail_counts_as_cant_resolve()
    {
        using var s = new MiScenario();
        var (_, asAlice) = await s.PersonAsync("Alice Smith");
        await s.NightAsync(At(A));
        s.Clock.Advance(TimeSpan.FromHours(8));

        var issue = (await s.Harness.Issues.GetAsync(MiScenario.Id(A)))!;
        var reason = new OutcomeReason(Guid.NewGuid(), "Old reason");
        var change = issue.ChangeWorkflowStatus(WorkflowStatus.Nonactionable, s.Clock.GetUtcNow(), reason).Value;
        await s.Harness.Issues.SaveAsync(issue, issue.Version - 1);
        var legacy = new IssueChange(change.Kind, [new AuditChange(nameof(Issue.WorkflowStatus), "todo", "ignored")]);
        await s.Harness.Audit.AppendAsync(AuditEntry.ForIssue(issue.Id, legacy, asAlice, s.Clock.GetUtcNow()));

        var work = await s.WorkAsync();

        Assert.Equal(1, work.Headline.CantResolve.Value);
    }
}
