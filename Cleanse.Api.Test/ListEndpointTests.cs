using System.Net;
using System.Text.Json;
using Cleanse.Api.Test.Support;
using Cleanse.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cleanse.Api.Test;

/// <summary>The admin-maintained lists. One store serves every test here, so names carry a suffix.</summary>
public sealed class ListEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static IEnumerable<string> Names(JsonElement list) =>
        list.EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!);

    [Fact]
    public async Task An_entry_is_added_listed_and_switched_off_but_never_removed()
    {
        using var client = factory.CreateAuthenticatedClient();
        var name = $"Updated SAM {Guid.NewGuid():N}";

        var added = await client.PostJsonAsync("/v1/lists/action-types", new { name = $"  {name} " });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var entry = await added.ReadJsonAsync();
        var id = entry.GetProperty("id").GetGuid();
        Assert.Equal(name, entry.GetProperty("name").GetString());
        Assert.Equal("action-types", entry.GetProperty("list").GetString());
        Assert.True(entry.GetProperty("isActive").GetBoolean());
        Assert.Contains(name, Names(await (await client.GetAsync("/v1/lists/action-types")).ReadJsonAsync()));
        Assert.DoesNotContain(name, Names(await (await client.GetAsync("/v1/lists/resolved-reasons")).ReadJsonAsync()));

        var off = await client.PutJsonAsync($"/v1/lists/action-types/{id}/active", new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await off.ReadJsonAsync()).GetProperty("isActive").GetBoolean());
        Assert.Contains(name, Names(await (await client.GetAsync("/v1/lists/action-types")).ReadJsonAsync()));
        Assert.DoesNotContain(name, Names(await (await client.GetAsync("/v1/lists/action-types?activeOnly=true")).ReadJsonAsync()));

        var on = await client.PutJsonAsync($"/v1/lists/action-types/{id}/active", new { isActive = true });
        Assert.True((await on.ReadJsonAsync()).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task A_name_already_in_the_list_is_a_conflict_whatever_its_case()
    {
        using var client = factory.CreateAuthenticatedClient();
        var name = $"Fixed in SAM {Guid.NewGuid():N}";
        await client.PostJsonAsync("/v1/lists/resolved-reasons", new { name });

        var again = await client.PostJsonAsync("/v1/lists/resolved-reasons", new { name = name.ToUpperInvariant() });
        var otherList = await client.PostJsonAsync("/v1/lists/cant-resolve-reasons", new { name });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("coded_value.duplicate_name", (await again.ReadJsonAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, otherList.StatusCode);
    }

    [Fact]
    public async Task Bad_requests_are_refused()
    {
        using var client = factory.CreateAuthenticatedClient();
        var id = (await (await client.PostJsonAsync("/v1/lists/action-types", new { name = $"Wrote {Guid.NewGuid():N}" }))
            .ReadJsonAsync()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/lists/nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostJsonAsync("/v1/lists/nonsense", new { name = "Anything" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostJsonAsync("/v1/lists/action-types", new { name = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostJsonAsync("/v1/lists/action-types", new { name = new string('x', 101) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutJsonAsync($"/v1/lists/action-types/{id}/active", new { })).StatusCode);

        // An entry belongs to one list, and is only found through it.
        var wrongList = await client.PutJsonAsync($"/v1/lists/resolved-reasons/{id}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.NotFound, wrongList.StatusCode);
        Assert.Equal("coded_value.not_found", (await wrongList.ReadJsonAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutJsonAsync($"/v1/lists/action-types/{Guid.NewGuid()}/active", new { isActive = false })).StatusCode);
    }

    [Fact]
    public async Task Only_the_front_end_may_change_the_lists()
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
        var name = new { name = $"Phoned {Guid.NewGuid():N}" };

        Assert.Equal(HttpStatusCode.Forbidden, (await otherService.PostJsonAsync("/v1/lists/action-types", name)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherService.GetAsync("/v1/lists/action-types")).StatusCode);
        var added = await frontend.PostJsonAsync("/v1/lists/action-types", name);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var id = (await added.ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await otherService.PutJsonAsync($"/v1/lists/action-types/{id}/active", new { isActive = false })).StatusCode);
    }
}
