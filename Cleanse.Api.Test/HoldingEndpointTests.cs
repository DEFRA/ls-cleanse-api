using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cleanse.Api;
using Cleanse.Api.Test.Support;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Test;

/// <summary>The holding page's endpoints, driven over HTTP against the real host.</summary>
public sealed class HoldingEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string Id(string cph, string ruleId) => IssueId.Derive(ruleId, cph).Value;

    private static string HoldingUrl(string group, string cph) =>
        $"/v1/holdings?groupId={Uri.EscapeDataString(group)}&cph={Uri.EscapeDataString(cph)}";

    /// <summary>Two issues at one holding and one at its neighbour, in a group of their own.</summary>
    private static async Task<(string Group, string Cph)> SeedAsync(HttpClient client)
    {
        var group = $"holding-{Guid.NewGuid():N}";

        // Issue ids leave the group out, so each test needs CPHs of its own. Real CPHs have slashes.
        var cph = $"01/036/{Guid.NewGuid():N}";
        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;
        await client.PostJsonAsync($"/v1/runs/{runId}/detections", new
        {
            detections = new[] { (cph, "1"), (cph, "3"), ($"{cph}-next", "1") }.Select(d => new
            {
                id = Id(d.Item1, d.Item2),
                ruleId = d.Item2,
                context = new Dictionary<string, string> { ["cph"] = d.Item1, ["ruleNumber"] = d.Item2 }
            }).ToArray()
        });
        await client.PostJsonAsync($"/v1/runs/{runId}/complete", new { });
        return (group, cph);
    }

    private static IEnumerable<object> Versions(JsonElement holding) =>
        holding.GetProperty("issues").EnumerateArray().Select(issue => new
        {
            id = issue.GetProperty("id").GetString(),
            expectedVersion = issue.GetProperty("version").GetInt32()
        });

    [Fact]
    public async Task The_holding_page_shows_every_issue_at_the_cph_and_their_status()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (group, cph) = await SeedAsync(client);

        var response = await client.GetAsync(HoldingUrl(group, cph));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var holding = await response.ReadJsonAsync();
        Assert.Equal(cph, holding.GetProperty("cph").GetString());
        Assert.Equal("todo", holding.GetProperty("status").GetString());
        Assert.Equal(0, holding.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, holding.GetProperty("assignedTo").ValueKind);
        Assert.Equal(
            [Id(cph, "1"), Id(cph, "3")],
            holding.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
        Assert.Empty(holding.GetProperty("notes").EnumerateArray());
    }

    [Theory]
    [InlineData("/v1/holdings?groupId=g")]
    [InlineData("/v1/holdings?cph=01/001/0001")]
    [InlineData("/v1/holdings?groupId=%20&cph=01/001/0001")]
    public async Task A_holding_needs_both_its_group_and_its_cph(string url)
    {
        var response = await factory.CreateAuthenticatedClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_cph_with_no_issues_is_not_found()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (group, _) = await SeedAsync(client);

        var response = await client.GetAsync(HoldingUrl(group, "99/999/9999"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("holding.not_found", (await response.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Issues_change_status_together_or_not_at_all()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (group, cph) = await SeedAsync(client);
        var reason = await IssueWorkflowTests.AddListEntryAsync(client, "cant-resolve-reasons", "Keeper has died");
        var shown = await (await client.GetAsync(HoldingUrl(group, cph))).ReadJsonAsync();
        var versions = Versions(shown).ToArray();

        var withoutReason = await client.PostJsonAsync("/v1/holdings/workflow-status",
            new { groupId = group, cph, workflowStatus = "nonactionable", issues = versions });
        Assert.Equal(HttpStatusCode.BadRequest, withoutReason.StatusCode);
        var problem = await withoutReason.ReadJsonAsync();
        Assert.Equal("issue.reason_required", problem.GetProperty("code").GetString());
        Assert.Equal(Id(cph, "1"), problem.GetProperty("subject").GetString());

        var stale = await client.PostJsonAsync("/v1/holdings/workflow-status", new
        {
            groupId = group, cph, workflowStatus = "nonactionable", reasonId = reason,
            issues = new object[] { versions[0], new { id = Id(cph, "3"), expectedVersion = 99 } }
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(Id(cph, "3"), (await stale.ReadJsonAsync()).GetProperty("subject").GetString());
        var unchanged = await (await client.GetAsync(HoldingUrl(group, cph))).ReadJsonAsync();
        Assert.All(unchanged.GetProperty("issues").EnumerateArray(),
            issue => Assert.Equal("todo", issue.GetProperty("workflowStatus").GetString()));

        var updated = await client.PostJsonAsync("/v1/holdings/workflow-status", new
        {
            groupId = group, cph, workflowStatus = "nonactionable", reasonId = reason,
            issues = versions, note = "Confirmed with the family"
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await updated.ReadJsonAsync();
        Assert.All(body.GetProperty("issues").EnumerateArray(),
            issue => Assert.Equal(reason, issue.GetProperty("outcomeReasonId").GetGuid()));
        Assert.StartsWith("Can't resolve (Keeper has died", body.GetProperty("note").GetProperty("context").GetString(),
            StringComparison.Ordinal);

        var after = await (await client.GetAsync(HoldingUrl(group, cph))).ReadJsonAsync();
        Assert.Equal("closed", after.GetProperty("status").GetString());
        Assert.Equal("Confirmed with the family", after.GetProperty("notes")[0].GetProperty("text").GetString());
        Assert.Equal(2, after.GetProperty("history").EnumerateArray()
            .Count(e => e.GetProperty("kind").GetString() == "status-changed"));
    }

    [Fact]
    public async Task Suppressing_issues_needs_an_end_date()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (group, cph) = await SeedAsync(client);
        var versions = Versions(await (await client.GetAsync(HoldingUrl(group, cph))).ReadJsonAsync()).ToArray();

        var withoutDate = await client.PostJsonAsync("/v1/holdings/workflow-status",
            new { groupId = group, cph, workflowStatus = "suppressed", issues = versions });
        var suppressed = await client.PostJsonAsync("/v1/holdings/workflow-status", new
        {
            groupId = group, cph, workflowStatus = "suppressed", issues = versions,
            suppressUntilUtc = DateTimeOffset.UtcNow.AddDays(14)
        });

        Assert.Equal(HttpStatusCode.BadRequest, withoutDate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, suppressed.StatusCode);
        var holding = await (await client.GetAsync(HoldingUrl(group, cph))).ReadJsonAsync();
        Assert.Equal("suppressed", holding.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, holding.GetProperty("suppressedUntilUtc").ValueKind);
    }

    [Fact]
    public async Task Actions_are_recorded_against_the_holding_with_an_optional_note()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (group, cph) = await SeedAsync(client);
        var phoned = await IssueWorkflowTests.AddListEntryAsync(client, "action-types", "Phoned the keeper");

        var recorded = await client.PostJsonAsync("/v1/holdings/actions",
            new { groupId = group, cph, actionTypeIds = new[] { phoned }, note = "No answer" });

        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        var body = await recorded.ReadJsonAsync();
        var name = Assert.Single(body.GetProperty("actions").EnumerateArray()).GetString()!;
        Assert.StartsWith("Phoned the keeper", name, StringComparison.Ordinal);
        Assert.Equal($"Recorded: {name}", body.GetProperty("note").GetProperty("context").GetString());

        var holding = await (await client.GetAsync(HoldingUrl(group, cph))).ReadJsonAsync();
        var entry = Assert.Single(holding.GetProperty("history").EnumerateArray(),
            e => e.GetProperty("kind").GetString() == "actions-recorded");
        Assert.Equal(name, entry.GetProperty("actions")[0].GetString());
        Assert.Equal("No answer", holding.GetProperty("notes")[0].GetProperty("text").GetString());
        Assert.All(holding.GetProperty("issues").EnumerateArray(),
            issue => Assert.Equal("todo", issue.GetProperty("workflowStatus").GetString()));

        // A type switched off is no longer offered, and nothing is recorded without a type.
        await client.PutJsonAsync($"/v1/lists/action-types/{phoned}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/v1/holdings/actions",
            new { groupId = group, cph, actionTypeIds = new[] { phoned } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/v1/holdings/actions",
            new { groupId = group, cph, actionTypeIds = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task Notes_are_added_and_listed_newest_first()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (group, cph) = await SeedAsync(client);

        var first = await client.PostJsonAsync("/v1/holdings/notes", new { groupId = group, cph, text = "First note" });
        await client.PostJsonAsync("/v1/holdings/notes", new { groupId = group, cph, text = "Second note" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("test-client", (await first.ReadJsonAsync()).GetProperty("createdById").GetString());
        var holding = await (await client.GetAsync(HoldingUrl(group, cph))).ReadJsonAsync();
        Assert.Equal(
            ["Second note", "First note"],
            holding.GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("text").GetString()!).ToArray());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostJsonAsync("/v1/holdings/notes", new { groupId = group, cph, text = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/v1/holdings/notes",
            new { groupId = group, cph, text = new string('x', HoldingNote.MaxLength + 1) })).StatusCode);
    }

    [Fact]
    public async Task A_change_made_for_a_signed_in_person_is_recorded_against_them()
    {
        using var client = factory.CreateAuthenticatedClient();
        var (group, cph) = await SeedAsync(client);
        var person = await IssueWorkflowTests.RegisterAsync(client, "carol.jones");
        var versions = Versions(await (await client.GetAsync(HoldingUrl(group, cph))).ReadJsonAsync()).ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/holdings/workflow-status")
        {
            Content = JsonContent.Create(new { groupId = group, cph, workflowStatus = "in-progress", issues = versions })
        };
        request.Headers.Add(ActingUser.HeaderName, person.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);

        var audit = await (await client.GetAsync($"/v1/issues/{Id(cph, "1")}/audit")).ReadJsonAsync();
        var changed = audit.GetProperty("items").EnumerateArray()
            .First(e => e.GetProperty("kind").GetString() == "IssueWorkflowStatusChanged");
        Assert.Equal(person.ToString(), changed.GetProperty("actorId").GetString());
        Assert.Equal("human", changed.GetProperty("actorKind").GetString());
        Assert.StartsWith("Carol Jones", changed.GetProperty("actorDisplayName").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Openapi_describes_the_holding_and_list_endpoints()
    {
        var doc = await (await factory.CreateClient().GetAsync("/v1/openapi.json")).ReadJsonAsync();
        var paths = doc.GetProperty("paths");

        Assert.True(paths.GetProperty("/v1/holdings").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/v1/holdings/assignee").TryGetProperty("put", out _));
        Assert.True(paths.GetProperty("/v1/holdings/workflow-status").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/v1/lists/{list}").TryGetProperty("post", out _));
        Assert.False(paths.TryGetProperty("/v1/issues/{id}/assignee", out _));
        var status = doc.GetProperty("components").GetProperty("schemas").GetProperty("HoldingDetailDto")
            .GetProperty("properties").GetProperty("status");
        Assert.Equal(
            ["todo", "in-progress", "suppressed", "closed"],
            status.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!).ToArray());
    }
}
