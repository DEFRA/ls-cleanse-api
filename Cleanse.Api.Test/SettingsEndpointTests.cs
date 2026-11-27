using System.Net;
using System.Text;
using Cleanse.Api.Test.Support;
using Cleanse.Auth;
using Cleanse.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cleanse.Api.Test;

/// <summary>
/// The service-wide settings, and the searches that apply them. The settings are global, so each
/// test runs on a host of its own.
/// </summary>
public sealed class SettingsEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed class MovableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HttpClient Authenticated(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, ApiFactory.ApiKey);
        return client;
    }

    private static async Task<int> MinimumIssueAgeAsync(HttpClient client) =>
        (await (await client.GetAsync("/v1/settings")).ReadJsonAsync()).GetProperty("minimumIssueAgeDays").GetInt32();

    private static async Task<int> TotalAsync(HttpClient client, string url) =>
        (await (await client.GetAsync(url)).ReadJsonAsync()).GetProperty("totalCount").GetInt32();

    [Fact]
    public async Task The_minimum_issue_age_is_fourteen_days_until_it_is_changed()
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        using var client = Authenticated(host);

        Assert.Equal(ServiceSettings.DefaultMinimumIssueAgeDays, await MinimumIssueAgeAsync(client));

        var updated = await client.PutJsonAsync("/v1/settings", new { minimumIssueAgeDays = 7 });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(7, (await updated.ReadJsonAsync()).GetProperty("minimumIssueAgeDays").GetInt32());
        Assert.Equal(7, await MinimumIssueAgeAsync(client));
    }

    [Theory]
    [InlineData("""{ "minimumIssueAgeDays": -1 }""")]
    [InlineData("""{ "minimumIssueAgeDays": 366 }""")]
    [InlineData("""{ "minimumIssueAgeDays": 1.5 }""")]
    [InlineData("""{ "minimumIssueAgeDays": "abc" }""")]
    [InlineData("""{ "minimumIssueAgeDays": null }""")]
    [InlineData("{}")]
    public async Task An_invalid_minimum_issue_age_is_refused_and_nothing_changes(string body)
    {
        using var host = factory.WithWebHostBuilder(_ => { });
        using var client = Authenticated(host);

        var response = await client.PutAsync(
            "/v1/settings", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ServiceSettings.DefaultMinimumIssueAgeDays, await MinimumIssueAgeAsync(client));
    }

    [Fact]
    public async Task Only_the_front_end_may_change_the_settings()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication("test-service")
                .AddScheme<AuthenticationSchemeOptions, DirectoryTestAuthenticationHandler>("test-service", _ => { });
            services.AddAuthorization(options => options.AddPolicy(
                CleansePolicies.Default, policy => policy.RequireAuthenticatedUser()));
        }));
        using var frontend = host.CreateClient();
        frontend.DefaultRequestHeaders.Add("x-test-service", "ls-cleanse-ui");
        using var otherService = host.CreateClient();
        otherService.DefaultRequestHeaders.Add("x-test-service", "another-service");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await otherService.PutJsonAsync("/v1/settings", new { minimumIssueAgeDays = 7 })).StatusCode);
        Assert.Equal(ServiceSettings.DefaultMinimumIssueAgeDays, await MinimumIssueAgeAsync(otherService));
        Assert.Equal(HttpStatusCode.OK,
            (await frontend.PutJsonAsync("/v1/settings", new { minimumIssueAgeDays = 7 })).StatusCode);
        Assert.Equal(7, await MinimumIssueAgeAsync(otherService));
    }

    [Fact]
    public async Task Searches_that_ask_leave_out_issues_younger_than_the_minimum_age()
    {
        var clock = new MovableClock();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<TimeProvider>(clock)));
        using var client = Authenticated(host);
        var group = $"age-{Guid.NewGuid():N}";
        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;
        await client.PostJsonAsync($"/v1/runs/{runId}/detections", new
        {
            detections = new[] { "01/001/0001", "01/001/0002" }.Select(cph => new
            {
                id = IssueId.Derive("rule2", cph).Value,
                ruleId = "rule2",
                context = new Dictionary<string, string> { ["cph"] = cph }
            }).ToArray()
        });
        await client.PostJsonAsync($"/v1/runs/{runId}/complete", new { });
        var list = $"/v1/issues?groupId={group}";
        var grouped = $"/v1/issues/by-cph?groupId={group}";

        clock.Now = clock.Now.AddDays(8);

        Assert.Equal(0, await TotalAsync(client, $"{list}&applyMinimumAge=true"));
        Assert.Equal(0, await TotalAsync(client, $"{grouped}&applyMinimumAge=true"));
        Assert.Equal(2, await TotalAsync(client, list));
        Assert.Equal(2, await TotalAsync(client, $"{list}&applyMinimumAge=false"));
        Assert.Equal(2, await TotalAsync(client, grouped));

        await client.PutJsonAsync("/v1/settings", new { minimumIssueAgeDays = 7 });

        Assert.Equal(2, await TotalAsync(client, $"{list}&applyMinimumAge=true"));
        Assert.Equal(2, await TotalAsync(client, $"{grouped}&applyMinimumAge=true"));
    }
}
