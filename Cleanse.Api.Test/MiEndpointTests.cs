using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cleanse.Api.Test.Support;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Test;

/// <summary>
/// The MI dashboard's endpoints over HTTP, with the real background refresh. Only the deployment's
/// own group has figures, so the tests share it and each one works on CPHs of its own.
/// </summary>
public sealed class MiEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Group = "cts-sam";
    private const string Mi = $"/v1/groups/{Group}/mi";

    private static string Id(string cph, string ruleId) => IssueId.Derive(ruleId, cph).Value;

    /// <summary>Two issues at one holding, found by a run that sweeps away every other test's.</summary>
    private static async Task<string> SeedAsync(HttpClient client)
    {
        var cph = $"03/{Guid.NewGuid():N}";
        var start = await client.PostJsonAsync("/v1/runs", new { groupId = Group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;
        await client.PostJsonAsync($"/v1/runs/{runId}/detections", new
        {
            detections = new[] { "1", "3" }.Select(rule => new
            {
                id = Id(cph, rule),
                ruleId = rule,
                context = new Dictionary<string, string> { ["cph"] = cph }
            }).ToArray()
        });
        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync($"/v1/runs/{runId}/complete", new { })).StatusCode);
        return cph;
    }

    /// <summary>Asks for a rebuild, and waits for the background refresh to finish it.</summary>
    private static async Task<JsonElement> RebuildAsync(HttpClient client)
    {
        var asked = await client.PostAsync($"{Mi}/rebuild", null);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.True((await asked.ReadJsonAsync()).GetProperty("building").GetBoolean());

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var status = await (await client.GetAsync($"{Mi}/status")).ReadJsonAsync();
            if (status.GetProperty("built").GetBoolean() && !status.GetProperty("building").GetBoolean())
            {
                return status;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The dashboard figures were not rebuilt within 10 seconds.");
        return default;
    }

    private static async Task ResolveAsync(HttpClient client, string cph, Guid person)
    {
        var holding = await (await client.GetAsync($"/v1/holdings?groupId={Group}&cph={Uri.EscapeDataString(cph)}")).ReadJsonAsync();
        var issues = holding.GetProperty("issues").EnumerateArray()
            .Select(issue => new { id = issue.GetProperty("id").GetString(), expectedVersion = issue.GetProperty("version").GetInt32() })
            .ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/holdings/workflow-status")
        {
            Content = JsonContent.Create(new { groupId = Group, cph, workflowStatus = "resolved", issues })
        };
        request.Headers.Add(ActingUser.HeaderName, person.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/v1/groups/elsewhere/mi/status")]
    [InlineData("GET", "/v1/groups/elsewhere/mi/work")]
    [InlineData("GET", "/v1/groups/elsewhere/mi/backlog")]
    [InlineData("POST", "/v1/groups/elsewhere/mi/rebuild")]
    public async Task Only_the_deployments_own_group_has_figures(string method, string url)
    {
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("group.not_found", (await response.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_rebuild_counts_the_work_a_person_did_in_cleanse()
    {
        using var client = factory.CreateAuthenticatedClient();
        var cph = await SeedAsync(client);
        var person = await IssueWorkflowTests.RegisterAsync(client, "dana.smith");
        await ResolveAsync(client, cph, person);

        var status = await RebuildAsync(client);
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("asAtUtc").ValueKind);
        Assert.False(status.GetProperty("stale").GetBoolean());

        var response = await client.GetAsync($"{Mi}/work?userId={person}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var work = await response.ReadJsonAsync();
        var headline = work.GetProperty("headline");
        Assert.Equal(2, headline.GetProperty("resolved").GetProperty("value").GetInt32());
        Assert.Equal(1, headline.GetProperty("holdingsClosed").GetProperty("value").GetInt32());
        var named = Assert.Single(work.GetProperty("people").EnumerateArray());
        Assert.Equal(person, named.GetProperty("userId").GetGuid());
        Assert.StartsWith("Dana Smith", named.GetProperty("name").GetString(), StringComparison.Ordinal);
        // The default is the last 7 days, cut to where the history starts: today, with the first run.
        var period = work.GetProperty("period");
        Assert.Equal(1, period.GetProperty("days").GetInt32());
        Assert.False(period.GetProperty("hasPrevious").GetBoolean());
    }

    [Fact]
    public async Task The_backlog_counts_what_is_open_now()
    {
        using var client = factory.CreateAuthenticatedClient();
        await SeedAsync(client);
        await RebuildAsync(client);

        var issues = await (await client.GetAsync($"{Mi}/backlog")).ReadJsonAsync();
        var holdings = await (await client.GetAsync($"{Mi}/backlog?unit=holdings&interval=day")).ReadJsonAsync();

        // The seeding run swept away every earlier test's issues, so only its own two are open.
        Assert.Equal(2, issues.GetProperty("openAtEnd").GetInt32());
        Assert.Equal("issues", issues.GetProperty("unit").GetString());
        Assert.Equal(1, holdings.GetProperty("openAtEnd").GetInt32());
        Assert.Single(holdings.GetProperty("buckets").EnumerateArray());
    }

    [Theory]
    [InlineData("work?from=27/11/2026", "yyyy-MM-dd")]
    [InlineData("work?userId=someone", "userId")]
    [InlineData("work?interval=year", "interval")]
    [InlineData("backlog?unit=farms", "unit")]
    [InlineData("backlog?unit=holdings&ruleId=1", "rule")]
    [InlineData("backlog?from=2026-02-01&to=2026-01-01", "before")]
    public async Task A_filter_that_makes_no_sense_is_refused_with_why(string query, string mentioned)
    {
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync($"{Mi}/{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.Contains(mentioned, problem.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_reset_takes_the_figures_with_it()
    {
        using var client = factory.CreateAuthenticatedClient();
        await SeedAsync(client);
        await RebuildAsync(client);

        using var reset = factory.CreateResetClient();
        Assert.Equal(HttpStatusCode.OK, (await reset.PostJsonAsync("/v1/maintenance/reset", new { groupId = Group })).StatusCode);

        var status = await (await client.GetAsync($"{Mi}/status")).ReadJsonAsync();
        Assert.False(status.GetProperty("built").GetBoolean());
    }

    [Fact]
    public async Task Openapi_describes_the_mi_endpoints()
    {
        var doc = await (await factory.CreateClient().GetAsync("/v1/openapi.json")).ReadJsonAsync();
        var paths = doc.GetProperty("paths");

        Assert.True(paths.GetProperty($"/v1/groups/{{groupId}}/mi/work").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty($"/v1/groups/{{groupId}}/mi/backlog").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty($"/v1/groups/{{groupId}}/mi/status").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty($"/v1/groups/{{groupId}}/mi/rebuild").TryGetProperty("post", out _));
    }
}
