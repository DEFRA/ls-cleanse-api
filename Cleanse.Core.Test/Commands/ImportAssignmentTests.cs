using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Imports;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;
using Cleanse.Core.Test.Support;
using Cleanse.Storage.InMemory;
using NSubstitute;

namespace Cleanse.Core.Test.Commands;

public sealed class ImportAssignmentTests
{
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private const string Client = "22222222-2222-4222-8222-222222222222";

    private static KnownUser Login(string name) => new(Guid.NewGuid(), "issuer", Guid.NewGuid().ToString(),
        Tenant, Guid.NewGuid().ToString(), Client, name, TestData.Night1, TestData.Night1);

    private static ApplyImportBatchCommandHandler Handler(
        IImportJobStore imports, IIssueStore issues, IUserStore users, IHoldingStore? holdings = null, IAuditStore? audit = null) =>
        new(imports, issues, holdings ?? new InMemoryHoldingStore(), new InMemoryIssueHistoryStore(),
            audit ?? new InMemoryAuditStore(), users, TimeProvider.System);

    private static HoldingKey KeyOf(Issue issue) => HoldingKey.For(issue)!.Value;

    private static ExternalIssueRow Row(Issue issue, string? assignedTo) =>
        new() { RowNumber = 2, IssueId = issue.Id, HasAssignee = true, AssignedTo = assignedTo };

    private static async Task<ImportJob> JobAsync(IImportJobStore imports)
    {
        var job = ImportJob.Create(Guid.NewGuid(), TestData.Group, "extract.csv", "imports/extract.csv",
            "service", 1, TestData.Night1, Tenant, Client);
        Assert.True(await imports.TryCreateAsync(job));
        return (await imports.LeaseNextPendingAsync(TestData.Night1))!;
    }

    [Fact]
    public async Task Names_are_trimmed_on_both_sides_without_fuzzy_matching_or_unnamed_candidates()
    {
        var users = new InMemoryUserStore();
        var known = await users.UpsertLoginAsync(Login("  Alex Example  "), []);
        await users.UpsertLoginAsync(Login(" "), []);
        var imports = new InMemoryImportJobStore();
        var issues = new InMemoryIssueStore();
        var holdings = new InMemoryHoldingStore();
        var issue = TestData.NewIssue("match");
        await issues.SaveAsync(issue, null);
        var job = await JobAsync(imports);
        var handler = Handler(imports, issues, users, holdings);
        var row = new ExternalIssueRow { RowNumber = 2, IssueId = issue.Id, HasAssignee = true, AssignedTo = " aLeX eXaMpLe " };

        Assert.True((await handler.HandleAsync(new ApplyImportBatchCommand(job.Id, [row], Actor.System))).IsSuccess);
        Assert.Equal(known.Id, (await holdings.GetAsync(KeyOf(issue)))!.AssignedUserId);
        Assert.True((await handler.HandleAsync(new ApplyImportBatchCommand(job.Id,
            [row with { AssignedTo = "Alex" }], Actor.System))).IsSuccess);
        var updated = (await holdings.GetAsync(KeyOf(issue)))!;
        Assert.Null(updated.AssignedUserId);
        Assert.Null(updated.AssignedUserFullName);
        Assert.Equal("Alex", updated.AssignedTo);
        Assert.Null(updated.AssignmentDetachedAtUtc);
        Assert.Equal(ImportRejectionReasons.AssigneeUnmatched,
            Assert.Single((await imports.GetRejectionsAsync(job.Id, PageRequest.Default)).Items).Reason);
    }

