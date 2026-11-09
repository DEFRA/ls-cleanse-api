using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Cleanse.Api.Test.Support;
using Cleanse.Auth;
using Cleanse.Core.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cleanse.Api.Test;

public sealed class UserEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static RecordUserLoginRequest Login() => new()
    {
        Issuer = "https://login.microsoftonline.com/tenant/v2.0",
        Subject = Guid.NewGuid().ToString(),
        TenantId = Guid.NewGuid().ToString(),
        ObjectId = Guid.NewGuid().ToString(),
        ClientId = Guid.NewGuid().ToString(),
        FullName = "Alex Example"
    };

    [Fact]
    public async Task Login_is_an_upsert_with_server_timestamps_and_updates_display_name()
    {
        using var client = factory.CreateAuthenticatedClient();
        var login = Login();
        var before = DateTimeOffset.UtcNow;
        var firstResponse = await client.PostAsJsonAsync("/v1/users/logins", login);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = (await firstResponse.Content.ReadFromJsonAsync<UserDto>())!;
        var secondResponse = await client.PostAsJsonAsync("/v1/users/logins", login with { FullName = "Alex Renamed" });
        var second = (await secondResponse.Content.ReadFromJsonAsync<UserDto>())!;

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.FirstLoggedInAtUtc, second.FirstLoggedInAtUtc);
        Assert.InRange(first.FirstLoggedInAtUtc!.Value, before, DateTimeOffset.UtcNow);
        Assert.InRange(second.LastLoggedInAtUtc!.Value, first.LastLoggedInAtUtc!.Value, DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.Zero, second.LastLoggedInAtUtc.Value.Offset);
        Assert.Equal("Alex Renamed", second.FullName);
        Assert.Equal(login.Issuer, second.Issuer);
        Assert.Equal(login.Subject, second.Subject);
        Assert.Equal(login.TenantId, second.TenantId);
        Assert.Equal(login.ObjectId, second.ObjectId);
        Assert.Equal(login.ClientId, second.ClientId);
    }

    [Fact]
    public async Task Directory_uses_standard_paging_and_page_alias()
    {
        using var client = factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync("/v1/users/logins", Login());
        await client.PostAsJsonAsync("/v1/users/logins", Login());

        var first = await (await client.GetAsync("/v1/users?pageNumber=1&pageSize=1")).ReadJsonAsync();
        var second = await (await client.GetAsync("/v1/users?page=2&pageSize=1")).ReadJsonAsync();
        var total = first.GetProperty("totalCount").GetInt32();
        Assert.True(total >= 2);
        Assert.Equal(1, first.GetProperty("pageNumber").GetInt32());
        Assert.Equal(1, first.GetProperty("pageSize").GetInt32());
        Assert.Equal(total, first.GetProperty("totalPages").GetInt32());
        Assert.NotEqual(first.GetProperty("items")[0].GetProperty("id").GetGuid(),
            second.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("?pageNumber=0")]
    [InlineData("?pageSize=501")]
    [InlineData("?pageNumber=2147483647")]
    [InlineData("?pageNumber=1&page=2")]
    public async Task Invalid_directory_queries_return_400(string query)
    {
        using var client = factory.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/v1/users{query}")).StatusCode);
    }

    [Fact]
    public async Task Registration_adds_people_who_have_not_signed_in_and_reports_those_already_known()
    {
        using var client = factory.CreateAuthenticatedClient();
        var suffix = Guid.NewGuid().ToString("N");
        var request = new RegisterUsersRequest
        {
            Emails = [$"Kyle.Lloyd2.{suffix}@apha.gov.uk", $" kyle.lloyd2.{suffix}@APHA.gov.uk ", $"seb.paterson.{suffix}@defra.gov.uk", "  "]
        };

        var firstResponse = await client.PostAsJsonAsync("/v1/users", request);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = (await firstResponse.Content.ReadFromJsonAsync<RegisterUsersResultDto>())!;
        var second = (await (await client.PostAsJsonAsync("/v1/users", request))
            .Content.ReadFromJsonAsync<RegisterUsersResultDto>())!;

        Assert.Equal(2, first.Created.Count);
        Assert.Empty(first.Existing);
        var kyle = first.Created[0];
        Assert.Equal($"kyle.lloyd2.{suffix}@apha.gov.uk", kyle.Email);
        Assert.StartsWith("Kyle Lloyd", kyle.FullName, StringComparison.Ordinal);
        Assert.Null(kyle.Subject);
        Assert.Null(kyle.LastLoggedInAtUtc);
        Assert.Empty(second.Created);
        Assert.Equal(first.Created.Select(u => u.Id), second.Existing.Select(u => u.Id));
    }

    [Fact]
    public async Task Registration_with_an_invalid_address_adds_nobody()
    {
        using var client = factory.CreateAuthenticatedClient();
        var valid = $"valid.{Guid.NewGuid():N}@example.test";

        var response = await client.PostAsJsonAsync("/v1/users",
            new RegisterUsersRequest { Emails = [valid, "Name <not-an-email>"] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not-an-email", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(valid, (await client.GetStringAsync("/v1/users?pageSize=500")), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/v1/users", new RegisterUsersRequest { Emails = [] })).StatusCode);
    }

    [Fact]
    public async Task A_registered_person_is_claimed_at_their_first_sign_in()
    {
        using var client = factory.CreateAuthenticatedClient();
        var email = $"alex.claim.{Guid.NewGuid():N}@example.test";
        var registered = (await (await client.PostAsJsonAsync("/v1/users", new RegisterUsersRequest { Emails = [email] }))
            .Content.ReadFromJsonAsync<RegisterUsersResultDto>())!.Created[0];

        var signedIn = (await (await client.PostAsJsonAsync("/v1/users/logins",
                Login() with { Email = null, PreferredUsername = email.ToUpperInvariant(), FullName = "Alex From Entra" }))
            .Content.ReadFromJsonAsync<UserDto>())!;

        Assert.Equal(registered.Id, signedIn.Id);
        Assert.Equal("Alex From Entra", signedIn.FullName);
        Assert.Equal(email, signedIn.Email);
        Assert.NotNull(signedIn.Subject);
    }

    [Fact]
    public async Task Invalid_and_missing_identity_claims_return_400()
    {
        using var client = factory.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/v1/users/logins", Login() with { Subject = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/v1/users/logins", Login() with { FullName = new string('x', 257) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/v1/users/logins", new { fullName = "not-attested" })).StatusCode);
    }

    [Fact]
    public async Task Anonymous_wrong_key_and_browser_bearer_requests_cannot_read_or_attest_users()
    {
        using var anonymous = factory.CreateClient();
        using var wrong = factory.CreateClientWithKey("wrong");
        using var browser = factory.CreateBearerClient("browser.entra.token");
        foreach (var client in new[] { anonymous, wrong, browser })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync("/v1/users/logins", Login())).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync("/v1/users", new { emails = new[] { "a@example.test" } })).StatusCode);
        }
    }

    [Fact]
    public async Task User_routes_and_stable_assignment_reject_other_authenticated_services()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication("test-service")
                .AddScheme<AuthenticationSchemeOptions, DirectoryTestAuthenticationHandler>("test-service", _ => { });
            // Deliberately allow every authenticated service generally, to prove the directory's
            // extra policy still restricts it to ls-cleanse-ui without tightening legacy assignment.
            services.AddAuthorization(options => options.AddPolicy(
                CleansePolicies.Default, policy => policy.RequireAuthenticatedUser()));
        }));
        using var frontend = host.CreateClient();
        frontend.DefaultRequestHeaders.Add("x-test-service", "ls-cleanse-ui");
        using var otherService = host.CreateClient();
        otherService.DefaultRequestHeaders.Add("x-test-service", "another-service");
        var login = Login();
        var created = await frontend.PostAsJsonAsync("/v1/users/logins", login);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var user = (await created.Content.ReadFromJsonAsync<UserDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await frontend.GetAsync("/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherService.GetAsync("/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await otherService.PostAsJsonAsync("/v1/users/logins", login)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await otherService.PostAsJsonAsync("/v1/users", new { emails = new[] { "a@example.test" } })).StatusCode);

        var suffix = Guid.NewGuid().ToString("N");
        await ResetSeed.RunAsync(frontend, $"service-users-{suffix}", suffix);
        var route = $"/v1/issues/{ResetSeed.Id($"{suffix}-a")}/assignee";
        var stable = new { userId = user.Id };
        Assert.Equal(HttpStatusCode.Forbidden, (await otherService.PutAsJsonAsync(route, stable)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await frontend.PutAsJsonAsync(route, stable)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await otherService.PutAsJsonAsync(route, new { assignTo = "legacy" })).StatusCode);
    }

    [Fact]
    public async Task Stable_assignment_validates_the_user_audits_version_and_detaches_from_imports()
    {
        using var client = factory.CreateAuthenticatedClient();
        var login = Login();
        var user = (await (await client.PostAsJsonAsync("/v1/users/logins", login))
            .Content.ReadFromJsonAsync<UserDto>())!;
        var suffix = Guid.NewGuid().ToString("N");
        await ResetSeed.RunAsync(client, $"users-{suffix}", suffix);
        var issueId = ResetSeed.Id($"{suffix}-a");
        var route = $"/v1/issues/{issueId}/assignee";
        var original = await (await client.GetAsync($"/v1/issues/{issueId}")).ReadJsonAsync();
        var version = original.GetProperty("version").GetInt32();

        foreach (var invalid in new object[]
        {
            new { userId = Guid.NewGuid(), expectedVersion = version },
            new { userId = user.Id, assignTo = "forged" }
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(route, invalid)).StatusCode);
        }

        // tenantId and clientId from an older front end are ignored rather than rejected.
        var payload = new { userId = user.Id, tenantId = "ignored", clientId = "ignored", expectedVersion = version };
        var assignedResponse = await client.PutAsJsonAsync(route, payload);
        Assert.Equal(HttpStatusCode.OK, assignedResponse.StatusCode);
        var assigned = await assignedResponse.ReadJsonAsync();
        Assert.Equal(user.Id, assigned.GetProperty("assignedUserId").GetGuid());
        Assert.Equal(user.FullName, assigned.GetProperty("assignedTo").GetString());
        Assert.Equal(user.FullName, assigned.GetProperty("assignedUserFullName").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.String, assigned.GetProperty("assignmentDetachedAtUtc").ValueKind);
        Assert.Equal(version + 1, assigned.GetProperty("version").GetInt32());

        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(route, payload)).StatusCode);
        var repeated = await (await client.PutAsJsonAsync(route,
            new { userId = user.Id, expectedVersion = version + 1 })).ReadJsonAsync();
        Assert.Equal(version + 1, repeated.GetProperty("version").GetInt32());

        var audit = await (await client.GetAsync($"/v1/issues/{issueId}/audit")).ReadJsonAsync();
        var changes = audit.GetProperty("items").EnumerateArray()
            .SelectMany(e => e.GetProperty("changes").EnumerateArray());
        Assert.Contains(changes, c => c.GetProperty("field").GetString() == "AssignedUserId" &&
            c.GetProperty("to").GetString() == user.Id.ToString());

        var legacy = await (await client.PutAsJsonAsync(route, new { assignTo = user.FullName })).ReadJsonAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, legacy.GetProperty("assignedUserId").ValueKind);
        Assert.Equal(user.FullName, legacy.GetProperty("assignedTo").GetString());
        await client.PutAsJsonAsync(route, new { userId = user.Id });
        var unassigned = await (await client.PutAsJsonAsync(route, new { userId = (Guid?)null })).ReadJsonAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, unassigned.GetProperty("assignedUserId").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, unassigned.GetProperty("assignedTo").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, unassigned.GetProperty("assignedUserFullName").ValueKind);
    }

    [Fact]
    public async Task Openapi_describes_the_user_endpoints_and_stable_assignment()
    {
        var doc = await (await factory.CreateClient().GetAsync("/v1/openapi.json")).ReadJsonAsync();
        var paths = doc.GetProperty("paths");
        Assert.True(paths.GetProperty("/v1/users/logins").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty("/v1/users").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/v1/users").TryGetProperty("post", out _));
        Assert.Contains("assignedUserId", doc.GetRawText());
    }
}

