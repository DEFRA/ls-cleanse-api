using Cleanse.Core;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.CodedValues;
using Cleanse.Core.Commands.Holdings;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Contracts;
using Cleanse.Core.DependencyInjection;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Holdings;
using Cleanse.Core.Queries.Issues;
using Cleanse.Core.Test.Support;
using Cleanse.Storage.InMemory;
using Cleanse.Storage.Sqlite;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Cleanse.Test;

/// <summary>
/// Core wired to each real storage provider in turn. The per-provider suites prove each store
/// honours the contract in isolation; this proves the use cases built on top behave identically
/// whichever one is plugged in, which is the claim the whole abstraction rests on.
/// </summary>
public sealed class CrossProviderScenarioTests : IAsyncDisposable
{
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<CleanseSqliteDatabase> _databases = [];

    public static TheoryData<string> Providers => new("InMemory", "Sqlite");

    private async Task<(ICleanseDispatcher Dispatcher, TestClock Clock)> BuildAsync(string provider)
    {
        var (dispatcher, clock, _) = await BuildWithServicesAsync(provider);
        return (dispatcher, clock);
    }

    private async Task<(ICleanseDispatcher Dispatcher, TestClock Clock, IServiceProvider Services)> BuildWithServicesAsync(
        string provider)
    {
        var clock = new TestClock(TestData.Night1);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddCleanseCore();

        if (provider == "Sqlite")
        {
            services.AddCleanseSqliteStorage(options => options.InMemoryName = $"x-{Guid.NewGuid():N}");
        }
        else
        {
            services.AddCleanseInMemoryStorage();
        }

        var built = services.BuildServiceProvider();
        _providers.Add(built);

        if (provider == "Sqlite")
        {
            var database = built.GetRequiredService<CleanseSqliteDatabase>();
            await database.InitialiseAsync();
            _databases.Add(database);
        }

        return (built.GetRequiredService<ICleanseDispatcher>(), clock, built);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_nightly_cycle_behaves_identically_on_every_provider(string provider)
    {
        var (dispatcher, clock) = await BuildAsync(provider);
        var engine = new FakeRulesEngine(dispatcher, TestData.Group);

        var source = new[]
        {
            new SourceHolding("12/345/0001", "J Smith"),
            new SourceHolding("12/345/0002", "A Jones"),
            new SourceHolding("12/345/0003", "P Patel")
        };

        ICleanseRule[] RulesAgainst(params SourceHolding[] target) =>
            [new HoldingMissingInTargetRule(source, target), new KeeperMismatchRule(source, target)];

        var night1 = await engine.RunAsync(RulesAgainst(source[0]));
        Assert.Equal(2, night1.IssuesCreated);
        Assert.Equal(0, night1.IssuesDeactivated);

        clock.AdvanceDays(1);
        var night2 = await engine.RunAsync(RulesAgainst(source[0], source[1]));
        Assert.Equal(0, night2.IssuesCreated);
        Assert.Equal(1, night2.IssuesDeactivated);

        clock.AdvanceDays(1);
        var night3 = await engine.RunAsync(RulesAgainst(source[0]));
        Assert.Equal(1, night3.IssuesReactivated);

        var active = await dispatcher.QueryAsync(new SearchIssuesQuery(new IssueFilter
        {
            GroupId = TestData.Group,
            Status = IssueStatus.Active
        }));

        Assert.True(active.IsSuccess);
        Assert.Equal(2, active.Value.TotalCount);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Human_triage_survives_re_detection_on_every_provider(string provider)
    {
        var (dispatcher, clock, services) = await BuildWithServicesAsync(provider);
        var id = TestData.Key("12/345/9999");

        var run = await dispatcher.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        Assert.True(run.IsSuccess);

        await dispatcher.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("12/345/9999")], Actor.System));

        var alice = await AddUserAsync(services, "Alice Brown");
        await dispatcher.SendAsync(new AssignHoldingCommand(new HoldingKey(TestData.Group, "12/345/9999"), alice.Id, TestData.Alice));
        await dispatcher.SendAsync(new SetIssueWorkflowStatusCommand(id, WorkflowStatus.Resolved, TestData.Alice));
        await dispatcher.SendAsync(new CompleteRunCommand("run-1", Actor.System));

        clock.AdvanceDays(1);
        await dispatcher.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-2"));
        await dispatcher.SendAsync(new UpsertDetectionsCommand(
            "run-2", [TestData.Detection("12/345/9999")], Actor.System));
        await dispatcher.SendAsync(new CompleteRunCommand("run-2", Actor.System));

        var issue = await dispatcher.QueryAsync(new GetIssueQuery(id));