    [Fact]
    public async Task Name_collisions_on_later_directory_pages_are_not_arbitrarily_assigned()
    {
        var users = new InMemoryUserStore();
        for (var index = 0; index < PageRequest.MaxPageSize - 1; index++)
        {
            await users.UpsertLoginAsync(Login($"A {index:D4}") with
            {
                Id = Guid.Parse($"11111111-1111-4111-8111-{index:D12}")
            }, []);
        }
        await users.UpsertLoginAsync(Login("Z Match") with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") }, []);
        await users.UpsertLoginAsync(Login("Z Match") with { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") }, []);
        var imports = new InMemoryImportJobStore();
        var issues = new InMemoryIssueStore();
        var holdings = new InMemoryHoldingStore();
        var issue = TestData.NewIssue("collision");
        await issues.SaveAsync(issue, null);
        var job = await JobAsync(imports);
        var handler = Handler(imports, issues, users, holdings);

        var result = await handler.HandleAsync(new ApplyImportBatchCommand(job.Id,
            [new ExternalIssueRow { RowNumber = 2, IssueId = issue.Id, HasAssignee = true, AssignedTo = "z MATCH" }],
            Actor.System));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Matched);
        Assert.Equal(0, result.Value.Unmatched);
        var updated = (await holdings.GetAsync(KeyOf(issue)))!;
        Assert.Null(updated.AssignedUserId);
        Assert.Equal("z MATCH", updated.AssignedTo);
        Assert.Equal(ImportRejectionReasons.AssigneeAmbiguous,
            Assert.Single((await imports.GetRejectionsAsync(job.Id, PageRequest.Default)).Items).Reason);
    }

    [Fact]
    public async Task Concurrent_issue_edits_are_not_overwritten_or_audited_as_applied()
    {
        var imports = new InMemoryImportJobStore();
        var job = await JobAsync(imports);
        var issue = TestData.NewIssue("concurrent");
        var issues = Substitute.For<IIssueStore>();
        issues.GetManyAsync(Arg.Any<IReadOnlyCollection<IssueId>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { issue });
        issues.SaveAsync(Arg.Any<Issue>(), issue.Version, Arg.Any<CancellationToken>()).Returns(false);
        var audit = new InMemoryAuditStore();
        var holdings = new InMemoryHoldingStore();
        var handler = Handler(imports, issues, new InMemoryUserStore(), holdings, audit);

        var result = await handler.HandleAsync(new ApplyImportBatchCommand(job.Id,
        [
            new ExternalIssueRow
            {
                RowNumber = 2, IssueId = issue.Id, HasAssignee = true, AssignedTo = "Legacy", ExternalNotes = "From SPOL"
            }
        ], Actor.System));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.IssueConcurrencyConflict, result.Error.Code);
        await issues.DidNotReceive().SaveBatchAsync(Arg.Any<IReadOnlyCollection<Issue>>(), Arg.Any<CancellationToken>());
        Assert.Empty((await audit.GetForIssueAsync(issue.Id, PageRequest.Default)).Items);
        Assert.Null(await holdings.GetAsync(KeyOf(issue)));
        Assert.Equal(0, (await imports.GetAsync(job.Id))!.Counters.ProcessedRows);
    }

    [Fact]
    public async Task An_email_matches_its_user_whatever_the_case_and_whoever_registered_them()
    {
        var users = new InMemoryUserStore();
        var registered = (await users.RegisterAsync([KnownUser.Unclaimed("kyle.lloyd2@apha.gov.uk", "Kyle Lloyd")]))[0].User;
        var imports = new InMemoryImportJobStore();
        var issues = new InMemoryIssueStore();
        var holdings = new InMemoryHoldingStore();
        var audit = new InMemoryAuditStore();
        var issue = TestData.NewIssue("email");
        await issues.SaveAsync(issue, null);
        var job = await JobAsync(imports);

        Assert.True((await Handler(imports, issues, users, holdings, audit).HandleAsync(new ApplyImportBatchCommand(job.Id,
            [Row(issue, " Kyle.Lloyd2@APHA.gov.uk ")], Actor.System))).IsSuccess);

        var updated = (await holdings.GetAsync(KeyOf(issue)))!;
        Assert.Equal(registered.Id, updated.AssignedUserId);
        Assert.Equal("Kyle Lloyd", updated.AssignedTo);
        Assert.Null(updated.AssignmentDetachedAtUtc);
        Assert.Equal(1, (await users.SearchAsync(PageRequest.Default)).TotalCount);
        Assert.Empty((await imports.GetRejectionsAsync(job.Id, PageRequest.Default)).Items);

        // Audited against the holding, and attributed to the import run.
        var entry = Assert.Single((await audit.GetForSubjectAsync(AuditSubjects.Holding(KeyOf(issue)), PageRequest.Default)).Items);
        Assert.Equal(AuditEventKind.HoldingAssigned, entry.Kind);
        Assert.Equal(job.Id.ToString(), entry.RunId);
    }

    [Fact]
    public async Task An_email_nobody_has_becomes_a_user_who_has_not_signed_in()
    {
        var users = new InMemoryUserStore();
        var imports = new InMemoryImportJobStore();
        var issues = new InMemoryIssueStore();
        var holdings = new InMemoryHoldingStore();
        var first = TestData.NewIssue("new-user-1");
        var second = TestData.NewIssue("new-user-2");
        await issues.SaveBatchAsync([first, second]);
        var job = await JobAsync(imports);

        Assert.True((await Handler(imports, issues, users, holdings).HandleAsync(new ApplyImportBatchCommand(job.Id,
            [Row(first, "StJohn.Hawkins1@defra.gov.uk"), Row(second, "stjohn.hawkins1@defra.gov.uk") with { RowNumber = 3 }],
            Actor.System))).IsSuccess);

        var created = Assert.Single((await users.SearchAsync(PageRequest.Default)).Items);
        Assert.Equal("StJohn Hawkins", created.FullName);
        Assert.Equal("stjohn.hawkins1@defra.gov.uk", created.Email);
        Assert.Null(created.Subject);
        Assert.Equal(created.Id, (await holdings.GetAsync(KeyOf(first)))!.AssignedUserId);
        Assert.Equal(created.Id, (await holdings.GetAsync(KeyOf(second)))!.AssignedUserId);
    }

    [Fact]
    public async Task An_assignment_changed_in_Cleanse_is_left_alone_by_later_imports()
    {
        var users = new InMemoryUserStore();
        var chosen = await users.UpsertLoginAsync(Login("Chosen Person"), []);
        var someone = await users.UpsertLoginAsync(Login("Someone"), []);
        var imports = new InMemoryImportJobStore();
        var issues = new InMemoryIssueStore();
        var holdings = new InMemoryHoldingStore();
        var detached = TestData.NewIssue("detached");
        var unassigned = TestData.NewIssue("unassigned-in-cleanse");
        var attached = TestData.NewIssue("attached");
        await issues.SaveBatchAsync([detached, unassigned, attached]);

        var detachedHolding = Holding.New(KeyOf(detached), TestData.Night1);
        detachedHolding.AssignKnownUser(chosen, TestData.Night1);
        Assert.True(await holdings.SaveAsync(detachedHolding, 0));
        var unassignedHolding = Holding.New(KeyOf(unassigned), TestData.Night1);
        unassignedHolding.AssignKnownUser(someone, TestData.Night1);
        unassignedHolding.AssignKnownUser(null, TestData.Night1);
        Assert.True(await holdings.SaveAsync(unassignedHolding, 0));
        var job = await JobAsync(imports);

        Assert.True((await Handler(imports, issues, users, holdings).HandleAsync(new ApplyImportBatchCommand(job.Id,
        [
            Row(detached, "new.person@apha.gov.uk"),
            Row(unassigned, "Unknown Name") with { RowNumber = 3 },
            Row(attached, "Chosen Person") with { RowNumber = 4 }
        ], Actor.System))).IsSuccess);

        Assert.Equal(chosen.Id, (await holdings.GetAsync(KeyOf(detached)))!.AssignedUserId);
        var stillUnassigned = (await holdings.GetAsync(KeyOf(unassigned)))!;
        Assert.Null(stillUnassigned.AssignedTo);
        Assert.Null(stillUnassigned.AssignedUserId);
        Assert.Equal(chosen.Id, (await holdings.GetAsync(KeyOf(attached)))!.AssignedUserId);
        Assert.Equal(2, (await users.SearchAsync(PageRequest.Default)).TotalCount);
        Assert.Empty((await imports.GetRejectionsAsync(job.Id, PageRequest.Default)).Items);
    }

    [Fact]
    public async Task A_blank_assignee_unassigns_a_holding_SPOL_still_owns()
    {
        var users = new InMemoryUserStore();
        var imports = new InMemoryImportJobStore();
        var issues = new InMemoryIssueStore();
        var holdings = new InMemoryHoldingStore();
        var issue = TestData.NewIssue("blank");
        await issues.SaveAsync(issue, null);
        var holding = Holding.New(KeyOf(issue), TestData.Night1);
        holding.SyncAssignment(null, "Former Assignee", TestData.Night1);
        Assert.True(await holdings.SaveAsync(holding, 0));
        var job = await JobAsync(imports);

        Assert.True((await Handler(imports, issues, users, holdings).HandleAsync(new ApplyImportBatchCommand(job.Id,
            [Row(issue, "  ")], Actor.System))).IsSuccess);

        Assert.Null((await holdings.GetAsync(KeyOf(issue)))!.AssignedTo);
    }

    [Fact]
    public async Task A_row_without_the_assignee_leaves_the_holding_alone()
    {
        var users = new InMemoryUserStore();
        var imports = new InMemoryImportJobStore();
        var issues = new InMemoryIssueStore();
        var holdings = new InMemoryHoldingStore();
        var issue = TestData.NewIssue("carried-elsewhere");
        await issues.SaveAsync(issue, null);
        var holding = Holding.New(KeyOf(issue), TestData.Night1);
        holding.SyncAssignment(null, "Kept", TestData.Night1);
        Assert.True(await holdings.SaveAsync(holding, 0));
        var job = await JobAsync(imports);

        Assert.True((await Handler(imports, issues, users, holdings).HandleAsync(new ApplyImportBatchCommand(job.Id,
            [Row(issue, "Someone Else") with { HasAssignee = false }], Actor.System))).IsSuccess);

        Assert.Equal("Kept", (await holdings.GetAsync(KeyOf(issue)))!.AssignedTo);
    }
}