internal sealed class DirectoryTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var service = Request.Headers["x-test-service"].ToString();
        if (string.IsNullOrEmpty(service))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, service),
            new Claim(ClaimTypes.Name, service),
            new Claim(CleanseClaims.CallerKind, CleanseClaims.ServiceCaller),
            new Claim(CleanseClaims.ServiceName, service)
        ], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

public sealed class UserDirectoryAuthorizationTests
{
    [Theory]
    [InlineData("Development", "service", "ls-cleanse-ui", true)]
    [InlineData("Production", "service", "ls-cleanse-ui", true)]
    [InlineData("Production", "service", "cleanse-app", false)]
    [InlineData("Production", "service", "another-service", false)]
    [InlineData("Production", "service", null, false)]
    [InlineData("Development", "api-key", null, true)]
    [InlineData("Production", "api-key", null, false)]
    public async Task Only_frontend_service_or_development_key_can_use_directory(
        string environmentName, string callerKind, string? serviceName, bool allowed)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        using var services = new ServiceCollection().AddLogging()
            .AddCleanseAuthorization(new CleanseAuthOptions(), environment).BuildServiceProvider();
        var claims = new List<Claim> { new(CleanseClaims.CallerKind, callerKind) };
        if (serviceName is not null)
        {
            claims.Add(new Claim(CleanseClaims.ServiceName, serviceName));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test-auth"));
        var result = await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, null, CleansePolicies.UserDirectory);
        Assert.Equal(allowed, result.Succeeded);
    }

    [Theory]
    [InlineData(false, "ls-cleanse-ui", false)]
    [InlineData(true, "local-developer", false)]
    [InlineData(true, "ls-cleanse-ui", true)]
    [InlineData(true, "cleanse-app", false)]
    public async Task Production_local_key_requires_explicit_opt_in_and_frontend_machine_name(
        bool optIn, string clientName, bool allowed)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var auth = new CleanseAuthOptions
        {
            ApiKey = new ApiKeyOptions { ClientName = clientName },
            UserDirectory = new UserDirectoryAuthOptions { AllowLocalApiKey = optIn }
        };
        using var services = new ServiceCollection().AddLogging()
            .AddCleanseAuthorization(auth, environment).BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, clientName),
            new Claim(CleanseClaims.CallerKind, CleanseClaims.ApiKeyCaller)
        ], "test-key"));
        var result = await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, null, CleansePolicies.UserDirectory);
        Assert.Equal(allowed, result.Succeeded);
    }
}
