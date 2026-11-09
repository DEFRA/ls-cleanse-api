using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Test.Storage;

public abstract class UserStoreContractTests
{
    protected abstract IUserStore CreateStore();
    protected abstract IIssueStore CreateIssueStore();
    protected abstract IDataResetStore CreateResetStore();

    protected static KnownUser Login(
        string subject = "subject", string client = "client", string tenant = "tenant", string? email = null) =>
        new(Guid.NewGuid(), $"https://login.microsoftonline.com/{tenant}/v2.0", subject, tenant, $"object-{subject}",
            client, "Alex Example", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, email);

    private static KnownUser Registered(string email, string name = "Registered Person") =>
        KnownUser.Unclaimed(email, name);

    [Fact]
    public async Task Assignment_filters_distinguish_stable_legacy_blank_and_unassigned_before_paging()
    {
        var user = await CreateStore().UpsertLoginAsync(Login(), []);
        var stable = Support.TestData.NewIssue("filter-stable");
        stable.AssignKnownUser(user, DateTimeOffset.UnixEpoch);
        var legacy = Support.TestData.NewIssue("filter-legacy");
        legacy.AssignTo("Legacy Person", DateTimeOffset.UnixEpoch);
        var empty = Support.TestData.NewIssue("filter-empty");
        var whitespace = Issue.FromSnapshot(Support.TestData.NewIssue("filter-space").ToSnapshot() with
        {
            AssignedTo = " \t\r\n\v\f\u00a0\u2003"
        });
        var stableWithoutText = Issue.FromSnapshot(Support.TestData.NewIssue("filter-id-only").ToSnapshot() with
        {
            AssignedUserId = user.Id
        });
        var store = CreateIssueStore();
        await store.SaveBatchAsync([stable, legacy, empty, whitespace, stableWithoutText]);

        var known = await store.SearchAsync(new IssueFilter { AssignedUserId = user.Id, Page = new PageRequest(1, 1) });
        Assert.Equal(2, known.TotalCount);
        Assert.Single(known.Items);
        var unassigned = await store.SearchAsync(new IssueFilter { Unassigned = true });
        Assert.Equal(2, unassigned.TotalCount);
        Assert.All(unassigned.Items, issue => Assert.Null(issue.AssignedUserId));
        Assert.DoesNotContain(unassigned.Items, issue => issue.Id == legacy.Id);
        Assert.Equal(3, (await store.SearchAsync(new IssueFilter { Unassigned = false })).TotalCount);
        Assert.Empty((await store.SearchAsync(new IssueFilter { AssignedUserId = Guid.NewGuid() })).Items);
    }

