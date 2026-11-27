using System.Net;
using Cleanse.Api.Test.Support;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Cleanse.Api.Test;

/// <summary>
/// The import driven over HTTP, end to end: a dataset loaded and processed in the background, its
/// progress, and the reconciliation report. The processor runs for real, so these also exercise
/// the lease.
/// </summary>
public class ImportEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static SpolDatasetRowDto Item(
        string cph, string ruleNo, string status, string notes = "", string priority = "4. Low",
        string? first = null, string? letter = null, string? assignedName = null, string? assignedEmail = null) => new()
    {
        DateReceived = "2025-11-27T00:00:00Z",
        Priority = priority,
        Cph = cph,
        RuleNo = ruleNo,
        RuleName = "Rule Name",
        ErrorCode = "1",
        ErrorCodeDescription = "Desc",
        Status = status,
        AssignedName = assignedName,
        AssignedEmail = assignedEmail,
        FirstContactDate = first,
        LetterEmailSent = letter,
        Notes = notes
    };

    private static async Task<Guid> ImportAsync(HttpClient client, string group, params SpolDatasetRowDto[] items) =>
        (await DatasetApi.ImportAsync(client, group, items)).JobId;

    [Theory]
    [InlineData("  aLeX eXaMpLe {0}  ", 1, null)]
    [InlineData("Alex Example {0}", 2, "assignee_ambiguous")]
    [InlineData("Unknown Person {0}", 1, "assignee_unmatched")]
    [InlineData("   ", 1, null)]
    public async Task Import_assigns_only_unique_names_and_reports_safe_warnings(
        string template, int sameNameUsers, string? warning)
    {
        using var client = factory.CreateAuthenticatedClient();
        var group = $"assignment-{Guid.NewGuid():N}";
        // Every test in this class shares one directory, so each case brings its own name.
        var person = $"Alex Example {group}";
        var name = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, group);
        using var services = factory.Services.CreateScope();
        var users = services.ServiceProvider.GetRequiredService<IUserStore>();
        KnownUser? known = null;
        for (var index = 0; index < sameNameUsers; index++)
        {
            known = await users.UpsertLoginAsync(new KnownUser(Guid.NewGuid(), "https://issuer.example.test",
                Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(), person, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), []);
        }
        var id = await SeedIssueAsync(client, group, group, "1");
        var holdings = services.ServiceProvider.GetRequiredService<IHoldingStore>();

        // As an earlier import would have left it, so this import may still change it.
        var earlier = Holding.New(new HoldingKey(group, group), DateTimeOffset.UtcNow);
        earlier.SyncAssignment(known, null, DateTimeOffset.UtcNow);
        Assert.True(await holdings.SaveAsync(earlier, 0));

        async Task<(System.Text.Json.JsonElement Issue, System.Text.Json.JsonElement Report)> RunAsync()
        {
            var jobId = await ImportAsync(client, group, Item(group, "1", "Completed", "notes preserved", assignedName: name));
            var finished = await WaitForCompletionAsync(client, jobId);
            Assert.Equal("completed", finished.GetProperty("status").GetString());
            Assert.Equal(1, finished.GetProperty("matched").GetInt32());
            Assert.Equal(0, finished.GetProperty("unmatched").GetInt32());
            return (await (await client.GetAsync($"/v1/issues/{id}")).ReadJsonAsync(),
                await (await client.GetAsync($"/v1/imports/{jobId}/rejections")).ReadJsonAsync());
        }

        var first = await RunAsync();
        var assigned = sameNameUsers == 1 && !string.IsNullOrWhiteSpace(name) && warning is null;
        Assert.Equal(assigned ? known!.Id : (Guid?)null,
            first.Issue.GetProperty("assignedUserId").ValueKind == System.Text.Json.JsonValueKind.Null
                ? null : first.Issue.GetProperty("assignedUserId").GetGuid());
        Assert.Equal(assigned ? known!.FullName : string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            first.Issue.GetProperty("assignedTo").GetString());
        Assert.Equal("resolved", first.Issue.GetProperty("workflowStatus").GetString());
        Assert.Equal("notes preserved", first.Issue.GetProperty("externalNotes").GetString());
        Assert.Equal(warning is null ? 0 : 1, first.Report.GetProperty("totalCount").GetInt32());
        if (warning is not null)
        {
            Assert.Equal(warning, first.Report.GetProperty("items")[0].GetProperty("reason").GetString());
            Assert.DoesNotContain(name.Trim(), first.Report.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        var second = await RunAsync();
        Assert.Equal(first.Issue.GetProperty("version").GetInt32(), second.Issue.GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData("?assignedUserId=invalid")]
    [InlineData("?assignedUserId=")]
    [InlineData("?assignedUserId=00000000-0000-0000-0000-000000000000")]
    [InlineData("?assignedUserId=00000000-0000-0000-0000-000000000001&unassigned=true")]
    [InlineData("?assignedTo=Legacy&unassigned=true")]
    [InlineData("?unassigned=invalid")]
    [InlineData("?unassigned=")]
    public async Task Invalid_assignee_search_returns_400(string query)
    {
        using var client = factory.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/v1/issues{query}")).StatusCode);
    }

    [Fact]
    public async Task Assignee_search_uses_stable_id_or_unassigned_not_display_name()
    {
        using var client = factory.CreateAuthenticatedClient();
        var group = $"filters-{Guid.NewGuid():N}";
        var id = await SeedIssueAsync(client, group, group, "1");
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var user = await users.UpsertLoginAsync(new KnownUser(Guid.NewGuid(), "issuer", group,
            "tenant", Guid.NewGuid().ToString(), "client", "Example Person", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), []);
        var holding = Holding.New(new HoldingKey(group, group), DateTimeOffset.UtcNow);
        holding.AssignKnownUser(user, DateTimeOffset.UtcNow);
        Assert.True(await scope.ServiceProvider.GetRequiredService<IHoldingStore>().SaveAsync(holding, 0));
        var result = await (await client.GetAsync($"/v1/issues?groupId={group}&assignedUserId={user.Id}")).ReadJsonAsync();
        Assert.Equal(1, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(id, result.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(0, (await (await client.GetAsync($"/v1/issues?groupId={group}&unassigned=true")).ReadJsonAsync())
            .GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await (await client.GetAsync($"/v1/issues?groupId={group}&unassigned=false")).ReadJsonAsync())
            .GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Assignees_become_users_named_as_in_SharePoint_before_the_items_are_applied()
    {
        using var client = factory.CreateAuthenticatedClient();
        var group = $"newcomer-{Guid.NewGuid():N}";
        var cph = $"01/036/{Guid.NewGuid():N}";
        await SeedIssueAsync(client, group, cph, "1");
        var email = $"alice.{Guid.NewGuid():N}@example.gov.uk";
        var item = Item(cph, "1", "New", assignedName: "Alice\u00a0 Smith", assignedEmail: email.ToUpperInvariant());

        var finished = await WaitForCompletionAsync(client, await ImportAsync(client, group, item));

        Assert.Equal("completed", finished.GetProperty("status").GetString());
        Assert.Equal(1, finished.GetProperty("usersCreated").GetInt32());
        using var scope = factory.Services.CreateScope();
        var users = await scope.ServiceProvider.GetRequiredService<IUserStore>().SearchAsync(new PageRequest(1, PageRequest.MaxPageSize));
        var alice = Assert.Single(users.Items, user => user.Email == email);
        Assert.Equal("Alice Smith", alice.FullName);
        Assert.Null(alice.Subject);
        var holding = await (await client.GetAsync($"/v1/holdings?groupId={group}&cph={Uri.EscapeDataString(cph)}")).ReadJsonAsync();
        Assert.Equal(alice.Id, holding.GetProperty("assignedUserId").GetGuid());

        // She is a user now, so processing again adds nobody.
        var again = await WaitForCompletionAsync(client, await ImportAsync(client, group, item));
        Assert.Equal(0, again.GetProperty("usersCreated").GetInt32());
    }

    [Fact]
    public async Task An_item_naming_several_people_gives_the_holding_the_first_adds_them_all_and_warns()
    {
        using var client = factory.CreateAuthenticatedClient();
        var group = $"several-{Guid.NewGuid():N}";
        var cph = $"01/036/{Guid.NewGuid():N}";
        await SeedIssueAsync(client, group, cph, "1");
        var alice = $"alice.{Guid.NewGuid():N}@example.gov.uk";
        var bob = $"bob.{Guid.NewGuid():N}@example.gov.uk";

        var jobId = await ImportAsync(client, group,
            Item(cph, "1", "New", assignedName: "Alice Several; Bob Several", assignedEmail: $"{alice}; {bob}"));
        var finished = await WaitForCompletionAsync(client, jobId);

        Assert.Equal(2, finished.GetProperty("usersCreated").GetInt32());
        var holding = await (await client.GetAsync($"/v1/holdings?groupId={group}&cph={Uri.EscapeDataString(cph)}")).ReadJsonAsync();
        Assert.Equal("Alice Several", holding.GetProperty("assignedTo").GetString());
        using var scope = factory.Services.CreateScope();
        var users = await scope.ServiceProvider.GetRequiredService<IUserStore>().SearchAsync(new PageRequest(1, PageRequest.MaxPageSize));
        Assert.Contains(users.Items, user => user.Email == bob && user.FullName == "Bob Several");

        var report = await (await client.GetAsync($"/v1/imports/{jobId}/rejections")).ReadJsonAsync();
        var warning = Assert.Single(report.GetProperty("items").EnumerateArray());
        Assert.Equal("assignee_several", warning.GetProperty("reason").GetString());
        Assert.Equal(1, warning.GetProperty("rowNumber").GetInt64());
        Assert.DoesNotContain("Bob", warning.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Items_for_one_cph_naming_different_people_give_the_holding_one_and_warn()
    {
        using var client = factory.CreateAuthenticatedClient();
        var group = $"conflict-{Guid.NewGuid():N}";

        // Issue ids leave the group out, so a CPH another test seeds would be a group mismatch.
        var cph = $"01/036/{Guid.NewGuid():N}";
        await SeedIssuesAsync(client, group, (cph, "1"), (cph, "2"));

        var jobId = await ImportAsync(client, group,
            Item(cph, "2", "In progress", assignedName: "Bob Open"),
            Item(cph, "1", "Completed", assignedName: "Alice Closed"));
        Assert.Equal("completed", (await WaitForCompletionAsync(client, jobId)).GetProperty("status").GetString());

        var holding = await (await client.GetAsync($"/v1/holdings?groupId={group}&cph={Uri.EscapeDataString(cph)}")).ReadJsonAsync();
        Assert.Equal("Bob Open", holding.GetProperty("assignedTo").GetString());
        Assert.Equal("in-progress", holding.GetProperty("status").GetString());

        var rejections = await (await client.GetAsync($"/v1/imports/{jobId}/rejections")).ReadJsonAsync();
        var conflict = Assert.Single(rejections.GetProperty("items").EnumerateArray(),
            r => r.GetProperty("reason").GetString() == "assignee_conflict");
        Assert.Contains(cph, conflict.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", conflict.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    private static async Task<string> SeedIssueAsync(HttpClient client, string group, string cph, string ruleNo) =>
        (await SeedIssuesAsync(client, group, (cph, ruleNo)))[0];

    private static async Task<string[]> SeedIssuesAsync(HttpClient client, string group, params (string Cph, string RuleNo)[] issues)
    {
        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;

        // The import matches on the key the rules engine produces, so it is built the same way,
        // and the issue carries its CPH as the engine writes it, which names its holding.
        var ids = issues.Select(issue => IssueId.Derive(issue.RuleNo, issue.Cph).Value).ToArray();
        await client.PostJsonAsync($"/v1/runs/{runId}/detections", new
        {
            detections = issues.Select((issue, index) => new
            {
                id = ids[index],
                ruleId = issue.RuleNo,
                context = new Dictionary<string, string> { ["cph"] = issue.Cph }
            }).ToArray()
        });
        await client.PostJsonAsync($"/v1/runs/{runId}/complete", new { });
        return ids;
    }

    private static async Task<System.Text.Json.JsonElement> WaitForCompletionAsync(
        HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await (await client.GetAsync($"/v1/imports/{jobId}")).ReadJsonAsync();
            var status = job.GetProperty("status").GetString();

            if (status is "completed" or "failed" or "abandoned")
            {
                return job;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Import {jobId} did not finish.");
    }

    [Fact]
    public async Task A_dataset_is_processed_in_the_background()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"imp-{Guid.NewGuid():N}";
        var issueId = await SeedIssueAsync(client, group, "01/036/8000", "1");

        var (datasetId, jobId) = await DatasetApi.ImportAsync(client, group,
            Item("01/036/8000", "1", "Completed", "archived on SAM. no longer on CTS NFA", "3. Medium",
                first: "2026-01-19T00:00:00Z"));

        var finished = await WaitForCompletionAsync(client, jobId);
        Assert.Equal("completed", finished.GetProperty("status").GetString());
        Assert.Equal(100, finished.GetProperty("percentComplete").GetInt32());
        Assert.Equal(1, finished.GetProperty("matched").GetInt32());
        Assert.Equal(datasetId, finished.GetProperty("datasetId").GetGuid());

        var issue = await (await client.GetAsync($"/v1/issues/{Uri.EscapeDataString(issueId)}")).ReadJsonAsync();
        Assert.Equal("resolved", issue.GetProperty("workflowStatus").GetString());
        Assert.Equal("medium", issue.GetProperty("priority").GetString());
        Assert.Equal("archived on SAM. no longer on CTS NFA", issue.GetProperty("externalNotes").GetString());

        var history = await (await client.GetAsync(
            $"/v1/issues/{Uri.EscapeDataString(issueId)}/history")).ReadJsonAsync();
        Assert.Equal(1, history.GetProperty("totalCount").GetInt32());

        // The dataset shows when it was last processed, and lists the run.
        var dataset = await (await client.GetAsync($"/v1/datasets/{datasetId}")).ReadJsonAsync();
        Assert.Equal(jobId, dataset.GetProperty("lastProcessing").GetProperty("importJobId").GetGuid());
        Assert.Equal("completed", dataset.GetProperty("lastProcessing").GetProperty("status").GetString());
        var runs = await (await client.GetAsync($"/v1/imports?datasetId={datasetId}")).ReadJsonAsync();
        Assert.Equal(jobId, Assert.Single(runs.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Processing_the_same_dataset_twice_changes_nothing_the_second_time()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"imp-{Guid.NewGuid():N}";
        var issueId = await SeedIssueAsync(client, group, "02/092/8000", "1");
        var escaped = Uri.EscapeDataString(issueId);

        var (datasetId, firstJob) = await DatasetApi.ImportAsync(client, group,
            Item("02/092/8000", "1", "Completed", "same notes", first: "2026-01-19T00:00:00Z",
                letter: "2026-02-01T00:00:00+00:00"));
        await WaitForCompletionAsync(client, firstJob);

        var afterFirst = await (await client.GetAsync($"/v1/issues/{escaped}")).ReadJsonAsync();
        var historyAfterFirst = await (await client.GetAsync($"/v1/issues/{escaped}/history")).ReadJsonAsync();
        var auditAfterFirst = await (await client.GetAsync($"/v1/issues/{escaped}/audit")).ReadJsonAsync();

        var secondJob = await DatasetApi.StartProcessingAsync(client, datasetId, group);
        await WaitForCompletionAsync(client, secondJob);

        var afterSecond = await (await client.GetAsync($"/v1/issues/{escaped}")).ReadJsonAsync();
        var historyAfterSecond = await (await client.GetAsync($"/v1/issues/{escaped}/history")).ReadJsonAsync();
        var auditAfterSecond = await (await client.GetAsync($"/v1/issues/{escaped}/audit")).ReadJsonAsync();

        // Deterministic history ids mean the second run upserts rather than inserting again.
        Assert.Equal(2, historyAfterFirst.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            historyAfterFirst.GetProperty("totalCount").GetInt32(),
            historyAfterSecond.GetProperty("totalCount").GetInt32());

        // Nothing material changed, so no second audit entry and no version bump.
        Assert.Equal(
            auditAfterFirst.GetProperty("totalCount").GetInt32(),
            auditAfterSecond.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            afterFirst.GetProperty("version").GetInt32(),
            afterSecond.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Items_with_no_matching_issue_are_reported_by_item_id_not_created()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"imp-{Guid.NewGuid():N}";

        var jobId = await ImportAsync(client, group, Item("99/999/9999", "1", "Completed") with { ItemId = 4321 });

        var finished = await WaitForCompletionAsync(client, jobId);
        Assert.Equal(1, finished.GetProperty("unmatched").GetInt32());
        Assert.Equal(0, finished.GetProperty("matched").GetInt32());

        var rejections = await (await client.GetAsync($"/v1/imports/{jobId}/rejections")).ReadJsonAsync();
        var first = rejections.GetProperty("items")[0];
        Assert.Equal("issue_not_found", first.GetProperty("reason").GetString());
        Assert.Equal(4321, first.GetProperty("rowNumber").GetInt64());

        // Nothing was invented from the dataset.
        var missing = await client.GetAsync(
            $"/v1/issues/{Uri.EscapeDataString($"{group}:1|99/999/9999")}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_import_never_touches_the_machine_owned_axis()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"imp-{Guid.NewGuid():N}";
        var issueId = await SeedIssueAsync(client, group, "03/043/0203", "1");
        var escaped = Uri.EscapeDataString(issueId);

        var before = await (await client.GetAsync($"/v1/issues/{escaped}")).ReadJsonAsync();

        await WaitForCompletionAsync(client, await ImportAsync(client, group, Item("03/043/0203", "1", "Unable to Action")));

        var after = await (await client.GetAsync($"/v1/issues/{escaped}")).ReadJsonAsync();

        Assert.Equal("nonactionable", after.GetProperty("workflowStatus").GetString());
        Assert.Equal(before.GetProperty("status").GetString(), after.GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("runId").GetString(), after.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task There_is_no_file_upload()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/v1/imports", new MultipartFormDataContent
        {
            { new StringContent("CPH,Rule No\n01/036/8000,1"), "file", "extract.csv" },
            { new StringContent("cts-sam"), "groupId" }
        });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
