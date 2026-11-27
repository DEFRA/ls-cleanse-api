using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Holdings;
using Cleanse.Core.Commands.Mi;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Mi;
using Cleanse.Core.Queries.Mi;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Mi;

/// <summary>
/// Drives the real commands, night by night, then asks the dashboard. Every figure a test checks
/// has come through the audit trail, the sweep and the summary, as it does in production.
/// </summary>
internal sealed class MiScenario : IDisposable
{
    public const string Rule1 = "rule1";
    public const string Rule2 = "rule2";

    /// <summary>A Monday, at 2am: after the nightly run, in GMT.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 2, 0, 0, TimeSpan.Zero);

    public MiScenario()
    {
        Harness = new CleanseHarness(Start);
        Engine = new FakeRulesEngine(Harness.Dispatcher, TestData.Group);
    }

    public CleanseHarness Harness { get; }

    public FakeRulesEngine Engine { get; }

    public TestClock Clock => Harness.Clock;

    public DateOnly Today => MiCalendar.UkDay(Clock.GetUtcNow());

    public static DateOnly Day(int offset) => DateOnly.FromDateTime(Start.UtcDateTime).AddDays(offset);

    /// <summary>A known user, and the actor the front end records their changes against.</summary>
    public async Task<(KnownUser User, Actor Actor)> PersonAsync(string fullName)
    {
        var user = await Harness.AddUserAsync(fullName);
        return (user, Actor.Human(user.Id.ToString("D"), user.FullName));
    }

    /// <summary>One night's run that finds exactly these issues; any other active issue goes inactive.</summary>
    public Task NightAsync(params (string Cph, string Rule)[] found) =>
        Engine.RunAsync([.. found.GroupBy(f => f.Rule).Select(group => new FixedRule(group.Key, [.. group.Select(f => f.Cph)]))]);

    public static IssueId Id(string cph, string rule = Rule2) => IssueId.Derive(rule, cph);

    /// <summary>Gives the holding's issues for <paramref name="rules"/> (all of them when none) one new status.</summary>
    public async Task SetAsync(
        Actor actor, string cph, WorkflowStatus status, Guid? reasonId = null, DateTimeOffset? until = null, params string[] rules)
    {
        var issues = await Harness.Issues.GetForHoldingsAsync(TestData.Group, [cph]);
        var chosen = issues.Where(issue => rules.Length == 0 || rules.Contains(issue.RuleId)).ToList();
        await Harness.SendAsync(new UpdateHoldingIssuesCommand(
            new HoldingKey(TestData.Group, cph),
            [.. chosen.Select(issue => new IssueVersion(issue.Id, issue.Version))],
            status,
            actor,
            reasonId,
            status == WorkflowStatus.Suppressed ? until ?? Clock.GetUtcNow().AddDays(30) : null));
    }

    public Task ActAsync(Actor actor, string cph, params Guid[] types) =>
        Harness.SendAsync(new RecordHoldingActionsCommand(new HoldingKey(TestData.Group, cph), types, null, actor));

    public Task NoteAsync(Actor actor, string cph, string text = "Phoned the keeper.") =>
        Harness.SendAsync(new AddHoldingNoteCommand(new HoldingKey(TestData.Group, cph), text, actor));

    public Task AssignAsync(Actor actor, string cph, KnownUser? user) =>
        Harness.SendAsync(new AssignHoldingCommand(new HoldingKey(TestData.Group, cph), user?.Id, actor));

    /// <summary>What a SPOL import does to an issue: its state mirrored, audited against the import.</summary>
    public async Task SpolAsync(Actor processedBy, string cph, WorkflowStatus status, string rule = Rule2)
    {
        var issue = (await Harness.Issues.GetAsync(Id(cph, rule)))!;
        var now = Clock.GetUtcNow();
        var change = issue.ApplyExternalState(new ExternalIssueState { WorkflowStatus = status }, now);
        await Harness.Issues.SaveAsync(issue, issue.Version - 1);
        await Harness.Audit.AppendAsync(AuditEntry.ForIssue(issue.Id, change, processedBy, now, Guid.NewGuid().ToString()));
    }

    /// <summary>A change saved without its audit entry, as a crash between the two writes would leave it.</summary>
    public async Task SetWithoutAuditAsync(string cph, WorkflowStatus status, string rule = Rule2)
    {
        var issue = (await Harness.Issues.GetAsync(Id(cph, rule)))!;
        Assert.True(issue.ChangeWorkflowStatus(status, Clock.GetUtcNow()).IsSuccess);
        await Harness.Issues.SaveAsync(issue, issue.Version - 1);
    }

    public Task<MiRefreshResultDto> RefreshAsync(bool force = true) =>
        Harness.SendAsync(new RefreshMiCommand(TestData.Group, force));

    public async Task<MiWorkDto> WorkAsync(
        DateOnly? from = null, DateOnly? to = null, string? interval = null, string? rule = null, Guid? user = null)
    {
        await RefreshAsync();
        return await Harness.QueryAsync(new GetMiWorkQuery(TestData.Group, from, to, interval, rule, user));
    }

    public async Task<MiBacklogDto> BacklogAsync(
        DateOnly? from = null, DateOnly? to = null, string? interval = null, string? rule = null, string? unit = null)
    {
        await RefreshAsync();
        return await Harness.QueryAsync(new GetMiBacklogQuery(TestData.Group, from, to, interval, rule, unit));
    }

    public void Dispose() => Harness.Dispose();

    private sealed class FixedRule(string ruleId, IReadOnlyList<string> cphs) : ICleanseRule
    {
        public string RuleId => ruleId;

        public IEnumerable<IssueDetection> Evaluate() =>
            cphs.Select(cph => new IssueDetection(
                IssueId.Derive(ruleId, cph), ruleId, new Dictionary<string, string> { ["cph"] = cph }));
    }
}