    [Fact]
    public async Task Repeated_login_keeps_identity_and_first_timestamp_but_refreshes_profile()
    {
        var users = CreateStore();
        var first = await users.UpsertLoginAsync(Login(), []);
        var later = await users.UpsertLoginAsync(Login() with
        {
            FullName = "Alex Renamed",
            FirstLoggedInAtUtc = DateTimeOffset.UnixEpoch.AddDays(1),
            LastLoggedInAtUtc = DateTimeOffset.UnixEpoch.AddDays(1)
        }, []);

        Assert.Equal(first.Id, later.Id);
        Assert.Equal(first.FirstLoggedInAtUtc, later.FirstLoggedInAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1), later.LastLoggedInAtUtc);
        Assert.Equal("Alex Renamed", later.FullName);
        var stale = await users.UpsertLoginAsync(Login(), []);
        Assert.Equal(later.LastLoggedInAtUtc, stale.LastLoggedInAtUtc);
        Assert.Equal(later.Id, stale.Id);
    }

    [Fact]
    public async Task Different_people_are_different_users_whatever_their_names()
    {
        var users = CreateStore();
        var first = await users.UpsertLoginAsync(Login(), []);
        var subject = await users.UpsertLoginAsync(Login("Subject"), []);
        var tenant = await users.UpsertLoginAsync(Login(tenant: "other-tenant"), []);

        Assert.Equal(3, new[] { first.Id, subject.Id, tenant.Id }.Distinct().Count());
        Assert.Equal(3, (await users.SearchAsync(PageRequest.Default)).TotalCount);
        Assert.Equal(first, await users.GetAsync(first.Id));
        Assert.Null(await users.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_same_person_through_a_new_app_registration_keeps_their_user()
    {
        var users = CreateStore();
        var first = await users.UpsertLoginAsync(Login(), []);
        var moved = await users.UpsertLoginAsync(Login() with
        {
            Issuer = "https://login.microsoftonline.com/tenant/v2.0/other",
            Subject = "pairwise-subject-for-the-new-app",
            ClientId = "new-client",
            LastLoggedInAtUtc = DateTimeOffset.UnixEpoch.AddDays(2)
        }, []);

        Assert.Equal(first.Id, moved.Id);
        Assert.Equal("new-client", moved.ClientId);
        Assert.Equal("pairwise-subject-for-the-new-app", moved.Subject);
        Assert.Equal(first.FirstLoggedInAtUtc, moved.FirstLoggedInAtUtc);
        Assert.Equal(1, (await users.SearchAsync(PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task Directory_includes_everyone_ordered_by_name_and_pages_without_duplicates()
    {
        var users = CreateStore();
        await users.RegisterAsync([Registered("zed@example.test", "zed Last"), Registered("bea@example.test", "Bea Middle")]);
        for (var i = 0; i < 3; i++)
        {
            await users.UpsertLoginAsync(Login($"s{i}") with { FullName = $"Alex {3 - i}" }, []);
        }

        var first = await users.SearchAsync(new PageRequest(1, 2));
        var second = await users.SearchAsync(new PageRequest(2, 2));
        var third = await users.SearchAsync(new PageRequest(3, 2));
        Assert.Equal(5, first.TotalCount);
        Assert.Equal(3, first.TotalPages);
        var names = first.Items.Concat(second.Items).Concat(third.Items).Select(u => u.FullName).ToArray();
        Assert.Equal(["Alex 1", "Alex 2", "Alex 3", "Bea Middle", "zed Last"], names);
        Assert.Empty((await users.SearchAsync(new PageRequest(4, 2))).Items);
    }

    [Fact]
    public async Task Registered_users_have_not_signed_in_and_a_known_email_is_not_duplicated()
    {
        var users = CreateStore();
        var first = await users.RegisterAsync([Registered("a@example.test"), Registered("b@example.test")]);
        var second = await users.RegisterAsync([Registered("b@example.test", "Another Name"), Registered("c@example.test")]);

        Assert.All(first, registration => Assert.True(registration.Created));
        Assert.False(second[0].Created);
        Assert.Equal(first[1].User, second[0].User);
        Assert.True(second[1].Created);
        var stored = (await users.GetAsync(first[0].User.Id))!;
        Assert.Equal("a@example.test", stored.Email);
        Assert.Null(stored.Subject);
        Assert.Null(stored.LastLoggedInAtUtc);
        Assert.Equal(3, (await users.SearchAsync(PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task A_first_sign_in_claims_the_registered_user_and_keeps_their_assignments()
    {
        var users = CreateStore();
        var registered = (await users.RegisterAsync([Registered("alex.example@example.test", "Alex Example")]))[0].User;
        var issue = Issue.FromDetection(new IssueDetection(IssueId.From("rule:claimed"), "rule"), "g", "r",
            DateTimeOffset.UnixEpoch);
        issue.AssignKnownUser(registered, DateTimeOffset.UnixEpoch);
        await CreateIssueStore().SaveAsync(issue, null);

        var claimed = await users.UpsertLoginAsync(
            Login() with { FullName = "Alex Entra", Email = "alex.example@example.test" },
            ["alex.example@example.test"]);

        Assert.Equal(registered.Id, claimed.Id);
        Assert.Equal("subject", claimed.Subject);
        Assert.Equal("Alex Entra", claimed.FullName);
        Assert.Equal("alex.example@example.test", claimed.Email);
        Assert.Equal(DateTimeOffset.UnixEpoch, claimed.FirstLoggedInAtUtc);
        Assert.Equal(1, (await users.SearchAsync(PageRequest.Default)).TotalCount);
        Assert.Equal(registered.Id, (await CreateIssueStore().GetAsync(issue.Id))!.AssignedUserId);
    }

    [Fact]
    public async Task Any_claim_email_can_match_and_the_registered_address_is_kept()
    {
        var users = CreateStore();
        var registered = (await users.RegisterAsync([Registered("kyle.lloyd2@example.test")]))[0].User;

        var claimed = await users.UpsertLoginAsync(Login() with { Email = "kyle.lloyd@example.test" },
            ["kyle.lloyd@example.test", "kyle.lloyd2@example.test"]);

        Assert.Equal(registered.Id, claimed.Id);
        Assert.Equal("kyle.lloyd2@example.test", claimed.Email);
    }

    [Fact]
    public async Task A_signed_in_user_is_never_claimed_again_and_an_email_belongs_to_one_user()
    {
        var users = CreateStore();
        await users.RegisterAsync([Registered("shared@example.test")]);
        var owner = await users.UpsertLoginAsync(Login("owner") with { Email = "shared@example.test" }, ["shared@example.test"]);
        var other = await users.UpsertLoginAsync(Login("other") with { Email = "shared@example.test" }, ["shared@example.test"]);

        Assert.NotEqual(owner.Id, other.Id);
        Assert.Equal("shared@example.test", owner.Email);
        Assert.Null(other.Email);
    }

    [Fact]
    public async Task A_new_user_records_its_email_and_a_later_sign_in_fills_a_missing_one()
    {
        var users = CreateStore();
        var first = await users.UpsertLoginAsync(Login("first") with { Email = "first@example.test" }, ["first@example.test"]);
        var withoutEmail = await users.UpsertLoginAsync(Login("second"), []);
        var filled = await users.UpsertLoginAsync(Login("second") with { Email = "second@example.test" }, ["second@example.test"]);

        Assert.Equal("first@example.test", first.Email);
        Assert.Null(withoutEmail.Email);
        Assert.Equal(withoutEmail.Id, filled.Id);
        Assert.Equal("second@example.test", filled.Email);
    }

    [Fact]
    public async Task Concurrent_first_logins_converge_to_one_id()
    {
        var stores = Enumerable.Range(0, 12).Select(_ => CreateStore()).ToArray();
        var tasks = stores.Select(store => Task.Run(
            () => store.UpsertLoginAsync(Login(), []))).ToArray();
        var users = await Task.WhenAll(tasks);
        Assert.Single(users.Select(u => u.Id).Distinct());
        Assert.Equal(1, (await CreateStore().SearchAsync(PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task Concurrent_first_sign_ins_claim_the_registered_user_once()
    {
        var registered = (await CreateStore().RegisterAsync([Registered("race@example.test")]))[0].User;
        var stores = Enumerable.Range(0, 8).Select(_ => CreateStore()).ToArray();
        var users = await Task.WhenAll(stores.Select(store => Task.Run(
            () => store.UpsertLoginAsync(Login() with { Email = "race@example.test" }, ["race@example.test"]))));

        Assert.All(users, user => Assert.Equal(registered.Id, user.Id));
        Assert.Equal(1, (await CreateStore().SearchAsync(PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task Stable_assignment_round_trips_single_and_batch_saves_and_survives_detection()
    {
        var user = await CreateStore().UpsertLoginAsync(Login(), []);
        var issues = CreateIssueStore();
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var issue = Issue.FromDetection(new IssueDetection(IssueId.From("rule:user-test"), "rule"), "g", "r", now);
        issue.AssignKnownUser(user, now);
        Assert.True(await issues.SaveAsync(issue, null));
        var reloaded = await issues.GetAsync(issue.Id);
        Assert.Equal(user.Id, reloaded!.AssignedUserId);
        Assert.Equal(user.FullName, reloaded.AssignedUserFullName);
        reloaded.RecordDetection("r2", IssueContext.Empty, now);

        // More than the PostgreSQL harness COPY threshold, to cover both batch write shapes.
        var batch = Enumerable.Range(0, 5).Select(i =>
        {
            var item = Issue.FromDetection(
                new IssueDetection(IssueId.From($"rule:user-{i}"), "rule"), "g", "r2", now);
            item.AssignKnownUser(user, now);
            return item;
        }).Append(reloaded).ToArray();
        await issues.SaveBatchAsync(batch);
        var page = await issues.SearchAsync(new IssueFilter { GroupId = "g" });
        Assert.All(page.Items, i => Assert.Equal(user.Id, i.AssignedUserId));
        Assert.All(page.Items, i => Assert.Equal(now, i.AssignmentDetachedAtUtc));
        Assert.Equal(6, page.TotalCount);
        foreach (var item in batch)
        {
            item.AssignTo("legacy-name", now);
        }
        await issues.SaveBatchAsync(batch);
        Assert.All((await issues.GetManyAsync(batch.Select(i => i.Id).ToArray())),
            i => Assert.Null(i.AssignedUserId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("g")]
    public async Task Issue_resets_do_not_delete_known_users(string? group)
    {
        var users = CreateStore();
        var user = await users.UpsertLoginAsync(Login(), []);
        var issue = Issue.FromDetection(new IssueDetection(IssueId.From("rule:reset-user"), "rule"),
            "g", "run", DateTimeOffset.UnixEpoch);
        issue.AssignKnownUser(user, DateTimeOffset.UnixEpoch);
        await CreateIssueStore().SaveAsync(issue, null);
        await CreateResetStore().ResetAsync(group, "test", DateTimeOffset.UnixEpoch);
        Assert.Equal(user, await users.GetAsync(user.Id));
        Assert.Null(await CreateIssueStore().GetAsync(issue.Id));
    }
}