        Assert.True(issue.IsSuccess);
        Assert.Equal("active", issue.Value.Status);
        Assert.Equal("resolved", issue.Value.WorkflowStatus);
        Assert.Equal("Alice Brown", issue.Value.AssignedTo);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_holding_is_worked_as_one_on_every_provider(string provider)
    {
        var (dispatcher, clock, services) = await BuildWithServicesAsync(provider);
        const string cph = "12/345/7777";
        var key = new HoldingKey(TestData.Group, cph);

        await dispatcher.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await dispatcher.SendAsync(new UpsertDetectionsCommand(
            "run-1",
            [TestData.Detection(cph, TestData.Rule2), TestData.Detection(cph, TestData.Rule5), TestData.Detection("12/345/0000")],
            Actor.System));

        var bob = await AddUserAsync(services, "Bob Jones");
        var assigned = await dispatcher.SendAsync(new AssignHoldingCommand(key, bob.Id, TestData.Alice, ExpectedVersion: 0));
        Assert.True(assigned.IsSuccess, $"{assigned.Error}");
        Assert.Equal(1, assigned.Value.Version);

        var phoned = (await dispatcher.SendAsync(new AddCodedValueCommand(CodedList.ActionType, "Phoned the keeper", TestData.Alice))).Value;
        var died = (await dispatcher.SendAsync(new AddCodedValueCommand(CodedList.CantResolveReason, "Keeper has died", TestData.Alice))).Value;
        var duplicate = await dispatcher.SendAsync(new AddCodedValueCommand(CodedList.ActionType, " phoned THE keeper ", TestData.Alice));
        Assert.Equal(ErrorCodes.CodedValueDuplicateName, duplicate.Error!.Code);

        clock.Advance(TimeSpan.FromMinutes(5));
        var recorded = await dispatcher.SendAsync(new RecordHoldingActionsCommand(key, [phoned.Id], "No answer", TestData.Alice));
        Assert.True(recorded.IsSuccess, $"{recorded.Error}");

        clock.Advance(TimeSpan.FromMinutes(5));
        var shown = (await dispatcher.QueryAsync(new GetHoldingQuery(key))).Value;
        Assert.Equal("todo", shown.Status);
        var versions = shown.Issues.Select(i => new IssueVersion(IssueId.From(i.Id), i.Version)).ToList();
        var updated = await dispatcher.SendAsync(new UpdateHoldingIssuesCommand(
            key, versions, WorkflowStatus.Nonactionable, TestData.Alice, ReasonId: died.Id, Note: "Confirmed with APHA"));
        Assert.True(updated.IsSuccess, $"{updated.Error}");

        // The page it was shown on is now stale, so a second change is refused and changes nothing.
        var stale = await dispatcher.SendAsync(new UpdateHoldingIssuesCommand(key, versions, WorkflowStatus.Todo, TestData.Alice));
        Assert.Equal(ErrorCodes.IssueConcurrencyConflict, stale.Error!.Code);

        var after = (await dispatcher.QueryAsync(new GetHoldingQuery(key))).Value;
        Assert.Equal("closed", after.Status);
        Assert.Equal(bob.Id, after.AssignedUserId);
        Assert.Equal(2, after.Issues.Count);
        Assert.All(after.Issues, issue =>
        {
            Assert.Equal("nonactionable", issue.WorkflowStatus);
            Assert.Equal("Keeper has died", issue.OutcomeReason);
        });
        Assert.Equal(["Confirmed with APHA", "No answer"], after.Notes.Select(n => n.Text).ToArray());
        Assert.Equal("Recorded: Phoned the keeper", after.Notes[1].Context);
        Assert.Contains(after.History, e => e.Kind == HoldingEventKinds.ActionsRecorded && e.Actions.Contains("Phoned the keeper"));
        Assert.Contains(after.History, e => e.Kind == HoldingEventKinds.AssignmentChanged && e.AssignedTo == "Bob Jones");
        Assert.Equal(2, after.History.Count(e => e.Kind == HoldingEventKinds.StatusChanged && e.OutcomeReason == "Keeper has died"));

        var bobs = await dispatcher.QueryAsync(new SearchIssuesQuery(new IssueFilter { AssignedUserId = bob.Id }));
        var unassigned = await dispatcher.QueryAsync(new SearchIssuesQuery(new IssueFilter { Unassigned = true }));
        Assert.Equal(2, bobs.Value.TotalCount);
        Assert.Equal(TestData.Key("12/345/0000").Value, Assert.Single(unassigned.Value.Items).Id);
    }

    private static async Task<KnownUser> AddUserAsync(IServiceProvider services, string fullName)
    {
        var email = $"{fullName.Replace(' ', '.').ToLowerInvariant()}@example.test";
        var registered = await services.GetRequiredService<IUserStore>().RegisterAsync([KnownUser.Unclaimed(email, fullName)]);
        return registered[0].User;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_lapsed_suppression_expires_on_every_provider(string provider)
    {
        var (dispatcher, clock) = await BuildAsync(provider);
        var id = TestData.Key("12/345/8888");

        await dispatcher.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await dispatcher.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("12/345/8888")], Actor.System));

        await dispatcher.SendAsync(new SuppressIssueCommand(id, TestData.Night1.AddDays(7), TestData.Alice));

        clock.AdvanceDays(3);
        var early = await dispatcher.SendAsync(new ExpireSuppressionsCommand(Actor.System));
        Assert.Equal(0, early.Value.Expired);

        clock.AdvanceDays(5);
        var due = await dispatcher.SendAsync(new ExpireSuppressionsCommand(Actor.System));
        Assert.Equal(1, due.Value.Expired);

        var issue = await dispatcher.QueryAsync(new GetIssueQuery(id));
        Assert.Equal("todo", issue.Value.WorkflowStatus);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var database in _databases)
        {
            await database.DisposeAsync();
        }

        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }
    }
}
